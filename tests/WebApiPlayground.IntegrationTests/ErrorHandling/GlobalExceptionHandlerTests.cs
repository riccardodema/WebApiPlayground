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

    [Fact]
    public async Task UnhandledException_ProblemDetailsCarriesW3CTraceId()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/__tests__/throw");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var traceId = problem.GetProperty("traceId").GetString();

        // Con OpenTelemetry attivo l'AspNetCore instrumentation popola Activity.Current: il traceId del
        // ProblemDetails (ProblemDetailsEnricher) è quindi l'Activity.Id in formato W3C trace-context
        // ("00-<32 hex traceId>-<16 hex spanId>-<2 hex flags>", 55 char) — la trace si salda all'errore.
        Assert.NotNull(traceId);
        Assert.StartsWith("00-", traceId);
        Assert.Equal(55, traceId!.Length);
    }
}
