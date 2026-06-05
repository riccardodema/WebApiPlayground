using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Idempotency;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Infrastructure.Caching;
using WebApiPlayground.Infrastructure.HealthChecks;
using WebApiPlayground.Infrastructure.Idempotency;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.Infrastructure.Popularity;
using WebApiPlayground.Infrastructure.Repositories;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;
using ZiggyCreatures.Caching.Fusion.Serialization.SystemTextJson;

namespace WebApiPlayground.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PlaygroundDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Default")));

        services.AddScoped<IBookRepository, BookRepository>();

        AddCaching(services, configuration);
        AddIdempotency(services, configuration);

        // Dipendenza esterna (Open Library) come HttpClient tipizzato + pipeline di resilienza Polly esplicita
        // (retry/circuit-breaker/timeout). L'astrazione IBookPopularityClient è in Application; il concreto e
        // la resilienza restano qui (regola architetturale auto-validata). Vedi .claude/context/resilience.md.
        services.AddBookPopularityClient(configuration);

        // Health check di readiness sul DB: verifica che il DbContext riesca a connettersi.
        // Tagged "ready" → esposto solo dal probe /health/ready (vedi Api/HealthChecks).
        services.AddHealthChecks()
            .AddDbContextCheck<PlaygroundDbContext>(name: "database", tags: [HealthCheckTags.Ready]);

        return services;
    }

    /// <summary>
    /// Registra FusionCache esposto come <c>HybridCache</c> standard (.NET): il resto dell'app
    /// dipende solo dall'astrazione, l'implementazione e il backing store sono scelti qui.
    /// L1 in memoria sempre attivo; L2 Redis + backplane (allineamento multi-istanza) si
    /// attivano solo se è configurata una connection string Redis. Le DefaultEntryOptions
    /// centralizzano durata e fail-safe per tutte le entry. Vedi <c>.claude/context/caching.md</c>.
    /// </summary>
    private static void AddCaching(IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CacheSettings.SectionName).Get<CacheSettings>()
                       ?? new CacheSettings();

        var fusion = services.AddFusionCache()
            .WithDefaultEntryOptions(options =>
            {
                options.Duration = settings.Duration;

                // Fail-safe: se la factory fallisce (es. DB giù), serve l'ultimo valore valido
                // (anche scaduto) entro FailSafeMaxDuration invece di propagare l'errore.
                options.IsFailSafeEnabled = true;
                options.FailSafeMaxDuration = settings.FailSafeMaxDuration;
                options.FailSafeThrottleDuration = TimeSpan.FromSeconds(30);

                // Timeout sulla factory: oltre il soft timeout si serve il valore stale (se c'è)
                // mentre il refresh continua in background → niente code lunghe sotto carico.
                options.FactorySoftTimeout = TimeSpan.FromMilliseconds(100);
                options.FactoryHardTimeout = TimeSpan.FromSeconds(2);

                // Eager refresh: rinfresca proattivamente quando si supera il 90% della durata,
                // così le entry "calde" non scadono mai sotto la richiesta dell'utente.
                options.EagerRefreshThreshold = 0.9f;
            });

        var redisConnection = settings.Redis.ConnectionString;
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            // L2 distribuito (Redis) + backplane: il backplane è un canale pub/sub che notifica a
            // TUTTE le istanze "questa chiave/questo tag è cambiato", invalidando il loro L1 →
            // cache allineata in scenari multi-istanza (ciò che HybridCache "base" non fa).
            fusion
                .WithSerializer(new FusionCacheSystemTextJsonSerializer())
                .WithDistributedCache(new RedisCache(new RedisCacheOptions { Configuration = redisConnection }))
                .WithBackplane(new RedisBackplane(new RedisBackplaneOptions { Configuration = redisConnection }));
        }

        // Espone FusionCache come HybridCache nel container (e abilita l'invalidazione multi-nodo
        // via backplane che l'implementazione Microsoft di HybridCache oggi non offre).
        fusion.AsHybridCache();
    }

    /// <summary>
    /// Registra lo store dell'idempotency su <c>IDistributedCache</c> (get-or-null + set con TTL):
    /// in memoria di default, Redis se è configurata una connection string (stesso setting della
    /// cache) → store condiviso fra istanze. Singleton: store e lock sono stateless/thread-safe e
    /// consumati dal middleware (a sua volta singleton). Vedi <c>.claude/context/idempotency.md</c>.
    /// </summary>
    private static void AddIdempotency(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.SectionName));

        var redisConnection = configuration
            .GetSection(CacheSettings.SectionName).Get<CacheSettings>()?.Redis.ConnectionString;

        if (!string.IsNullOrWhiteSpace(redisConnection))
            services.AddStackExchangeRedisCache(options => options.Configuration = redisConnection);
        else
            services.AddDistributedMemoryCache();

        services.AddSingleton<IIdempotencyStore, DistributedCacheIdempotencyStore>();
        services.AddSingleton<KeyedAsyncLock>();
    }
}
