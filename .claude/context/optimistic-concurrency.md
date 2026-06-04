# Optimistic concurrency — rowversion + ETag/If-Match → 412/428

## A cosa serve

Due client leggono lo stesso libro e fanno `PUT`/`DELETE` quasi simultaneamente. Senza protezione la
seconda scrittura sovrascrive (o cancella) ciò che ha fatto la prima: **lost update** silenzioso. Anche
con una sola istanza, più utenti concorrenti rendono lo scenario reale.

L'**optimistic concurrency** non blocca nessuno (niente lock pessimistici, inadatti a HTTP stateless):
ogni risorsa porta una **versione**; la scrittura procede solo se la versione che il client si aspetta
è ancora quella corrente. Altrimenti → conflitto, il client rilegge e riprova.

```
GET /api/v1/books/1 → 200, ETag: "AAAAAAAAB9E="        (token di versione = rowversion)
PUT /api/v1/books/1   If-Match: "AAAAAAAAB9E="          → 200 (+ nuovo ETag) se la versione combacia
                                                         → 412 se nel frattempo è cambiata
PUT /api/v1/books/1   (senza If-Match)                   → 428 Precondition Required
```

## Le tre parti (best practice .NET 2026)

1. **DB — `rowversion`.** Colonna auto-mantenuta da SQL Server: si auto-incrementa a **ogni** UPDATE
   della riga, senza codice applicativo. In [`Books.sql`](../../database/Schema/Tables/Books.sql):
   `[RowVersion] ROWVERSION NOT NULL` (parità DACPAC col modello EF).
2. **EF Core — concurrency token.** In
   [`PlaygroundDbContext`](../../src/WebApiPlayground.Infrastructure/Persistence/PlaygroundDbContext.cs):
   `entity.Property(b => b.RowVersion).IsRowVersion()`. EF la marca store-generated + concurrency token:
   l'`UPDATE`/`DELETE` diventa condizionale (`WHERE Id=@id AND RowVersion=@original`); se 0 righe →
   `DbUpdateConcurrencyException`. Il [`BookRepository`](../../src/WebApiPlayground.Infrastructure/Repositories/BookRepository.cs)
   forza l'`OriginalValue` del token alla versione **attesa dal client** (altrimenti EF userebbe quella
   appena letta e non rileverebbe mai un conflitto) e traduce l'eccezione EF in
   [`ConcurrencyConflictException`](../../src/WebApiPlayground.Application/Concurrency/ConcurrencyConflictException.cs)
   (Application) — EF resta confinato in Infrastructure.
3. **HTTP — ETag + If-Match (RFC 9110/7232).** Il token viaggia come **ETag**; il client lo rimanda in
   **If-Match** sulle scritture. **Riusa l'infrastruttura ETag esistente**: l'ETag del singolo libro non è
   più l'hash della rappresentazione ma il **token di versione** (reversibile → dai byte della rowversion),
   così lo **stesso header** serve sia il caching condizionale (`304`) sia la concorrenza (`412/428`).

## Mappa dei componenti

| Cosa | Dove |
|---|---|
| Token di versione sul DTO (base64 rowversion), `[JsonIgnore]` → solo header | [`BookDto`](../../src/WebApiPlayground.Application/DTOs/BookDto.cs), [`BookDetailsDto`](../../src/WebApiPlayground.Application/DTOs/BookDetailsDto.cs) via [`IVersionedResource`](../../src/WebApiPlayground.Application/Concurrency/IVersionedResource.cs) |
| ETag = token di versione su GET/PUT/POST; hash sulle liste | [`ETagResultFilter`](../../src/WebApiPlayground.Api/Http/ETagResultFilter.cs) + [`ETag`](../../src/WebApiPlayground.Api/Http/ETag.cs) (`FromVersion`/`TryParseToken`) |
| If-Match obbligatorio: assente → 428, malformato → 400 | [`BooksController`](../../src/WebApiPlayground.Api/Controllers/BooksController.cs) (`RequireIfMatch`) + [`PreconditionException`](../../src/WebApiPlayground.Api/ErrorHandling/PreconditionException.cs) |
| Conflitto → 412, precondizione → 428/400 (ProblemDetails) | [`PreconditionExceptionHandler`](../../src/WebApiPlayground.Api/ErrorHandling/PreconditionExceptionHandler.cs) (prima del global) |
| Contratto OpenAPI (If-Match + 412 + 428 su PUT/DELETE) | [`ConcurrencyOperationTransformer`](../../src/WebApiPlayground.Api/OpenApi/ConcurrencyOperationTransformer.cs) |

