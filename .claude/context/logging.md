# Logging — Serilog Structured Logging

## Stack

| Componente | Pacchetto | Versione |
|------------|-----------|----------|
| Integrazione ASP.NET Core | `Serilog.AspNetCore` | 10.0.0 |
| Sink console | incluso in `Serilog.AspNetCore` | — |
| Configurazione da JSON | incluso in `Serilog.AspNetCore` | — |
| ILogger<T> in Application layer | `Microsoft.Extensions.Logging.Abstractions` | 10.0.0 |

## CorrelationId — Raggruppare i log per chiamata

Ogni richiesta HTTP riceve un `CorrelationId` univoco che appare su **tutti** i log generati da quella chiamata (controller → service → repository → middleware HTTP).

### Come funziona

`CorrelationIdMiddleware` (`Api/Middleware/CorrelationIdMiddleware.cs`) è il **primo middleware** nella pipeline:

1. Legge `X-Correlation-Id` dall'header della request (se il client ne passa uno) oppure genera un `Guid.NewGuid().ToString("N")` fresco
2. Chiama `LogContext.PushProperty("CorrelationId", correlationId)` — da questo momento tutti i log Serilog nell'ambito del `using` avranno la proprietà `{CorrelationId}`
3. Aggiunge `X-Correlation-Id` all'header della response (utile per correlare i log lato client)
4. Al termine della request, il `using` libera la proprietà dal contesto

```csharp
// Middleware/CorrelationIdMiddleware.cs — pattern chiave
using (LogContext.PushProperty("CorrelationId", correlationId))
{
    await next(context);  // l'intera pipeline è dentro questo scope
}
```

### Ordine obbligatorio in Program.cs

```csharp
app.UseMiddleware<CorrelationIdMiddleware>();   // 1° — deve essere PRIMA di Serilog
app.UseSerilogRequestLogging(...);              // 2° — così il CorrelationId è già in scope
```

Se l'ordine si inverte, il log del middleware HTTP non conterrà il `{CorrelationId}`.

### CorrelationId nell'output template

```json
"outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {SourceContext}: {Message:lj}{NewLine}{Exception}"
```

- Per log **dentro** una request: `[CorrelationId]` mostra l'ID generato
- Per log **fuori** da una request (startup, fatal): `[]` — nessuna property in scope

### Client: passare un proprio CorrelationId

```http
GET /api/books
X-Correlation-Id: my-frontend-trace-abc123
```

Il middleware riusa l'ID fornito, così il log lato server e lato client condividono lo stesso identificatore.

---

## Configurazione

### appsettings.json (produzione/base)
- Livello di default: `Information`
- Namespace Microsoft/System/EF Core soppressi a `Warning` per evitare rumore
- Template console: `[HH:mm:ss LVL] [CorrelationId] SourceContext: Message`

### appsettings.Development.json (sviluppo)
- Livello di default: `Debug`
- Namespace `WebApiPlayground.*` espliciti a `Debug`
- Microsoft/System/EF Core rimangono a `Warning`

## Bootstrap Logger (Program.cs)

Il bootstrap logger è configurato **prima** di `WebApplication.CreateBuilder()` per catturare errori di startup:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();
```

Viene sostituito dal logger completo (letto da `appsettings.json`) tramite:

```csharp
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services));
```

Il blocco `try/catch/finally` in `Program.cs` cattura eccezioni fatali di startup e chiama `Log.CloseAndFlush()`.

## Request Logging Middleware

```csharp
app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.000} ms";
});
```

Emette un unico log `Information` per ogni richiesta HTTP con metodo, path, status code e durata in ms.  
**NON** duplicare questo logging a mano nei controller.

## Livelli per Layer

| Layer | Tipo di evento | Livello |
|-------|----------------|---------|
| HTTP request/response | middleware Serilog | `Information` |
| Controller — operazione avviata | `GetBooks`, `CreateBook`, `DeleteBook` | `Information` |
| Controller — risorsa non trovata (404) | warning visibile al caller | `Warning` |
| Controller — parametri di lookup | `GetBookById` con ID | `Debug` |
| Service — elaborazione interna | mapping, chain verso repository | `Debug` |
| Repository — query SQL | prima e dopo ogni query | `Debug` |
| Repository — errore DB | `DbUpdateException` | `Error` (con exception) |
| Startup — avvio applicazione | bootstrap prima del builder | `Information` |
| Startup — crash fatale | eccezione non gestita nel main | `Fatal` (con exception) |

## Named Properties (Message Templates)

Usare **sempre** named properties strutturate — mai string interpolation nei log.

| Proprietà | Tipo | Usata in |
|-----------|------|----------|
| `{CorrelationId}` | `string` | automatico su tutti i log dentro una request |
| `{BookId}` | `int` | tutte le operazioni su libro singolo |
| `{BookTitle}` | `string` | create, find |
| `{AuthorId}` | `int` | create |
| `{AuthorName}` | `string` | retrieve con autore risolto |
| `{BookCount}` | `int` | GetAll |
| `{RequestMethod}` | `string` | middleware HTTP |
| `{RequestPath}` | `string` | middleware HTTP |
| `{StatusCode}` | `int` | middleware HTTP |
| `{Elapsed}` | `double` ms | middleware HTTP |

**Esempio corretto:**
```csharp
_logger.LogInformation("Book created successfully — ID: {BookId}, Title: '{BookTitle}'", created.Id, created.Title);
```

**Esempio sbagliato:**
```csharp
_logger.LogInformation($"Book created: {created.Id} - {created.Title}"); // NO — perde struttura
```

## Output Console (esempio)

```
[10:37:00 INF] [] Starting WebApiPlayground API

