using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.ErrorHandling;

[Collection("Integration")]
public class GlobalExceptionHandlerTests
{
    private readonly PlaygroundApiFactory _factory;

    public GlobalExceptionHandlerTests(PlaygroundApiFactory factory) => _factory = factory;

    [Fact]
    public async Task UnhandledException_Returns500AsProblemJson()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/__tests__/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UnhandledException_ProblemDetailsCarriesCorrelationIdMatchingHeader()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/__tests__/throw");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));

        // Il correlationId nel body deve combaciare con l'header di risposta: log↔risposta correlabili.
        var headerCorrelationId = response.Headers.GetValues(
            Api.Middleware.CorrelationIdMiddleware.HeaderName).Single();
        Assert.Equal(headerCorrelationId, problem.GetProperty("correlationId").GetString());
    }
}
