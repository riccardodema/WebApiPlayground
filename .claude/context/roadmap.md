# Roadmap — Backend engineering a 360°

Questo repo è un **POC didattico**: il dominio (books/authors) è banale di proposito, il
valore è nell'**ingegneria intorno**. Questa roadmap traccia le capability backend che il
progetto vuole dimostrare, organizzate per priorità. Ogni voce = idealmente **una PR**
(codice + test + aggiornamento di `.claude/context/` + entry in `lessons.md` se emerge un
pitfall), coerente con `main` protetta e la commit convention.

Legenda stato: ✅ fatto · 🚧 in corso · ⬜ da fare

---

## Già coperto (baseline)

- ✅ Clean Architecture a 4 layer con regole di dipendenza esplicite (`architecture.md`)
- ✅ Repository + Service + DTO + DI per layer
- ✅ Paginazione offset + sort type-safe (whitelist, tiebreaker deterministico — `[L07]`)
- ✅ Auth Entra ID (JWT, scope delegated + app-role, policy read/write — `auth.md`)
- ✅ Logging strutturato Serilog + CorrelationId middleware (`logging.md`)
- ✅ Test pyramid: unit (Moq), integration (Testcontainers), IaC (Bicep→ARM), architecture (NetArchTest)
- ✅ CI/CD doppia (Azure DevOps + GitHub Actions), DB as code (DACPAC), IaC (Bicep + Key Vault)

---

## Tier 1 — Production readiness (fondamenta)

Gap evidenti per qualunque API di produzione. Bassa complessità, alto segnale.

