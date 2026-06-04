using System.Text.Json;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.OpenApi;

/// <summary>
/// Il contratto tra client e API dev'essere leggibile nello spec OpenAPI: qui si verifica che i
/// comportamenti guidati da header — idempotency sui POST (<c>Idempotency-Key</c> + 422) e HTTP
/// caching sui GET (<c>If-None-Match</c>/<c>ETag</c> + 304) — siano effettivamente documentati,
/// non solo implementati nel middleware/filter.
/// </summary>
[Collection("Integration")]
public class OpenApiContractTests
{
    private readonly PlaygroundApiFactory _factory;

    public OpenApiContractTests(PlaygroundApiFactory factory) => _factory = factory;

    private async Task<JsonDocument> GetOpenApiDocumentAsync()
    {
        var json = await _factory.CreateClient().GetStringAsync("/openapi/v1.json");
        return JsonDocument.Parse(json);
    }

    /// <summary>Operazioni (di un dato verbo) presenti nel documento, tra tutti i path.</summary>
    private static IEnumerable<JsonElement> Operations(JsonDocument document, string httpVerb) =>
        document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Where(path => path.Value.TryGetProperty(httpVerb, out _))
            .Select(path => path.Value.GetProperty(httpVerb));

    private static bool HasHeaderParameter(JsonElement operation, string name) =>
        operation.TryGetProperty("parameters", out var parameters) &&
        parameters.EnumerateArray().Any(p =>
            p.GetProperty("name").GetString() == name && p.GetProperty("in").GetString() == "header");

    [Fact]
    public async Task OpenApi_DocumentsIdempotencyKeyHeaderAnd422_OnPost()
    {
        using var document = await GetOpenApiDocumentAsync();

        var post = Operations(document, "post").FirstOrDefault(p => HasHeaderParameter(p, "Idempotency-Key"));

        Assert.True(post.ValueKind == JsonValueKind.Object,
            "Nessuna operazione POST documenta l'header 'Idempotency-Key' nello spec OpenAPI.");
        Assert.True(post.GetProperty("responses").TryGetProperty("422", out _),
            "L'operazione POST non documenta la risposta 422 dell'idempotency.");
    }

    [Fact]
    public async Task OpenApi_DocumentsRateLimiting429_WithRetryAfter_OnAllOperations()
    {
        using var document = await GetOpenApiDocumentAsync();

        var operations = new[] { "get", "post", "put", "delete" }
            .SelectMany(verb => Operations(document, verb))
            .ToList();

        Assert.NotEmpty(operations);
        foreach (var operation in operations)
        {
            Assert.True(operation.GetProperty("responses").TryGetProperty("429", out var response),
                "Un'operazione non documenta la risposta 429 del rate limiting nello spec OpenAPI.");
            Assert.True(response.GetProperty("headers").TryGetProperty("Retry-After", out _),
                "La 429 non documenta l'header di risposta 'Retry-After'.");
        }
    }

    [Fact]
    public async Task OpenApi_DocumentsConditionalGetContract_OnGet()
    {
        using var document = await GetOpenApiDocumentAsync();

        var get = Operations(document, "get").FirstOrDefault(g => HasHeaderParameter(g, "If-None-Match"));

        Assert.True(get.ValueKind == JsonValueKind.Object,
            "Nessuna operazione GET documenta l'header 'If-None-Match' nello spec OpenAPI.");
        Assert.True(get.GetProperty("responses").TryGetProperty("304", out _),
            "L'operazione GET non documenta la risposta 304 Not Modified.");

        // La 200 espone l'header di risposta ETag.
        var okHeaders = get.GetProperty("responses").GetProperty("200").GetProperty("headers");
        Assert.True(okHeaders.TryGetProperty("ETag", out _), "La 200 non documenta l'header di risposta 'ETag'.");
    }
}
