using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;

namespace WebApiPlayground.Application.Caching;

/// <summary>
/// Decoratore di <see cref="IBooksService"/> che aggiunge il caching server-side tramite
/// l'astrazione standard <see cref="HybridCache"/> (l'implementazione concreta — FusionCache,
/// con eventuale L2 Redis + backplane — è scelta nella composition root, non qui).
///
/// <para>Le letture passano da <see cref="HybridCache.GetOrCreateAsync"/>: cache hit → nessuna
/// chiamata al service interno (e quindi al DB); cache miss → una sola esecuzione della factory
/// (stampede protection del provider). Tutte le entry portano il tag
/// <see cref="BookCacheKeys.Books"/>, così le scritture invalidano in blocco liste e singoli libri
/// con un solo <see cref="HybridCache.RemoveByTagAsync(string, System.Threading.CancellationToken)"/>.</para>
///
/// <para>Le durate e il fail-safe sono configurati una volta nelle <c>DefaultEntryOptions</c> di
/// FusionCache (layer Infrastructure): il decoratore resta minimale e indipendente dal provider.</para>
/// </summary>
public sealed class CachingBooksService : IBooksService
{
    private readonly IBooksService _inner;
    private readonly HybridCache _cache;
    private readonly ILogger<CachingBooksService> _logger;

    public CachingBooksService(IBooksService inner, HybridCache cache, ILogger<CachingBooksService> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PagedResult<BookDto>> GetBooksAsync(BooksQueryParameters query)
    {
        var key = BookCacheKeys.ForList(query);
        return await _cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                _logger.LogDebug("Cache miss for {CacheKey} — fetching book list from inner service", key);
                return await _inner.GetBooksAsync(query);
            },
            tags: BookCacheKeys.BooksTag);
    }

    public async Task<BookDto?> GetBookByIdAsync(int id)
    {
        var key = BookCacheKeys.ById(id);
        return await _cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                _logger.LogDebug("Cache miss for {CacheKey} — fetching book from inner service", key);
                // Anche un risultato null (libro inesistente) viene cache-ato: il negative caching
                // è sicuro perché ogni create invalida il tag (vedi InvalidateAsync).
                return await _inner.GetBookByIdAsync(id);
            },
            tags: BookCacheKeys.BooksTag);
    }

    public async Task<PagedResult<BookDetailsDto>> GetBooksDetailedAsync(BooksQueryParameters query)
    {
        var key = BookCacheKeys.ForListDetailed(query);
        return await _cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                _logger.LogDebug("Cache miss for {CacheKey} — fetching detailed (v2) book list from inner service", key);
                return await _inner.GetBooksDetailedAsync(query);
            },
            tags: BookCacheKeys.BooksTag);
    }

    public async Task<BookDetailsDto?> GetBookDetailsByIdAsync(int id)
    {
        var key = BookCacheKeys.ByIdDetailed(id);
        return await _cache.GetOrCreateAsync(
            key,
            async _ =>
            {
                _logger.LogDebug("Cache miss for {CacheKey} — fetching detailed (v2) book from inner service", key);
                return await _inner.GetBookDetailsByIdAsync(id);
            },
            tags: BookCacheKeys.BooksTag);
    }

    public async Task<BookDto> CreateBookAsync(CreateBookDto dto)
    {
        var created = await _inner.CreateBookAsync(dto);
        await InvalidateAsync();
        return created;
    }

    public async Task<BookDto?> UpdateBookAsync(int id, UpdateBookDto dto, byte[] expectedVersion)
    {
        // Su conflitto di concorrenza l'inner lancia prima di tornare: l'invalidazione NON parte
        // (corretto — nulla è cambiato nel DB), l'eccezione si propaga fino al mapping 412.
        var updated = await _inner.UpdateBookAsync(id, dto, expectedVersion);
        if (updated is not null)
            await InvalidateAsync();
        return updated;
    }

    public async Task<bool> DeleteBookAsync(int id, byte[] expectedVersion)
    {
        var deleted = await _inner.DeleteBookAsync(id, expectedVersion);
        if (deleted)
            await InvalidateAsync();
        return deleted;
    }

    /// <summary>Invalida tutte le entry "libro" (liste + singoli) dopo una scrittura andata a buon fine.</summary>
    private async Task InvalidateAsync()
    {
        _logger.LogDebug("Book mutation committed — invalidating cache tag '{CacheTag}'", BookCacheKeys.Books);
        await _cache.RemoveByTagAsync(BookCacheKeys.Books);
    }
}
