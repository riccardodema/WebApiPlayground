using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Proietta nel contratto OpenAPI l'HTTP caching dei GET, così le richieste condizionali sono parte
/// leggibile dell'API specification e non un comportamento implicito nel
/// <see cref="WebApiPlayground.Api.Http.ETagResultFilter"/>: gli header di risposta <c>ETag</c> e
/// <c>Cache-Control</c> sulle 200, l'header di richiesta <c>If-None-Match</c> e la risposta
/// <c>304 Not Modified</c>. Vedi <c>.claude/context/caching.md</c>.
/// </summary>
public sealed class CachingOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // L'ETag/conditional GET è onorato sui GET (come il filter): documentalo solo lì.
        if (!string.Equals(context.Description.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "If-None-Match",
            In = ParameterLocation.Header,
            Required = false,
            Description =
                "ETag ottenuto da una risposta precedente. Se combacia con la rappresentazione " +
                "corrente, il server risponde 304 Not Modified senza body (richiesta condizionale).",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });

        operation.Responses ??= new OpenApiResponses();

        // Header di risposta ETag + Cache-Control sulla 200.
        if (operation.Responses.TryGetValue("200", out var okResponse) && okResponse is OpenApiResponse ok)
        {
            ok.Headers ??= new Dictionary<string, IOpenApiHeader>();
            ok.Headers["ETag"] = new OpenApiHeader
            {
                Description = "Impronta (strong) della rappresentazione, da rimandare in If-None-Match per le richieste condizionali.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
            ok.Headers["Cache-Control"] = new OpenApiHeader
            {
                Description = "Direttiva di caching: 'private, no-cache' — cacheabile solo dal client e da rivalidare sempre via ETag.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        // 304: risposta alla richiesta condizionale combaciante (nessun body).
        operation.Responses["304"] = new OpenApiResponse
        {
            Description = "La rappresentazione non è cambiata rispetto all'ETag inviato in If-None-Match: nessun body.",
        };

        return Task.CompletedTask;
    }
}