- ✅ **Exception handling globale + ProblemDetails (RFC 7807).** `AddProblemDetails()` +
  `IExceptionHandler` con `correlationId`/`traceId` (correlazione log↔risposta), `Detail` solo in
  Development. Vedi `.claude/context/error-handling.md`, `[L08]`. (PR #10)
- ✅ **Health checks** `/health/live` (liveness) + `/health/ready` (readiness con probe DB via
  `AddDbContextCheck`). CI/CD agganciato a `/health/ready` al posto di `/openapi/v1.json` (che in
  prod non esisteva). Vedi `.claude/context/health-checks.md`, `[L09]`.
- ✅ **Validation input** (FluentValidation) su `CreateBookDto`/`UpdateBookDto` (Title non
  vuoto/lunghezza ≤ 100, AuthorId > 0) + nuovo endpoint **Update (PUT)** per completare il CRUD.
  Errori → 400 ProblemDetails con mappa `errors` e messaggi parlanti, coerente col punto sopra
  (stesso `correlationId`/`traceId`); regole proiettate nello schema OpenAPI. Vedi
  `.claude/context/validation.md`, `[L10]`.

## Tier 2 — Pattern moderni (differenziatori)

- ✅ **Caching**: HTTP caching (ETag + `Cache-Control` + 304) sui GET + server-side `HybridCache`
  via **FusionCache** (`.AsHybridCache()`; L1 in-memory ora, **L2 Redis + backplane** config-gated per
  il multi-istanza). Storia completa di **cache invalidation** per tag su create/update/delete.
  Decoratore `CachingBooksService` (Application) sull'astrazione `HybridCache`; FusionCache/Redis in
  Infrastructure (regola architetturale che lo enforce). Vedi `.claude/context/caching.md`, `[L11]`.
- ✅ **Idempotency**: middleware `Idempotency-Key` per i POST (store + replay della prima risposta;
  422 sul riuso con payload diverso; store `IDistributedCache` memory ora, Redis-ready come la cache).
  Semantica exactly-once → niente duplicati sui retry. Vedi `.claude/context/idempotency.md`, `[L14]`.
- ✅ **Rate limiting**: rate limiter **nativo .NET** (`AddRateLimiter`/`UseRateLimiter`) con due policy
  **sliding window** — `read` (100/60s) e `write` (20/60s), valori motivati — partizionate per client
  (utente autenticato → claim, anonimo → IP, come l'idempotency). Oltre il limite → **429 ProblemDetails**
  (RFC 7807, stesso `correlationId`/`traceId`) con header `Retry-After`; 429 documentato nel contratto
  OpenAPI. In-memory per-istanza (Redis = percorso di scale-out, come cache/idempotency). Vedi
  `.claude/context/rate-limiting.md`, `[L15]`.
- ✅ **API versioning** (`Asp.Versioning`): schema per **segmento URL** (`/api/v{n}/books`), un
  **documento OpenAPI per versione** in Scalar, esempio **v2** con DTO evoluto (autore annidato) e
  scritture condivise tra le versioni; `ReportApiVersions` → header `api-supported-versions`. Vedi
  `.claude/context/api-versioning.md`, `[L16]`.
- ✅ **Optimistic concurrency**: colonna `rowversion` (concurrency token EF Core) esposta come **ETag**;
  `If-Match` **obbligatorio** su **PUT e DELETE** → **412** se stale, **428** se mancante (conflitto e
  precondizione come ProblemDetails RFC 7807, stesso `correlationId`/`traceId`). **Riusa l'`ETagResultFilter`**
  esistente: l'ETag del singolo libro diventa il token di versione (un header per caching condizionale *e*
  concorrenza). Vedi `.claude/context/optimistic-concurrency.md`, `[L17]`.

## Tier 3 — Observability distribuita

- ✅ **OpenTelemetry**: traces + metrics + logs via **OTLP** (export config-gated come Cache/Redis).
  Auto-instrumentation ASP.NET Core/HttpClient/EF Core/runtime + **source/meter custom** (`ActivitySource`/
  `Meter` con primitive BCL in Application, SDK solo in Api → arch-test-safe): span `Books.Create` + metrica
  `books.created`. **Logs via bridge Serilog→OTLP** (`Serilog.Sinks.OpenTelemetry`) che riattacca
  `TraceId`/`SpanId` e porta il `CorrelationId` — chiude il cerchio di correlazione (`CorrelationId`↔`TraceId`↔
  `traceId` dei ProblemDetails). Metriche del rate limiter incluse. Vedi `.claude/context/opentelemetry.md`, `[L18]`.
- ✅ **Resilience** (`Microsoft.Extensions.Http.Resilience` / Polly v8): pipeline **esplicita**
  (timeout totale → retry backoff+jitter → circuit breaker → timeout per-tentativo) su una dipendenza
  esterna reale — **Open Library** come HttpClient tipizzato. Nuovo endpoint
  `GET /api/v1/books/{id}/popularity` (proxy gratuito di domanda: rating + reading-log; i dati di vendita
  reali non sono pubblici). Esaurimento → **503 ProblemDetails** + `Retry-After` (handler dedicato nella
  catena, stesso `correlationId`/`traceId`). Astrazione `IBookPopularityClient` in Application, resilienza
  in Infrastructure (regola NetArchTest che lo enforce). La chiamata esterna è anche **cachata** (FusionCache
  via `IFusionCache`, factory timeout infiniti = il budget lo governa la pipeline) con **degrade-to-stale**
  (fail-safe → stale-200 invece di 503 durante un'outage) e stampede protection. Vedi
  `.claude/context/resilience.md`, `[L19]`, `[L20]`.

## Tier 4 — Asincronia / messaging (capstone)

- ✅ **Background processing in-process**: `IHostedService` + `System.Threading.Channels` con backpressure.
  Arricchimento popolarità **event-driven** sulle write: `POST/PUT /books` accoda (best-effort, non bloccante)
  su una coda bounded; un `BackgroundService` (base riusabile `BackgroundQueueWorker<T>`: scope-per-item,
  isolamento eccezioni, shutdown graceful) chiama il client resiliente (scalda la cache) e persiste uno
  **snapshot durevole** su DB. Read invariato (cache→live), snapshot solo come **fallback d'outage**; niente
  refresh periodico né SWR. Astrazione `IBackgroundTaskQueue<T>` in Application, Channel/host in Infrastructure
  (regola NetArchTest). Debolezza voluta (in-memory, at-most-once) = movente dell'Outbox. Vedi
  `.claude/context/background-processing.md`, `[L21]`.
- ✅ **Outbox pattern** + broker (**Azure Service Bus**): scrittura transazionale outbox →
  dispatcher → consumer. Il pezzo più "distributed systems".
  - ✅ **PR-1 — Outbox transazionale (senza broker).** La write scrive libro + riga `OutboxMessages` nella
    **stessa transazione** (transazione esplicita EF; chiave IDENTITY → factory evento valutata post-INSERT);
    `OutboxProcessor` (unità di lavoro) separato dal loop `OutboxDispatcher`; consegna **at-least-once** durevole
    in-process (`ProcessedAt` solo a successo, consumer idempotente, isolamento/`Attempts`/poison); logica
    riusabile `IPopularityEnricher` (sostituisce il worker su canale per la popolarità). Vedi
    `.claude/context/outbox.md`, `[L22]`.
  - ✅ **PR-2 — Broker Azure Service Bus + Bicep IaC.** Trasporto astratto `IIntegrationEventPublisher`: ASB è il
    percorso **reale** (docker-compose con emulatore + Production con managed identity), l'in-process resta solo
    come fallback per il bare `dotnet run` offline (fail-fast su `ServiceBus:FullyQualifiedNamespace` fuori da
    Development). Il `OutboxProcessor` **pubblica** invece di arricchire (`ProcessedAt` = handoff durevole al
    broker); consumer disaccoppiato `ServiceBusIntegrationEventConsumer` (settlement manuale, at-least-once lato
    broker, dead-letter, idempotente, retry di start) che riusa `IntegrationEventHandler`/`IPopularityEnricher`;
    auth managed identity **no SAS**; regola NetArchTest su `Azure.Messaging`. IaC: `infra/modules/servicebus.bicep`
    (RBAC least-privilege Sender+Receiver, `disableLocalAuth`) + toggle `enableServiceBus` in `main.bicep`.
    **Verificato end-to-end senza account Azure**: emulatore ASB ufficiale nei test (Testcontainers) **e** in
    `docker compose up` (POST → outbox `PROCESSED` → consumer → snapshot). Il Bicep è validato con `bicep build` +
    test IaC ma **non ancora deployato/`what-if`** (manca un profilo Azure). Vedi `.claude/context/outbox.md` (sez.
    PR-2), `[L24]`, `infra/README.md`.
    - ⬜ *Follow-up (alla creazione dell'account Azure):* `what-if`/deploy del modulo e smoke contro un
      namespace reale.

## Tier 5 — Meta / polish

- ✅ **Architecture tests** (NetArchTest): fanno *rispettare* le regole di layering prima solo
  documentate in `architecture.md` → architettura auto-validante. Progetto
  `tests/WebApiPlayground.ArchitectureTests/`, agganciato alla CI insieme agli unit test (Tier 5
  parzialmente avviato in anticipo rispetto alla cache, su richiesta). Vedi `architecture.md`.
- ✅ **Dockerfile + docker-compose** per l'API: immagine multi-stage **chiseled non-root** (porta
  8080), servizio `db-migrations` one-shot che pubblica lo schema via DACPAC (riusa `deploy.sh`),
  stack locale in un comando (API + SQL + opz. Redis/Aspire via override). Più **fail-fast esplicito**
  sulla config obbligatoria fuori da Development. Test contract + smoke live in `DockerTests`. Prima
  Docker serviva solo ai test (Testcontainers). Vedi `docker.md` + [L23].
- ✅ **Key Vault config provider** a runtime: i segreti (connection string DB; in compose anche quella
  dell'emulatore ASB) entrano in `IConfiguration` **dal vault**, config-gated su `KeyVault:Uri` (vuoto =
  spento, come Redis/OTLP/ASB), aggiunto per ULTIMO (vince su appsettings/env) e PRIMA del fail-fast (i
  secret soddisfano il validator). Credential **esplicita** per ambiente (`ManagedIdentity` default /
  `AzureCli` locale-vs-vault-reale / `Emulator` solo-Development) — niente `DefaultAzureCredential`.
  Vault irraggiungibile/negato → errore PARLANTE (uri, credential, cause, rimedio) + exit code non-zero
  (fix del catch di Program.cs che usciva 0). In compose l'**emulatore community** (immagine pinnata,
  cert one-shot openssl, seed REST one-shot) sostituisce le connection string nell'env dell'api; sul
  vault reale: `infra/set-secrets.sh`. E2e con emulatore via Testcontainers self-made (NO NuGet del
  progetto emulatore: scriverebbe nel trust store host). Scoperto+fixato [L25]: config WAF invisibile in
  fase builder → `ServiceBusOutboxTests` girava in-process; ora `UseSetting` + probe sul trasporto.
  Vedi `.claude/context/keyvault.md`, `docs/keyvault.md`, `[L25]` `[L26]`.

## Tier 6 — Test quality & prossimi passi (post-roadmap iniziale)

Backlog nato dalla retrospettiva a roadmap iniziale completata (giugno 2026), ordinato per
valore-CV. Il gap più grande del progetto resta **il deploy reale su Azure** (tutto è
emulato/locale): scala in cima appena esiste una subscription.

- ✅ **Test-quality hardening** — "dimostrare che i test funzionano", non solo averli:
  **Stryker.NET** (mutation testing: incrementale `--since` su ogni PR + full SOLO manuale via
  `mutation-full.yml` — niente schedulazioni, scelta utente), **coverage gate RATCHET**
  (line+branch combinata unit+integration contro `tests/coverage-thresholds.json`, solo in salita;
  scoperto che `--collect` in CI non raccoglieva nulla: mancava `coverlet.collector`), **badge
  self-hosted** (branch `badges`, shields-endpoint, no Codecov), **parità DACPAC↔EF** (l'app gira
  sullo schema deployato dal pacchetto vero via DacFx: confronto colonna-per-colonna + SELECT su
  ogni entità + write-path; prima i test giravano su uno schema che nessuno deploya — [L27]),
  **pipeline JWT reale** contro authority OIDC finta in-proc (matrice esaustiva
  firma/issuer/audience/lifetime/scope/ruoli — [L28]) + colmati i buchi di coverage individuati
  dalla baseline (v2 endpoints, DLQ poison del consumer ASB, upsert snapshot, FK failure→500,
  bypass dev, rami del transformer OpenAPI/client popolarità/DI Redis).
  Vedi `.claude/context/testing.md`.
- ✅ **Mutation score 25% → ~82%** — letti i report Stryker, scritti/raffinati gli unit test mancanti
  (comportamento + edge/boundary, non implementazione): SQLite in-memory per repository/processor
  DB-bound, unit diretti su 5 transformer OpenAPI + middleware (idempotency/correlation/ETag/handler),
  service di popolarità (degrade-to-snapshot), ctor-null/diagnostics/contratti d'eccezione. Combinato
  **81.7%** (App 84.8 / Infra 82.6 / Api 80.5), break ratchet alzato a 78; mutanti residui
  onestamente non killabili documentati (ref-count interno, versione assembly, TLS emulatore). Vedi [L30].
- ⬜ **Deploy reale su Azure** *(bloccato dalla creazione dell'account)*: what-if/deploy del Bicep,
  immagine su GHCR/ACR, **Azure Container Apps**, managed identity vera verso KV+ASB, CD attivo,
  smoke su namespace/vault reali. Chiude i follow-up di Tier 4/keyvault.
- ⬜ **Worker service split**: consumer ASB fuori dall'API in un progetto Worker dedicato
  (predisposto in PR-2): scale-out indipendente, secondo servizio in compose.
- ⬜ **Keyset (cursor) pagination + SQL performance**: indici motivati, confronto piani, benchmark
  — la profondità SQL oggi mancante.
- ⬜ **Supply chain**: Central Package Management (`Directory.Packages.props`), dependabot +
  CodeQL, scan immagine (Trivy) in CI.
- ⬜ **OpenAPI contract gate**: snapshot del documento committato + diff in CI che blocca breaking
  change non dichiarati.
- ⬜ **Presentazione**: sezione "recruiter tour" + diagramma mermaid nel README, ADR pubblici in
  `docs/adr/` (ricavati dalle lessons), release/tag `v1.0`.

---

## Principi di esecuzione

- Una capability per PR; branch dedicato; merge solo con check verde su `main` protetta.
- Ogni feature aggiorna la documentazione in `.claude/context/` e il router in `CLAUDE.md`.
- I pitfall scoperti vanno in `.claude/lessons.md` (`[L0N]`), non dispersi nei commit.
- Niente segreti nel repo; azioni sensibili (push/deploy) confermate prima di eseguirle.
