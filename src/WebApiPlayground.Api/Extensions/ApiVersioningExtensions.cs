using Asp.Versioning;
using WebApiPlayground.Api.OpenApi;
using WebApiPlayground.Api.Versioning;

namespace WebApiPlayground.Api.Extensions;

/// <summary>
/// Registra l'API versioning (Asp.Versioning) per <b>segmento URL</b> (<c>/api/v{version}/...</c>) e i
/// documenti OpenAPI nativi per versione. L'<c>ApiExplorer</c> assegna a ogni operazione un
/// <c>GroupName</c> (<c>"v1"</c>/<c>"v2"</c>), e il documento OpenAPI nativo dello stesso nome include
/// solo le operazioni di quella versione — così Scalar mostra un documento per versione, con i
/// transformer dell'API condivisi (<see cref="OpenApiTransformerRegistration.AddPlaygroundTransformers"/>).
/// Vedi <c>.claude/context/api-versioning.md</c>.
/// </summary>
public static class ApiVersioningExtensions
{
    public static IServiceCollection AddApiVersioningWithOpenApi(this IServiceCollection services)
    {
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(ApiVersions.V1);
            // La versione è esplicita nel segmento URL: non si assume un default quando manca.
            options.AssumeDefaultVersionWhenUnspecified = false;
            // Emette gli header api-supported-versions / api-deprecated-versions su ogni risposta.
            options.ReportApiVersions = true;
            options.ApiVersionReader = new UrlSegmentApiVersionReader();
        })
        .AddMvc()
        .AddApiExplorer(options =>
        {
            // 1.0 → "v1": allineato a ApiVersions.GroupName (nome del documento OpenAPI per versione).
            options.GroupNameFormat = "'v'VVV";
            // Sostituisce il token {version} nelle rotte mostrate nello spec.
            options.SubstituteApiVersionInUrl = true;
        });

        // Un documento OpenAPI nativo per versione. ApiVersions.All è l'unica fonte delle versioni.
        foreach (var version in ApiVersions.All)
            services.AddOpenApi(ApiVersions.GroupName(version), options => options.AddPlaygroundTransformers());

        return services;
    }
}
