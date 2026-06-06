# Outbox pattern — arricchimento popolarità durevole (at-least-once)

**Tier 4, step 2** della roadmap. **PR-1** (questa pagina): outbox transazionale **senza broker** — risolve la
debolezza at-most-once dello step 1 ([background-processing.md](background-processing.md), [L21]). **PR-2** (in
fondo): broker **Azure Service Bus** dietro la stessa astrazione. Pitfall in `.claude/lessons.md` [L22].

## Perché

Lo step 1 accodava l'arricchimento su una coda **in-memory dopo** il commit del libro → **at-most-once**: item
persi al crash, scartati a coda piena. L'enqueue non era transazionale con la write.

**Outbox ≠ broker.** L'Outbox è un pattern *lato DB*: il messaggio si scrive in una **tabella outbox nella stessa
transazione** della write di business → durevole e atomico (o committano insieme, o rollback insieme). Un
dispatcher legge la tabella e consegna. Il broker è solo il *trasporto* (PR-2); l'Outbox da solo dà già
**at-least-once durevole** (sopravvive a restart), eseguendo la consegna in-process.

## Architettura

```
POST/PUT /books ─► BookRepository (transazione esplicita):
                     1. INSERT Books            ┐ stessa
                     2. INSERT OutboxMessages   ┘ transazione  → atomico
                              │
                              ▼ (durevole su DB)
   OutboxDispatcher (BackgroundService, polling)  ─scope per giro─►  OutboxProcessor.ProcessPendingAsync()
                                                                        • SELECT non processati (FIFO, indice filtrato)
                                                                        • routing per Type → IPopularityEnricher.EnrichAsync
                                                                        • ProcessedAt = now  (SOLO a successo)
```

| Componente | Layer | Ruolo |
|------------|-------|-------|
| `IntegrationEvent` (+ `PopularityEnrichmentRequested`) | Application | Evento serializzabile (JSON); `EventType` = discriminatore. |
| `IPopularityEnricher` | Application | Logica di arricchimento riusabile (client resiliente → upsert snapshot). |
| `IBookRepository.Create/UpdateAsync(book, Func<int,IntegrationEvent>)` | Application | Firma che porta la **factory** dell'evento outbox. |
| `OutboxMessage` (+ tabella) | Domain / DB | Riga outbox: `Type`/`Payload`/`OccurredAt`/`ProcessedAt`/`Attempts`/`Error`. |
| `OutboxMessageFactory` | Infrastructure | `IntegrationEvent` → riga (Type + JSON). Opzioni serializer **condivise** col processore. |
| `OutboxProcessor` | Infrastructure | **Unità di lavoro** (scoped): processa un batch, marca a successo, isola i fallimenti. |
| `OutboxDispatcher : BackgroundService` | Infrastructure | **Loop di hosting**: polla e delega al processore (scope per giro). |
| `PopularityEnricher` | Infrastructure | Impl di `IPopularityEnricher` (estratta dal vecchio worker su canale). |

## Scrittura transazionale (il cuore del pattern)

Il producer ([BooksService](../../src/WebApiPlayground.Application/Services/BooksService.cs)) **non** accoda più: passa
al repository una factory `bookId => new PopularityEnrichmentRequested(bookId, traceParent)`. Il
[BookRepository](../../src/WebApiPlayground.Infrastructure/Repositories/BookRepository.cs) la materializa **dentro una
transazione esplicita**:

```
BeginTransaction → SaveChanges (book INSERT, Id IDENTITY assegnato)
                 → Add OutboxMessage con quell'Id → SaveChanges → Commit
```

Il `BookId` è **store-generated** (IDENTITY): non esiste prima dell'INSERT, perciò la riga outbox si materializza
*dopo* il primo `SaveChanges`, nella stessa transazione. Crash prima del commit → rollback di **entrambi**. (Un
`SaveChangesInterceptor` sarebbe pulito solo con chiavi client-generated; qui la transazione esplicita è più chiara.)

