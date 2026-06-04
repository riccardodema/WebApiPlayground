using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Application.Concurrency;

namespace WebApiPlayground.Api.ErrorHandling;

/// <summary>
/// Traduce gli errori di <b>precondizione / concorrenza</b> in <c>application/problem+json</c>
/// (RFC 7807), prima del <see cref="GlobalExceptionHandler"/> (che cattura tutto il resto come 500):
///
/// <list type="bullet">
/// <item><see cref="ConcurrencyConflictException"/> (dal repository, traducendo la
/// <c>DbUpdateConcurrencyException</c> di EF Core) → <b>412 Precondition Failed</b>: il token
/// <c>If-Match</c> è stale, la risorsa è cambiata sotto i piedi del client.</item>
/// <item><see cref="PreconditionException"/> (dal controller) → <b>428</b> (If-Match assente) o
/// <b>400</b> (If-Match malformato).</item>
/// </list>
///
/// <para>Scrive via <see cref="IProblemDetailsService"/> così il body passa per
/// <c>CustomizeProblemDetails</c> e riceve <c>correlationId</c>/<c>traceId</c> come ogni altro errore.
/// I messaggi sono di tipo client-error (non sensibili) → inclusi anche in produzione. Restituendo
/// <c>false</c> per eccezioni non di sua competenza, lascia proseguire la catena di handler.</para>
/// </summary>
public sealed class PreconditionExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ConcurrencyConflictException ex => Build(
                StatusCodes.Status412PreconditionFailed, "Precondition Failed",
                "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.13", ex.Message),
            PreconditionException ex => Build(
                ex.StatusCode, ex.ProblemTitle, TypeFor(ex.StatusCode), ex.Message),
            _ => null,
        };

        if (problem is null)
            return false; // Non di nostra competenza → prova il prossimo handler (GlobalExceptionHandler).

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problem,
        });
    }

    private static ProblemDetails Build(int status, string title, string type, string detail) =>
        new() { Status = status, Title = title, Type = type, Detail = detail };

    private static string TypeFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status428PreconditionRequired => "https://datatracker.ietf.org/doc/html/rfc6585#section-3",
        _ => "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1", // 400 Bad Request
    };
}
