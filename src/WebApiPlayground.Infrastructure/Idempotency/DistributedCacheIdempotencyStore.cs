using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using WebApiPlayground.Application.Idempotency;

namespace WebApiPlayground.Infrastructure.Idempotency;

/// <summary>
/// Implementazione di <see cref="IIdempotencyStore"/> su <see cref="IDistributedCache"/>: l'astrazione
/// dà esattamente la semantica che serve (get-or-null + set con TTL). Backing in memoria ora, Redis
/// quando configurato (stesso <c>IDistributedCache</c>) → store condiviso fra istanze senza cambi al codice.
/// </summary>
public sealed class DistributedCacheIdempotencyStore : IIdempotencyStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDistributedCache _cache;

    public DistributedCacheIdempotencyStore(IDistributedCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var bytes = await _cache.GetAsync(key, cancellationToken);
        return bytes is null ? null : JsonSerializer.Deserialize<IdempotencyRecord>(bytes, SerializerOptions);
    }

    public Task SaveAsync(string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions);
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl };
        return _cache.SetAsync(key, bytes, options, cancellationToken);
    }
}
