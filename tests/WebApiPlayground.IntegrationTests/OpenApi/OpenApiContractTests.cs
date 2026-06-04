using System.Text.Json;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.OpenApi;

/// <summary>
/// Il contratto tra client e API dev'essere leggibile nello spec OpenAPI: qui si verifica che il
/// supporto all'idempotency (header <c>Idempotency-Key</c> + risposta 422) sia effettivamente
/// documentato sul POST, non solo implementato nel middleware.
/// </summary>
[Collection("Integration")]
public class OpenApiContractTests
{
    private readonly PlaygroundApiFactory _factory;

    public OpenApiContractTests(PlaygroundApiFactory factory) => _factory = factory;

    [Fact]
    public async Task OpenApi_DocumentsIdempotencyKeyHeaderAnd422_OnPost()
    {
        var client = _factory.CreateClient();

        var json = await client.GetStringAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(json);

        // Cerca un'operazione POST che dichiari l'header Idempotency-Key (robusto al casing del path).
        var postWithIdempotency = document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Value)
            .Where(operations => operations.TryGetProperty("post", out _))
            .Select(operations => operations.GetProperty("post"))
            .FirstOrDefault(post =>
                post.TryGetProperty("parameters", out var parameters) &&
                parameters.EnumerateArray().Any(p =>
                    p.GetProperty("name").GetString() == "Idempotency-Key" &&
                    p.GetProperty("in").GetString() == "header"));

        Assert.True(postWithIdempotency.ValueKind == JsonValueKind.Object,
            "Nessuna operazione POST documenta l'header 'Idempotency-Key' nello spec OpenAPI.");

        // La stessa operazione deve documentare la risposta 422 (riuso chiave con payload diverso).
        Assert.True(postWithIdempotency.GetProperty("responses").TryGetProperty("422", out _),
            "L'operazione POST non documenta la risposta 422 dell'idempotency.");
    }
}
