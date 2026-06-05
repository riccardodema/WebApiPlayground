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

        // L'ordine conta: gli IExceptionHandler sono provati nell'ordine di registrazione finché uno
        // gestisce. Il PreconditionExceptionHandler mappa precondizioni/concorrenza (412/428/400);
        // l'ExternalServiceUnavailableExceptionHandler mappa la resilienza esaurita su dipendenze esterne
        // (503 + Retry-After); entrambi declinano il resto. Il GlobalExceptionHandler è il catch-all (500),
        // quindi va per ultimo.
        services.AddExceptionHandler<PreconditionExceptionHandler>();
        services.AddExceptionHandler<ExternalServiceUnavailableExceptionHandler>();
        services.AddExceptionHandler<GlobalExceptionHandler>();

        return services;
    }
}
