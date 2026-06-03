using WebApiPlayground.Api.ErrorHandling;

namespace WebApiPlayground.Api.Extensions;

/// <summary>
/// Gestione errori centralizzata: ProblemDetails (RFC 7807) come formato d'errore unico
/// + l'exception handler globale, seguendo il pattern <c>AddApplication</c>/<c>AddInfrastructure</c>.
/// </summary>
public static class ErrorHandlingExtensions
{
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        // Arricchisce OGNI ProblemDetails che passa per IProblemDetailsService con
        // correlationId + traceId. Il canale di validazione 400 (che NON passa di qui)
        // riusa lo stesso ProblemDetailsEnricher: punto unico di arricchimento.
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context =>
                ProblemDetailsEnricher.Enrich(context.HttpContext, context.ProblemDetails));

        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }
}
