using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Idempotency;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Infrastructure.Caching;
using WebApiPlayground.Infrastructure.HealthChecks;
using WebApiPlayground.Infrastructure.Idempotency;
using WebApiPlayground.Infrastructure.Outbox;
using WebApiPlayground.Infrastructure.Outbox.ServiceBus;
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
        services.AddScoped<IBookPopularitySnapshotRepository, BookPopularitySnapshotRepository>();

        // Logica di arricchimento riusabile dal dispatcher dell'outbox (e domani dal consumer ASB).
        services.AddScoped<IPopularityEnricher, PopularityEnricher>();

        AddCaching(services, configuration);
        AddIdempotency(services, configuration);
        AddOutboxProcessing(services, configuration);

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

    /// <summary>
    /// Outbox transazionale: l'evento di integrazione è scritto nella stessa transazione della write di
    /// business (nel repository); il <see cref="OutboxDispatcher"/> (hosted service) polla i messaggi non
    /// processati e li <b>pubblica</b> tramite <see cref="IIntegrationEventPublisher"/>, marcandoli solo a
    /// successo (at-least-once durevole).
    ///
    /// <para>Il <b>trasporto</b> è scelto qui in base alla config (come Redis/OTLP). Azure Service Bus è il
    /// percorso <b>reale</b> (docker-compose con emulatore e Production con managed identity); l'in-process è solo
    /// un <b>fallback per il dev offline</b> (bare <c>dotnet run</c> senza broker):</para>
    /// <list type="bullet">
    ///   <item><b>se <c>ServiceBus</c> è configurato</b> → publisher Azure Service Bus + consumer disaccoppiato:
    ///   l'outbox pubblica sul broker, un hosted service riceve e arricchisce fuori dal path di write (PR-2);</item>
    ///   <item><b>altrimenti</b> → <see cref="InProcessIntegrationEventPublisher"/>: l'evento è gestito subito
    ///   in-process (nessuna dipendenza esterna). Fuori da Development il broker è <b>obbligatorio</b> (fail-fast
    ///   in <c>StartupConfigurationValidator</c>), quindi questo fallback vale solo in Development.</item>
    /// </list>
    /// In entrambi i casi la logica di gestione vive in un unico <see cref="IntegrationEventHandler"/> (riusa
    /// <see cref="IPopularityEnricher"/>). Vedi <c>.claude/context/outbox.md</c>.
    /// </summary>
    private static void AddOutboxProcessing(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        // L'unità di lavoro (scoped) è separata dal loop di hosting: così i test la guidano deterministicamente
        // (un ProcessPendingAsync esplicito) senza il polling continuo del dispatcher.
        services.AddScoped<OutboxProcessor>();
        services.AddHostedService<OutboxDispatcher>();

        // Routing+esecuzione condiviso dai due trasporti (in-process e consumer ASB). Scoped: enricher/DbContext
        // freschi per ogni evento gestito.
        services.AddScoped<IntegrationEventHandler>();

        // Trasporto config-gated: Azure Service Bus se configurato, altrimenti consegna in-process (default).
        services.Configure<ServiceBusOptions>(configuration.GetSection(ServiceBusOptions.SectionName));
        var serviceBusOptions = configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>()
                                ?? new ServiceBusOptions();

        if (serviceBusOptions.IsConfigured)
            services.AddServiceBusTransport();
        else
            services.AddScoped<IIntegrationEventPublisher, InProcessIntegrationEventPublisher>();
    }
}
