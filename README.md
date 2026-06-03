# WebApiPlayground

[![CI/CD](https://github.com/riccardodema/WebApiPlayground/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/riccardodema/WebApiPlayground/actions/workflows/ci-cd.yml)
[![PR Validation](https://github.com/riccardodema/WebApiPlayground/actions/workflows/pr-validation.yml/badge.svg)](https://github.com/riccardodema/WebApiPlayground/actions/workflows/pr-validation.yml)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

A small CRUD Web API (books/authors) used as a **production-like playground**: the
interesting part is not the domain but the engineering around it — Clean Architecture,
a versioned database, structured logging, a full test pyramid and CI/CD implemented on
**both Azure DevOps and GitHub Actions**.

> **About this project.** It's also a proof-of-concept for using **Claude as a coding
> copilot wired into a real GitHub workflow** — protected `main` with PR-based merges,
> MCP integrations (e.g. live SQL Server access), and CI/CD — aiming for a development
> loop that is modern and fast yet safe and detail-oriented (multi-step checks before
> any sensitive or irreversible action, no secrets in the repo).

## Architecture

Clean Architecture, dependencies point inwards (outer layers depend on inner, never the reverse):

```
┌─────────────────────────────────────────────────────────┐
│  Api            Controllers · Middleware · OpenAPI        │  ← HTTP, DI composition root
│  ─ depends on ─▶ Application                               │
│      Application   Services · DTOs · Interfaces           │  ← use cases, no infra detail
│      ─ depends on ─▶ Domain                                │
│          Domain        Entities                            │  ← pure model, zero dependencies
│  Infrastructure  EF Core DbContext · Repositories         │  ← implements Application interfaces
└─────────────────────────────────────────────────────────┘
```

| Pattern / practice | Where |
|---|---|
| Repository + Service layer | `Application/Services`, `Infrastructure/Repositories` |
| DTOs (no entity leakage over HTTP) | `Application/DTOs` |
| Dependency Injection per layer | `*/DependencyInjection.cs` (composed in `Api/Program.cs`) |
| Interface segregation (testable seams) | `Application/Interfaces` (`IBookRepository`, `IBooksService`) |
| Structured logging + correlation id | Serilog + `Api/Middleware/CorrelationIdMiddleware` |
| RFC 7807 error responses (ProblemDetails) | `Api/ErrorHandling/GlobalExceptionHandler` |
| Liveness / readiness health probes | `Api/HealthChecks` (`/health/live`, `/health/ready`) |

## Stack

- **.NET 10** Web API · **EF Core 10** (SQL Server / Azure SQL)
- **Scalar** for OpenAPI UI (Swashbuckle is incompatible with .NET 10)
- **Serilog** structured logging
- **xUnit · Moq · Testcontainers.MsSql** for testing
- **SQL Database Project (DACPAC)** for the database schema

## Database as code

The schema is **versioned in the solution** as a SQL Database Project
([`database/`](database/), SDK `Microsoft.Build.Sql`) — declarative `CREATE` per object,
built into a **DACPAC** and deployed with **SqlPackage** (computes the diff, no hand-written
ALTERs). The EF Core model is mapped 1:1 to it, and an idempotent post-deployment script
seeds reference data. See [database/README.md](database/README.md).

```bash
dotnet build database/WebApiPlayground.Database.sqlproj -c Release   # → .dacpac
DB_CONNECTION='...' ./database/deploy.sh                             # publish (or 'script' to review the diff)
```

## Infrastructure as code

Azure resources are **versioned in the repo** as [Bicep](https://learn.microsoft.com/azure/azure-resource-manager/bicep/)
([`infra/`](infra/)) — declarative, **idempotent** (re-applying the same state is a no-op),
with **what-if** as a mandatory preview and automated tests (Bicep build/lint, **xUnit** unit
tests over the compiled ARM, and **PSRule for Azure**). The foundation is an **Azure Key Vault** (RBAC auth, soft-delete, purge protection in
prod) where the DB connection string lives in production — created by IaC, with the secret
*value* set outside it so no secret ever flows through ARM deployments. See [infra/README.md](infra/README.md).

```bash
AZURE_SUBSCRIPTION_ID='...' ./infra/deploy.sh          # what-if (preview the diff)
AZURE_SUBSCRIPTION_ID='...' ./infra/deploy.sh deploy   # apply (idempotent)
```

CI/CD on both platforms ([`.github/workflows/infra.yml`](.github/workflows/infra.yml) ·
[`.azure/pipelines/infra.yml`](.azure/pipelines/infra.yml)): validate on PR, what-if → deploy on
`main` gated by the `production` environment. Same *disabled-until-configured* pattern as the app
deploy — the deploy job is skipped (not failed) until `AZURE_LOCATION` is set.

## Testing

- **Unit** — `tests/WebApiPlayground.Tests` (xUnit + Moq), services in isolation.
- **Integration** — `tests/WebApiPlayground.IntegrationTests`, real SQL Server spun up via
  **Testcontainers** (Docker), exercising the API end-to-end.
- **Architecture** — `tests/WebApiPlayground.ArchitectureTests` (**NetArchTest**), enforces the
  Clean Architecture layering rules at build time (e.g. Domain/Application must not reference
  EF Core or ASP.NET; lower layers must not depend on the API). Fast, no DB or Docker.
- **Infrastructure** — `tests/WebApiPlayground.IacTests`, compiles the Bicep to ARM and asserts
  the security posture / idempotency (no Azure or Docker; skipped if the Bicep CLI is absent).

```bash
dotnet test
```

## CI/CD — two implementations

The same pipeline is implemented twice to show portability across platforms:

| Stage | Azure DevOps ([`.azure/`](.azure/)) | GitHub Actions ([`.github/workflows/`](.github/workflows/)) |
|---|---|---|
| PR gate | `pr-validation.yml` | `pr-validation.yml` |
| Build + test (DRY) | `templates/` | reusable `build-test.yml` (`workflow_call`) |
| Release | `ci.yml` + `cd.yml` | `ci-cd.yml` |

Both: build the DACPAC, **publish the DB schema before deploying the app**, deploy to
Azure App Service, then **health-check** the `/health/ready` readiness probe. The deploy stage is gated by a
manual approval (Azure *Environment* / GitHub *Environment*), and GitHub Actions auth to
Azure uses **OIDC federated credentials** (no long-lived secrets). Setup details in
[.github/workflows/README.md](.github/workflows/README.md) and [.claude/context/cicd.md](.claude/context/cicd.md).

> Runnable on Azure free tier (App Service F1 + Azure SQL Database free, serverless with auto-pause).

## Run locally

```bash
# DB connection goes in src/WebApiPlayground.Api/appsettings.Development.json (git-ignored)
dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj
# Scalar UI:   http://localhost:5242/scalar/v1
# OpenAPI doc: http://localhost:5242/openapi/v1.json
```

## Development workflow

`main` is protected: no direct pushes, changes land via pull request, and a PR can
only be merged after the **PR Validation** check (`validate / build-test`) is green
and the branch is up to date. History is kept linear (squash/rebase), force-pushes
and branch deletion are disabled.

## Repository layout

```
src/
  WebApiPlayground.Domain          entities (no dependencies)
  WebApiPlayground.Application     services, DTOs, interfaces
  WebApiPlayground.Infrastructure  EF Core DbContext, repositories
  WebApiPlayground.Api             controllers, middleware, DI, OpenAPI
tests/
  WebApiPlayground.Tests           unit tests
  WebApiPlayground.ArchitectureTests NetArchTest layering rules (auto-validated architecture)
  WebApiPlayground.IntegrationTests Testcontainers-based integration tests
  WebApiPlayground.IacTests        Bicep→ARM infrastructure unit tests
database/                          SQL project (DACPAC) — schema as code
infra/                             Bicep IaC (Azure Key Vault) — infra as code
.azure/ · .github/                 CI/CD on Azure DevOps and GitHub Actions
```
