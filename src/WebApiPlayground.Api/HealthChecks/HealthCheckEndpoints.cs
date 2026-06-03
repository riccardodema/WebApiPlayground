using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WebApiPlayground.Api.Middleware;
using WebApiPlayground.Infrastructure.HealthChecks;

namespace WebApiPlayground.Api.HealthChecks;

/// <summary>
/// Espone due probe distinti, secondo la best practice liveness/readiness:
/// <list type="bullet">
/// <item><c>/health/live</c> — <b>liveness</b>: il processo è vivo? Nessuna dipendenza testata
/// (un fallimento per DB giù causerebbe un restart inutile dell'app).</item>
/// <item><c>/health/ready</c> — <b>readiness</b>: l'app può servire traffico? Esegue i check
/// marcati <see cref="HealthCheckTags.Ready"/> (es. la connessione al DB).</item>
/// </list>
/// Entrambi sono anonimi (i probe dell'orchestratore non hanno token) e attivi in ogni ambiente.
/// </summary>
public static class HealthCheckEndpoints
{
    public static IEndpointRouteBuilder MapApiHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false, // nessun check: risponde Healthy finché il processo risponde
            ResponseWriter = WriteResponse,
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthCheckTags.Ready),
            ResponseWriter = WriteResponse,
        });

        return endpoints;
    }

    // Lo status code lo imposta il middleware (200 Healthy/Degraded, 503 Unhealthy); qui scriviamo
    // solo un body JSON diagnostico, correlato ai log tramite correlationId.
    private static Task WriteResponse(HttpContext context, HealthReport report)
    {
        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var id)
            ? id as string
            : null;

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            correlationId,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = entry.Value.Duration.TotalMilliseconds,
            }),
        };

        return context.Response.WriteAsJsonAsync(payload);
    }
}