## Scelte (decise nel POC)

- **If-Match obbligatorio** sulle scritture → mancante = **428**, stale = **412**. Garanzia massima
  (nessun lost update). È un breaking change per i client PUT/DELETE preesistenti: ora devono fare prima
  una GET per l'ETag (i test sono stati adeguati).
- **Scope: PUT + DELETE** (entrambi condizionali; delete-if-unchanged).
- **Token solo nell'header ETag** (canonico): il body JSON di v1/v2 resta invariato (`Version` è
  `[JsonIgnore]`).
- **Meccanismo: `rowversion`** (idiomatico su SQL Server). Scartate: colonna `int` versionata a mano,
  "tutte le colonne come token".
- **412 via eccezione** (non result object): il conflitto nasce in profondità nel `SaveChanges`, quindi
  un'eccezione è la via naturale; mappata in un punto unico come ogni altro errore RFC 7807.

## Limite consapevole (cache L2)

Il token è `[JsonIgnore]` (header-only). FusionCache **L1 (memoria, default)** tiene l'oggetto vivo →
`Version` sopravvive, tutto funziona. Con **L2 Redis** (config-gated, oggi spento) il DTO viene
**serializzato** e `[JsonIgnore]` strippa il token → su un hit servito da L2 l'ETag ricadrebbe sull'hash e
la concorrenza salterebbe. Accettabile finché L2 è inattivo. Fix futuro: serializzare il token nel payload
di cache quando L2 è attivo. Vedi `.claude/lessons.md` **[L17]**.

## Come si integra col resto

Trasversale a ciò che esiste: le scritture mantengono **auth** (policy write), **rate limiting** (429),
**validazione** (400) ed errori **ProblemDetails**; l'ETag è lo stesso usato dall'**HTTP caching**
(`.claude/context/caching.md`). Le scritture sono **condivise** v1/v2 (un solo `BooksController`), quindi
la concorrenza vale su entrambe le versioni; le letture v2 (`BooksV2Controller` → `BookDetailsDto`) espongono
anch'esse l'ETag.

## Test

- **Unit** ([`Tests/Http/ETagTests`](../../tests/WebApiPlayground.Tests/Http/ETagTests.cs)): `FromVersion`
  ⇄ `TryParseToken` (round-trip dei byte, prefisso debole, input invalidi).
  [`Tests/Services/BooksServiceTests`](../../tests/WebApiPlayground.Tests/Services/BooksServiceTests.cs): il
  token If-Match fluisce al repo come `Book.RowVersion`; la rowversion è proiettata in `Version` base64.
  [`Tests/Caching/CachingBooksServiceTests`](../../tests/WebApiPlayground.Tests/Caching/CachingBooksServiceTests.cs):
  un conflitto **non** invalida la cache.
- **Integration** ([`IntegrationTests/Concurrency/OptimisticConcurrencyTests`](../../tests/WebApiPlayground.IntegrationTests/Concurrency/OptimisticConcurrencyTests.cs)):
  GET espone ETag strong; PUT/DELETE con ETag corrente → 200/204 + nuovo ETag; stale → 412; assente → 428;
  malformato → 400; scenario **"due client"** (lost update prevenuto); 412 porta il `correlationId`.
- **Contratto** ([`OpenApiContractTests`](../../tests/WebApiPlayground.IntegrationTests/OpenApi/OpenApiContractTests.cs)):
  PUT/DELETE documentano `If-Match` + 412 + 428.
