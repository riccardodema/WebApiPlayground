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
