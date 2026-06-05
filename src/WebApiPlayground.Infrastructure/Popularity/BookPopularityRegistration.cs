using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using WebApiPlayground.Application.Popularity;
using ZiggyCreatures.Caching.Fusion;

namespace WebApiPlayground.Infrastructure.Popularity;

/// <summary>
/// Registra il client esterno di popolarità come <b>HttpClient tipizzato</b> + la <b>pipeline di resilienza
/// esplicita</b> (Polly v8 via <c>Microsoft.Extensions.Http.Resilience</c>). Pattern identico alla cache:
/// l'astrazione (<see cref="IBookPopularityClient"/>) vive in Application, il concreto e la resilienza qui in
/// Infrastructure. Vedi <c>.claude/context/resilience.md</c> e <c>.claude/lessons.md</c> [L19].
/// </summary>
public static class BookPopularityRegistration
{
    /// <summary>Nome della pipeline (compare nella telemetria di resilienza).</summary>
    public const string PipelineName = "book-popularity";

    public static IServiceCollection AddBookPopularityClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BookPopularityOptions>(configuration.GetSection(BookPopularityOptions.SectionName));

        // Typed client CONCRETO (OpenLibraryPopularityClient): è il client resiliente "nudo". L'astrazione
        // IBookPopularityClient è poi il decoratore di caching che lo avvolge (sotto). Stesso schema di
        // BooksService (concreto) decorato da CachingBooksService. Vedi Application/DependencyInjection.cs.
        services.AddHttpClient<OpenLibraryPopularityClient>((serviceProvider, httpClient) =>
            {
                var options = serviceProvider.GetRequiredService<IOptionsMonitor<BookPopularityOptions>>().CurrentValue;

                httpClient.BaseAddress = new Uri(options.BaseAddress);

                // Il timeout lo governa la pipeline (per-tentativo + totale), NON HttpClient.Timeout: lasciarlo
                // al default (100s) sovrapporrebbe un timeout grezzo, non ritentabile, sopra la pipeline.
                // Infinite = "delego interamente alla resilienza". Vedi [L19].
                httpClient.Timeout = Timeout.InfiniteTimeSpan;

                // Bound difensivo sulla memoria: una risposta ostile/enorme non ci fa esplodere l'heap.
                httpClient.MaxResponseContentBufferSize = 1024 * 1024; // 1 MB

                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "WebApiPlayground/1.0 (didactic sample; +https://github.com)");
                httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            })
            // Overload con context: le opzioni si leggono LAZY dal service provider (post-build), così gli
            // override di WebApplicationFactory/test valgono — niente cattura eager alla registrazione. Vedi [L15]/[L19].
            .AddResilienceHandler(PipelineName, (builder, context) =>
            {
                var resilience = context.ServiceProvider
                    .GetRequiredService<IOptionsMonitor<BookPopularityOptions>>().CurrentValue.Resilience;

                // ORDINE outer → inner (conta!):
                //   1) Timeout TOTALE  — cappa l'intera sequenza retry (il chiamante non aspetta all'infinito).
                //   2) Retry           — backoff esponenziale + jitter, solo errori transitori (i 4xx NON si ritentano).
                //   3) Circuit breaker — se l'upstream è giù, apre e fa fail-fast (protegge noi e loro).
                //   4) Timeout TENTATIVO — il più interno: taglia il singolo HTTP call lento.
                builder
                    .AddTimeout(resilience.TotalTimeout)
                    .AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = resilience.Retry.MaxRetryAttempts,
                        Delay = resilience.Retry.BaseDelay,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        // ShouldHandle di default = HttpClientResiliencePredicates.IsTransient:
                        // 5xx, 408, 429, HttpRequestException, TimeoutRejectedException. I 4xx restano non ritentati.
                    })
                    .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                    {
                        FailureRatio = resilience.CircuitBreaker.FailureRatio,
                        SamplingDuration = resilience.CircuitBreaker.SamplingDuration,
                        MinimumThroughput = resilience.CircuitBreaker.MinimumThroughput,
                        BreakDuration = resilience.CircuitBreaker.BreakDuration,
                    })
                    .AddTimeout(resilience.AttemptTimeout);
            });

        // L'astrazione vista dal resto dell'app è il DECORATORE di caching, che avvolge il client resiliente
        // concreto. Il gate Enabled è lazy dentro il decoratore (legge IOptionsMonitor a ogni richiesta), così
        // gli override di test valgono e si può spegnere la cache da config senza ricompilare.
        services.AddScoped<IBookPopularityClient>(serviceProvider => new CachingBookPopularityClient(
            serviceProvider.GetRequiredService<OpenLibraryPopularityClient>(),
            serviceProvider.GetRequiredService<IFusionCache>(),
            serviceProvider.GetRequiredService<IOptionsMonitor<BookPopularityOptions>>(),
            serviceProvider.GetRequiredService<ILogger<CachingBookPopularityClient>>()));

        return services;
    }
}
