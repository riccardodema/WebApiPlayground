using System.Reflection;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using WebApiPlayground.Api.Observability;
using WebApiPlayground.Application.Diagnostics;

namespace WebApiPlayground.Api.Extensions;

/// <summary>
/// Registra OpenTelemetry (traces + metrics) nella composition root. Le trace/metriche del framework
/// (ASP.NET Core, HttpClient, EF Core, runtime, rate limiter) si aggiungono come auto-instrumentation;
/// la source/meter <b>custom</b> del dominio (<see cref="BooksDiagnostics"/>) si abilita via
/// <c>AddSource</c>/<c>AddMeter</c>. L'export è <b>config-gated</b> (sezione <c>OpenTelemetry</c>): OTLP se
/// <c>OtlpEndpoint</c> è valorizzato, console se <c>ConsoleExporter=true</c>, altrimenti raccolta a vuoto
/// (overhead trascurabile). I <b>log</b> seguono un percorso separato (Serilog → OTLP, vedi Program.cs).
/// Vedi <c>.claude/context/opentelemetry.md</c>.
/// </summary>
public static class OpenTelemetryExtensions
{
    public static IServiceCollection AddApiObservability(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(OpenTelemetryOptions.SectionName);
        services.Configure<OpenTelemetryOptions>(section);
        var options = section.Get<OpenTelemetryOptions>() ?? new OpenTelemetryOptions();

        var serviceVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: Environment.MachineName)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                // Source custom (Books.Create) + auto-instrumentation. Il filtro scarta gli endpoint di
                // infrastruttura (health/openapi/scalar) per non sporcare le trace con rumore non di dominio.
                .AddSource(BooksDiagnostics.ActivitySourceName)
                .AddAspNetCoreInstrumentation(o => o.Filter = IsTraceableRequest)
                .AddHttpClientInstrumentation()
                .AddEntityFrameworkCoreInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(BooksDiagnostics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Metriche native del rate limiter già presente (lease attivi, code, richieste respinte).
                .AddMeter("Microsoft.AspNetCore.RateLimiting"));

        // Un'unica chiamata cross-cutting registra l'exporter OTLP per traces e metrics (DRY): l'app non
        // conosce il vendor, parla solo OTLP verso un endpoint. I log vanno via Serilog (sink OTLP separato).
        if (!string.IsNullOrWhiteSpace(options.OtlpEndpoint))
            otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(options.OtlpEndpoint));

        // Console exporter opt-in: telemetria visibile in locale senza un collector.
        if (options.ConsoleExporter)
            otel.WithTracing(tracing => tracing.AddConsoleExporter())
                .WithMetrics(metrics => metrics.AddConsoleExporter());

        return services;
    }

    /// <summary>
    /// Tiene fuori dalle trace gli endpoint di infrastruttura: probe di health, documento OpenAPI e UI
    /// Scalar non sono richieste di dominio e genererebbero solo rumore (e span ad alta frequenza).
    /// </summary>
    private static bool IsTraceableRequest(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
            return true;

        return !path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase);
    }
}
