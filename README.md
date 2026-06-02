# WebApiPlayground

[![CI/CD](https://github.com/riccardodema/WebApiPlayground/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/riccardodema/WebApiPlayground/actions/workflows/ci-cd.yml)
[![PR Validation](https://github.com/riccardodema/WebApiPlayground/actions/workflows/pr-validation.yml/badge.svg)](https://github.com/riccardodema/WebApiPlayground/actions/workflows/pr-validation.yml)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)

A small CRUD Web API (books/authors) used as a **production-like playground**: the
interesting part is not the domain but the engineering around it — Clean Architecture,
a versioned database, structured logging, a full test pyramid and CI/CD implemented on
**both Azure DevOps and GitHub Actions**.

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

## Testing

- **Unit** — `tests/WebApiPlayground.Tests` (xUnit + Moq), services in isolation.
- **Integration** — `tests/WebApiPlayground.IntegrationTests`, real SQL Server spun up via
  **Testcontainers** (Docker), exercising the API end-to-end.

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
Azure App Service, then **health-check** `/openapi/v1.json`. The deploy stage is gated by a
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

## Repository layout

```
src/
  WebApiPlayground.Domain          entities (no dependencies)
  WebApiPlayground.Application     services, DTOs, interfaces
  WebApiPlayground.Infrastructure  EF Core DbContext, repositories
  WebApiPlayground.Api             controllers, middleware, DI, OpenAPI
tests/
  WebApiPlayground.Tests           unit tests
  WebApiPlayground.IntegrationTests Testcontainers-based integration tests
database/                          SQL project (DACPAC) — schema as code
.azure/ · .github/                 CI/CD on Azure DevOps and GitHub Actions
```
