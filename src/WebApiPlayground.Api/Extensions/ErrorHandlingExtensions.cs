using System.Diagnostics;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Api.Middleware;

namespace WebApiPlayground.Api.Extensions;

/// <summary>
/// Gestione errori centralizzata: ProblemDetails (RFC 7807) come formato d'errore unico
/// + l'exception handler globale, seguendo il pattern <c>AddApplication</c>/<c>AddInfrastructure</c>.
/// </summary>
public static class ErrorHandlingExtensions
{
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        // Arricchisce OGNI ProblemDetails con correlationId + traceId: punto unico, così
        // l'errore restituito al client è correlabile ai log (CorrelationId) e alle trace.
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
            {
                if (context.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId)
                    && correlationId is not null)
                {
                    context.ProblemDetails.Extensions["correlationId"] = correlationId;
                }

                context.ProblemDetails.Extensions["traceId"] =
                    Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
            });

        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }
}
