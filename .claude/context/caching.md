# Caching — HTTP caching (ETag) + server-side HybridCache (FusionCache)

Due livelli **complementari** di cache, entrambi sui GET dei libri.

```
        ┌─────────────── HTTP caching (rete) ───────────────┐
client  │  ETag + Cache-Control + If-None-Match → 304        │  ETagResultFilter (API)
        └────────────────────────────────────────────────────┘
        ┌──────────── server-side (DB/CPU) ─────────────────┐
        │  HybridCache: L1 memoria (+ L2 Redis opzionale)    │  CachingBooksService (Application)
        └────────────────────────────────────────────────────┘
                              │
                          BooksService → Repository → SQL Server
```

A cosa serve: ogni GET, senza cache, rifà query DB + mapping + serializzazione + trasferimento di
byte identici. **HybridCache** taglia DB/CPU (risposta da memoria in microsecondi), **ETag** taglia
banda e (ri)serializzazione (risposta `304` senza body). Insieme: un GET ripetuto = dato dall'L1 →
eventuale `304`.

---

## 1. HTTP caching con ETag (layer API)

**ETag** = impronta (hash SHA-256, strong, quotato) della rappresentazione di una risorsa.
Flusso *conditional request* (RFC 9110):

```
GET /api/books/1                         GET /api/books/1
→ 200 OK                                  If-None-Match: "9f86d0…"
  ETag: "9f86d0…"                        → 304 Not Modified   (niente body!)
  Cache-Control: private, no-cache          ETag: "9f86d0…"
  { …body… }
```

- `Cache-Control: private, no-cache`: **private** perché gli endpoint sono autenticati (mai cache
  condivise/proxy); **no-cache** = "rivalida sempre prima di riusare" → mette in mostra il `304`.
- ETag **strong** (`"…"`), non weak (`W/"…"`): byte-identici.

**Implementazione**: [`Api/Http/ETagResultFilter.cs`](../../src/WebApiPlayground.Api/Http/ETagResultFilter.cs)
(`IAsyncResultFilter`, registrato globalmente in `Program.cs`). Agisce **solo** su `GET` con
`ObjectResult` 200: calcola l'ETag dalla rappresentazione, setta gli header e — se `If-None-Match`
combacia — sostituisce il risultato con `304` (nessun body). Il calcolo è isolato in
[`Api/Http/ETag.cs`](../../src/WebApiPlayground.Api/Http/ETag.cs) (funzione pura, unit-testata).

> L'ETag è una funzione deterministica del **value** della risposta (serializzato con
> `JsonSerializerOptions.Web`): a noi serve solo coerenza interna (stesso value ⇒ stesso ETag),
> non l'uguaglianza byte-a-byte col body finale.

---

## 2. Server-side caching con HybridCache via FusionCache

**`HybridCache`** è l'astrazione standard di .NET (`Microsoft.Extensions.Caching.Hybrid`) che unifica
**L1** (in-memory, per-istanza, velocissimo) e **L2** (distribuito, condiviso) con stampede
protection, serializzazione e tagging. L'app dipende **solo** da questa astrazione.

**FusionCache** (ZiggyCreatures) è l'implementazione concreta scelta, registrata con `.AsHybridCache()`:
oltre a L1+L2 aggiunge **fail-safe** (serve il valore scaduto se la factory fallisce), timeout sulla
factory, eager refresh e — fondamentale per il multi-istanza — il **backplane**.

### Decoratore (layer Application)

[`Application/Caching/CachingBooksService.cs`](../../src/WebApiPlayground.Application/Caching/CachingBooksService.cs)
decora `IBooksService`:

- **Letture** (`GetBookByIdAsync`, `GetBooksAsync`) → `HybridCache.GetOrCreateAsync(key, factory, tags)`.
  Cache hit = nessuna chiamata al DB; cache miss = una sola esecuzione della factory.
