# OpenTelemetry — observability distribuita (traces + metrics + logs, OTLP)

## A cosa serve

Il progetto ha già **logging strutturato** (Serilog) e un **CorrelationId** per richiesta, ma niente
**trace distribuite**, **metriche** o un canale **vendor-neutral** verso un backend di osservabilità.
OpenTelemetry (OTel) è lo standard CNCF per emettere i tre segnali con un'unica API/SDK ed esportarli via
**OTLP** verso *qualunque* backend (Jaeger, Tempo, Prometheus, Aspire Dashboard, Datadog, App Insights…)
senza accoppiare il codice al vendor.

- **Traces** — la richiesta diventa una *waterfall* di span (HTTP in ingresso → service → query SQL):
  *dove* va il tempo e *cosa* ha fatto una singola richiesta end-to-end.
- **Metrics** — serie temporali aggregate (RPS, latenza, error rate, GC, code del rate limiter): *quanto*
  succede, per alert e dashboard. ASP.NET Core/EF/runtime le emettono già "gratis".
- **Logs** — i log Serilog esistenti, **correlati alla trace** (ogni log dentro uno span porta
  `TraceId`/`SpanId`): da un log salti alla trace completa e viceversa.

Il valore qui è **chiudere il cerchio della correlazione** già avviato con CorrelationId/Serilog:
`CorrelationId` (header lato client) ↔ `TraceId` (W3C, cross-service) ↔ `traceId` già presente nei
ProblemDetails (`ProblemDetailsEnricher` usa `Activity.Current?.Id`).

```
GET /api/v1/books → span server (ASP.NET Core)
                      └─ Books.Create (span custom, su POST)   ← instrumentation manuale
                           └─ query SQL (EF Core instrumentation)
log "Book created…"  →  stesso TraceId/SpanId  →  salta alla trace
```

## Le tre parti (best practice .NET 2026)

1. **SDK solo nella composition root (Api).** Il codice di business è strumentato con le **primitive BCL**
   (`System.Diagnostics.ActivitySource` / `System.Diagnostics.Metrics.Meter`) nel layer Application; l'SDK
   OTel e gli exporter vivono **solo** in Api. È l'approccio raccomandato OTel e rispetta i NetArchTest
   (Application non può dipendere da AspNetCore/EF/Infrastructure, ma `System.Diagnostics` è BCL — come già
   `Microsoft.Extensions.Logging.Abstractions`).
2. **Auto-instrumentation + custom.** Il framework emette span/metriche per ASP.NET Core, HttpClient, EF
   Core e runtime; la source/meter **custom** del dominio aggiunge uno span di business e una metrica.
3. **Export config-gated** (come Cache/Redis/idempotency/rate-limiting): senza endpoint la telemetria è solo
   raccolta; valorizzando `OtlpEndpoint` si esporta via OTLP. I **log** seguono un percorso separato
   (Serilog → OTLP) che riattacca `TraceId`/`SpanId`.

## Mappa dei componenti

| Cosa | Dove |
|---|---|
| Source + Meter custom (DRY, "no magic strings"): `ActivitySource`, `Meter`, counter `books.created`, helper | [`BooksDiagnostics`](../../src/WebApiPlayground.Application/Diagnostics/BooksDiagnostics.cs) (Application, primitive BCL) |
| Span di business `Books.Create` + `books.created` sul create | [`BooksService.CreateBookAsync`](../../src/WebApiPlayground.Application/Services/BooksService.cs) |
| Wiring SDK: resource, tracing, metrics, exporter config-gated | [`OpenTelemetryExtensions`](../../src/WebApiPlayground.Api/Extensions/OpenTelemetryExtensions.cs) (`AddApiObservability`) |
| Opzioni (`OtlpEndpoint`, `ServiceName`, `ConsoleExporter`) | [`OpenTelemetryOptions`](../../src/WebApiPlayground.Api/Observability/OpenTelemetryOptions.cs) |
| Bridge log Serilog → OTLP (config-gated, TraceId/SpanId) | [`Program.cs`](../../src/WebApiPlayground.Api/Program.cs) (lambda `UseSerilog`) |
| Config | [`appsettings.json`](../../src/WebApiPlayground.Api/appsettings.json) sezione `OpenTelemetry` |

## Scelte (decise nel POC)

- **Traces + metrics via SDK OTel; logs via Serilog → OTLP** (`Serilog.Sinks.OpenTelemetry`), non il logging
  provider OTel: il progetto è Serilog-centrico. Il sink riattacca `TraceId`/`SpanId` dall'Activity corrente
  e porta con sé le property del `LogContext`, **incluso il `CorrelationId`**. Il sink Console resta invariato.
