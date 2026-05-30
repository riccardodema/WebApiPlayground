# Convenzioni di codice per layer

## Entity (`Domain/Entities/`)
- POCO con `[Key]` sull'`Id`
- Stringhe → `= string.Empty`; FK nullable → `Author? Author`; collezioni → `= new List<T>()`
- Zero dipendenze da EF Core o altri layer

## DTO (`Application/DTOs/`)
- `record` immutabile con costruttore posizionale, suffisso `Dto`
- Separare request/response: `CreateBookDto` vs `BookDto`
- Solo campi primitivi, nessuna navigation property

## Interfaccia repository (`Application/Interfaces/`)
- Nome: `I{Name}Repository`; metodi async che restituiscono entità di dominio (non DTO)
- Firma standard: `GetAllAsync()`, `GetByIdAsync(int id)`, `CreateAsync(T)`, `DeleteAsync(int id)`

## Service (`Application/Services/`)
- Implementa `I{Name}Service`; dipende solo dall'interfaccia repository
- `ArgumentNullException.ThrowIfNull()` nel costruttore
- Responsabile del mapping entità → DTO con metodo `private static {Name}Dto MapToDto({Name} e)`

## Repository (`Infrastructure/Repositories/`)
- Implementa l'interfaccia da Application; usa LINQ + `Include` + `async/await`
- Restituisce entità di dominio (il mapping è responsabilità del service)

## Controller (`API/Controllers/`)
- `[ApiController]` + `[Route("api/[controller]")]`, eredita `ControllerBase`
- Dipende dall'interfaccia service (mai dal repository direttamente)
- Ritorni: `Ok()`, `NotFound()`, `CreatedAtAction()`, `NoContent()`
- Parametri di route tipizzati: `{id:int}`

## Test (`Tests/Services/`)
- Unit test puri: mock del repository con Moq, nessun DB, nessun EF Core
- Pattern: `Mock<IRepo> _mock = new(); Service _sut = new(_mock.Object);`
