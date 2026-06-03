# Validazione input — FluentValidation → 400 ProblemDetails

L'input dei body delle richieste è validato con **FluentValidation**; le violazioni producono una
risposta **400 `application/problem+json`** (RFC 7807) coerente con il resto della gestione errori
(stesso `correlationId`/`traceId`). I messaggi sono **"parlanti"**: dicono all'utente cosa è sbagliato
e come correggerlo. Le regole sono riportate anche nello **schema OpenAPI** generato, così il
contratto è auto-descrittivo.

## Componenti

| Cosa | Dove |
|---|---|
| Validator (regole + messaggi) | `Application/Validation/` (`CreateBookDtoValidator`, `UpdateBookDtoValidator`) |
| Regole condivise + costanti (es. `TitleMaxLength`) | `Application/Validation/BookValidationRules.cs` |
| Registrazione validator (Singleton) | `Application/DependencyInjection.cs` (`AddValidatorsFromAssemblyContaining`) |
| Esecuzione FluentValidation nella pipeline | `Api/Validation/ValidationFilter.cs` (action filter globale) |
| Forma della risposta 400 (unica per tutti i canali) | `Api/Validation/ValidationProblemDetailsFactory.cs` |
| Arricchimento correlationId/traceId (punto unico) | `Api/ErrorHandling/ProblemDetailsEnricher.cs` |
| Proiezione regole → schema OpenAPI | `Api/OpenApi/FluentValidationSchemaTransformer.cs` |
| Aggancio pipeline | `Program.cs`: `AddControllers(... ValidationFilter ...)` + `ConfigureApiBehaviorOptions` + `AddSchemaTransformer` |

## Regole attive

| DTO | Campo | Regola | Messaggio |
|---|---|---|---|
| `CreateBookDto` / `UpdateBookDto` | `Title` | obbligatorio, non vuoto/whitespace | `Title is required and cannot be empty or whitespace.` |
| `CreateBookDto` / `UpdateBookDto` | `Title` | lunghezza ≤ 100 (= `Books.Title NVARCHAR(100)`) | `Title must not exceed 100 characters.` |
| `CreateBookDto` / `UpdateBookDto` | `AuthorId` | intero > 0 | `AuthorId must be a positive integer (greater than 0).` |

Il limite di 100 vive **una sola volta** in `BookValidationRules.TitleMaxLength` ed è allineato allo
schema DB. Create e Update condividono le regole tramite gli extension method `ValidBookTitle()` /
`ValidAuthorId()`, così non possono divergere.

## Come funziona la pipeline

1. **Model binding + DataAnnotations** (`[ApiController]`): errori di binding (JSON malformato) e di
   `[Range]` sui query parameter (es. `BooksQueryParameters`) → 400 automatico tramite
   `InvalidModelStateResponseFactory`.
2. **`ValidationFilter`** (dopo il binding): per ogni argomento dell'action risolve l'eventuale
   `IValidator<T>` dal container ed esegue FluentValidation. Le violazioni confluiscono nel
   `ModelState` e la risposta è prodotta dallo **stesso** `InvalidModelStateResponseFactory`.
3. **`ValidationProblemDetailsFactory`**: costruisce un `ValidationProblemDetails` (mappa `errors`
   campo→messaggi), lo arricchisce con `ProblemDetailsEnricher` e lo serializza come
   `application/problem+json`.

Risultato: **un'unica forma d'errore 400** sia per DataAnnotations sia per FluentValidation, sempre
correlabile ai log. Esempio di body:

```json
{
  "type": "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "See the 'errors' property for the fields that failed validation and how to fix them.",
  "instance": "/api/books",
  "errors": {
    "Title": ["Title is required and cannot be empty or whitespace."],
    "AuthorId": ["AuthorId must be a positive integer (greater than 0)."]
  },
  "correlationId": "…",
  "traceId": "…"
}
```

## Regole nello schema OpenAPI

`FluentValidationSchemaTransformer` legge il descriptor di ogni `IValidator<T>` e proietta le regole
nello schema del DTO **senza duplicarle** (i validator restano l'unica fonte di verità):

- `NotEmpty` → `required` + (string) `minLength: 1`
- `MaximumLength(n)` → `maxLength: n`
- `GreaterThan(0)` → `minimum: 1` · `GreaterThanOrEqual(n)` → `minimum: n`
- + una `description` leggibile per campo (es. *"Validation: required; max length 100."*)

Così Scalar/`openapi/v1.json` mostra input, output, uso degli endpoint **e** i vincoli di validazione.

## Scelte progettuali (best practice)

- **FluentValidation manuale, non `FluentValidation.AspNetCore`**: il pacchetto di auto-binding è
  deprecato. Il `ValidationFilter` generico copre ogni endpoint presente e futuro senza modifiche.
- **Validator Singleton**: sono stateless → risolvibili sia dal filter sia dallo schema transformer
  (che gira sul root provider).
- **Un solo punto di arricchimento** (`ProblemDetailsEnricher`), riusato da `CustomizeProblemDetails`
  e dalla factory di validazione.

## Test

- Unit (puri): `tests/WebApiPlayground.Tests/Validation/` — regole e messaggi per ogni campo.
- Integration: `tests/WebApiPlayground.IntegrationTests/Books/BooksControllerTests.cs` — 400
  end-to-end (content-type `application/problem+json`, mappa `errors`, `correlationId` = header,
  input invalido non persistito) + CRUD `PUT`.

> ⚠️ Pitfall: scrivere un `ValidationProblemDetails` via `IProblemDetailsService` lo serializza sul
> tipo statico `ProblemDetails` e **scarta `errors`**. Vedi `.claude/lessons.md` `[L10]`.
