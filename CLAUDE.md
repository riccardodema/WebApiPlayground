# WebApiPlayground — Istruzioni per Claude

## Panoramica del progetto

Web API RESTful in ASP.NET Core 10 per la gestione di **Books** e **Authors**.
Playground didattico per esplorare pattern e best practice per le Web API .NET, strutturato secondo Clean Architecture.

- Framework: ASP.NET Core 10 (`net10.0`)
- ORM: Entity Framework Core 10 + provider SQL Server
- Documentazione API: Swagger/OpenAPI (Swashbuckle 7.3)
- Test: xUnit 2.9 + Moq 4.20
- Nullable reference types abilitati

---

## Struttura della solution

```
WebApiPlayground/
├── WebApiPlayground.sln
├── src/
│   ├── WebApiPlayground.Api/             # API layer: controllers, Program.cs (→ Application, Infrastructure)
│   │   ├── Controllers/
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   └── appsettings.Development.json  ← connection string locale qui
│   ├── WebApiPlayground.Domain/          # Entità di dominio (nessuna dipendenza esterna)
│   │   └── Entities/
│   ├── WebApiPlayground.Application/     # DTOs, interfacce, servizi (→ Domain)
│   │   ├── DTOs/
│   │   ├── Interfaces/
│   │   └── Services/
│   └── WebApiPlayground.Infrastructure/  # EF Core, repository (→ Domain, Application)
│       ├── Persistence/
│       ├── Repositories/
│       └── DependencyInjection.cs
└── tests/
    └── WebApiPlayground.Tests/           # xUnit + Moq (→ Application, Domain)
        └── Services/
```

---

## Architettura (Clean Architecture)

```
API  →  Application  →  Domain
 ↓            ↓
Infrastructure (EF Core, implementazioni repository)
```

**Regola delle dipendenze** (mai violare):
- `Domain` non dipende da nulla
- `Application` dipende solo da `Domain`
- `Infrastructure` dipende da `Domain` e `Application`
- `API` dipende da `Application` (business) e `Infrastructure` (solo per la registrazione DI in `Program.cs`)
- `Tests` dipende da `Application` e `Domain` (mai da Infrastructure o API)

**Flusso di una request:**
```
Controller  →  IBooksService  →  IBookRepository  →  BookRepository  →  PlaygroundDbContext  →  SQL Server
             (Application)      (Application)        (Infrastructure)
```

---

## Convenzioni di codice

### Entità (`Domain/Entities/`)
- Classe POCO con `[Key]` sull'`Id`
- Nessuna dipendenza da EF Core o altri layer
- Stringhe inizializzate a `string.Empty`
- Navigation property nullable per FK many-to-one
- Collezioni inizializzate a `new List<T>()` per one-to-many

```csharp
public class Book
{
    [Key]
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int AuthorId { get; set; }
    public Author? Author { get; set; }
}
```

### DTO (`Application/DTOs/`)
- C# `record` immutabile con costruttore posizionale, suffisso `Dto`
- DTO separati per request/response se necessario (`CreateBookDto` vs `BookDto`)
- Solo i campi necessari, nessuna navigation property

```csharp
public record BookDto(int Id, string Title, string AuthorFullName);
public record CreateBookDto(string Title, int AuthorId);
```

### Interfacce repository (`Application/Interfaces/`)
- Metodi async che restituiscono entità di dominio (non DTO)
- `I{Name}Repository`

```csharp
public interface IBookRepository
{
    Task<ICollection<Book>> GetAllAsync();
    Task<Book?> GetByIdAsync(int id);
    Task<Book> CreateAsync(Book book);
    Task<bool> DeleteAsync(int id);
}
```

### Service (`Application/Services/`)
- Implementa `I{Name}Service`
- Dipende dall'interfaccia repository (mai dall'implementazione)
- Responsabile del mapping entità → DTO
- `ArgumentNullException.ThrowIfNull()` nel costruttore

```csharp
public class BooksService : IBooksService
{
    private readonly IBookRepository _repository;
    public BooksService(IBookRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }
    private static BookDto MapToDto(Book book) =>
        new(book.Id, book.Title, book.Author?.FullName ?? string.Empty);
}
```

### Repository (`Infrastructure/Repositories/`)
- Implementa l'interfaccia definita in Application
- Restituisce entità di dominio (non DTO — il mapping è responsabilità del service)
- Query tramite LINQ + `Include`, sempre async

### Controller (`API/Controllers/`)
- `[ApiController]` + `[Route("api/[controller]")]`, eredita `ControllerBase`
- Dipende dall'interfaccia service (mai dal repository direttamente)
- Async `Task<IActionResult>`, restituisce `Ok()`, `NotFound()`, `CreatedAtAction()`, `NoContent()`
- Parametri di route tipizzati: `{id:int}`

### Dependency Injection
- Ogni layer espone un extension method `Add{Layer}(this IServiceCollection services)`
- `Program.cs` chiama solo `builder.Services.AddApplication()` e `builder.Services.AddInfrastructure(config)`
- Repository: `AddScoped`, Service: `AddScoped`

---

## Packages NuGet

| Package | Progetto | Versione |
|---------|----------|----------|
| `Microsoft.AspNetCore.OpenApi` | API | 10.0.0 |
| `Swashbuckle.AspNetCore` | API | 7.3.1 |
| `Microsoft.EntityFrameworkCore` | Infrastructure | 10.0.0 |
| `Microsoft.EntityFrameworkCore.SqlServer` | Infrastructure | 10.0.0 |
| `xunit` | Tests | 2.9.3 |
| `Moq` | Tests | 4.20.72 |
| `Microsoft.NET.Test.Sdk` | Tests | 17.12.0 |

---

## Connection string

La connection string locale va in `WebApiPlayground/appsettings.Development.json`:

```json
{
  "ConnectionStrings": {
    "Default": "Server=localhost;Database=WebApiPlayground;..."
  }
}
```

**Non mettere** connection string in `appsettings.json` o nel codice.

---

## Avvio locale

```bash
dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj
```

- Swagger UI: `https://localhost:7123/swagger`
- HTTP: `http://localhost:5242`

Prerequisito: .NET 10 SDK installato + connection string valida in `appsettings.Development.json`.

---

## Test

```bash
dotnet test tests/WebApiPlayground.Tests/WebApiPlayground.Tests.csproj
```

I test sono **unit test puri** del service layer: il repository viene mockato con Moq.
Non richiedono database né EF Core.

---

## Comandi EF Core

```bash
# Tool globale (una tantum)
dotnet tool install --global dotnet-ef

# Aggiungere una migration
dotnet ef migrations add <NomeMigration> --project src/WebApiPlayground.Infrastructure --startup-project src/WebApiPlayground.Api

# Applicare al database
dotnet ef database update --project src/WebApiPlayground.Infrastructure --startup-project src/WebApiPlayground.Api

# Rimuovere l'ultima migration (se non applicata)
dotnet ef migrations remove --project src/WebApiPlayground.Infrastructure --startup-project src/WebApiPlayground.Api
```

---

## Custom commands disponibili

- `/scaffold <NomeRisorsa>` — scaffolding completo Clean Architecture (entity, DTO, repository, service, controller, DI)
- `/migration <NomeMigration>` — guida per aggiungere ed applicare migrations EF Core
- `/run` — avvio del progetto con istruzioni per la connection string
