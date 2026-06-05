using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Rende leggibile nel contratto OpenAPI la semantica di <b>resilienza</b> verso le dipendenze esterne: le
/// operazioni che dichiarano un <c>503</c> (via <c>[ProducesResponseType(503)]</c>, oggi la sotto-risorsa
/// <c>popularity</c>) ricevono una descrizione esplicita + l'header <c>Retry-After</c>, esattamente come il 429
/// del rate limiting. Si applica <b>solo</b> dove il 503 è già dichiarato → non sporca gli altri endpoint.
/// Allineato a <see cref="WebApiPlayground.Api.ErrorHandling.ExternalServiceUnavailableExceptionHandler"/> —
/// vedi <c>.claude/context/resilience.md</c>.
/// </summary>
public sealed class ResilienceOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // Solo gli endpoint che dipendono da una risorsa esterna dichiarano un 503: arricchiamo quello, senza
        // toccare le altre operazioni. La response generata da [ProducesResponseType] è un OpenApiResponse
        // concreto (l'interfaccia espone Headers in sola lettura).
        if (operation.Responses is null
            || !operation.Responses.TryGetValue("503", out var existing)
            || existing is not OpenApiResponse response)
        {
            return Task.CompletedTask;
        }

        response.Description =
            "La dipendenza esterna è temporaneamente indisponibile (la pipeline di resilienza si è esaurita: " +
            "circuito aperto, retry finiti o timeout). Il body è un ProblemDetails (RFC 7807); l'header " +
            "'Retry-After' indica dopo quanti secondi ritentare.";

        response.Headers ??= new Dictionary<string, IOpenApiHeader>();
        response.Headers["Retry-After"] = new OpenApiHeader
        {
            Description = "Secondi da attendere prima di ritentare la richiesta.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
        };

        return Task.CompletedTask;
    }
}
