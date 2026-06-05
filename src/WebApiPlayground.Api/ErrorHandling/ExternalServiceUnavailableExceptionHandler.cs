using System.Globalization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Application.Popularity;

namespace WebApiPlayground.Api.ErrorHandling;

/// <summary>
/// Traduce <see cref="ExternalServiceUnavailableException"/> (resilienza esaurita su una dipendenza esterna:
/// circuito aperto, retry finiti o timeout) in <b>503 Service Unavailable</b> come <c>application/problem+json</c>
/// (RFC 7807), con header <c>Retry-After</c>. Così un guasto <i>a valle</i> non diventa un 500 opaco del nostro
/// servizio: il client capisce che è temporaneo e quando ritentare.
///
/// <para>Registrato <b>prima</b> del <see cref="GlobalExceptionHandler"/> (catch-all 500) e dopo il
/// <see cref="PreconditionExceptionHandler"/>. Scrive via <see cref="IProblemDetailsService"/> → il body passa
/// per <c>CustomizeProblemDetails</c>/<see cref="ProblemDetailsEnricher"/> e riceve <c>correlationId</c>/<c>traceId</c>
/// come ogni altro errore (DRY). Il <c>Detail</c> è generico (nessun dettaglio dell'upstream → niente info-leak).</para>
/// </summary>
public sealed class ExternalServiceUnavailableExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    // Suggerimento di attesa quando l'eccezione non ne porta uno (es. circuito aperto / timeout).
    private static readonly TimeSpan RetryAfterFallback = TimeSpan.FromSeconds(10);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ExternalServiceUnavailableException ex)
            return false; // Non di nostra competenza → prova il prossimo handler (GlobalExceptionHandler).

        var retryAfter = ex.RetryAfter is { } hint && hint > TimeSpan.Zero ? hint : RetryAfterFallback;
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Service Unavailable",
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.4",
                Detail = $"The '{ex.ServiceName}' dependency is temporarily unavailable. Please retry later.",
            },
        });
    }
}
