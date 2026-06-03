using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.HealthChecks;

[Collection("Integration")]
public class HealthCheckTests
{
    private readonly PlaygroundApiFactory _factory;

    public HealthCheckTests(PlaygroundApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Liveness_IsReachableAnonymously_AndReportsHealthy()
    {
        // I probe non passano auth: devono rispondere senza token (no 401).
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", report.GetProperty("status").GetString());
        // Liveness non esegue check di dipendenza: la lista deve essere vuota.
        Assert.Empty(report.GetProperty("checks").EnumerateArray());
    }

    [Fact]
    public async Task Readiness_WhenDatabaseReachable_ReportsHealthyWithDatabaseCheck()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", report.GetProperty("status").GetString());

        // Readiness esegue il check del DB (tagged "ready"): deve comparire la entry "database".
        var checkNames = report.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToArray();
        Assert.Contains("database", checkNames);
    }

    [Fact]
    public async Task Readiness_ResponseCarriesCorrelationIdMatchingHeader()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/health/ready");

        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        var headerCorrelationId = response.Headers.GetValues(
            Api.Middleware.CorrelationIdMiddleware.HeaderName).Single();
        Assert.Equal(headerCorrelationId, report.GetProperty("correlationId").GetString());
    }
}
