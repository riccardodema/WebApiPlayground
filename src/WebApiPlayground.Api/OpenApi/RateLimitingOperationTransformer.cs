using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Proietta nel contratto OpenAPI il rate limiting, così è parte leggibile dell'API specification e
/// non un comportamento implicito nel middleware: ogni operazione può restituire <c>429 Too Many
/// Requests</c> (ProblemDetails RFC 7807) con l'header <c>Retry-After</c> che indica tra quanti
/// secondi ritentare. Si documenta solo ciò che è accurato: <c>Retry-After</c> è emesso nativamente
/// dalla sliding window. Allineato a
/// <see cref="WebApiPlayground.Api.Extensions.RateLimitingExtensions"/> — vedi
/// <c>.claude/context/rate-limiting.md</c>.
/// </summary>
public sealed class RateLimitingOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        operation.Responses ??= new OpenApiResponses();
        operation.Responses["429"] = new OpenApiResponse
        {
            Description =
                "Troppe richieste: il client ha superato il rate limit della sua partizione " +
                "(utente autenticato o IP). Il body è un ProblemDetails (RFC 7807); l'header " +
                "'Retry-After' indica dopo quanti secondi ritentare.",
            Headers = new Dictionary<string, IOpenApiHeader>
            {
                ["Retry-After"] = new OpenApiHeader
                {
                    Description = "Secondi da attendere prima di ritentare la richiesta.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
                },
            },
        };

        return Task.CompletedTask;
    }
}
