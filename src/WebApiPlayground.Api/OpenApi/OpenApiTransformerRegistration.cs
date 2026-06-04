using Microsoft.AspNetCore.OpenApi;

namespace WebApiPlayground.Api.OpenApi;

/// <summary>
/// Registrazione condivisa dei transformer OpenAPI dell'API, applicata a <b>ogni</b> documento di
/// versione (DRY): così v1 e v2 espongono lo stesso contratto trasversale (auth, validazione,
/// idempotency, caching, rate limiting, versioning) senza duplicare la configurazione per documento.
/// Vedi <c>.claude/context/api-versioning.md</c>.
/// </summary>
public static class OpenApiTransformerRegistration
{
    public static OpenApiOptions AddPlaygroundTransformers(this OpenApiOptions options)
    {
        options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        // Proietta le regole FluentValidation nello schema (required/maxLength/minimum + descrizione).
        options.AddSchemaTransformer<FluentValidationSchemaTransformer>();
        // Documenta idempotency dei POST (Idempotency-Key/Replayed + 422).
        options.AddOperationTransformer<IdempotencyOperationTransformer>();
        // Documenta HTTP caching dei GET (ETag/Cache-Control/If-None-Match + 304).
        options.AddOperationTransformer<CachingOperationTransformer>();
        // Documenta l'optimistic concurrency delle scritture (If-Match + 412/428).
        options.AddOperationTransformer<ConcurrencyOperationTransformer>();
        // Documenta il rate limiting (429 ProblemDetails + Retry-After).
        options.AddOperationTransformer<RateLimitingOperationTransformer>();
        // Documenta gli header di scoperta versioni (api-supported-versions / api-deprecated-versions).
        options.AddOperationTransformer<ApiVersioningOperationTransformer>();
        return options;
    }
}
