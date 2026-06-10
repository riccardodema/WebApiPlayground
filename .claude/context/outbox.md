# Outbox pattern — arricchimento popolarità durevole (at-least-once)

**Tier 4, step 2** della roadmap. **PR-1**: outbox transazionale **senza broker** — risolve la debolezza
at-most-once dello step 1 ([background-processing.md](background-processing.md), [L21]). **PR-2** ([in fondo](#pr-2--broker-azure-service-bus-config-gated)):
broker **Azure Service Bus** dietro la stessa astrazione, con **consumer disaccoppiato** — attivo solo se
configurato, altrimenti si resta sul path in-process di PR-1. Pitfall in `.claude/lessons.md` [L22] [L24].

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
                                                                        • deserializza l'evento → IIntegrationEventPublisher.PublishAsync
                                                                        • ProcessedAt = now  (SOLO a successo)

   IIntegrationEventPublisher (il "dove va" l'evento — scelto in config):
     ├─ default  →  InProcessIntegrationEventPublisher ─► IntegrationEventHandler ─► IPopularityEnricher  (PR-1, in-process)
     └─ ServiceBus configurato → ServiceBusIntegrationEventPublisher ─► [queue] ─► ServiceBusIntegrationEventConsumer
                                                                                      └─► IntegrationEventHandler ─► IPopularityEnricher
```

| Componente | Layer | Ruolo |
|------------|-------|-------|
| `IntegrationEvent` (+ `PopularityEnrichmentRequested`) | Application | Evento serializzabile (JSON); `EventType` = discriminatore. |
| `IIntegrationEventPublisher` | Application | **Trasporto** dell'evento (dove va consegnato). Astrazione del seam outbox→consegna. |
| `IPopularityEnricher` | Application | Logica di arricchimento riusabile (client resiliente → upsert snapshot). |
| `IBookRepository.Create/UpdateAsync(book, Func<int,IntegrationEvent>)` | Application | Firma che porta la **factory** dell'evento outbox. |
| `OutboxMessage` (+ tabella) | Domain / DB | Riga outbox: `Type`/`Payload`/`OccurredAt`/`ProcessedAt`/`Attempts`/`Error`. |
| `IntegrationEventSerialization` | Infrastructure | Sorgente unica del contratto JSON + mappa `Type → concreto` (riga outbox e body ASB). |
| `OutboxMessageFactory` | Infrastructure | `IntegrationEvent` → riga (Type + JSON), via `IntegrationEventSerialization`. |
| `OutboxProcessor` | Infrastructure | **Unità di lavoro** (scoped): processa un batch, **pubblica** via trasporto, marca a successo, isola i fallimenti. |
| `OutboxDispatcher : BackgroundService` | Infrastructure | **Loop di hosting**: polla e delega al processore (scope per giro). |
| `IntegrationEventHandler` | Infrastructure | **Routing** evento→enricher (+ span correlato). Condiviso dai due trasporti. |
| `InProcessIntegrationEventPublisher` | Infrastructure | Trasporto **default**: gestisce subito in-process (= comportamento PR-1). |
| `ServiceBus*` (publisher, consumer, options, registration) | Infrastructure | Trasporto **broker** (PR-2): pubblica sulla coda / consuma e arricchisce. Vedi sotto. |
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
`MaxAttempts` (soglia poison). L'outbox è **sempre attiva e durevole**; la *pubblicazione su ASB* è **config-gated**
(sezione `ServiceBus`, come Redis/OTLP), con fallback al path in-process (vedi sotto).

---

# PR-2 — Broker Azure Service Bus (config-gated)

PR-1 consegna **mono-processo**: un solo dispatcher a polling arricchisce in-process. Per scalare a più istanze
(competing-consumers) servirebbe un lock di riga (`UPDLOCK`/`READPAST`) o un **broker**. PR-2 introduce il broker
**Azure Service Bus** *dietro la stessa astrazione*, **senza cambiare il path di write** e **senza richiedere Azure**
per girare (gating).

## Il seam: `IIntegrationEventPublisher`

Il `OutboxProcessor` non chiama più l'enricher: deserializza la riga e la **pubblica** via `IIntegrationEventPublisher`
(Application, BCL pura). Il *dove va* l'evento è scelto nella composition root (`AddOutboxProcessing`):

| Trasporto | Quando | `PublishAsync` significa | `ProcessedAt` marcato quando |
|-----------|--------|--------------------------|------------------------------|
| `ServiceBusIntegrationEventPublisher` | `ServiceBus:ConnectionString` o `:FullyQualifiedNamespace` valorizzati | **invia** il messaggio sulla coda | il **broker** ha accettato il messaggio (durevole) |
| `InProcessIntegrationEventPublisher` | nessuna config `ServiceBus` (solo dev offline) | gestisci **subito** in-process (→ `IntegrationEventHandler` → enricher) | l'arricchimento in-process è riuscito |

> **ASB è il percorso reale, non opzionale.** È attivo in **docker-compose** (emulatore, vedi sotto) e in
> **Production** (managed identity). Fuori da Development il broker è **obbligatorio**: `StartupConfigurationValidator`
> fa **fail-fast** se manca `ServiceBus:FullyQualifiedNamespace`. L'in-process resta solo come comodità per il bare
> `dotnet run` **senza** Docker/emulatore (Development), così l'app gira anche offline — stesso spirito del gating di
> Redis/OTLP.

> **Punto chiave:** in modalità ASB, "consegnato" (e quindi `ProcessedAt`) significa **handoff durevole al broker**,
> non "arricchito". L'arricchimento avviene **dopo**, nel consumer, con il *suo* at-least-once. È il design canonico
> outbox→broker: l'outbox garantisce che l'evento arrivi al broker, il broker garantisce che arrivi al consumer.

## Consumer disaccoppiato + at-least-once lato broker

`ServiceBusIntegrationEventConsumer` (`BackgroundService`, registrato solo se ASB è configurato) riceve dalla coda e
riusa lo **stesso** `IntegrationEventHandler` del path in-process (zero duplicazione). Settlement **manuale**
(`AutoCompleteMessages = false`):

- successo → `CompleteMessageAsync` (rimosso dalla coda);
- fallimento → `AbandonMessageAsync` → ASB **ridistribuisce**; oltre `maxDeliveryCount` della coda → **dead-letter**
  (resta per diagnostica, non blocca gli altri);
- **scope per messaggio** (enricher/DbContext freschi e isolati, come il dispatcher in-process).

Il consumer è **idempotente** perché l'enricher fa upsert dello snapshot 1:1 col libro → una redelivery è sicura.

## Correlazione oltre il broker

Lo span `Popularity.Enrich` parte **dentro** `IntegrationEventHandler` dal `traceparent` W3C trasportato
nell'evento (catturato all'enqueue). Vale per **entrambi** i trasporti: nel path ASB la trace della write originaria
prosegue **oltre il broker**, agganciando lo span del consumer alla richiesta che ha generato l'evento.

## Auth: managed identity (no SAS)

`ServiceBusOptions` supporta due modi: `ConnectionString` (SAS — pensata per **emulatore/locale**) oppure
`FullyQualifiedNamespace` + `DefaultAzureCredential` (managed identity in Azure → **nessun segreto**). In Azure si usa
il secondo; il modulo Bicep forza `disableLocalAuth: true` (SAS disabilitate), coerente col principio "no SAS" del
Key Vault. RBAC least-privilege sull'ambito **coda**: Data Sender (l'outbox pubblica) + Data Receiver (il consumer
riceve), non Owner.

## IaC (`infra/modules/servicebus.bicep`)

Namespace **Standard** (le code lo richiedono; abilita anche i topic per il futuro) + coda
`popularity-enrichment` (dead-lettering, `maxDeliveryCount`, lock duration), RBAC condizionale/idempotente,
diagnostica opzionale verso Log Analytics. Cablato in `main.bicep` dietro il toggle `enableServiceBus` (lo SKU
Standard ha un costo fisso → spegnibile su ambienti non-live, come `enableMonitoring`). **Scritto e validato con
`bicep build` + test IaC (`ServiceBusModuleTests`), ma — finché non esiste un profilo Azure — NON ancora deployato
né verificato con `what-if`.** Vedi `infra/README.md`.

## In locale: docker-compose con l'emulatore

`docker compose up` accende anche l'**emulatore ufficiale** Service Bus (`docker-compose.yml`, servizio
`servicebus`) → il giro reale **publisher → coda → consumer** gira in locale, non è una modalità a parte. L'app
riceve `ServiceBus__ConnectionString` puntata all'emulatore (host = nome servizio, `UseDevelopmentEmulator=true`,
SAS statica nota) e la coda è dichiarata in `docker/servicebus-emulator/Config.json` (stesso nome
`popularity-enrichment`). L'emulatore richiede un SQL di supporto: riusa il container `db` (`SQL_SERVER=db`), niente
terzo container. Il consumer **riprova** `StartProcessingAsync` finché l'emulatore non è pronto (ordine di boot in
compose). Solo amd64 → `platform: linux/amd64` su arm64 [L23]. Vedi [docker.md](docker.md).

## Predisposto per il Worker

Il consumer non dipende dall'API: è già pronto per lo split in un processo Worker dedicato
(`Microsoft.NET.Sdk.Worker`) — riusa `IPopularityEnricher`/`IntegrationEventHandler`, codice messaging segregato in
`Outbox/ServiceBus/` apposta.

## Test (vedi anche `.claude/lessons.md` [L24])

- **Seam senza broker** (`OutboxTransportTests`): sostituisce il trasporto con un publisher **fake** che registra e
  non arricchisce → prova che il processore *pubblica* l'evento corretto e marca processato, **senza** creare lo
  snapshot (arricchimento delegato al trasporto). Deterministico, niente Docker extra.
- **Routing/serializzazione** (`IntegrationEventHandlerTests`, `IntegrationEventSerializationTests`): unit dei pezzi
  condivisi (tipi internal esposti ai test via `InternalsVisibleTo`).
- **End-to-end col broker reale** (`ServiceBusOutboxTests`): **emulatore** ufficiale ASB via Testcontainers (coda di
  default `queue.1`), dispatcher reale acceso → POST → publish→consume→enrich → snapshot. Factory dedicata
  (`ServiceBusEnabledApiFactory`) con container isolato, nella collection serializzata [L18]. **Niente account Azure.**

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
