using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.Timeout;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Infrastructure.Popularity;
using Xunit;

namespace WebApiPlayground.Tests.Popularity;

/// <summary>
/// Esercita la <b>pipeline di resilienza reale</b> (quella registrata da <see cref="BookPopularityRegistration"/>,
/// la stessa che gira in produzione) sostituendo solo il <i>primary handler</i> con uno stub programmabile:
/// così si verifica il comportamento di retry / circuit-breaker / timeout senza toccare la rete. I knob sono
/// minuscoli (config in-memory) per scatenare gli scenari in fretta. Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
public class PopularityResiliencePipelineTests
{
    /// <summary>Config di base con knob piccoli; il circuit breaker è "disattivato di fatto"
    /// (MinimumThroughput altissimo) così i test di retry/timeout non lo fanno scattare per sbaglio.</summary>
    private static Dictionary<string, string?> BaseConfig() => new()
    {
        ["BookPopularity:BaseAddress"] = "https://openlibrary.test",
        ["BookPopularity:Resilience:AttemptTimeout"] = "00:00:00.200",
        ["BookPopularity:Resilience:TotalTimeout"] = "00:00:10",
        ["BookPopularity:Resilience:Retry:MaxRetryAttempts"] = "2",
        ["BookPopularity:Resilience:Retry:BaseDelay"] = "00:00:00.001",
        ["BookPopularity:Resilience:CircuitBreaker:FailureRatio"] = "0.5",
        ["BookPopularity:Resilience:CircuitBreaker:SamplingDuration"] = "00:00:30",
        ["BookPopularity:Resilience:CircuitBreaker:MinimumThroughput"] = "1000",
        ["BookPopularity:Resilience:CircuitBreaker:BreakDuration"] = "00:00:10",
    };

    private static IBookPopularityClient BuildClient(
        StubHttpMessageHandler handler, Action<Dictionary<string, string?>>? tweak = null)
    {
        var dict = BaseConfig();
        tweak?.Invoke(dict);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBookPopularityClient(configuration); // registrazione REALE (pipeline inclusa)
        services.AddHttpClient<IBookPopularityClient, OpenLibraryPopularityClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler); // ultimo wins → intercetta la rete

        return services.BuildServiceProvider().GetRequiredService<IBookPopularityClient>();
    }

    [Fact]
    public async Task Retries_TransientFailures_ThenSucceeds()
    {
        // 503, 503, 200 → con MaxRetryAttempts=2 (3 tentativi) l'ultimo va a buon fine.
        var handler = StubHttpMessageHandler.Sequence(
            HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var client = BuildClient(handler);

        var result = await client.GetPopularityAsync("Dune", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(4.5, result!.AverageRating);
        Assert.Equal(3, handler.Invocations); // 1 iniziale + 2 retry
    }

    [Fact]
    public async Task Throws_WhenRetriesAreExhausted()
    {
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        Assert.Equal(3, handler.Invocations); // 1 + 2 retry, poi si arrende
    }

    [Fact]
    public async Task DoesNotRetry_NonTransient4xx()
    {
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.BadRequest);
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        Assert.Equal(1, handler.Invocations); // i 4xx NON sono transitori → nessun retry
    }

    [Fact]
    public async Task PerAttemptTimeout_FiresOnSlowResponse()
    {
        // Ogni tentativo ritarda 2s ma l'AttemptTimeout è 200ms → ogni tentativo va in TimeoutRejectedException.
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.OK, delay: TimeSpan.FromSeconds(2));
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        Assert.IsType<TimeoutRejectedException>(ex.InnerException);
        Assert.True(handler.Invocations >= 1);
    }

    [Fact]
    public async Task TotalTimeout_CapsTheWholeSequence()
    {
        // TotalTimeout piccolo (100ms) + risposta lenta (2s): il timeout TOTALE (outermost) taglia subito,
        // prima che l'AttemptTimeout (5s) o i retry entrino in gioco → una sola invocazione.
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.OK, delay: TimeSpan.FromSeconds(2));
        var client = BuildClient(handler, dict =>
        {
            dict["BookPopularity:Resilience:TotalTimeout"] = "00:00:00.100";
            dict["BookPopularity:Resilience:AttemptTimeout"] = "00:00:05";
        });

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        Assert.IsType<TimeoutRejectedException>(ex.InnerException);
        Assert.Equal(1, handler.Invocations);
    }

    [Fact]
    public async Task CircuitBreaker_OpensAndFailsFast_WithoutHittingTransport()
    {
        // MinimumThroughput=2 + 1 retry (2 tentativi per chiamata): la prima chiamata registra 2 fallimenti
        // → il circuito si apre. La seconda chiamata fa fail-fast (BrokenCircuitException) SENZA toccare il transport.
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.ServiceUnavailable);
        var client = BuildClient(handler, dict =>
        {
            dict["BookPopularity:Resilience:CircuitBreaker:MinimumThroughput"] = "2";
            dict["BookPopularity:Resilience:Retry:MaxRetryAttempts"] = "1";
        });

        // Call 1: apre il circuito (2 tentativi falliti).
        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));
        var invocationsAfterFirstCall = handler.Invocations;
        Assert.Equal(2, invocationsAfterFirstCall);

        // Call 2: circuito aperto → fail-fast, il transport NON viene colpito.
        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => client.GetPopularityAsync("Dune", null, CancellationToken.None));

        Assert.IsType<BrokenCircuitException>(ex.InnerException);
        Assert.Equal(invocationsAfterFirstCall, handler.Invocations); // nessuna nuova invocazione
    }

    [Fact]
    public async Task ReturnsNull_WhenUpstreamHasNoMatch()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Ok(StubHttpMessageHandler.NoMatchJson));
        var client = BuildClient(handler);

        var result = await client.GetPopularityAsync("No Such Book", null, CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, handler.Invocations);
    }
}
