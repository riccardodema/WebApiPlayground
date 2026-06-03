# Health checks — liveness & readiness

L'API espone **due probe distinti**, secondo la best practice usata dagli orchestratori
(Kubernetes, App Service, load balancer):

| Endpoint | Probe | Domanda | Cosa controlla | Uso |
|---|---|---|---|---|
| `/health/live` | **liveness** | Il processo è vivo? | nulla (nessuna dipendenza) | l'orchestratore **riavvia** l'istanza se fallisce |
| `/health/ready` | **readiness** | Posso servire traffico? | dipendenze tagged `ready` (DB) | l'orchestratore **toglie/aggiunge** l'istanza dal routing |

**Perché separati.** Se mettessi il check del DB nel liveness, un DB temporaneamente irraggiungibile
farebbe **riavviare** l'app (inutile e dannoso: il restart non ripara il DB). Il readiness invece
deve toglierla dal traffico finché la dipendenza non torna. Liveness = "sono vivo", readiness =
"sono pronto".

## Dove sta nel codice

| Cosa | Dove |
|---|---|
| Registrazione check DB (`AddDbContextCheck`, tag `ready`) | `Infrastructure/DependencyInjection.cs` |
| Tag che classifica i check di dipendenza | `Infrastructure/HealthChecks/HealthCheckTags.cs` (`Ready`) |
| Mapping endpoint + response writer JSON | `Api/HealthChecks/HealthCheckEndpoints.cs` (`MapApiHealthChecks`) |
| Aggancio pipeline | `Program.cs`: `app.MapApiHealthChecks()` (in **ogni** ambiente) |

**Scelta di layering:** il check del DB vive in **Infrastructure** (è il layer che possiede la
persistenza, e quindi sa *come* verificarla); l'API possiede gli **endpoint** e consuma il tag
`HealthCheckTags.Ready` per filtrare cosa entra nel readiness. La direzione delle dipendenze
(API → Infrastructure) resta rispettata.

## Dettagli implementativi

- **Liveness** usa `Predicate = _ => false`: non esegue alcun check, risponde `Healthy` finché il
  processo è in grado di rispondere.
- **Readiness** usa `Predicate = c => c.Tags.Contains("ready")`: oggi solo il check `database`
  (`CanConnect` sul `PlaygroundDbContext`). Aggiungere una dipendenza al readiness = registrarne
  il check con il tag `Ready`.
- **Status code** li imposta il middleware: `200` Healthy/Degraded, `503` Unhealthy. Il
  `ResponseWriter` custom scrive un body JSON diagnostico (`status`, `totalDurationMs`, lista dei
  `checks`) arricchito col `correlationId` (da `HttpContext.Items`), così è correlabile ai log.
- **Anonimi**: i probe non passano per `[Authorize]` (l'orchestratore non ha un token) e sono
  attivi in tutti gli ambienti — a differenza di OpenAPI/Scalar, mappati solo in Development.

## CI/CD

L'health-check post-deploy delle pipeline colpisce **`/health/ready`** (vedi
`.claude/context/cicd.md`): in produzione conferma che l'app è su **e** raggiunge il DB. Prima si
usava `/openapi/v1.json`, che in prod non esiste → falso verde. Vedi `[L09]`.

## Test

`tests/WebApiPlayground.IntegrationTests/HealthChecks/HealthCheckTests.cs`: liveness raggiungibile
anonimamente e `Healthy` con lista check vuota; readiness `Healthy` con la entry `database` (DB
reale via Testcontainers); `correlationId` nel body = header `X-Correlation-Id`.
