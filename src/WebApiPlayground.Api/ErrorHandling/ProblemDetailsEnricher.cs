using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Api.Middleware;

namespace WebApiPlayground.Api.ErrorHandling;

/// <summary>
/// Punto unico di arricchimento dei ProblemDetails con <c>correlationId</c> + <c>traceId</c>,
/// così l'errore restituito al client è correlabile ai log e alle trace.
/// Usato sia da <c>CustomizeProblemDetails</c> (eccezioni non gestite, canale
/// <see cref="IProblemDetailsService"/>) sia dalla factory delle risposte di validazione 400,
/// che non passa per <see cref="IProblemDetailsService"/> e quindi va arricchita esplicitamente.
/// </summary>
public static class ProblemDetailsEnricher
{
    public static void Enrich(HttpContext httpContext, ProblemDetails problemDetails)
    {
        if (httpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId)
            && correlationId is not null)
        {
            problemDetails.Extensions["correlationId"] = correlationId;
        }

        problemDetails.Extensions["traceId"] =
            Activity.Current?.Id ?? httpContext.TraceIdentifier;
    }
}
