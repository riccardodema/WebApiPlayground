# API versioning — Asp.Versioning per segmento URL + documento OpenAPI per versione

## A cosa serve

Un'API con client esterni non può cambiare il contratto a piacimento: rinominare un campo, annidare
un oggetto, togliere una proprietà **rompe** i client esistenti. Il versioning permette di far
**coesistere** più contratti (`v1`, `v2`, …) sulla stessa risorsa: i client vecchi restano su `v1`,
i nuovi adottano `v2`, e l'evoluzione non è un big-bang.

Si usa **Asp.Versioning** (il successore mantenuto di `Microsoft.AspNetCore.Mvc.Versioning`) con
schema **per segmento URL**: la versione è esplicita e visibile nel path.

```
GET /api/v1/books/1  → { "id":1, "title":"Clean Code", "authorFullName":"Robert C. Martin" }
GET /api/v2/books/1  → { "id":1, "title":"Clean Code", "author":{ "id":7, "fullName":"Robert C. Martin" } }
```

## 1. Schema — segmento URL

[`Api/Versioning/ApiRoutes.cs`](../../src/WebApiPlayground.Api/Versioning/ApiRoutes.cs):
`/api/v{version:apiVersion}/books`. Lo schema URL è il più **visibile** (un recruiter lo vede subito
in Scalar, un client lo prova dal browser) e dà un **documento OpenAPI per versione**. Alternative
non scelte: header (`api-version`) o query string — URL pulito ma versioning "invisibile";
`ApiVersionReader.Combine(...)` per leggerla da più sorgenti — più ampio ma overkill per un POC.

Le versioni e i numeri sono centralizzati (no magic number) in
[`Api/Versioning/ApiVersions.cs`](../../src/WebApiPlayground.Api/Versioning/ApiVersions.cs):
`const double V1/V2` (costanti compile-time valide negli attributi `[ApiVersion]`/`[MapToApiVersion]`),
`All` come unica fonte delle versioni esposte, e `GroupName(v)` (`"v1"`, `"v2"`) allineato a
`GroupNameFormat = "'v'VVV"`.

## 2. Esempio v2 — evoluzione della lettura, scritture condivise

La v2 cambia la **forma di lettura**: l'autore passa da nome piatto (`BookDto.AuthorFullName`) a
oggetto annidato ([`BookDetailsDto`](../../src/WebApiPlayground.Application/DTOs/BookDetailsDto.cs) con
[`AuthorDto`](../../src/WebApiPlayground.Application/DTOs/AuthorDto.cs)) — un **breaking change** di
risposta, il caso da manuale.

- **Letture v1** ([`BooksController`](../../src/WebApiPlayground.Api/Controllers/BooksController.cs),
  `[MapToApiVersion(V1)]`) → `BookDto`.
- **Letture v2** ([`BooksV2Controller`](../../src/WebApiPlayground.Api/Controllers/BooksV2Controller.cs),
  `[ApiVersion(V2)]`, stessa rotta `/books`) → `BookDetailsDto`.
- **Scritture condivise**: `BooksController` dichiara sia `[ApiVersion(V1)]` sia `[ApiVersion(V2)]`,
  e POST/PUT/DELETE (senza `[MapToApiVersion]`) servono **entrambe** le versioni — il contratto di
  richiesta non cambia (DRY: nessuna duplicazione delle scritture).

DRY anche nel service: `GetBooksDetailedAsync`/`GetBookDetailsByIdAsync`
([`BooksService`](../../src/WebApiPlayground.Application/Services/BooksService.cs)) riusano lo **stesso
fetch** del repository di v1 (helper privato `GetPagedBooksAsync`); cambia solo la proiezione finale
(`MapToDetailsDto`). Il decoratore di caching usa chiavi v2 distinte ma lo **stesso tag** `books`, così
le scritture invalidano anche le letture v2.

## 3. Registrazione + OpenAPI per versione

[`Api/Extensions/ApiVersioningExtensions.cs`](../../src/WebApiPlayground.Api/Extensions/ApiVersioningExtensions.cs):
`AddApiVersioning(DefaultApiVersion=V1, ReportApiVersions=true, UrlSegmentApiVersionReader).AddMvc()
.AddApiExplorer(GroupNameFormat="'v'VVV", SubstituteApiVersionInUrl=true)`.

