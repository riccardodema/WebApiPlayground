using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.Popularity;
using ZiggyCreatures.Caching.Fusion;

namespace WebApiPlayground.Infrastructure.Popularity;

/// <summary>
/// Decoratore di <see cref="IBookPopularityClient"/> che cacha la risposta esterna (popolarità). Avvolge il
/// client resiliente: <c>cache → [miss] → pipeline Polly → HttpClient → Open Library</c>. Una <b>hit</b> non
/// tocca né la rete né il circuit breaker; una <b>miss</b> esegue la chiamata resiliente (single-flight:
/// stampede protection) e ne cacha l'esito.
///
/// <para><b>Perché <see cref="IFusionCache"/> e non l'astrazione <see cref="Microsoft.Extensions.Caching.Hybrid.HybridCache"/></b>
/// (a differenza di <c>CachingBooksService</c>): servono entry options che l'astrazione non espone —
/// <b>factory timeout infiniti</b> (il budget di timeout lo governa la pipeline di resilienza, non il
/// <c>FactoryHardTimeout=2s</c> globale tarato sui books, che altrimenti abortirebbe la chiamata esterna su
/// una miss fredda) e <b>fail-safe</b> esteso. Quest'ultimo dà la sinergia chiave: se Open Library è giù e
/// l'entry è scaduta, si serve l'ultimo valore buono (degrade-to-stale) invece di propagare il 503. Vedi
/// <c>.claude/context/resilience.md</c> e <c>.claude/lessons.md</c> [L20].</para>
/// </summary>
public sealed class CachingBookPopularityClient : IBookPopularityClient
{
    private readonly IBookPopularityClient _inner;
    private readonly IFusionCache _cache;
    private readonly IOptionsMonitor<BookPopularityOptions> _options;
    private readonly ILogger<CachingBookPopularityClient> _logger;

    public CachingBookPopularityClient(
        IBookPopularityClient inner,
        IFusionCache cache,
        IOptionsMonitor<BookPopularityOptions> options,
        ILogger<CachingBookPopularityClient> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public string SourceName => _inner.SourceName;

    public async Task<BookPopularity?> GetPopularityAsync(string title, string? author, CancellationToken cancellationToken)
    {
        // Opzioni lette lazy (IOptionsMonitor): gli override di test/WebApplicationFactory valgono (come [L15]).
        var settings = _options.CurrentValue.Cache;

        // Caching off, o niente da cercare: passa diretto all'inner (pipeline di resilienza inclusa).
        if (!settings.Enabled || string.IsNullOrWhiteSpace(title))
            return await _inner.GetPopularityAsync(title, author, cancellationToken);

        var key = PopularityCacheKeys.For(title, author);

        var entryOptions = new FusionCacheEntryOptions
        {
            Duration = settings.Duration,

            // Fail-safe = degrade-to-stale: con la factory in errore (OL giù / circuito aperto) e un valore
            // scaduto ancora entro FailSafeMaxDuration, si serve lo stale invece di propagare il 503.
            IsFailSafeEnabled = true,
            FailSafeMaxDuration = settings.FailSafeMaxDuration,

            // Factory timeout INFINITI: il timeout della chiamata esterna lo governa la pipeline di resilienza
            // (3s per-tentativo / 10s totale), NON FusionCache. Senza questo, il FactoryHardTimeout=2s globale
            // (giusto per i books) abortirebbe la chiamata esterna su una miss fredda, neutralizzando i retry.
            FactorySoftTimeout = Timeout.InfiniteTimeSpan,
            FactoryHardTimeout = Timeout.InfiniteTimeSpan,
        };

        return await _cache.GetOrSetAsync<BookPopularity?>(
            key,
            async (ctx, factoryToken) =>
            {
                _logger.LogDebug("Popularity cache miss for {CacheKey} — calling {Source}", key, _inner.SourceName);
                var fetched = await _inner.GetPopularityAsync(title, author, factoryToken);

                // Negative caching off: non scrivere il "no match" in cache (il single-flight resta comunque).
                if (fetched is null && !settings.CacheNotFound)
                {
                    ctx.Options.SkipMemoryCacheWrite = true;
                    ctx.Options.SkipDistributedCacheWrite = true;
                }

                return fetched;
            },
            entryOptions,
            tags: PopularityCacheKeys.Tags,
            token: cancellationToken);
    }
}
