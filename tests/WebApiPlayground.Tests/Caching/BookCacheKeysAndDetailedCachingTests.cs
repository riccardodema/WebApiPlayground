using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebApiPlayground.Application.Caching;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace WebApiPlayground.Tests.Caching;

/// <summary>
/// Copertura della forma <b>v2</b> (autore annidato) nel layer di caching: chiavi dedicate
/// (<c>books:v2:*</c> — DTO diversi non devono mai condividere la chiave con la v1) e decoratore
/// <see cref="CachingBooksService"/> su <c>GetBooksDetailedAsync</c>/<c>GetBookDetailsByIdAsync</c>.
/// Stesso setup REALE di <see cref="CachingBooksServiceTests"/> (FusionCache memory-only come HybridCache).
/// </summary>
public class BookCacheKeysAndDetailedCachingTests
{
    private readonly Mock<IBooksService> _innerMock = new();
    private readonly CachingBooksService _sut;

    public BookCacheKeysAndDetailedCachingTests()
    {
        var provider = new ServiceCollection()
            .AddFusionCache().AsHybridCache().Services
            .BuildServiceProvider();

        _sut = new CachingBooksService(
            _innerMock.Object, provider.GetRequiredService<HybridCache>(), NullLogger<CachingBooksService>.Instance);
    }

    private static BooksQueryParameters Query(int pageNumber = 1) =>
        new() { PageNumber = pageNumber, PageSize = 20, SortBy = "Title", SortDir = "ASC" };

    private static PagedResult<BookDetailsDto> Page() =>
        new([new BookDetailsDto(1, "Dune", new AuthorDto(7, "Frank Herbert"))], 1, 20, 1);

    // ---- Chiavi v2: distinte dalla v1 e normalizzate -------------------------

    [Fact]
    public void Detailed_keys_live_in_a_v2_namespace_distinct_from_v1()
    {
        var query = Query();

        Assert.Equal("books:v2:id:42", BookCacheKeys.ByIdDetailed(42));
        // SortBy/SortDir normalizzati lowercase: "Title"/"ASC" e "title"/"asc" sono la stessa pagina.
        Assert.Equal("books:v2:list:1:20:title:asc", BookCacheKeys.ForListDetailed(query));
        Assert.NotEqual(BookCacheKeys.ForList(query), BookCacheKeys.ForListDetailed(query));
    }

    // ---- Decoratore: GetBooksDetailedAsync -----------------------------------

    [Fact]
    public async Task Detailed_list_second_call_is_served_from_cache()
    {
        _innerMock.Setup(s => s.GetBooksDetailedAsync(It.IsAny<BooksQueryParameters>())).ReturnsAsync(Page());

        var first = await _sut.GetBooksDetailedAsync(Query());
        var second = await _sut.GetBooksDetailedAsync(Query());

        Assert.Equal("Frank Herbert", first.Items[0].Author.FullName);
        Assert.Equal("Frank Herbert", second.Items[0].Author.FullName);
        _innerMock.Verify(s => s.GetBooksDetailedAsync(It.IsAny<BooksQueryParameters>()), Times.Once);
    }

    [Fact]
    public async Task Detailed_list_caches_per_query_key()
    {
        _innerMock.Setup(s => s.GetBooksDetailedAsync(It.IsAny<BooksQueryParameters>())).ReturnsAsync(Page());

        await _sut.GetBooksDetailedAsync(Query(pageNumber: 1));
        await _sut.GetBooksDetailedAsync(Query(pageNumber: 2)); // chiave diversa → seconda chiamata vera

        _innerMock.Verify(s => s.GetBooksDetailedAsync(It.IsAny<BooksQueryParameters>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Detailed_byid_second_call_is_served_from_cache()
    {
        _innerMock.Setup(s => s.GetBookDetailsByIdAsync(1))
            .ReturnsAsync(new BookDetailsDto(1, "Dune", new AuthorDto(7, "Frank Herbert")));

        var first = await _sut.GetBookDetailsByIdAsync(1);
        var second = await _sut.GetBookDetailsByIdAsync(1);

        Assert.NotNull(first);
        Assert.NotNull(second);
        _innerMock.Verify(s => s.GetBookDetailsByIdAsync(1), Times.Once);
    }
}
