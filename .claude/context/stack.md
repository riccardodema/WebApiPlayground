# Stack tecnico e comandi

## Framework e package

| Package | Progetto | Versione |
|---------|----------|----------|
| `Microsoft.AspNetCore.OpenApi` | API | 10.0.0 |
| `Azure.Extensions.AspNetCore.Configuration.Secrets` | API | 1.5.1 (Key Vault config provider, vedi keyvault.md) |
| `Azure.Identity` | API | 1.21.0 (credential esplicite per il vault; allineata a Infrastructure) |
| `Scalar.AspNetCore` | API | 2.6.0 |
| `Microsoft.Extensions.Caching.Hybrid` | Application | 10.0.0 |
| `Microsoft.EntityFrameworkCore` | Infrastructure | 10.0.0 |
| `Microsoft.EntityFrameworkCore.SqlServer` | Infrastructure | 10.0.0 |
| `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` | Infrastructure | 10.0.0 |
| `Microsoft.Extensions.Configuration.Binder` | Infrastructure | 10.0.0 |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | Infrastructure | 10.0.0 |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | Infrastructure | 10.0.0 |
| `Microsoft.Extensions.Http.Resilience` | Infrastructure | 10.0.0 (Polly v8; pin 10.0.0 = baseline, vedi [L19]) |
| `ZiggyCreatures.FusionCache` | Infrastructure | 2.6.0 |
| `ZiggyCreatures.FusionCache.Serialization.SystemTextJson` | Infrastructure | 2.6.0 |
| `ZiggyCreatures.FusionCache.Backplane.StackExchangeRedis` | Infrastructure | 2.6.0 |
| `OpenTelemetry.Extensions.Hosting` | API | 1.15.3 |
| `OpenTelemetry.Instrumentation.AspNetCore` | API | 1.15.2 |
| `OpenTelemetry.Instrumentation.Http` | API | 1.15.1 |
| `OpenTelemetry.Instrumentation.Runtime` | API | 1.15.1 |
| `OpenTelemetry.Instrumentation.EntityFrameworkCore` | API | 1.15.1-beta.1 (semantic conv DB sperimentali) |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | API | 1.15.3 |
| `OpenTelemetry.Exporter.Console` | API | 1.15.3 |
| `Serilog.Sinks.OpenTelemetry` | API | 4.2.0 |
| `xunit` | Tests | 2.9.3 |
| `Moq` | Tests | 4.20.72 |
| `Microsoft.NET.Test.Sdk` | Tests | 17.12.0 |
| `Microsoft.Extensions.Diagnostics.Testing` | Tests | (MetricCollector\<T>) |
| `NetArchTest.Rules` | ArchitectureTests | 1.3.2 |

> `ActivitySource`/`Meter` custom vivono in **Application** con le sole primitive BCL
> (`System.Diagnostics.DiagnosticSource`, transitiva via `Microsoft.Extensions.Caching.Hybrid`): l'SDK
> OpenTelemetry resta confinato in **API** (regola architetturale auto-validata).

## URL locali

| Risorsa | URL |
|---------|-----|
| Scalar UI | `http://localhost:5242/scalar/v1` |
| OpenAPI JSON | `http://localhost:5242/openapi/v1.json` |
| HTTP base | `http://localhost:5242` |

> ⚠️ Non usare `/swagger` — vedere `.claude/lessons.md`

## Comandi

```bash
# Avvio
dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj

# Test (unit)
dotnet test tests/WebApiPlayground.Tests/WebApiPlayground.Tests.csproj

# Test (architecture: regole di layering via NetArchTest — veloce, niente DB/Docker)
dotnet test tests/WebApiPlayground.ArchitectureTests/WebApiPlayground.ArchitectureTests.csproj

# EF Core migrations
dotnet ef migrations add <Nome> \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api

dotnet ef database update \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api

dotnet ef migrations remove \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project src/WebApiPlayground.Api
```

## Connection string

Va in `src/WebApiPlayground.Api/appsettings.Development.json` (mai in `appsettings.json` o nel codice):

```json
{ "ConnectionStrings": { "Default": "Server=localhost;Database=WebApiPlayground;Trusted_Connection=True;TrustServerCertificate=True;" } }
```

## Config Key Vault (sezione `KeyVault`)

Config-gated come la cache: `Uri` vuoto = provider spento (segreti da appsettings/env var).
Valorizzandolo i secret del vault entrano in `IConfiguration` (nome `--` → `:`) e vincono su
appsettings/env. Credential esplicita: `ManagedIdentity` (default) | `AzureCli` | `Emulator`
(solo Development). Vedi `.claude/context/keyvault.md` e `docs/keyvault.md`.

```json
{ "KeyVault": { "Uri": "", "Credential": "ManagedIdentity", "ManagedIdentityClientId": "", "ReloadInterval": "" } }
```

## Config cache (sezione `Cache`)

Default sensati: senza configurazione la cache è solo L1 in memoria. Valorizzando
`Cache:Redis:ConnectionString` si attivano L2 Redis + backplane (multi-istanza). Vedi
`.claude/context/caching.md`.

```json
{ "Cache": { "Duration": "00:01:00", "FailSafeMaxDuration": "02:00:00", "Redis": { "ConnectionString": "" } } }
```

## Config idempotency (sezione `Idempotency`)

Store su `IDistributedCache` (memoria; Redis se `Cache:Redis:ConnectionString` valorizzata). Vedi
`.claude/context/idempotency.md`.

```json
{ "Idempotency": { "Ttl": "24:00:00" } }
```

## Config OpenTelemetry (sezione `OpenTelemetry`)

Config-gated come la cache. `OtlpEndpoint` vuoto = telemetria solo raccolta (nessun export); valorizzandolo
si esportano traces + metrics + logs via OTLP. `ConsoleExporter=true` stampa traces/metrics su console
(visibilità locale senza collector). Vedi `.claude/context/opentelemetry.md`.

```json
{ "OpenTelemetry": { "OtlpEndpoint": "", "ServiceName": "WebApiPlayground.Api", "ConsoleExporter": false } }
```

## Config resilience / dipendenza esterna (sezione `BookPopularity`)

Config-gated/out-of-the-box come la cache: `BaseAddress` punta a Open Library (key-less → nessun segreto, host
fisso → niente SSRF). `Resilience` configura la pipeline Polly esplicita (timeout totale → retry backoff+jitter
→ circuit breaker → timeout per-tentativo). `Cache` configura la cache della risposta esterna (FusionCache via
`IFusionCache`): `Duration` = TTL freschezza, `FailSafeMaxDuration` = finestra di degrade-to-stale durante
un'outage, `CacheNotFound` = negative caching, `Enabled=false` = nessuna cache. Vedi `.claude/context/resilience.md`.

```json
{ "BookPopularity": {
    "BaseAddress": "https://openlibrary.org",
    "Resilience": {
      "AttemptTimeout": "00:00:03", "TotalTimeout": "00:00:10",
      "Retry": { "MaxRetryAttempts": 3, "BaseDelay": "00:00:00.500" },
      "CircuitBreaker": { "FailureRatio": 0.5, "SamplingDuration": "00:00:30", "MinimumThroughput": 10, "BreakDuration": "00:00:15" }
    },
    "Cache": { "Enabled": true, "Duration": "00:15:00", "FailSafeMaxDuration": "24:00:00", "CacheNotFound": true }
} } }
```
