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
- ✅ Test pyramid: unit (Moq), integration (Testcontainers), IaC (Bicep→ARM)
- ✅ CI/CD doppia (Azure DevOps + GitHub Actions), DB as code (DACPAC), IaC (Bicep + Key Vault)

---

## Tier 1 — Production readiness (fondamenta)

Gap evidenti per qualunque API di produzione. Bassa complessità, alto segnale.

- ⬜ **Exception handling globale + ProblemDetails (RFC 7807).** Oggi un'eccezione non gestita
  → 500 grezzo. Aggiungere `AddProblemDetails()` + `IExceptionHandler` che emette un payload
  ProblemDetails con il `CorrelationId` (correlazione log↔risposta). Mapping eccezioni note →
  status code.
- ⬜ **Health checks** `/health/live` (liveness) + `/health/ready` (readiness con probe DB via
  EF `DbContext`). Agganciare il CI/CD a `/health/ready` al posto di `/openapi/v1.json` (che in
  prod **non esiste**: OpenAPI è mappato solo in Development — vedi `Program.cs`).
- ⬜ **Validation input** (FluentValidation) su `CreateBookDto` (Title non vuoto/lunghezza,
  AuthorId > 0) + nuovo endpoint **Update (PUT)** per completare il CRUD. Errori → 400
  ProblemDetails coerente col punto sopra.

## Tier 2 — Pattern moderni (differenziatori)

- ⬜ **Caching**: HTTP caching (ETag + `Cache-Control`) sui GET + server-side `HybridCache`
  (in-memory ora, Redis-ready). Storia completa di **cache invalidation** su create/update/delete.
- ⬜ **Idempotency**: middleware `Idempotency-Key` per i POST (store + replay della prima risposta).
- ⬜ **Rate limiting**: rate limiter nativo .NET con policy (es. fixed/sliding window) + 429
  ProblemDetails.
- ⬜ **API versioning** (`Asp.Versioning`) + **optimistic concurrency** (rowversion/ETag) sul PUT.

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

- ⬜ **Architecture tests** (NetArchTest): fanno *rispettare* le regole di layering oggi solo
  documentate in `architecture.md` → architettura auto-validante.
- ⬜ **Dockerfile + docker-compose** per l'API (oggi si containerizzano solo i test via
  Testcontainers).
- ⬜ **Key Vault config provider** a runtime: l'app legge i secret dalla KV già creata in IaC.

---

## Principi di esecuzione

- Una capability per PR; branch dedicato; merge solo con check verde su `main` protetta.
- Ogni feature aggiorna la documentazione in `.claude/context/` e il router in `CLAUDE.md`.
- I pitfall scoperti vanno in `.claude/lessons.md` (`[L0N]`), non dispersi nei commit.
- Niente segreti nel repo; azioni sensibili (push/deploy) confermate prima di eseguirle.