[10:39:40 INF] [a1b2c3d4e5f6789abcdef01234567890] WebApiPlayground.Controllers.BooksController: Fetching all books
[10:39:40 DBG] [a1b2c3d4e5f6789abcdef01234567890] WebApiPlayground.Application.Services.BooksService: Retrieving all books from repository
[10:39:40 DBG] [a1b2c3d4e5f6789abcdef01234567890] WebApiPlayground.Infrastructure.Repositories.BookRepository: Query returned 2 book(s)
[10:39:40 INF] [a1b2c3d4e5f6789abcdef01234567890] WebApiPlayground.Controllers.BooksController: Successfully retrieved 2 book(s)
[10:39:40 INF] [a1b2c3d4e5f6789abcdef01234567890] Serilog.AspNetCore.RequestLoggingMiddleware: HTTP GET /api/books responded 200 in 45.312 ms

[10:39:41 WRN] [b2c3d4e5f6789abc0123456789abcdef] WebApiPlayground.Controllers.BooksController: Book with ID 99 was not found
[10:39:41 INF] [b2c3d4e5f6789abc0123456789abcdef] Serilog.AspNetCore.RequestLoggingMiddleware: HTTP GET /api/books/99 responded 404 in 12.008 ms

[10:32:15 ERR] [c3d4e5f6789abcde0123456789abcdef] WebApiPlayground.Infrastructure.Repositories.BookRepository: Database error while inserting book 'Test'
System.Data.SqlClient.SqlException: ...
```

## Aggiungere Logging a una Nuova Risorsa

1. **Controller**: iniettare `ILogger<{Name}Controller>` nel costruttore
   - `LogInformation` per operazioni list/create/delete
   - `LogWarning` per ogni risposta `NotFound()`
   - `LogDebug` per operazioni get-by-id e parametri di dettaglio

2. **Service**: iniettare `ILogger<{Name}Service>` nel costruttore
   - `LogDebug` per chiamate al repository e risultati di mapping

3. **Repository**: iniettare `ILogger<{Name}Repository>` nel costruttore
   - `LogDebug` prima e dopo ogni query
   - `LogError(ex, ...)` + `throw` in catch di `DbUpdateException` su `SaveChangesAsync()`

Il `{CorrelationId}` viene aggiunto automaticamente dal middleware — nessun codice aggiuntivo nei layer.

## Errori e Eccezioni

Le eccezioni del DB sono intercettate nel repository con log `Error` + rethrow:

```csharp
catch (DbUpdateException ex)
{
    _logger.LogError(ex, "Database error while inserting book '{BookTitle}'", book.Title);
    throw;
}
```

Non loggare l'eccezione più di una volta nella catena di chiamate.

## Regole da non violare

- Non usare string interpolation (`$"..."`) nei message template
- Non loggare dati sensibili (password, token, dati personali completi)
- Non duplicare il log HTTP: il middleware lo gestisce già
- Non loggare la stessa eccezione a più livelli della catena (una sola volta nel punto più basso)
- Non usare `Log.` statico nei layer Application/Infrastructure — solo `ILogger<T>` iniettato via DI
- `CorrelationIdMiddleware` deve essere il **primo** middleware registrato in `Program.cs`
