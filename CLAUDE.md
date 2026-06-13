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
| Resilience / chiamata esterna robusta (Polly v8 / `Microsoft.Extensions.Http.Resilience`: pipeline esplicita timeout totale→retry backoff+jitter→circuit breaker→timeout per-tentativo su HttpClient tipizzato; dipendenza Open Library su `GET /books/{id}/popularity`; esaurimento→503 ProblemDetails + `Retry-After`; astrazione in Application, resilienza in Infrastructure, regola NetArchTest) + **cache della chiamata esterna** (FusionCache via `IFusionCache`, factory timeout infiniti, fail-safe→degrade-to-stale, stampede protection; perché non `HybridCache`/Application) | `.claude/context/resilience.md` + `.claude/lessons.md` [L19] [L20] |
| Background processing asincrono in-process (`IHostedService`/`BackgroundService` + `System.Threading.Channels` bounded con backpressure; producer event-driven sulle write, consumer riusabile `BackgroundQueueWorker<T>` con scope-per-item/isolamento eccezioni/shutdown graceful; arricchimento popolarità → snapshot durevole su DB usato come fallback d'outage; read cache→live invariato, no refresh periodico/SWR; astrazione `IBackgroundTaskQueue<T>` in Application, Channel/host in Infrastructure + regola NetArchTest; debolezza at-most-once → movente Outbox) | `.claude/context/background-processing.md` + `.claude/lessons.md` [L21] |
| Outbox pattern / arricchimento durevole (tabella `OutboxMessages` scritta nella **stessa transazione** della write tramite transazione esplicita EF — chiave IDENTITY → factory `Func<int,IntegrationEvent>` valutata post-INSERT; `OutboxProcessor` unità di lavoro **separata** dal loop `OutboxDispatcher : BackgroundService`; consegna **at-least-once** durevole, `ProcessedAt` solo a successo, consumer idempotente, isolamento/`Attempts`/poison; logica riusabile `IPopularityEnricher`; astrazioni in Application, EF/host in Infrastructure; test deterministici via `DrainOutboxAsync` con dispatcher hosted disattivato) | `.claude/context/outbox.md` + `.claude/lessons.md` [L22] |
| Outbox **PR-2 — broker Azure Service Bus** (trasporto `IIntegrationEventPublisher` config-gated: in-process di default, ASB se `ServiceBus` configurato; `OutboxProcessor` **pubblica** invece di arricchire, `ProcessedAt` = handoff durevole al broker; consumer disaccoppiato `ServiceBusIntegrationEventConsumer` con settlement manuale `Complete`/`Abandon`→dead-letter, idempotente; routing condiviso `IntegrationEventHandler`; auth managed identity **no SAS**; regola NetArchTest su `Azure.Messaging`; modulo `servicebus.bicep` + toggle `enableServiceBus`; test fake + **emulatore** ASB Testcontainers, no account Azure) | `.claude/context/outbox.md` (sez. PR-2) + `.claude/lessons.md` [L24] + `infra/README.md` |
| Key Vault config provider (segreti a runtime dal vault: `KeyVault:Uri` config-gated, credential esplicite ManagedIdentity/AzureCli/Emulator, fail-fast parlante + exit code non-zero, emulatore in compose/test — hardening no-NuGet, seed `infra/set-secrets.sh`, naming `--`→`:`) | `.claude/context/keyvault.md` + `docs/keyvault.md` + `.claude/lessons.md` [L25] [L26] |
| Test quality / scrivere o valutare test (mutation testing Stryker via `tests/run-mutation.sh` → score ~82%, coverage gate ratchet + badge, parità DACPAC↔EF, pipeline JWT reale con authority finta, probe strutturali; come alzare lo score leggendo il report — comportamento non implementazione; cosa è escluso dalla mutazione e perché) | `.claude/context/testing.md` + `.claude/lessons.md` [L25] [L27] [L28] [L29] [L30] |
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
| Containerizzazione: Dockerfile multi-stage chiseled non-root, docker-compose (API+SQL+migrations DACPAC, override Redis/Aspire), differenza vs Testcontainers, fail-fast config in Production, smoke test live; arm64 `platform: linux/amd64` | `.claude/context/docker.md` + `.claude/lessons.md` [L23] |

## Quick reference

```
Run:    dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj
Test:   dotnet test tests/WebApiPlayground.Tests/WebApiPlayground.Tests.csproj
DB:     dotnet build database/WebApiPlayground.Database.sqlproj -c Release   (→ DACPAC)
Deploy: DB_CONNECTION=... ./database/deploy.sh   (publish | script)
IaC:    AZURE_SUBSCRIPTION_ID=... ./infra/deploy.sh   (whatif | deploy)
Docker: cp .env.example .env && docker compose up --build   (API+SQL → http://localhost:8080/scalar/v1)
UI:     http://localhost:5242/scalar/v1   (docker compose: :8080)
JSON:   http://localhost:5242/openapi/v1.json
```

## Skills disponibili

- `/scaffold <NomeRisorsa>` — genera tutti i file Clean Architecture per una nuova risorsa
- `/migration <NomeMigration>` — aggiunge e applica una migration EF Core
- `/run` — avvia il progetto con istruzioni per connection string

## Commit convention

`<type>[scope]: <desc>` — tipi: `feat` `fix` `chore` `refactor` `test` `docs` `ci`

Esempio: `feat(books): add pagination to GetAllBooks endpoint`
