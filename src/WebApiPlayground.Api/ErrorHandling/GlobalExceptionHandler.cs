using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace WebApiPlayground.Api.ErrorHandling;

/// <summary>
/// Trasforma qualunque eccezione non gestita in una risposta <c>application/problem+json</c>
/// (ProblemDetails, RFC 7807) con status 500, invece di lasciare trapelare uno stack trace grezzo.
/// L'eccezione è loggata una sola volta qui (livello <c>Error</c>); l'arricchimento con
/// <c>correlationId</c>/<c>traceId</c> è centralizzato in <c>CustomizeProblemDetails</c>
/// (vedi <see cref="Extensions.ErrorHandlingExtensions"/>), così ogni ProblemDetails è coerente.
/// I dettagli dell'eccezione finiscono nel body solo in Development.
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception,
            "Unhandled exception processing {RequestMethod} {RequestPath}",
            httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "An unexpected error occurred.",
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.6.1",
            // Mai esporre il messaggio dell'eccezione in produzione (info leak); solo in dev.
            Detail = environment.IsDevelopment() ? exception.Message : null,
        };

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails,
        });
    }
}
