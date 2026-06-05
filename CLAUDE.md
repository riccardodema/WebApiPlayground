# WebApiPlayground — Claude Router

## Quando leggere cosa

| Task | File da leggere |
|------|-----------------|
| Capire dove sta andando il progetto / cosa implementare (priorità, backlog capability backend) | `.claude/context/roadmap.md` |
| Aggiungere risorsa REST | `.claude/context/architecture.md` + `.claude/context/conventions.md` |
| Capire struttura/dipendenze | `.claude/context/architecture.md` |
| Regole di layering auto-validate / architecture test (NetArchTest, dipendenze tra layer) | `.claude/context/architecture.md` (sez. regole dipendenze) + `tests/WebApiPlayground.ArchitectureTests/` |
| Scrivere codice (entity/dto/service/repo/controller) | `.claude/context/conventions.md` |
| Paginare/ordinare un endpoint GET lista (`PagedResult<T>`, page/size, sort) | `.claude/context/conventions.md` (sez. Paginazione) + `.claude/lessons.md` [L07] |
| Autenticazione/autorizzazione endpoint (Entra ID, JWT, ruoli/scope, `[Authorize]`, policy); run locale senza Entra / errore `IDW10106` su Scalar | `.claude/context/auth.md` + `.claude/lessons.md` [L12] |
| Errori/eccezioni → risposta HTTP (ProblemDetails RFC 7807, exception handler, 500, correlationId nel body) | `.claude/context/error-handling.md` |
| Validazione input (FluentValidation, 400 ProblemDetails con `errors`, messaggi parlanti, regole nello schema OpenAPI) | `.claude/context/validation.md` + `.claude/lessons.md` [L10] |
| Health check / probe liveness-readiness (`/health/live`, `/health/ready`, probe DB, orchestratore) | `.claude/context/health-checks.md` |
| Caching (HTTP ETag/Cache-Control/304, HybridCache, FusionCache, L1/L2 Redis + backplane multi-istanza, invalidazione per tag) | `.claude/context/caching.md` + `.claude/lessons.md` [L11] |
| Idempotency (`Idempotency-Key` sui POST, replay prima risposta, 422 su riuso, store IDistributedCache Redis-ready, exactly-once) | `.claude/context/idempotency.md` + `.claude/lessons.md` [L14] |
| Rate limiting (rate limiter nativo .NET, policy sliding window read/write, partizione per client, 429 ProblemDetails + `Retry-After`, `[EnableRateLimiting]`) | `.claude/context/rate-limiting.md` + `.claude/lessons.md` [L15] |
| API versioning (Asp.Versioning, segmento URL `/api/v{n}/`, `[ApiVersion]`/`[MapToApiVersion]`, documento OpenAPI per versione, esempio v2 con DTO evoluto, header `api-supported-versions`) | `.claude/context/api-versioning.md` + `.claude/lessons.md` [L16] |
| Optimistic concurrency (rowversion EF Core, ETag = token di versione + `If-Match` obbligatorio su PUT/DELETE → 412 stale / 428 mancante, conflitto→ProblemDetails, riuso ETagResultFilter) | `.claude/context/optimistic-concurrency.md` + `.claude/lessons.md` [L17] |
| Observability / OpenTelemetry (traces + metrics + logs via OTLP, auto-instrumentation ASP.NET Core/HttpClient/EF/runtime + `ActivitySource`/`Meter` custom in Application, export config-gated, bridge Serilog→OTLP, correlazione `TraceId`↔`CorrelationId`↔`traceId` ProblemDetails) | `.claude/context/opentelemetry.md` + `.claude/lessons.md` [L18] |
| Resilience / chiamata esterna robusta (Polly v8 / `Microsoft.Extensions.Http.Resilience`: pipeline esplicita timeout totale→retry backoff+jitter→circuit breaker→timeout per-tentativo su HttpClient tipizzato; dipendenza Open Library su `GET /books/{id}/popularity`; esaurimento→503 ProblemDetails + `Retry-After`; astrazione in Application, resilienza in Infrastructure, regola NetArchTest) | `.claude/context/resilience.md` + `.claude/lessons.md` [L19] |
| Package, URL, comandi dotnet/ef | `.claude/context/stack.md` |
| Prima di usare Swagger/Swashbuckle o configurare OpenAPI | `.claude/lessons.md` |
| Errori ricorrenti o approcci da evitare | `.claude/lessons.md` |
| Aggiungere logging a un layer / nuova risorsa | `.claude/context/logging.md` |
| Capire livelli, named properties, regole Serilog | `.claude/context/logging.md` |
| Configurare/capire pipeline CI/CD Azure DevOps | `.claude/context/cicd.md` |
| Configurare/capire CI/CD GitHub Actions | `.github/workflows/README.md` |
| Infrastruttura Azure (IaC/Bicep), Key Vault, what-if, deploy | `.claude/context/iac.md` + `infra/README.md` |
| Monitoring/diagnostics del Key Vault (audit log, Log Analytics, KQL) | `infra/docs/monitoring.md` |
| Schema DB versionato, SQL project, DACPAC, deploy/seed | `.claude/context/database.md` |
| Modificare tabelle/schema o allineare entità EF al DB | `.claude/context/database.md` + `.claude/context/conventions.md` |

## Quick reference

```
Run:    dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj
Test:   dotnet test tests/WebApiPlayground.Tests/WebApiPlayground.Tests.csproj
DB:     dotnet build database/WebApiPlayground.Database.sqlproj -c Release   (→ DACPAC)
Deploy: DB_CONNECTION=... ./database/deploy.sh   (publish | script)
IaC:    AZURE_SUBSCRIPTION_ID=... ./infra/deploy.sh   (whatif | deploy)
UI:     http://localhost:5242/scalar/v1
JSON:   http://localhost:5242/openapi/v1.json
```

## Skills disponibili

- `/scaffold <NomeRisorsa>` — genera tutti i file Clean Architecture per una nuova risorsa
- `/migration <NomeMigration>` — aggiunge e applica una migration EF Core
- `/run` — avvia il progetto con istruzioni per connection string

## Commit convention

`<type>[scope]: <desc>` — tipi: `feat` `fix` `chore` `refactor` `test` `docs` `ci`

Esempio: `feat(books): add pagination to GetAllBooks endpoint`
