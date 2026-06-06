# Background processing in-process — arricchimento asincrono della popolarità

**Tier 4, step 1** della roadmap (`IHostedService` + `System.Threading.Channels`, con backpressure). Questa
pagina copre il processamento asincrono **in-process**, senza Outbox né broker (step 2). Pitfall in
`.claude/lessons.md` [L21].

> **Aggiornamento (step 2, PR-1):** il **flusso popolarità è migrato all'Outbox transazionale** — vedi
> [outbox.md](outbox.md) e [L22]. Il `PopularityEnrichmentWorker` su canale è **superato** dal dispatcher
> dell'outbox (durevole, at-least-once); la sua logica vive ora in `IPopularityEnricher`. Il **toolbox generico**
> `IBackgroundTaskQueue<T>`/`ChannelBackgroundTaskQueue<T>`/`BackgroundQueueWorker<T>` resta come primitiva
> riusabile (non più cablata alla popolarità). Le sezioni sotto descrivono il meccanismo a canale dello step 1.

## Perché

`GET /books/{id}/popularity` chiama una dipendenza esterna (Open Library) — resiliente e cachata, ma a cache
fredda la latenza è sul path della richiesta, e la create/update di un libro non pre-calcola nulla. **Idea:**
spostare la chiamata esterna **fuori dal path di scrittura**. Alla create/update si accoda un work item; un
`BackgroundService` lo consuma, chiama il client resiliente (che **scalda la cache** `(title,author)`) e
**persiste uno snapshot durevole**. Benefici:

- **Write veloci**: il `POST/PUT` non aspetta Open Library.
- **Primo read caldo**: la cache è già scaldata quando l'utente legge il libro appena creato.
- **Fallback durevole**: lo snapshot regge le outage *anche a cache fredda* (oltre il fail-safe volatile L1), es. dopo un restart.

## Modello di freschezza (decisione di design)

Le letture restano **cache-first** e si auto-rinfrescano; lo snapshot **non è** la fonte normale del read:

```
GET popularity → client (cache → live, come sempre)
   cache hit / fresh ........................ 200
   cache miss → ATTENDE la chiamata live .... 200 + ri-cache   (no stale preemptivo)
   outage (resilienza esaurita & fail-safe vuoto)
        → snapshot durevole presente? ....... 200 last-known-good
                              assente? ....... 503 (come prima)
```

- **Niente refresh periodico** — non si vuole martellare la dipendenza esterna a ogni giro su tutto il catalogo.
  La freschezza la dà il TTL della cache + la ri-chiamata live sul miss: un libro vecchio letto oggi ha la entry
  scaduta da tempo → cache miss → **live fresco**, non la popolarità di quando fu inserito.
- **Niente stale-while-revalidate** — non si serve un dato vecchio per poi rinfrescarlo; sul miss si attende il live.
- **Lo snapshot appare solo in degrado** (outage + cache fredda), mai come fonte preemptiva.

> Cache e snapshot **coesistono** a ruoli distinti: la cache `(title,author)` accelera la *chiamata esterna*
> (volatile, TTL breve, stampede protection); lo snapshot `BookId` è la *persistenza durevole* (sopravvive a
> restart/outage, con `RetrievedAt`/`Source`). Vedi `caching.md` e `resilience.md`.

## Architettura

```
POST/PUT /books ─(dopo commit OK)─► BooksService: queue.TryEnqueue(req)   [best-effort, non bloccante]
                                            ▼
            IBackgroundTaskQueue<PopularityEnrichmentRequest>           [astrazione: Application]
            = ChannelBackgroundTaskQueue<T> (Channel<T> bounded, cap N)  [impl: Infrastructure]
                                            │ DequeueAsync (loop, scope-per-item)
                                            ▼
      PopularityEnrichmentWorker : BackgroundQueueWorker<T>             [Infrastructure]
        per item → IBookPopularityClient.GetPopularityAsync (RIUSATO):
           • scalda la cache (title,author)
           • upsert BookPopularitySnapshots (store durevole)
```

