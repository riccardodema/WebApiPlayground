using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Proietta nel contratto OpenAPI l'<b>optimistic concurrency</b> delle scritture mutanti (PUT/DELETE),
/// così la semantica <c>If-Match</c> → 412/428 è parte leggibile della specification e non un
/// comportamento implicito nel controller. Documenta l'header di richiesta <c>If-Match</c> (richiesto)
/// e le risposte <c>412 Precondition Failed</c> (ETag stale) e <c>428 Precondition Required</c>
/// (If-Match assente). Vedi <c>.claude/context/optimistic-concurrency.md</c>.
/// </summary>
public sealed class ConcurrencyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var method = context.Description.HttpMethod;
        var isConditionalWrite =
            string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase);

        if (!isConditionalWrite)
            return Task.CompletedTask;

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "If-Match",
            In = ParameterLocation.Header,
            Required = true,
            Description =
                "ETag corrente della risorsa, ottenuto da una GET (o dalla risposta a una scrittura " +
                "precedente). La scrittura procede solo se combacia con la versione attuale: protegge " +
                "dal lost update (optimistic concurrency). Header assente → 428; ETag stale → 412.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["412"] = new OpenApiResponse
        {
            Description =
                "Precondition Failed: l'ETag in If-Match è stale — la risorsa è stata modificata da " +
                "un'altra richiesta. Rileggi la risorsa (nuovo ETag) e riprova. ProblemDetails (RFC 7807).",
        };
        operation.Responses["428"] = new OpenApiResponse
        {
            Description =
                "Precondition Required: header If-Match mancante. Le scritture richiedono un update " +
                "condizionale. ProblemDetails (RFC 7807).",
        };

        return Task.CompletedTask;
    }
}