## Consegna: at-least-once + idempotenza

Il processore marca `ProcessedAt` **solo a successo** → un messaggio non consegnato resta e viene **riprovato**
(consegna **at-least-once**). Marcare dopo un semplice relay reintrodurrebbe l'at-most-once. Conseguenza: il
consumer dev'essere **idempotente** — lo snapshot è 1:1 col libro (`UpsertAsync` su `BookId`), quindi rielaborare
lo stesso evento è sicuro. Un fallimento del singolo messaggio è **isolato** (incrementa `Attempts`/`Error`, il
loop prosegue); oltre `MaxAttempts` è "poison" e non si riprova (resta in tabella per diagnostica).

Le **letture** restano invariate (cache→live, snapshot solo fallback d'outage — vedi [background-processing.md](background-processing.md)).

## Layering (regola NetArchTest)

Astrazioni in Application (BCL pura): `IntegrationEvent`, `IPopularityEnricher`. EF/`BackgroundService` in
Infrastructure. La regola esistente `Application_should_not_depend_on_hosting_or_channels` resta valida (l'Outbox
non introduce hosting in Application).

## Configurazione

Sezione `Outbox` (appsettings): `PollingInterval` (latenza max a regime), `BatchSize` (FIFO per giro),
`MaxAttempts` (soglia poison). L'outbox è **sempre attiva e durevole**; in PR-2 la *pubblicazione su ASB* sarà
config-gated (come Redis/OTLP), con fallback al path in-process.

## Limite voluto → PR-2 (Azure Service Bus)

Consegna **mono-processo** (didattico, tutto nello stesso repo POC) e dispatcher a **polling**: un solo processo.
Per multi-istanza servirebbe un lock di riga (`UPDLOCK`/`READPAST`) o un broker. È il movente di **PR-2**: pubblicare
su ASB dietro `IIntegrationEventPublisher`, con un **consumer disaccoppiato** che riusa `IPopularityEnricher` —
predisposto per uno split in un Worker (`Microsoft.NET.Sdk.Worker`) a costo basso. Codice messaging segregato in
`Outbox/` apposta.

## Test (deterministici, niente polling)

Il `OutboxProcessor` è **separato** dal loop di hosting proprio per i test: un hosted service che polla in continuo
su un DB **condiviso** dalla collection è flaky (corre con `EnsureCreated`, interferisce fra i test — vedi [L22]).
Nei test il dispatcher hosted è **disattivato** e si pilota `OutboxProcessor.ProcessPendingAsync` via
`PlaygroundApiFactory.DrainOutboxAsync()`:

- **Unit** (`PopularityEnricherTests`): l'enricher persiste lo snapshot col `TimeProvider` iniettato; salta i libri
  inesistenti; propaga l'errore in outage (nessuno snapshot).
- **Unit** (`BooksServiceTests`): create/update passano al repo una factory che produce `PopularityEnrichmentRequested`
  per l'Id corretto.
- **Integration** (`OutboxProcessingTests`): `POST` scrive la riga outbox **transazionalmente** (payload col BookId);
  `DrainOutboxAsync` → snapshot + `ProcessedAt` marcato; tipo sconosciuto → isolato, `Attempts++`, non processato.
- **Integration** (`PopularityEnrichmentTests`): POST → drain → snapshot; fallback d'outage col snapshot durevole.
- **Integration loop reale** (`OutboxDispatcherHostTests`): il vero `OutboxDispatcher` (hosted) **acceso** su una
  factory dedicata con **container isolato** (`DispatcherEnabledApiFactory`, niente interferenza) consegna da solo
  dopo una POST e raccoglie una riga preesistente non processata. Deterministico senza essere "a tempo": ambiente
  isolato + attesa dell'esito con timeout generoso (in pratica &lt;1s). Resta nella collection serializzata [L18].
