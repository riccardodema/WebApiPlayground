# Stack tecnico e comandi

## Framework e package

| Package | Progetto | Versione |
|---------|----------|----------|
| `Microsoft.AspNetCore.OpenApi` | API | 10.0.0 |
| `Scalar.AspNetCore` | API | 2.6.0 |
| `Microsoft.EntityFrameworkCore` | Infrastructure | 10.0.0 |
| `Microsoft.EntityFrameworkCore.SqlServer` | Infrastructure | 10.0.0 |
| `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` | Infrastructure | 10.0.0 |
| `xunit` | Tests | 2.9.3 |
| `Moq` | Tests | 4.20.72 |
| `Microsoft.NET.Test.Sdk` | Tests | 17.12.0 |
| `NetArchTest.Rules` | ArchitectureTests | 1.3.2 |

## URL locali

| Risorsa | URL |
|---------|-----|
| Scalar UI | `http://localhost:5242/scalar/v1` |
| OpenAPI JSON | `http://localhost:5242/openapi/v1.json` |
| HTTP base | `http://localhost:5242` |

> ⚠️ Non usare `/swagger` — vedere `.claude/lessons.md`

## Comandi

```bash
# Avvio
dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj

# Test (unit)
dotnet test tests/WebApiPlayground.Tests/WebApiPlayground.Tests.csproj

# Test (architecture: regole di layering via NetArchTest — veloce, niente DB/Docker)
dotnet test tests/WebApiPlayground.ArchitectureTests/WebApiPlayground.ArchitectureTests.csproj

# EF Core migrations
dotnet ef migrations add <Nome> \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api

dotnet ef database update \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api

dotnet ef migrations remove \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api
```

## Connection string

Va in `src/WebApiPlayground.Api/appsettings.Development.json` (mai in `appsettings.json` o nel codice):

```json
{ "ConnectionStrings": { "Default": "Server=localhost;Database=WebApiPlayground;Trusted_Connection=True;TrustServerCertificate=True;" } }
```
