using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Api.ErrorHandling;

namespace WebApiPlayground.Api.Validation;

/// <summary>
/// Costruisce la risposta 400 per gli errori di validazione: un <see cref="ValidationProblemDetails"/>
/// (RFC 7807) con i campi invalidi nella proprietà <c>errors</c> e messaggi "parlanti".
/// È il punto unico usato sia per le violazioni di model binding/DataAnnotations
/// (via <c>InvalidModelStateResponseFactory</c>) sia per le violazioni FluentValidation
/// (via <c>ValidationFilter</c>): un'unica forma d'errore <c>application/problem+json</c>.
/// </summary>
public static class ValidationProblemDetailsFactory
{
    public static IActionResult Create(ActionContext context)
    {
        var problemDetails = new ValidationProblemDetails(context.ModelState)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred.",
            Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.1",
            Detail = "See the 'errors' property for the fields that failed validation and how to fix them.",
            Instance = context.HttpContext.Request.Path,
        };

        return new ValidationProblemDetailsResult(problemDetails);
    }

    /// <summary>
    /// Serializza il <see cref="ValidationProblemDetails"/> sul suo tipo concreto (così la mappa
    /// <c>errors</c> per campo è preservata) come <c>application/problem+json</c>, dopo averlo
    /// arricchito con correlationId/traceId tramite <see cref="ProblemDetailsEnricher"/>.
    /// Non si usa <see cref="IProblemDetailsService"/> perché serializza sul tipo statico
    /// <see cref="ProblemDetails"/> e scarterebbe la proprietà <c>errors</c>.
    /// </summary>
    private sealed class ValidationProblemDetailsResult(ValidationProblemDetails problemDetails) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            var http = context.HttpContext;
            http.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status400BadRequest;

            ProblemDetailsEnricher.Enrich(http, problemDetails);

            return http.Response.WriteAsJsonAsync(
                problemDetails, problemDetails.GetType(), options: null, contentType: "application/problem+json");
        }
    }
}
