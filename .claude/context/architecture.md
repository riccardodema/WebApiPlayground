# Architettura — Clean Architecture

## Struttura layer e dipendenze

```
API  →  Application  →  Domain
 ↓            ↓
Infrastructure
```

**Regole dipendenze (mai violare):**
- `Domain` → nessuna dipendenza
- `Application` → solo `Domain`
- `Infrastructure` → `Domain` + `Application`
- `API` → `Application` (business) + `Infrastructure` (solo DI in `Program.cs`)
- `Tests` → `Application` + `Domain` (mai `Infrastructure` o `API`)

> Queste regole non sono solo documentate: sono **auto-validate** dagli architecture test
> (`tests/WebApiPlayground.ArchitectureTests/`, NetArchTest). I test ispezionano l'IL degli
> assembly e falliscono il build se un layer introduce una dipendenza vietata (es. `Domain` o
> `Application` che referenziano EF Core / ASP.NET, oppure un layer inferiore che risale verso
> l'API). Aggiungendo un nuovo layer o namespace, aggiornare gli anchor in `ArchitectureRules.cs`.

**Dettagli tecnologici confinati (regole specifiche auto-validate):** oltre EF Core e ASP.NET,
`Application` non deve dipendere dai **concreti della cache** (`FusionCache`/`Redis` — solo l'astrazione
`HybridCache`, vedi `caching.md`) né dai concreti della **resilienza/HTTP** (`Polly`,
`Microsoft.Extensions.Http(.Resilience)` — solo l'astrazione `IBookPopularityClient`, vedi `resilience.md`).
HttpClient tipizzato e pipeline Polly vivono nella composition root (Infrastructure).

## Struttura cartelle

```
src/
  WebApiPlayground.Api/             # Controllers, Program.cs
    Controllers/
  WebApiPlayground.Domain/          # Entità POCO
    Entities/
  WebApiPlayground.Application/     # DTOs, interfacce, servizi
    DTOs/
    Interfaces/
    Services/
  WebApiPlayground.Infrastructure/  # EF Core, repository
    Persistence/
    Repositories/
    DependencyInjection.cs
tests/
  WebApiPlayground.Tests/
    Services/                       # Unit test con Moq
```

## Flusso request

```
Controller → IService → IRepository → RepositoryImpl → PlaygroundDbContext → SQL Server
            (App)        (App)          (Infra)
```

## DI pattern

Ogni layer espone `Add{Layer}(this IServiceCollection services)`.  
`Program.cs` chiama solo `AddApplication()` e `AddInfrastructure(config)`.  
Scope: `AddScoped` per repository e service.
