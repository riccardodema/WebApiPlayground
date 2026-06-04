using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebApiPlayground.Application.Caching;
using WebApiPlayground.Application.Concurrency;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace WebApiPlayground.Tests.Caching;

/// <summary>
/// Test del decoratore di caching contro un <see cref="HybridCache"/> REALE (FusionCache
/// memory-only, lo stesso provider della produzione): così si esercita davvero la cache e il
/// tagging/invalidazione, non un mock. Il service interno è mockato per contare le chiamate.
/// </summary>
public class CachingBooksServiceTests
{
    private static readonly byte[] Version = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly Mock<IBooksService> _innerMock = new();
    private readonly HybridCache _cache;
    private readonly CachingBooksService _sut;

    public CachingBooksServiceTests()
    {
        // FusionCache esposto come HybridCache, solo L1 in memoria (nessun Redis): identico
        // alla configurazione di default dell'app, senso garantito sul comportamento reale.
        var provider = new ServiceCollection()
            .AddFusionCache().AsHybridCache().Services
            .BuildServiceProvider();
        _cache = provider.GetRequiredService<HybridCache>();

        _sut = new CachingBooksService(_innerMock.Object, _cache, NullLogger<CachingBooksService>.Instance);
    }

    private static BooksQueryParameters Query(int pageNumber = 1, int pageSize = 20) =>
        new() { PageNumber = pageNumber, PageSize = pageSize, SortBy = "id", SortDir = "asc" };

    [Fact]
    public async Task GetBookByIdAsync_SecondCall_IsServedFromCache()
    {
        _innerMock.Setup(s => s.GetBookByIdAsync(1))
            .ReturnsAsync(new BookDto(1, "Clean Code", "Robert C. Martin"));

        var first = await _sut.GetBookByIdAsync(1);
        var second = await _sut.GetBookByIdAsync(1);

        Assert.Equal("Clean Code", first!.Title);
        Assert.Equal("Clean Code", second!.Title);
        // Cache hit sulla seconda lettura → il service interno (e quindi il DB) è chiamato una sola volta.
        _innerMock.Verify(s => s.GetBookByIdAsync(1), Times.Once);
    }

    [Fact]
    public async Task GetBooksAsync_CachesPerQueryKey()
    {
        _innerMock.Setup(s => s.GetBooksAsync(It.IsAny<BooksQueryParameters>()))
            .ReturnsAsync(new PagedResult<BookDto>([], 1, 20, 0));

        await _sut.GetBooksAsync(Query(pageNumber: 1));
        await _sut.GetBooksAsync(Query(pageNumber: 1)); // stessa chiave → cache hit
        await _sut.GetBooksAsync(Query(pageNumber: 2)); // chiave diversa → cache miss

        _innerMock.Verify(s => s.GetBooksAsync(It.Is<BooksQueryParameters>(q => q.PageNumber == 1)), Times.Once);
        _innerMock.Verify(s => s.GetBooksAsync(It.Is<BooksQueryParameters>(q => q.PageNumber == 2)), Times.Once);
    }

    [Fact]
    public async Task GetBookDetailsByIdAsync_SecondCall_IsServedFromCache()
    {
        _innerMock.Setup(s => s.GetBookDetailsByIdAsync(1))
            .ReturnsAsync(new BookDetailsDto(1, "Clean Code", new AuthorDto(1, "Robert C. Martin")));

        var first = await _sut.GetBookDetailsByIdAsync(1);
        var second = await _sut.GetBookDetailsByIdAsync(1);

        Assert.Equal("Robert C. Martin", first!.Author.FullName);
        Assert.Equal("Robert C. Martin", second!.Author.FullName);
        // Le letture v2 hanno chiavi proprie ma stesso meccanismo di cache: una sola chiamata all'inner.
        _innerMock.Verify(s => s.GetBookDetailsByIdAsync(1), Times.Once);
    }

