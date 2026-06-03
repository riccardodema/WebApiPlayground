# Convenzioni di codice per layer

## Principi trasversali

- **No magic strings.** Un valore del contratto pubblico ripetuto (token di sort/filtro, chiavi,
  stati, nomi di policy) non va scritto inline in più punti: centralizzalo in un **enum type-safe**
  e/o `const`, con un **unico parser** che traduce le stringhe grezze in input. Il resto del codice
  ragiona sui tipi, non sulle stringhe. Esempio: ordinamento libri in `Application/Querying/`
  (`BookSortField`/`SortDirection` + `BookSortParser`) — vedi sez. *Paginazione*.
- **DRY sull'ordinamento/query.** Logica ripetuta (es. direzione asc/desc + tiebreaker) va estratta
  in un helper privato, non copiata per ogni ramo dello `switch`.

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
  range. `SortBy`/`SortDir` non riconosciuti → fallback ai default nel service (log `Warning`), non 400.
- **Vocabolario sort type-safe** (`Application/Querying/`): enum `BookSortField` (whitelist) e
  `SortDirection`; le **magic string** del contratto HTTP (`"id"|"title"|"author"`, `"asc"|"desc"`)
  vivono **solo** in `BookSortParser` (`TryParseField` + `ParseDirection`). Service e repository NON
  contengono literal di ordinamento e ragionano solo su enum (anti-injection di colonna + niente
  duplicazione). Estendere l'ordinamento = aggiungere un valore all'enum + un case nel parser e nel repo.
- **Repository** `GetPagedAsync(pageNumber, pageSize, BookSortField, SortDirection)` →
  `(IReadOnlyList<T>, int TotalCount)`: `switch` sull'enum; l'helper privato `OrderByWithIdTiebreaker`
  centralizza direzione + **tiebreaker `.ThenBy(Id)`** (obbligatorio: senza, l'OFFSET non è ripetibile
  su colonne non univoche — vedi `[L07]`); poi `CountAsync()` + `Skip((page-1)*size).Take(size)`.
- **Controller**: `GetX([FromQuery] XQueryParameters query)` → `Ok(PagedResult<XDto>)`.