- **Scritture** (`Create`/`Update`/`Delete`) → eseguono l'inner, poi `RemoveByTagAsync("books")`.
- **Chiavi e tag** centralizzati in
  [`Application/Caching/BookCacheKeys.cs`](../../src/WebApiPlayground.Application/Caching/BookCacheKeys.cs)
  (no magic string): `books:id:{id}`, `books:list:{page}:{size}:{sort}:{dir}`, tag `books`.
- **Invalidazione per tag**: ogni entry porta il tag `books`, quindi un solo `RemoveByTagAsync("books")`
  butta sia i singoli libri sia tutte le pagine di lista — storia di *cache invalidation* completa.
- **Negative caching**: anche un libro inesistente (`null`) viene cache-ato; è sicuro perché ogni
  create invalida il tag.

Le **durate/fail-safe** vivono una sola volta nelle `DefaultEntryOptions` di FusionCache
(Infrastructure), non nel decoratore → il decoratore resta minimale e provider-agnostico.

### Registrazione (layer Infrastructure = composition root)

[`Infrastructure/DependencyInjection.cs`](../../src/WebApiPlayground.Infrastructure/DependencyInjection.cs)
(`AddCaching`): `AddFusionCache().WithDefaultEntryOptions(…).AsHybridCache()`. L1 memoria **sempre**.
Se è configurata una connection string Redis → si aggiungono **L2 + backplane** (vedi sotto).

---

## 3. Redis multi-istanza (Redis-ready, config-gated)

Con più istanze dietro un load balancer, ogni istanza ha il suo L1. Senza coordinamento, un update
su un'istanza lascerebbe gli L1 delle altre **stale**. Soluzione FusionCache:

- **L2 Redis**: cache distribuita condivisa da tutte le istanze.
- **Backplane** (canale Redis pub/sub): quando un'istanza invalida una chiave/tag, **notifica** tutte
  le altre, che svuotano il proprio L1 → **cache allineata** su tutte le istanze. È ciò che
  l'implementazione Microsoft di `HybridCache` oggi non offre.

Si attiva **senza toccare il codice**, solo da configurazione:

```jsonc
// appsettings.json → sezione "Cache"
"Cache": {
  "Duration": "00:01:00",            // freschezza di una entry
  "FailSafeMaxDuration": "02:00:00", // finestra di fail-safe
  "Redis": { "ConnectionString": "" } // vuoto = solo L1; valorizzata = L2 + backplane
}
```

Opzioni mappate in
[`Infrastructure/Caching/CacheSettings.cs`](../../src/WebApiPlayground.Infrastructure/Caching/CacheSettings.cs).
Avviare davvero Redis (docker-compose) è un passo separato della roadmap (Tier 5).

---

## Vincoli architetturali (auto-validati)

- Application dipende **solo** dall'astrazione `HybridCache`, mai dai concreti FusionCache/Redis:
  regola enforce in `tests/WebApiPlayground.ArchitectureTests` (`Application_should_not_depend_on_cache_implementations`).
- FusionCache/Redis stanno in Infrastructure (composition root), coerente con `architecture.md`.

## Test

- **Unit** ([`tests/WebApiPlayground.Tests/Caching`](../../tests/WebApiPlayground.Tests/Caching)):
  decoratore contro un `HybridCache` reale (FusionCache memory-only) → hit, chiavi per-query,
  invalidazione su write. ETag determinismo in `tests/WebApiPlayground.Tests/Http`.
- **Integration** ([`tests/WebApiPlayground.IntegrationTests/Caching`](../../tests/WebApiPlayground.IntegrationTests/Caching)):
  GET → ETag/Cache-Control; `If-None-Match` → `304` senza body; dopo PUT l'ETag cambia.
- ⚠️ I test che fanno seed **diretto** sul DB devono svuotare la cache nel reset del factory — vedi
  `.claude/lessons.md` **[L11]**.
