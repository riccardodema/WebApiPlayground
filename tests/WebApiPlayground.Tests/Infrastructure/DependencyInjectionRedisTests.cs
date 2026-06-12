using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Infrastructure;
using Xunit;

namespace WebApiPlayground.Tests.Infrastructure;

/// <summary>
/// Il ramo <b>Redis-configurato</b> della composition root (L2 + backplane per la cache, store
/// distribuito per l'idempotency): config-gated su <c>Cache:Redis:ConnectionString</c>. Qui si
/// verifica il WIRING (descrittori registrati), senza connettersi a un Redis vero: le connessioni
/// sono lazy per design — il comportamento L2 end-to-end resta fuori scope (richiederebbe un container).
/// </summary>
public class DependencyInjectionRedisTests
{
    private static ServiceCollection Build(string? redisConnectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=unused;Database=unused;TrustServerCertificate=True;",
            ["Cache:Redis:ConnectionString"] = redisConnectionString,
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        return services;
    }

    [Fact]
    public void With_redis_connection_string_the_idempotency_store_uses_redis()
    {
        var services = Build("localhost:6379,abortConnect=false");

        // AddStackExchangeRedisCache registra l'implementazione Redis come IDistributedCache
        // (connessione lazy). IsAssignableFrom, non Equal: a seconda della versione del package il
        // tipo concreto è RedisCache o l'internal RedisCacheImpl che ne deriva.
        var descriptor = services.Last(d => d.ServiceType == typeof(IDistributedCache));
        Assert.True(typeof(RedisCache).IsAssignableFrom(descriptor.ImplementationType),
            $"atteso un RedisCache, trovato {descriptor.ImplementationType}");
    }

    [Fact]
    public void Without_redis_the_idempotency_store_falls_back_to_memory()
    {
        var services = Build(redisConnectionString: null);

        var descriptor = services.Last(d => d.ServiceType == typeof(IDistributedCache));
        Assert.True(typeof(MemoryDistributedCache).IsAssignableFrom(descriptor.ImplementationType),
            $"atteso un MemoryDistributedCache, trovato {descriptor.ImplementationType}");
    }
}