| Componente | Layer | Ruolo |
|------------|-------|-------|
| `IBackgroundTaskQueue<T>` | Application | Astrazione coda (primitive BCL: `TryEnqueue`/`DequeueAsync`/`Depth`). |
| `PopularityEnrichmentRequest` | Application | Work item minimale: `BookId` + `ActivityContext` (trace propagata oltre il confine async). |
| `IBookPopularitySnapshotRepository` | Application | Get/Upsert dello snapshot (entità di dominio). |
| `BackgroundProcessingDiagnostics` | Application | `Meter`/`ActivitySource` custom (counter enqueued/dropped/processed/failed). |
| `ChannelBackgroundTaskQueue<T>` | Infrastructure | Coda bounded su `Channel` (backpressure, depth). |
| `BackgroundQueueWorker<T>` | Infrastructure | **Base riusabile**: scope-per-item, isolamento eccezioni, stop graceful. |
| `PopularityEnrichmentWorker` | Infrastructure | Consumer concreto: client → snapshot. |
| `BookPopularitySnapshot` (+ tabella) | Domain / DB | Store durevole 1:1 col libro (DACPAC, FK cascade). |

**Producer**: `BooksService.Create/UpdateBookAsync` (Application) accoda dopo il commit.
**Consumer**: `PopularityEnrichmentWorker` registrato con `AddHostedService` in `AddInfrastructure`.
**Read fallback**: `BookPopularityService` cattura `ExternalServiceUnavailableException` → legge lo snapshot.

## Layering (regola NetArchTest)

L'astrazione `IBackgroundTaskQueue<T>` (pura BCL) vive in Application. Il `Channel`, il `BackgroundService` e
l'`IServiceScopeFactory` stanno in Infrastructure. Regola: **Application non dipende da
`Microsoft.Extensions.Hosting` né `System.Threading.Channels`** (`Application_should_not_depend_on_hosting_or_channels`)
— stesso principio di cache (FusionCache) e resilienza (Polly).

## Backpressure

Coda **bounded** (capacità da `BackgroundProcessing:QueueCapacity`, default 100). `TryEnqueue` è **non
bloccante**: se la coda è piena scarta + conta `background.tasks.dropped` (best-effort), così la write HTTP non
rallenta mai. È un trade-off conscio: sotto sovraccarico prolungato si perde qualche arricchimento, ma le
scritture restano veloci.

## Shutdown graceful

Il loop rispetta lo `stoppingToken`: l'item in volo finisce, poi si esce pulito. Gli item ancora **in coda** allo
stop vengono abbandonati → semantica **at-most-once** (vedi sotto).

## Osservabilità (tie-in Tier 3)

- Lo span di processing (`Popularity.Enrich`) si **aggancia al `ParentContext`** catturato all'enqueue → il lavoro
  in background compare nella stessa trace della write (correlazione end-to-end oltre il confine async).
- Metriche: `background.tasks.{enqueued,dropped,processed,failed}` (Meter custom, registrato in `AddApiObservability`).

## La debolezza voluta → Outbox (step 2) — **realizzata in PR-1**

La coda era **in-memory** e l'enqueue **non transazionale** con la write DB: item persi al crash/restart, drop su
coda piena. Semantica **at-most-once**. Accettabile qui (read normale cache→live, snapshot solo fallback), ma è il
**movente diretto** dello step 2: **Outbox pattern** (riga outbox nella stessa transazione del libro) + dispatcher
→ arricchimento **at-least-once / durevole**. **PR-1 l'ha realizzato** (in-process, senza broker): vedi
[outbox.md](outbox.md). Il **broker Azure Service Bus** arriva in PR-2. `IPopularityEnricher` riusa qui la logica
di arricchimento; `BackgroundQueueWorker<T>`/`IBackgroundTaskQueue<T>` restano come toolbox generico riusabile.

## Test

- **Unit** (`tests/.../BackgroundProcessing/`): coda (FIFO, backpressure, cancellation, depth); base worker
  (**isolamento eccezioni** — un item che lancia non ferma il loop; **scope-per-item**; stop graceful); enrichment
  worker (persiste snapshot con `TimeProvider` iniettato; salta i libri inesistenti; isola gli errori del client).
- **Unit aggiornati**: `BookPopularityServiceTests` (fallback snapshot in outage; 503 se assente);
  `BooksServiceTests` (enqueue su create/update; drop best-effort senza throw).
- **Integration** (`PopularityEnrichmentTests`): `POST /books` → polling sullo snapshot → 200; outage + snapshot → 200 last-known-good.
- **Architecture**: `Application_should_not_depend_on_hosting_or_channels`.

I test asincroni sono **deterministici** via `TaskCompletionSource` (unit) e **polling con timeout** (integration);
mai `Task.Delay` arbitrari nelle asserzioni. Vedi [L21].
