using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.Idempotency;
using WebApiPlayground.Infrastructure.Idempotency;
using Xunit;

namespace WebApiPlayground.Tests.Idempotency;

/// <summary>
/// Round-trip dello store su un <see cref="IDistributedCache"/> reale in memoria
/// (<see cref="MemoryDistributedCache"/>, lo stesso contratto del backing Redis in produzione).
/// </summary>
public class DistributedCacheIdempotencyStoreTests
{
    private static DistributedCacheIdempotencyStore CreateStore() =>
        new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    private static IdempotencyRecord SampleRecord() =>
        new(201, "/api/books/42", "application/json", "{\"id\":42}", "fingerprint-abc");

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenKeyAbsent()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync("missing"));
    }

    [Fact]
    public async Task SaveThenGet_RoundTripsTheRecord()
    {
        var store = CreateStore();
        var record = SampleRecord();

        await store.SaveAsync("idem:key", record, TimeSpan.FromMinutes(5));
        var loaded = await store.GetAsync("idem:key");

        Assert.NotNull(loaded);
        Assert.Equal(record, loaded);
    }

    [Fact]
    public async Task Save_IsScopedPerKey()
    {
        var store = CreateStore();
        await store.SaveAsync("idem:a", SampleRecord(), TimeSpan.FromMinutes(5));

        Assert.NotNull(await store.GetAsync("idem:a"));
        Assert.Null(await store.GetAsync("idem:b"));
    }
}