    [Fact]
    public async Task CreateBookAsync_InvalidatesV2CacheToo()
    {
        // Le letture v2 condividono il tag "books": una scrittura deve invalidarle come quelle v1.
        _innerMock.Setup(s => s.GetBookDetailsByIdAsync(1))
            .ReturnsAsync(new BookDetailsDto(1, "Old", new AuthorDto(1, "Author")));
        _innerMock.Setup(s => s.CreateBookAsync(It.IsAny<CreateBookDto>()))
            .ReturnsAsync(new BookDto(2, "New", "Author"));

        await _sut.GetBookDetailsByIdAsync(1);                   // popola la cache v2
        await _sut.CreateBookAsync(new CreateBookDto("New", 1)); // invalida il tag "books"
        await _sut.GetBookDetailsByIdAsync(1);                   // cache miss → ri-legge dall'inner

        _innerMock.Verify(s => s.GetBookDetailsByIdAsync(1), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateBookAsync_InvalidatesCache()
    {
        _innerMock.Setup(s => s.GetBookByIdAsync(1)).ReturnsAsync(new BookDto(1, "Old", "Author"));
        _innerMock.Setup(s => s.CreateBookAsync(It.IsAny<CreateBookDto>()))
            .ReturnsAsync(new BookDto(2, "New", "Author"));

        await _sut.GetBookByIdAsync(1);                       // popola la cache
        await _sut.CreateBookAsync(new CreateBookDto("New", 1)); // invalida il tag "books"
        await _sut.GetBookByIdAsync(1);                       // cache miss → ri-legge dall'inner

        _innerMock.Verify(s => s.GetBookByIdAsync(1), Times.Exactly(2));
    }

    [Fact]
    public async Task UpdateBookAsync_WhenNotFound_DoesNotInvalidateCache()
    {
        _innerMock.Setup(s => s.GetBookByIdAsync(1)).ReturnsAsync(new BookDto(1, "Title", "Author"));
        _innerMock.Setup(s => s.UpdateBookAsync(99, It.IsAny<UpdateBookDto>(), It.IsAny<byte[]>()))
            .ReturnsAsync((BookDto?)null); // libro inesistente → niente è cambiato

        await _sut.GetBookByIdAsync(1);                                  // popola la cache
        await _sut.UpdateBookAsync(99, new UpdateBookDto("X", 1), Version); // no-op → nessuna invalidazione
        await _sut.GetBookByIdAsync(1);                                  // ancora cache hit

        _innerMock.Verify(s => s.GetBookByIdAsync(1), Times.Once);
    }

    [Fact]
    public async Task UpdateBookAsync_OnConcurrencyConflict_DoesNotInvalidateCache()
    {
        // Il conflitto (412) significa che nulla è cambiato nel DB: la cache NON va invalidata.
        _innerMock.Setup(s => s.GetBookByIdAsync(1)).ReturnsAsync(new BookDto(1, "Title", "Author"));
        _innerMock.Setup(s => s.UpdateBookAsync(1, It.IsAny<UpdateBookDto>(), It.IsAny<byte[]>()))
            .ThrowsAsync(new ConcurrencyConflictException(1));

        await _sut.GetBookByIdAsync(1); // popola la cache

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => _sut.UpdateBookAsync(1, new UpdateBookDto("X", 1), Version));

        await _sut.GetBookByIdAsync(1); // ancora cache hit (nessuna invalidazione)

        _innerMock.Verify(s => s.GetBookByIdAsync(1), Times.Once);
    }

    [Fact]
    public async Task DeleteBookAsync_WhenDeleted_InvalidatesCache()
    {
        _innerMock.Setup(s => s.GetBookByIdAsync(1)).ReturnsAsync(new BookDto(1, "Title", "Author"));
        _innerMock.Setup(s => s.DeleteBookAsync(1, It.IsAny<byte[]>())).ReturnsAsync(true);

        await _sut.GetBookByIdAsync(1);    // popola la cache
        await _sut.DeleteBookAsync(1, Version); // invalida
        await _sut.GetBookByIdAsync(1);    // ri-legge

        _innerMock.Verify(s => s.GetBookByIdAsync(1), Times.Exactly(2));
    }
}
