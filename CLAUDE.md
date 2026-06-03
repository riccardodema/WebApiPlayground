# WebApiPlayground — Claude Router

## Quando leggere cosa

| Task | File da leggere |
|------|-----------------|
| Capire dove sta andando il progetto / cosa implementare (priorità, backlog capability backend) | `.claude/context/roadmap.md` |
| Aggiungere risorsa REST | `.claude/context/architecture.md` + `.claude/context/conventions.md` |
| Capire struttura/dipendenze | `.claude/context/architecture.md` |
| Scrivere codice (entity/dto/service/repo/controller) | `.claude/context/conventions.md` |
| Paginare/ordinare un endpoint GET lista (`PagedResult<T>`, page/size, sort) | `.claude/context/conventions.md` (sez. Paginazione) + `.claude/lessons.md` [L07] |
| Autenticazione/autorizzazione endpoint (Entra ID, JWT, ruoli/scope, `[Authorize]`, policy) | `.claude/context/auth.md` |
| Errori/eccezioni → risposta HTTP (ProblemDetails RFC 7807, exception handler, 500, correlationId nel body) | `.claude/context/error-handling.md` |
| Health check / probe liveness-readiness (`/health/live`, `/health/ready`, probe DB, orchestratore) | `.claude/context/health-checks.md` |
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
