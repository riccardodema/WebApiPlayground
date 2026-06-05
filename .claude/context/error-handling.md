# Gestione errori — ProblemDetails (RFC 7807)

Tutti gli errori dell'API sono restituiti come **`application/problem+json`** (ProblemDetails,
RFC 7807): un formato d'errore unico, machine-readable e correlabile ai log.

## Componenti

| Cosa | Dove |
|---|---|
| Exception handler globale (eccezioni non gestite → 500) | `Api/ErrorHandling/GlobalExceptionHandler.cs` |
| Precondizioni/concorrenza (412/428/400) | `Api/ErrorHandling/PreconditionExceptionHandler.cs` (vedi `[L17]`) |
| Dipendenza esterna indisponibile (503 + `Retry-After`) | `Api/ErrorHandling/ExternalServiceUnavailableExceptionHandler.cs` (vedi `[L19]`) |
| Registrazione `AddProblemDetails` + handler + enrichment | `Api/Extensions/ErrorHandlingExtensions.cs` (`AddApiProblemDetails`) |
| Aggancio pipeline | `Program.cs`: `AddApiProblemDetails()` + `app.UseExceptionHandler()` |

### Catena di `IExceptionHandler` (l'ordine di registrazione conta)

Gli handler sono provati **in ordine** finché uno gestisce (gli altri ritornano `false` e declinano):

1. **`PreconditionExceptionHandler`** → 412 (concorrenza stale) / 428 (If-Match mancante) / 400 (malformato).
2. **`ExternalServiceUnavailableExceptionHandler`** → 503 + `Retry-After` quando la resilienza su una
   dipendenza esterna è esaurita (`ExternalServiceUnavailableException`). Vedi `.claude/context/resilience.md`.
3. **`GlobalExceptionHandler`** → catch-all 500 (sempre per ultimo).

Tutti scrivono via `IProblemDetailsService` → stesso `correlationId`/`traceId` (DRY, `ProblemDetailsEnricher`).

## Come funziona

- **`AddApiProblemDetails`** registra `AddProblemDetails(...)` con un `CustomizeProblemDetails`
  che arricchisce **ogni** ProblemDetails (da qualunque sorgente passi per `IProblemDetailsService`)
  con due extension: `correlationId` (letto da `HttpContext.Items`, vedi `CorrelationIdMiddleware`)
  e `traceId` (`Activity.Current?.Id` o `TraceIdentifier`). Punto unico → coerenza garantita.
- **`GlobalExceptionHandler : IExceptionHandler`** cattura le eccezioni non gestite: logga **una
  sola volta** a livello `Error` (con l'eccezione), imposta status 500 e scrive un ProblemDetails
  via `IProblemDetailsService.TryWriteAsync` (così passa anche dal `CustomizeProblemDetails`).
- Il **`Detail`** con il messaggio dell'eccezione è incluso **solo in Development** (no info leak in
  produzione). `Title`/`Type`/`Status` sono sempre generici.

## Ordine in `Program.cs` (obbligatorio)

```csharp
app.UseMiddleware<CorrelationIdMiddleware>();  // 1° — popola HttpContext.Items["CorrelationId"]
app.UseExceptionHandler();                     // 2° — così il body d'errore include il correlationId
app.UseSerilogRequestLogging(...);             // 3°
```

Se `UseExceptionHandler` sta **prima** del CorrelationId, il ProblemDetails non avrà `correlationId`.

## Relazione con `[ApiController]`

Le validazioni di model binding (`[Range]`, `[Required]`, …) con `[ApiController]` e quelle
**FluentValidation** sui body confluiscono nello **stesso** 400 `ValidationProblemDetails`,
arricchito con lo stesso `correlationId`/`traceId` (punto unico: `ProblemDetailsEnricher`).
Dettagli in `.claude/context/validation.md`; pitfall sul serializzatore in `[L10]`.

## Test

`tests/WebApiPlayground.IntegrationTests/ErrorHandling/GlobalExceptionHandlerTests.cs` esercita
lo handler **end-to-end** colpendo `ThrowingTestController` (endpoint `/__tests__/throw`),
registrato nella pipeline reale solo nei test via `AddApplicationPart` (nessun endpoint fittizio
in produzione). Verifica: status 500, content-type `application/problem+json`, e `correlationId`
nel body che combacia con l'header `X-Correlation-Id`. Vedi `[L08]` per il pitfall sull'ambiente
di `WebApplicationFactory`.
