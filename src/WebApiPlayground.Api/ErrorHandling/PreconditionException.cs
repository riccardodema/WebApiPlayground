using Microsoft.AspNetCore.Http;

namespace WebApiPlayground.Api.ErrorHandling;

/// <summary>
/// Errore di <b>precondizione HTTP</b> (RFC 9110/6585) sollevato dal controller quando l'header
/// <c>If-Match</c> richiesto sulle scritture manca o è malformato. Porta lo status code da restituire,
/// così <see cref="PreconditionExceptionHandler"/> lo traduce in ProblemDetails con un punto unico di
/// arricchimento (correlationId/traceId). Il conflitto di concorrenza vero (412) è invece segnalato
/// dal repository con <c>ConcurrencyConflictException</c>.
/// </summary>
public sealed class PreconditionException : Exception
{
    public int StatusCode { get; }
    public string ProblemTitle { get; }

    private PreconditionException(int statusCode, string title, string message) : base(message)
    {
        StatusCode = statusCode;
        ProblemTitle = title;
    }

    /// <summary><c>If-Match</c> assente su una scrittura che lo richiede → <b>428 Precondition Required</b>.</summary>
    public static PreconditionException Required() =>
        new(StatusCodes.Status428PreconditionRequired, "Precondition Required",
            "This write requires an 'If-Match' header carrying the current ETag of the resource. " +
            "GET the resource first to obtain its ETag, then retry with If-Match.");

    /// <summary><c>If-Match</c> presente ma non un ETag strong valido → <b>400 Bad Request</b>.</summary>
    public static PreconditionException MalformedIfMatch() =>
        new(StatusCodes.Status400BadRequest, "Invalid If-Match header",
            "The 'If-Match' header must carry a valid strong ETag (a quoted token) obtained from a " +
            "previous response to this resource.");
}
