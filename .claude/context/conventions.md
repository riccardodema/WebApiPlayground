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

## Paginazione (endpoint GET lista)

Strategia **offset/page-based** + risposta **envelope** (`PagedResult<T>`). Default e regole:

- **Envelope** `PagedResult<T>` (`Application/DTOs/PagedResult.cs`): `record` con `Items`,
  `PageNumber`, `PageSize`, `TotalCount` + proprietà calcolate `TotalPages`, `HasPrevious`, `HasNext`.
  È il body restituito al frontend (auto-descrittivo, facile da bindare).
- **Parametri query** `BooksQueryParameters` (`Application/DTOs/`): è un *binding/validation model*,
  quindi `class` (non `record`) con `[Range]`. Default `PageNumber=1`, `PageSize=20`, tetto
  `MaxPageSize=100`; `SortBy="id"`, `SortDir="asc"`. Bind nel controller con `[FromQuery]`.
- **Validazione**: `[Range]` + `[ApiController]` → 400 ProblemDetails automatico per page/size fuori
  range. `SortBy`/`SortDir` non in whitelist → fallback ai default nel service (log `Warning`), non 400.
- **Whitelist sort** nel **service** (`HashSet` case-insensitive, es. `{ id, title, author }`):
  mai passare stringhe arbitrarie all'`OrderBy` (anti-injection di colonna).
- **Repository** `GetPagedAsync(pageNumber, pageSize, sortBy, descending)` → `(IReadOnlyList<T>, int TotalCount)`:
  `IQueryable` con `switch` sull'ordinamento, **`ORDER BY` deterministico con tiebreaker `.ThenBy(Id)`**
  (obbligatorio: senza, l'OFFSET non è ripetibile su colonne non univoche), poi `CountAsync()` +
  `Skip((page-1)*size).Take(size)`. Vedi `[L07]` in `.claude/lessons.md`.
- **Controller**: `GetX([FromQuery] XQueryParameters query)` → `Ok(PagedResult<XDto>)`.
