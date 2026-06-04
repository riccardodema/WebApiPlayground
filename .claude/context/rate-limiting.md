# Rate limiting — rate limiter nativo .NET (sliding window) + 429

## A cosa serve

Un'API esposta va protetta dall'uso eccessivo: un client che martella (scraping, retry storm, bug,
abuso) può degradare il servizio per tutti, saturare il DB e gonfiare i costi. Il rate limiting pone
un tetto alla **frequenza** di richieste per client, e oltre il tetto risponde **429 Too Many
Requests** invece di eseguire il lavoro.

Si usa il **rate limiter nativo di .NET** (`AddRateLimiter`/`UseRateLimiter`,
`System.Threading.RateLimiting`, nel framework condiviso ASP.NET Core — nessun pacchetto extra), con
due policy **sliding window** distinte e partizionate per client.

```
GET  /api/books   (policy "read",  100/60s)  → 200 …  oltre il limite → 429 + Retry-After
POST /api/books   (policy "write",  20/60s)  → 201 …  oltre il limite → 429 + Retry-After
```

## 1. Algoritmo — sliding window

Finestra scorrevole suddivisa in segmenti: "N richieste negli ultimi 60s". A differenza del **fixed
window** non soffre del doppio-burst ai bordi (un client non può sparare 2× il limite a cavallo di
due finestre solari); rispetto al **token bucket** è più intuitivo da spiegare e tarare. Più segmenti
= scorrimento più fluido al costo di un po' di memoria. **`QueueLimit = 0`**: niente accodamento, il
rifiuto è **immediato** (backpressure deterministico, nessuna latenza nascosta dietro una coda).

## 2. Partizione — per client

[`Api/RateLimiting/ClientPartition.cs`](../../src/WebApiPlayground.Api/RateLimiting/ClientPartition.cs)
calcola la chiave del bucket: utente autenticato → `user:{id}` (stesso claim della storage key
dell'idempotency: `oid` Entra, poi `NameIdentifier`); client anonimo → `ip:{indirizzo}`. Il limite è
**per client**, non globale: un client aggressivo non consuma la quota degli altri. Il limiter gira
**dopo l'autenticazione**, quindi qui il principal è già popolato.

## 3. Policy read vs write — e perché quei numeri

[`Api/RateLimiting/RateLimitingOptions.cs`](../../src/WebApiPlayground.Api/RateLimiting/RateLimitingOptions.cs),
applicate con `[EnableRateLimiting("read"|"write")]` sui metodi di
[`BooksController`](../../src/WebApiPlayground.Api/Controllers/BooksController.cs). Le due policy sono
**bucket indipendenti**: esaurire le scritture non intacca le letture.

| Policy | Default | Razionale |
|---|---|---|
| **read** (GET) | 100/60s | ≈ 1.6 req/s sostenute: ben sopra qualunque navigazione UI umana (liste + dettaglio, Scalar), ma taglia lo scraping scriptato. |
| **write** (POST/PUT/DELETE) | 20/60s | ≈ 1 scrittura ogni 3s: sopra l'uso interattivo legittimo, ma blocca i bulk-insert abusivi. |

Le scritture mutano stato, colpiscono il DB e invalidano la cache → superficie più sensibile, limite
più stretto. Si sposano con l'idempotency: un retry-storm con la **stessa** `Idempotency-Key` è
assorbito dall'idempotency (replay), uno con payload **diversi** è tagliato qui dal rate limiter. I
valori sono difendibili, non arbitrari, e tunabili da config.

```jsonc
// appsettings.json
"RateLimiting": {
  "Read":  { "PermitLimit": 100, "WindowSeconds": 60, "SegmentsPerWindow": 6, "QueueLimit": 0 },
  "Write": { "PermitLimit": 20,  "WindowSeconds": 60, "SegmentsPerWindow": 6, "QueueLimit": 0 }
}
```

## 4. Registrazione e ordine nella pipeline

[`Api/Extensions/RateLimitingExtensions.cs`](../../src/WebApiPlayground.Api/Extensions/RateLimitingExtensions.cs)
(`AddApiRateLimiting`) registra le due policy. Le opzioni si leggono **a tempo di richiesta**
(`IOptions<RateLimitingOptions>` dentro il partitioner), non alla registrazione: così riflettono
l'effettiva configurazione bindata (binding lazy, post-build), niente cattura eager.

In `Program.cs` `app.UseRateLimiter()` sta **dopo `UseAuthorization()`** (così la partizione vede il
claim utente) e **prima** di `UseMiddleware<IdempotencyMiddleware>()`: rifiuta presto le richieste in
eccesso, prima del buffering del body fatto dall'idempotency. Vedi `.claude/lessons.md` [L15].

## 5. Risposta 429 (ProblemDetails RFC 7807)

`OnRejected` produce un **429** sullo stesso canale ProblemDetails degli altri errori: arricchito con
`correlationId`/`traceId` (via [`ProblemDetailsEnricher`](../../src/WebApiPlayground.Api/ErrorHandling/ProblemDetailsEnricher.cs))
e servito come `application/problem+json`. Tipo: RFC 6585 §4 (il 429 è definito lì, **non** in RFC
9110). Header **`Retry-After`** sempre presente: i secondi dal metadata nativo del lease se disponibile,
altrimenti la finestra della policy che ha respinto.

## Limiti / scelte

- **In-memory, per-istanza**: il limiter nativo conta in memoria del processo. Dietro N istanze il
  limite effettivo è ~N× il configurato. Per il taglio "nativo .NET" del POC va bene; lo **scale-out**
  è un rate limiter distribuito su Redis (come cache/idempotency già fanno per il loro store) — niente
  built-in in .NET, servirebbe una libreria di terze parti. Documentato, non implementato qui.
- **Opzioni per partizione**: la sliding window di una partizione "congela" i suoi parametri alla prima
  richiesta di quel client; un reload di config influenza le partizioni nuove.

## Contratto (OpenAPI)

L'operation transformer
[`Api/OpenApi/RateLimitingOperationTransformer.cs`](../../src/WebApiPlayground.Api/OpenApi/RateLimitingOperationTransformer.cs)
documenta su **ogni operazione** la risposta `429` con l'header `Retry-After` — visibile in Scalar e in
`/openapi/v1.json`. Si documenta solo ciò che è accurato (`Retry-After`); niente header `RateLimit-*`
draft con valori potenzialmente inaccurati. Stesso meccanismo dei transformer idempotency/caching. Un
test ne verifica la presenza nel contratto.

## Test

- **Unit** ([`tests/WebApiPlayground.Tests/RateLimiting`](../../tests/WebApiPlayground.Tests/RateLimiting)):
  `ClientPartition` (claim oid→NameIdentifier→IP) e binding/default di `RateLimitingOptions`.
- **Integration** ([`tests/WebApiPlayground.IntegrationTests/RateLimiting`](../../tests/WebApiPlayground.IntegrationTests/RateLimiting)):
  oltre il limite write → **429** ProblemDetails con `correlationId`/`traceId` + `Retry-After`; read più
  generoso; read e write sono bucket separati; client distinti non condividono il bucket. Limiti
  minuscoli via `WithWebHostBuilder` + identità distinte (`X-Test-User`) perché il limiter è un singleton
  in-memory condiviso. La factory base alza i limiti ad altissimi per non throttlare il resto della suite.
- **Contratto** ([`OpenApiContractTests`](../../tests/WebApiPlayground.IntegrationTests/OpenApi/OpenApiContractTests.cs)):
  ogni operazione documenta la `429` con `Retry-After`.