- **Export OTLP con un'unica chiamata cross-cutting** `UseOtlpExporter(...)` (DRY: registra traces + metrics
  insieme). Esclude per design la combinazione con `AddOtlpExporter` per-segnale.
- **Custom instrumentation** come showcase manuale: span `Books.Create` (annidato nello span server) +
  contatore `books.created`. I nomi sono **costanti** in un solo punto (`BooksDiagnostics`).
- **Span filtrati**: gli endpoint di infrastruttura (`/health`, `/openapi`, `/scalar`) sono esclusi dalle
  trace (rumore non di dominio, alta frequenza).
- **Resource attributes**: `service.name`, `service.version` (informational version dell'assembly),
  `service.instance.id` (machine name), `deployment.environment` (ambiente host).
- **Metriche del rate limiter** già presente: aggiunto il meter `Microsoft.AspNetCore.RateLimiting`
  (lease attivi, code, richieste respinte) — si salda alla storia rate-limiting.

## Limiti consapevoli

- **EF Core instrumentation è -beta** (`1.15.1-beta.1`): le semantic convention DB sono sperimentali, i
  nomi/attributi degli span possono cambiare. Scelta consapevole per avere gli span SQL nelle trace (i test
  asseriscono lo span DB in modo *soft*: solo Kind=Client nello stesso trace, non il nome esatto).
- **Sampling always-on** (parent-based): adatto al POC. In produzione si tara (ratio/tail sampling) per
  contenere volume/costo.
- **Per-processo**: la telemetria è del singolo processo; la correlazione cross-service avviene via
  propagazione W3C `traceparent` (già attiva con l'HttpClient instrumentation).
- **Nessuna modifica al contratto OpenAPI**: OTel è invisibile a livello HTTP → niente operation transformer
  (si documenta solo ciò che è accurato).

## Come si integra col resto

Trasversale a ciò che esiste: il `traceId` dei ProblemDetails (errori 4xx/5xx, validazione, 429, 412/428)
diventa un **W3C trace id** quando OTel è attivo, saldando errore ↔ trace; il `CorrelationId` viaggia nei
log OTLP; le metriche del rate limiter entrano nel pannello. Le scritture restano strumentate dal solo
`BooksController` (DRY v1/v2).

## Come vederlo in locale

Senza collector: imposta `OpenTelemetry:ConsoleExporter=true` → span/metriche su console.
Con un backend (consigliato), il **.NET Aspire Dashboard** è un viewer OTLP one-container:

```bash
docker run --rm -it -p 18888:18888 -p 4317:4317 mcr.microsoft.com/dotnet/aspire-dashboard:latest
# poi avvia l'API con OpenTelemetry:OtlpEndpoint=http://localhost:4317 e apri http://localhost:18888
```

## Test

- **Unit** ([`Tests/Diagnostics/BooksDiagnosticsTests`](../../tests/WebApiPlayground.Tests/Diagnostics/BooksDiagnosticsTests.cs)):
  i nomi di source/meter/metrica sono stabili (contratto per le dashboard); `books.created` emette misure
  di 1 via `MetricCollector<long>`.
  [`Tests/Services/BooksServiceTests`](../../tests/WebApiPlayground.Tests/Services/BooksServiceTests.cs):
  `CreateBookAsync` (service reale) avvia lo span `Books.Create` (tag `book.id`) e incrementa la metrica.
  [`Tests/Observability/OpenTelemetryOptionsTests`](../../tests/WebApiPlayground.Tests/Observability/OpenTelemetryOptionsTests.cs):
  default collect-only + binding della sezione.
- **Integration** ([`IntegrationTests/Observability/OpenTelemetryTracingTests`](../../tests/WebApiPlayground.IntegrationTests/Observability/OpenTelemetryTracingTests.cs)):
  `ActivityListener` in-process → GET produce lo span server; POST produce lo span custom `Books.Create`
  nello **stesso trace** dello span server (+ span Client EF). [`GlobalExceptionHandlerTests`](../../tests/WebApiPlayground.IntegrationTests/ErrorHandling/GlobalExceptionHandlerTests.cs):
  il `traceId` del 500 è in formato W3C (la trace si salda all'errore).

Vedi anche `.claude/lessons.md` **[L18]** per i pitfall (EF beta, sink Serilog, listener process-global nei test).
