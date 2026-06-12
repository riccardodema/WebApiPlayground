using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Infrastructure.Popularity;
using Xunit;

namespace WebApiPlayground.Tests.Popularity;

/// <summary>
/// Rami "di bordo" di <see cref="OpenLibraryPopularityClient"/> non coperti dai test della pipeline:
/// input vuoto (nessuna chiamata di rete), fallimenti a livello transport / timeout / corpo
/// non interpretabile (tutti → <see cref="ExternalServiceUnavailableException"/>) e la lettura
/// dell'header <c>Retry-After</c> nelle sue tre forme (delta, http-date, assente).
/// </summary>
public class OpenLibraryPopularityClientEdgeTests
{
    /// <summary>Stessa registrazione REALE dei test di pipeline; retry quasi azzerati per andare veloci.</summary>
    private static OpenLibraryPopularityClient BuildClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BookPopularity:BaseAddress"] = "https://openlibrary.test",
            ["BookPopularity:Resilience:AttemptTimeout"] = "00:00:00.200",
            ["BookPopularity:Resilience:TotalTimeout"] = "00:00:05",
            ["BookPopularity:Resilience:Retry:MaxRetryAttempts"] = "1",
            ["BookPopularity:Resilience:Retry:BaseDelay"] = "00:00:00.001",
            ["BookPopularity:Resilience:CircuitBreaker:FailureRatio"] = "0.5",
            ["BookPopularity:Resilience:CircuitBreaker:SamplingDuration"] = "00:00:30",
            ["BookPopularity:Resilience:CircuitBreaker:MinimumThroughput"] = "1000",
            ["BookPopularity:Resilience:CircuitBreaker:BreakDuration"] = "00:00:10",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBookPopularityClient(configuration);
        services.AddHttpClient<OpenLibraryPopularityClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<OpenLibraryPopularityClient>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_title_short_circuits_to_null_without_touching_the_network(string title)
    {
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.OK);
        var client = BuildClient(handler);

        var result = await client.GetPopularityAsync(title, null, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, handler.Invocations);
    }

    [Fact]
    public async Task Transport_level_failure_maps_to_service_unavailable()
    {
        // Il responder lancia: equivale a DNS/socket failure. La pipeline ritenta, poi il client la
        // traduce nell'eccezione di dominio (→ 503 con ProblemDetails per il chiamante HTTP).
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("socket closed"));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));
    }

    [Fact]
    public async Task Attempt_timeouts_exhausted_map_to_service_unavailable()
    {
        // Ogni tentativo supera l'AttemptTimeout (200ms) → TimeoutRejectedException esaurita la pipeline.
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.OK, delay: TimeSpan.FromSeconds(2));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));
    }

    [Fact]
    public async Task Unparsable_2xx_body_maps_to_service_unavailable()
    {
        // 200 con corpo non-JSON: problema dell'upstream, non del client → 503, non un 500 nostro.
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Ok("definitely-not-json"));
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));
    }

    // NB: la strategy di retry HTTP onora Retry-After come delay tra i tentativi → valori piccoli,
    // altrimenti il TotalTimeout scatta PRIMA che l'ultima risposta (con l'header) torni al client.

    [Fact]
    public async Task Retry_after_delta_header_is_propagated()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = StubHttpMessageHandler.Build(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return response;
        });
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(1), ex.RetryAfter);
    }

    [Fact]
    public async Task Retry_after_http_date_header_is_converted_to_a_delta()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = StubHttpMessageHandler.Build(HttpStatusCode.ServiceUnavailable);
            // Ricreata a ogni tentativo → la data resta ~1s nel futuro anche sull'ultima risposta.
            response.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(1));
            return response;
        });
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        // date - UtcNow: un delta positivo ≤ 1s (il tempo di propagare l'eccezione lo erode un po').
        Assert.NotNull(ex.RetryAfter);
        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Missing_retry_after_header_yields_null()
    {
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        Assert.Null(ex.RetryAfter);
    }
}
