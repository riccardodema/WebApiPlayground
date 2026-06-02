using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Dichiara nello schema OpenAPI uno schema di sicurezza HTTP Bearer, così la UI Scalar
/// mostra il campo "Authentication" e invia l'header <c>Authorization: Bearer &lt;token&gt;</c>.
/// </summary>
public sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    private const string SchemeId = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var scheme = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = ParameterLocation.Header,
            Description = "Inserisci un access token Entra ID (senza il prefisso 'Bearer ').",
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeId] = scheme;

        var requirement = new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(SchemeId, document, null)] = [],
        };
        document.Security ??= new List<OpenApiSecurityRequirement>();
        document.Security.Add(requirement);

        return Task.CompletedTask;
    }
}