L'`ApiExplorer` assegna a ogni operazione un **GroupName** (`"v1"`/`"v2"`). Il documento OpenAPI
**nativo** dello stesso nome (`AddOpenApi("v1")`, `AddOpenApi("v2")`) include solo le operazioni di
quella versione (un'operazione con GroupName valorizzato entra solo nel documento omonimo). I
transformer dell'API sono **condivisi** su ogni documento via
[`OpenApiTransformerRegistration.AddPlaygroundTransformers`](../../src/WebApiPlayground.Api/OpenApi/OpenApiTransformerRegistration.cs)
(auth, validazione, idempotency, caching, rate limiting, versioning), senza duplicazione.

> **Nota integrazione**: il pacchetto `Asp.Versioning.OpenApi` (con `WithDocumentPerVersion()`/
> `AddScalarTransformers()`) **non è pubblicato su NuGet**. Si usa il percorso con i pacchetti reali
> (`Asp.Versioning.Mvc` + `Asp.Versioning.Mvc.ApiExplorer`) sfruttando il filtro per GroupName del
> native OpenAPI. Vedi `[L16]`.

In `Program.cs`: `app.MapOpenApi()` serve `/openapi/v1.json` e `/openapi/v2.json`;
`MapScalarApiReference` cicla `ApiVersions.All` e registra un documento Scalar per versione (selettore
di versione in UI).

## 4. Contratto (OpenAPI) — info complete per il client

- **Un documento per versione** in Scalar: il client vede esattamente il contratto di `v1` e di `v2`.
- `ReportApiVersions=true` emette su **ogni risposta** gli header `api-supported-versions` (e
  `api-deprecated-versions`), documentati nel contratto da
  [`ApiVersioningOperationTransformer`](../../src/WebApiPlayground.Api/OpenApi/ApiVersioningOperationTransformer.cs):
  il client scopre le versioni disponibili da qualunque risposta.

## 5. Limiti consapevoli (POC)

- **Versione sconosciuta → 404, non 400.** Con lo schema per segmento URL la versione è parte della
  rotta: `/api/v3/books` non corrisponde ad alcun endpoint → 404. Con i reader a header/query si
  otterrebbe 400 + `api-supported-versions`. È una conseguenza dello schema scelto, non un bug.
- **Deprecation + Sunset (RFC 8594)**: `ReportApiVersions` emette gli header, ma nessuna versione è
  ritirata, quindi la policy `Sunset`/`Deprecated` è **documentata ma non attivata** (forzare un caso
  finto contraddirebbe v1 come superficie di scrittura). Si mostra il "come", non un caso artificiale.
- **Scritture condivise**: la `Location` di una create usa la `GetBookById` di v1; la risorsa creata è
  comunque leggibile in v2 con `GET /api/v2/books/{id}`.

## 6. Come si integra col resto

Versioning trasversale a ciò che esiste già: ogni operazione versionata mantiene **auth** (Entra,
policy read/write), **rate limiting** (429), **idempotency** (POST), **HTTP caching** (ETag) e gli
errori **ProblemDetails** — perché i transformer OpenAPI sono condivisi e i controller v2 riusano gli
stessi attributi `[Authorize]`/`[EnableRateLimiting]`.

## Test

- **Unit** ([`Tests/Services/BooksServiceTests`](../../tests/WebApiPlayground.Tests/Services/BooksServiceTests.cs),
  [`Tests/Caching/CachingBooksServiceTests`](../../tests/WebApiPlayground.Tests/Caching/CachingBooksServiceTests.cs)):
  la proiezione v2 annida l'autore e riusa lo stesso fetch; la cache v2 ha chiavi proprie ma è
  invalidata dalle scritture (tag condiviso).
- **Integration** ([`IntegrationTests/Versioning`](../../tests/WebApiPlayground.IntegrationTests/Versioning/ApiVersioningTests.cs)):
  stesso libro → autore piatto in v1, annidato in v2; header `api-supported-versions`; versione
  sconosciuta → 404; create condivisa su v1 e v2.
- **Contratto** ([`OpenApiContractTests`](../../tests/WebApiPlayground.IntegrationTests/OpenApi/OpenApiContractTests.cs)):
  esistono `/openapi/v1.json` e `/openapi/v2.json`; lo schema v2 ha `author` annidato; le risposte
  documentano `api-supported-versions`.
