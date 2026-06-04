using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Proietta nel contratto OpenAPI il supporto all'idempotency dei POST, così è parte leggibile
/// dell'API specification e non un comportamento implicito nel middleware: l'header di richiesta
/// opzionale <c>Idempotency-Key</c>, l'header di risposta <c>Idempotency-Replayed</c> sulle risposte
/// 2xx e la risposta <c>422</c> per il riuso della chiave con un payload diverso.
/// Allineato a <see cref="WebApiPlayground.Api.Middleware.IdempotencyMiddleware"/> — vedi
/// <c>.claude/context/idempotency.md</c>.
/// </summary>
public sealed class IdempotencyOperationTransformer : IOpenApiOperationTransformer
{
    private const string RequestHeader = "Idempotency-Key";
    private const string ReplayedHeader = "Idempotency-Replayed";

    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // L'idempotency è onorata sui POST (come il middleware): documentala solo lì.
        if (!string.Equals(context.Description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = RequestHeader,
            In = ParameterLocation.Header,
            Required = false,
            Description =
                "Chiave unica (es. UUID) per rendere idempotente la create. Se la richiesta viene " +
                $"ritentata con la stessa chiave, il server rigioca la prima risposta (header '{ReplayedHeader}: true') " +
                "senza creare un duplicato. La stessa chiave con un payload diverso → 422.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });

        operation.Responses ??= new OpenApiResponses();
        operation.Responses["422"] = new OpenApiResponse
        {
            Description = $"La '{RequestHeader}' è già stata usata per una richiesta con un payload diverso.",
        };

        // Documenta l'header di risposta sul replay, sulle risposte di successo già dichiarate.
        foreach (var (statusCode, response) in operation.Responses)
        {
            if (!statusCode.StartsWith('2') || response is not OpenApiResponse concrete)
                continue;

            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
            concrete.Headers[ReplayedHeader] = new OpenApiHeader
            {
                Description = "Presente e 'true' quando la risposta è il replay di una richiesta già elaborata con la stessa Idempotency-Key.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean },
            };
        }

        return Task.CompletedTask;
    }
}
