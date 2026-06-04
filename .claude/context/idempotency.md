# Idempotency — header `Idempotency-Key` sui POST

## A cosa serve

La rete non è affidabile. Un client fa `POST /api/books`, il server **crea** la risorsa, ma la
risposta si perde (timeout, connessione caduta, retry automatico del client HTTP, doppio click).
Il client non sa se è andata e **ritenta** → senza protezione si crea un **duplicato**.

L'idempotency dà semantica **exactly-once** sulle scritture: il client allega una chiave unica
(`Idempotency-Key: <uuid>`); la prima richiesta viene eseguita e la sua risposta **memorizzata**;
ogni ritentativo con la stessa chiave **rigioca** quella risposta senza ri-eseguire l'effetto.

```
POST /api/books   Idempotency-Key: abc   → 201 Created  Location: /api/books/42      (creato)
POST /api/books   Idempotency-Key: abc   → 201 Created  Location: /api/books/42      (replay)
                                            Idempotency-Replayed: true                (nessun duplicato)
```

`GET/PUT/DELETE` sono già idempotenti per semantica HTTP; il problema è solo il **POST**. È lo
standard de-facto (Stripe, PayPal, bozza IETF `idempotency-key-header`).

## Flusso (middleware)

[`Api/Middleware/IdempotencyMiddleware.cs`](../../src/WebApiPlayground.Api/Middleware/IdempotencyMiddleware.cs),
registrato in `Program.cs` **dopo `UseAuthorization()`** (così conosce il client autenticato):

1. **Self-guard**: agisce solo su `POST` con header `Idempotency-Key` presente (opt-in del client).
   Chiave vuota o troppo lunga → **400** ProblemDetails.
2. **Fingerprint** del corpo richiesta (SHA-256). Il body viene bufferizzato (`EnableBuffering`) e
   riavvolto per il model binding.
3. **Storage key** = `SHA-256(userId | metodo | path | Idempotency-Key)` → scopata per client +
   endpoint, due client non collidono.
4. **Lock per-chiave** (`KeyedAsyncLock`): richieste concorrenti con la stessa chiave sono
   serializzate nel processo; la seconda trova il record e rigioca.
5. **Lookup nello store**:
   - record presente + **fingerprint uguale** → **replay** (status + `Location` + body + header
     `Idempotency-Replayed: true`);
   - record presente + **fingerprint diverso** → **422** (stessa chiave riusata per un payload diverso:
     bug del client, non si rigioca la risposta sbagliata);
   - assente → esegue la pipeline, **cattura la risposta reale** (status, header `Location` generato,
     body) bufferizzando lo stream, la inoltra al client e la **memorizza**.

**Cosa si memorizza**: risposte **2xx–4xx** (esiti deterministici). **Mai 5xx**: un errore transitorio
deve poter essere davvero ritentato.

## Store (Redis-ready)

Astrazione [`IIdempotencyStore`](../../src/WebApiPlayground.Application/Idempotency/IIdempotencyStore.cs)
(Application) → implementazione
[`DistributedCacheIdempotencyStore`](../../src/WebApiPlayground.Infrastructure/Idempotency/DistributedCacheIdempotencyStore.cs)
su **`IDistributedCache`** (get-or-null + set con TTL — esattamente la semantica giusta). Registrato in
`AddIdempotency` (Infrastructure): **in memoria** di default, **Redis** quando
`Cache:Redis:ConnectionString` è valorizzata (stesso setting della cache) → store **condiviso fra
istanze**. TTL da `Idempotency:Ttl` (default 24h).

```jsonc
// appsettings.json
"Idempotency": { "Ttl": "24:00:00" }   // finestra entro cui un retry rigioca la prima risposta
```

## Vantaggi

- **Retry sicuri** sui POST: niente risorse duplicate anche se il client ritenta.
- **Risposta coerente** al ritentativo (stessa identica, byte per byte, header inclusi).
- **Riuso errato della chiave** rilevato (422) invece di restituire silenziosamente la risposta sbagliata.
- Pronto **multi-istanza** con Redis senza cambi al codice.

## Scelte / limiti

- **Honor-if-present**: la chiave non è obbligatoria (opt-in del client, come Stripe) — i client/test
  esistenti senza chiave continuano a funzionare.
- **Atomicità cross-istanza**: il lock è in-process; due istanze potrebbero eseguire in parallelo la
  stessa chiave (raro). Hardening futuro: lock distribuito (Redis SETNX). Mitigato dallo store condiviso.

## Contratto (OpenAPI)

Il supporto è **documentato nello spec** (non un comportamento implicito): l'operation transformer
[`Api/OpenApi/IdempotencyOperationTransformer.cs`](../../src/WebApiPlayground.Api/OpenApi/IdempotencyOperationTransformer.cs)
aggiunge sui POST l'header di richiesta `Idempotency-Key`, l'header di risposta
`Idempotency-Replayed` (sulle 2xx) e la risposta `422` — visibili in Scalar e in `/openapi/v1.json`.
Stesso meccanismo dei transformer Bearer e FluentValidation. Un test ne verifica la presenza nel
contratto (vedi sotto).

## Test

- **Unit** ([`tests/WebApiPlayground.Tests/Idempotency`](../../tests/WebApiPlayground.Tests/Idempotency)):
  round-trip dello store su `MemoryDistributedCache`.
- **Integration** ([`tests/WebApiPlayground.IntegrationTests/Idempotency`](../../tests/WebApiPlayground.IntegrationTests/Idempotency)):
  stessa key+body → replay marcato e **un solo** libro in DB; stessa key+body diverso → 422; senza key →
  due POST creano due libri. Chiavi GUID fresche per test (store memoria condiviso dalla factory).
