using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Proietta nel contratto OpenAPI gli header di scoperta delle versioni emessi da
/// <c>ReportApiVersions</c>: <c>api-supported-versions</c> (e <c>api-deprecated-versions</c>) su ogni
/// risposta, così il client sa quali versioni esistono e quando una è deprecata — senza doverlo
/// dedurre. Allineato a <see cref="WebApiPlayground.Api.Extensions.ApiVersioningExtensions"/> — vedi
/// <c>.claude/context/api-versioning.md</c>.
/// </summary>
public sealed class ApiVersioningOperationTransformer : IOpenApiOperationTransformer
{
    private const string SupportedVersionsHeader = "api-supported-versions";
    private const string DeprecatedVersionsHeader = "api-deprecated-versions";

    public Task TransformAsync(
        OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        operation.Responses ??= new OpenApiResponses();

        foreach (var (_, response) in operation.Responses)
        {
            if (response is not OpenApiResponse concrete)
                continue;

            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
            concrete.Headers[SupportedVersionsHeader] = new OpenApiHeader
            {
                Description =
                    "Versioni dell'API supportate da questo endpoint (es. \"1.0, 2.0\"), emesse da " +
                    "ReportApiVersions: il client può scoprire le versioni disponibili da qualunque risposta.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
            concrete.Headers[DeprecatedVersionsHeader] = new OpenApiHeader
            {
                Description =
                    "Versioni deprecate, se presenti. Una versione deprecata può accompagnarsi agli " +
                    "header 'Sunset'/'Link' (RFC 8594) con la data di ritiro e il link alla policy.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            };
        }

        return Task.CompletedTask;
    }
}
