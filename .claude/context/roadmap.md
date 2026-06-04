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
- ⬜ **Optimistic concurrency** (rowversion/ETag + `If-Match`) sul PUT → 412/428: PR dedicata
  (separata dal versioning per "una capability per PR"). Riusa l'infrastruttura ETag esistente.

## Tier 3 — Observability distribuita

- ⬜ **OpenTelemetry**: traces + metrics (export OTLP), correlati alla storia CorrelationId/Serilog.
- ⬜ **Resilience** (`Microsoft.Extensions.Http.Resilience` / Polly): retry/circuit-breaker/timeout
  su una dipendenza esterna (da introdurre insieme).

## Tier 4 — Asincronia / messaging (capstone)

- ⬜ **Background processing in-process**: `IHostedService` + `System.Threading.Channels`
  (es. job async "reindex"/notifica), con backpressure.
- ⬜ **Outbox pattern** + broker (**Azure Service Bus**): scrittura transazionale outbox →
  dispatcher → consumer. Il pezzo più "distributed systems".

## Tier 5 — Meta / polish

- ✅ **Architecture tests** (NetArchTest): fanno *rispettare* le regole di layering prima solo
  documentate in `architecture.md` → architettura auto-validante. Progetto
  `tests/WebApiPlayground.ArchitectureTests/`, agganciato alla CI insieme agli unit test (Tier 5
  parzialmente avviato in anticipo rispetto alla cache, su richiesta). Vedi `architecture.md`.
- ⬜ **Dockerfile + docker-compose** per l'API (oggi si containerizzano solo i test via
  Testcontainers).
- ⬜ **Key Vault config provider** a runtime: l'app legge i secret dalla KV già creata in IaC.

---

## Principi di esecuzione

- Una capability per PR; branch dedicato; merge solo con check verde su `main` protetta.
- Ogni feature aggiorna la documentazione in `.claude/context/` e il router in `CLAUDE.md`.
- I pitfall scoperti vanno in `.claude/lessons.md` (`[L0N]`), non dispersi nei commit.
- Niente segreti nel repo; azioni sensibili (push/deploy) confermate prima di eseguirle.
