# Resilience — chiamate esterne robuste (Polly v8 / Microsoft.Extensions.Http.Resilience)

> Scopo didattico: mostrare *come* un backend .NET 2026 si difende da una dipendenza esterna lenta o
> instabile. Il dominio (libri) resta banale; il valore è l'ingegneria attorno alla chiamata HTTP in uscita.

## Cosa fa questa feature

Nuovo endpoint **`GET /api/v1/books/{bookId}/popularity`**: carica il libro dal nostro DB e lo arricchisce
con i **segnali di popolarità** letti da una **dipendenza esterna**, [Open Library](https://openlibrary.org)
(`search.json`): voto medio, numero di voti e i contatori del reading-log (da-leggere / in-lettura / già-letto).

La chiamata in uscita è avvolta da una **pipeline di resilienza** (retry + circuit breaker + timeout). Se la
dipendenza è indisponibile, l'endpoint risponde **503** (ProblemDetails RFC 7807) con `Retry-After`, **non** un
500 opaco: un guasto *a valle* non diventa un errore *nostro*.

### Perché Open Library e non "le vendite"

I dati di **vendita reali** dei libri (Nielsen BookScan ecc.) **non sono gratuiti né pubblici**. Open Library
(Internet Archive) è il miglior proxy *gratuito* di domanda/popolarità: **key-less** (nessun segreto), JSON
stabile, limiti generosi. Modellare onestamente il dominio — "popolarità", non "vendite" — è esso stesso una
lezione di ingegneria. Alternative valutate: Google Books (rating + `saleInfo` incostante, quota per-IP) e NYT
Best Sellers (rank di vendita reale **ma** richiede API key → gestione segreti).

## Le 4 strategie e l'ordine (conta!)

La pipeline è **esplicita** (`AddResilienceHandler`, non lo standard handler) così ogni strategia è visibile,
nominata e configurabile. Ordine **outer → inner** in
[`BookPopularityRegistration`](../../src/WebApiPlayground.Infrastructure/Popularity/BookPopularityRegistration.cs):

```
Timeout TOTALE → Retry → Circuit breaker → Timeout PER-TENTATIVO → HttpClient → rete
   (1)            (2)         (3)                 (4)
```

1. **Timeout totale** (outermost) — cappa l'intera sequenza retry: il chiamante non aspetta mai più di N
   secondi, anche con tutti i retry sommati. Tradeoff: troppo corto → fallimenti falsi sotto carico; troppo
   lungo → la latenza dei retry si accumula.
2. **Retry** — backoff **esponenziale + jitter**, solo errori **transitori** (5xx, 408, 429,
   `HttpRequestException`, `TimeoutRejectedException`). I **4xx non si ritentano** (riprovare un 400/404 è
   inutile e dannoso). Il *jitter* evita il *retry storm* (tutti i client che ritentano in sincrono). Tradeoff:
   retry solo su operazioni **idempotenti** — qui è una GET, quindi sicuro; su una POST non idempotente i retry
   duplicherebbero gli effetti (mitigato altrove dall'**idempotency**, vedi [L14]).
3. **Circuit breaker** — se l'upstream è giù, dopo una frazione di fallimenti nella finestra il circuito si
   **apre** e fa **fail-fast** (`BrokenCircuitException`) **senza toccare la rete**: protegge **noi** (niente
   thread/socket sprecati ad attendere un servizio morto) e **loro** (gli diamo respiro per riprendersi). Dopo
   `BreakDuration` passa a *half-open* e prova una sonda. Tradeoff: troppo sensibile → si apre su un blip;
   troppo lasco → non protegge. `MinimumThroughput` evita aperture su pochi campioni.
4. **Timeout per-tentativo** (innermost) — taglia il **singolo** HTTP call lento, così il retry può subentrare
   invece di restare appeso. Tradeoff come (1) ma per tentativo.

> **Perché l'ordine.** Il retry deve stare **sopra** il circuit breaker (ogni tentativo conta nel breaker) e
> **sotto** il timeout totale (i retry non possono sforare il budget complessivo). Il timeout per-tentativo è
> il più interno perché deve agire sul singolo invio, non sull'intera pipeline.

## Mappatura degli errori → 503

Il client
[`OpenLibraryPopularityClient`](../../src/WebApiPlayground.Infrastructure/Popularity/OpenLibraryPopularityClient.cs)
**traduce** le eccezioni di trasporto/Polly (`BrokenCircuitException`, `TimeoutRejectedException`,
`HttpRequestException`, risposta non-2xx esaurita, corpo non interpretabile) in
[`ExternalServiceUnavailableException`](../../src/WebApiPlayground.Application/Popularity/ExternalServiceUnavailableException.cs)
(Application) — così l'errore d'infrastruttura **non risale** oltre il suo layer (stesso principio della
`ConcurrencyConflictException`, vedi [L17]).

In Api,
[`ExternalServiceUnavailableExceptionHandler`](../../src/WebApiPlayground.Api/ErrorHandling/ExternalServiceUnavailableExceptionHandler.cs)
la mappa su **503** + header **`Retry-After`**, scrivendo via `IProblemDetailsService` così riusa
`CustomizeProblemDetails`/`ProblemDetailsEnricher` e ottiene **`correlationId`/`traceId`** come ogni altro
errore (DRY con gli handler 412/428/500). Registrato **tra** `PreconditionExceptionHandler` e
`GlobalExceptionHandler` (la catena prova gli handler in ordine; il Global resta catch-all 500). Il `Detail` è
generico: nessun dettaglio dell'upstream → niente info-leak.

## Architettura (Clean Architecture, auto-validata)

Stesso pattern della cache: **astrazione in Application, implementazione + resilienza in Infrastructure**.

| Layer | Cosa vive qui |
|-------|---------------|
| **Application** | `IBookPopularityClient`, modello `BookPopularity`, `BookPopularityDto`, `ExternalServiceUnavailableException`, `BookPopularityService`. **Niente** package HTTP/Polly. |
| **Infrastructure** | `OpenLibraryPopularityClient` (HttpClient tipizzato), contratto JSON `OpenLibrarySearchResponse`, `BookPopularityOptions`, registrazione della pipeline. |
| **Api** | `BookPopularityController`, handler 503, transformer OpenAPI, config-gating. |

Una **regola NetArchTest** (`Application_should_not_depend_on_resilience_implementations`) impedisce a `Polly` e
`Microsoft.Extensions.Http` di trapelare in Application — come per `FusionCache`/`Redis` sulla cache.

## Caching della chiamata esterna (degrade-to-stale)

La risposta di Open Library è **cachata**: è il candidato ideale (latenza di rete, cortesia verso un servizio
gratuito/condiviso, dato che cambia a giorni) e **la cache diventa un pattern di resilienza**. Decoratore
[`CachingBookPopularityClient`](../../src/WebApiPlayground.Infrastructure/Popularity/CachingBookPopularityClient.cs)
su `IBookPopularityClient`: `cache → [miss] → pipeline Polly → HttpClient → Open Library`.

- **Hit** = niente rete né circuit breaker (le hit non consumano la throughput del breaker → statistiche più
  oneste). **Miss** = chiamata resiliente single-flight (**stampede protection**: un burst sullo stesso libro
  → *una* sola chiamata in uscita).
- **Degrade-to-stale**: con il **fail-safe** abilitato, se Open Library è giù (circuito aperto / esaurito) e
  l'entry è scaduta ma entro `FailSafeMaxDuration` (24h), si serve l'ultimo valore buono invece del 503 →
  un'outage con cache calda è invisibile all'utente. Il **503 resta** per cache fredda + dipendenza giù.
- **Negative caching**: anche il "no match" viene cachato (presenza su OL stabile) → niente ri-chieste.

> **Perché `IFusionCache` (Infrastructure) e non l'astrazione `HybridCache` (come `CachingBooksService`).**
> Non è stilistico. `HybridCacheEntryOptions` espone **solo** `Expiration`/`LocalCacheExpiration`; NON il
> **factory timeout** né il **fail-safe**, che sono concetti *di FusionCache*. Ci servono entrambi:
> - **factory timeout INFINITI** — altrimenti il `FactoryHardTimeout = 2s` globale (giusto per le factory
>   veloci dei books) **aborterebbe la chiamata esterna a 2s su una miss fredda, neutralizzando la pipeline di
>   resilienza** (3s/10s). Per la popolarità il budget di timeout lo governa la pipeline, non la cache.
> - **fail-safe esteso (24h)** per il degrade-to-stale.
>
> Quelle manopole si passano solo via `FusionCacheEntryOptions` → `IFusionCache` (concreto) → che per regola
> NetArchTest vive solo in Infrastructure. "Due `HybridCache` con parametri diversi" non basta: quei parametri
> non sono parametri di `HybridCache`. L'astrazione che conta resta pulita (Application vede solo
> `IBookPopularityClient`); il decoratore wrappa il **client concreto** registrato come typed client. È
> l'asimmetria didattica: cache di un *use case veloce in-process* vs cache di una *dipendenza esterna lenta
> dietro resilienza*. Pitfall e dettagli in [L20].

## Sicurezza

- **No SSRF**: l'host è `BaseAddress`, **fisso da config**, mai input utente; titolo/autore finiscono solo come
  query string **URL-encoded** (`Uri.EscapeDataString`), mai in host/schema/path.
- **No secret**: Open Library è key-less → niente da esporre. (Con NYT, la key andrebbe in Key Vault, mai nel repo.)
- **HTTPS only** (base address `https`).
- **Resilienza = difesa di disponibilità**: timeout e circuit breaker limitano thread/socket/connection-pool
  contro una dipendenza lenta/appesa — un vettore di esaurimento risorse, non solo un fastidio di latenza.
- **No leak upstream**: errori esterni mappati a un 503 generico; il `Detail` esteso resta dev-only (global handler).
- **Bound su memoria/payload**: `MaxResponseContentBufferSize` (1 MB) + `fields=` minimale nella query.

## Configurazione (sezione `BookPopularity`)

Config-gated/out-of-the-box come Cache/OTel: i default puntano a Open Library, **nessun segreto richiesto**.

```json
{ "BookPopularity": {
    "BaseAddress": "https://openlibrary.org",
    "Resilience": {
      "AttemptTimeout": "00:00:03", "TotalTimeout": "00:00:10",
      "Retry": { "MaxRetryAttempts": 3, "BaseDelay": "00:00:00.500" },
      "CircuitBreaker": { "FailureRatio": 0.5, "SamplingDuration": "00:00:30", "MinimumThroughput": 10, "BreakDuration": "00:00:15" }
    },
    "Cache": {
      "Enabled": true,
      "Duration": "00:15:00",
      "FailSafeMaxDuration": "24:00:00",
      "CacheNotFound": true
} } }
```
`Cache.Duration` = TTL di freschezza (15 min); `FailSafeMaxDuration` = finestra di degrade-to-stale (24h);
`CacheNotFound` = negative caching; `Enabled=false` = niente cache (ogni richiesta passa per la pipeline).

Le opzioni si leggono **lazy** (post-build) via `IOptionsMonitor` dentro l'overload con `context` di
`AddResilienceHandler`, così gli override di test/`WebApplicationFactory` valgono (vedi [L15]/[L19]).

## Test

| Livello | File | Cosa verifica |
|---------|------|---------------|
| Unit (service) | `tests/.../Tests/Popularity/BookPopularityServiceTests.cs` | composizione DB+esterno: 404 se libro assente (client non chiamato), mapping segnali, metriche null se nessun match, propagazione dell'eccezione di indisponibilità. |
| Unit (pipeline) | `tests/.../Tests/Popularity/PopularityResiliencePipelineTests.cs` | la **pipeline reale** (risolve il client **concreto** per bypassare la cache) col primary handler stubbato: retry su transitorio, retry esaurito, **niente retry su 4xx**, timeout per-tentativo, timeout totale, **circuit breaker fail-fast** (il transport non viene colpito). |
| Unit (cache) | `tests/.../Tests/Popularity/CachingBookPopularityClientTests.cs` | il decoratore con `IFusionCache` reale + inner mockato: hit, **normalizzazione chiave**, negative caching on/off, bypass se disabilitato, e **degrade-to-stale** (l'inner fallisce dopo la scadenza → il fail-safe serve lo stale). |
| Integration | `tests/.../IntegrationTests/Popularity/BookPopularityEndpointTests.cs` | endpoint end-to-end: 200 con segnali, 404, 401/403, **503 ProblemDetails** + `Retry-After` + `correlationId`/`traceId`, e **la cache riduce le chiamate in uscita** (due GET → 1 sola chiamata allo stub). Lo stub esterno evita la rete reale (successo nella factory base, fallimento via `WithWebHostBuilder`). |
| Integration | `tests/.../IntegrationTests/OpenApi/OpenApiContractTests.cs` | il path `popularity` e la **503 + Retry-After** sono documentati nello spec OpenAPI. |
| Architecture | `tests/.../ArchitectureTests/LayerDependencyTests.cs` | resilienza confinata a Infrastructure (Application non dipende da Polly/Http). |

## File chiave

- `src/WebApiPlayground.Application/Popularity/` — `IBookPopularityClient`, `BookPopularity`, `ExternalServiceUnavailableException`
- `src/WebApiPlayground.Application/Services/BookPopularityService.cs`, `DTOs/BookPopularityDto.cs`
- `src/WebApiPlayground.Infrastructure/Popularity/` — client, contratto JSON, options, **registrazione pipeline**, **`CachingBookPopularityClient`** + `PopularityCacheKeys`
- `src/WebApiPlayground.Api/Controllers/BookPopularityController.cs`, `ErrorHandling/ExternalServiceUnavailableExceptionHandler.cs`, `OpenApi/ResilienceOperationTransformer.cs`

Pitfall scoperti e soluzioni: `.claude/lessons.md` [L19].
