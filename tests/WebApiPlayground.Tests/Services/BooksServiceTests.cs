using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using WebApiPlayground.Application.Diagnostics;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Application.Querying;
using WebApiPlayground.Application.Services;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Tests.Services;

public class BooksServiceTests
{
    // Token di versione atteso dal client (If-Match) → instradato al repo come Book.RowVersion.
    private static readonly byte[] Version = [0, 0, 0, 0, 0, 0, 7, 209];
    private static readonly string VersionBase64 = Convert.ToBase64String(Version);

    private readonly Mock<IBookRepository> _repositoryMock = new();
    private readonly BooksService _sut;

    public BooksServiceTests()
    {
        _sut = new BooksService(_repositoryMock.Object, NullLogger<BooksService>.Instance);
    }

    private static BooksQueryParameters Query(
        int pageNumber = 1, int pageSize = 20, string sortBy = "id", string sortDir = "asc") =>
        new() { PageNumber = pageNumber, PageSize = pageSize, SortBy = sortBy, SortDir = sortDir };

    [Fact]
    public async Task GetBooksAsync_ReturnsMappedDtos_WhenBooksExist()
    {
        var books = new List<Book>
        {
            new() { Id = 1, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } },
            new() { Id = 2, Title = "The Pragmatic Programmer", AuthorId = 2, Author = new Author { Id = 2, FullName = "Dave Thomas" } }
        };
        _repositoryMock
            .Setup(r => r.GetPagedAsync(1, 20, BookSortField.Id, SortDirection.Ascending))
            .ReturnsAsync((books, 2));

        var result = await _sut.GetBooksAsync(Query());

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Clean Code", result.Items.First().Title);
        Assert.Equal("Robert C. Martin", result.Items.First().AuthorFullName);
    }

    [Fact]
    public async Task GetBooksAsync_ReturnsEmptyPage_WhenNoBooksExist()
    {
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((new List<Book>(), 0));

        var result = await _sut.GetBooksAsync(Query());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
        Assert.Equal(0, result.TotalPages);
        Assert.False(result.HasNext);
        Assert.False(result.HasPrevious);
    }

    [Fact]
    public async Task GetBooksAsync_ComputesPagingMetadata()
    {
        // page 2 di 7 con 127 elementi totali, size 20
        _repositoryMock
            .Setup(r => r.GetPagedAsync(2, 20, BookSortField.Id, SortDirection.Ascending))
            .ReturnsAsync((new List<Book> { new() { Id = 21, Title = "X", Author = new Author { FullName = "A" } } }, 127));

        var result = await _sut.GetBooksAsync(Query(pageNumber: 2));

        Assert.Equal(2, result.PageNumber);
        Assert.Equal(20, result.PageSize);
        Assert.Equal(127, result.TotalCount);
        Assert.Equal(7, result.TotalPages);
        Assert.True(result.HasPrevious);
        Assert.True(result.HasNext);
    }

    [Fact]
    public async Task GetBooksAsync_MapsSortByTitleDesc_ToEnums()
    {
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((new List<Book>(), 0));

        await _sut.GetBooksAsync(Query(sortBy: "title", sortDir: "DESC"));

        _repositoryMock.Verify(
            r => r.GetPagedAsync(1, 20, BookSortField.Title, SortDirection.Descending), Times.Once);
    }

    [Fact]
    public async Task GetBooksAsync_FallsBackToId_WhenSortByNotAllowed()
    {
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((new List<Book>(), 0));

        await _sut.GetBooksAsync(Query(sortBy: "DROP TABLE"));

        _repositoryMock.Verify(
            r => r.GetPagedAsync(1, 20, BookSortField.Id, SortDirection.Ascending), Times.Once);
    }

    [Fact]
    public async Task GetBookByIdAsync_ReturnsDto_WhenBookExists()
    {
        var book = new Book { Id = 1, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } };
        _repositoryMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(book);

        var result = await _sut.GetBookByIdAsync(1);

        Assert.NotNull(result);
        Assert.Equal(1, result.Id);
        Assert.Equal("Clean Code", result.Title);
        Assert.Equal("Robert C. Martin", result.AuthorFullName);
    }

    [Fact]
    public async Task GetBookByIdAsync_ReturnsNull_WhenBookNotFound()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Book?)null);

        var result = await _sut.GetBookByIdAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateBookAsync_ReturnsMappedDto_WhenCreated()
    {
        var dto = new CreateBookDto("Clean Code", 1);
        var createdBook = new Book { Id = 1, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } };
        _repositoryMock
            .Setup(r => r.CreateAsync(
                It.Is<Book>(b => b.Title == dto.Title && b.AuthorId == dto.AuthorId),
                It.IsAny<Func<int, IntegrationEvent>>()))
            .ReturnsAsync(createdBook);

        var result = await _sut.CreateBookAsync(dto);

        Assert.Equal(1, result.Id);
        Assert.Equal("Clean Code", result.Title);
        Assert.Equal("Robert C. Martin", result.AuthorFullName);
    }

    [Fact]
    public async Task CreateBookAsync_StartsBusinessActivity_AndRecordsMetric()
    {
        // Rete di salvataggio: la strumentazione OTel custom (span + metrica) deve passare per il *service
        // reale*, non solo per l'helper. Un refactoring che la rimuove da CreateBookAsync rompe questo test.
        var dto = new CreateBookDto("Domain-Driven Design", 4);
        var createdBook = new Book
        {
            Id = 42, Title = dto.Title, AuthorId = 4, Author = new Author { Id = 4, FullName = "Eric Evans" },
        };
        _repositoryMock
            .Setup(r => r.CreateAsync(
                It.Is<Book>(b => b.Title == dto.Title && b.AuthorId == dto.AuthorId),
                It.IsAny<Func<int, IntegrationEvent>>()))
            .ReturnsAsync(createdBook);

        // L'ActivityListener è process-global e ConcurrentBag thread-safe: altri test possono creare span
        // sulla stessa source in parallelo, perciò si filtra per il tag book.id (univoco in questo test).
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BooksDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);

        _ = BooksDiagnostics.ActivitySource; // forza init Meter/Counter prima del collector
        using var metrics = new MetricCollector<long>(
            meterScope: null, BooksDiagnostics.MeterName, BooksDiagnostics.BooksCreatedCounterName);

        await _sut.CreateBookAsync(dto);

        var activity = Assert.Single(
            activities,
            a => a.OperationName == BooksDiagnostics.CreateBookActivityName && a.GetTagItem("book.id") is 42);
        Assert.Equal(4, activity.GetTagItem("book.author_id"));

        // La metrica è incrementata attraverso il service (almeno la nostra +1; il contatore è globale).
        var snapshot = metrics.GetMeasurementSnapshot();
        Assert.True(snapshot.Count >= 1);
        Assert.All(snapshot, m => Assert.Equal(1, m.Value));
    }

    [Fact]
    public async Task CreateBookAsync_WritesPopularityOutboxEvent_ForCreatedBook()
    {
        var dto = new CreateBookDto("Clean Code", 1);
        var createdBook = new Book { Id = 11, Title = "Clean Code", AuthorId = 1, Author = new Author { Id = 1, FullName = "Robert C. Martin" } };

        // Cattura la factory passata al repository e la valuta con l'Id creato: il service deve emettere
        // un evento di arricchimento popolarità per il libro appena creato (scritto in outbox dal repo).
        Func<int, IntegrationEvent>? captured = null;
        _repositoryMock
            .Setup(r => r.CreateAsync(It.IsAny<Book>(), It.IsAny<Func<int, IntegrationEvent>>()))
            .Callback<Book, Func<int, IntegrationEvent>>((_, factory) => captured = factory)
            .ReturnsAsync(createdBook);

        await _sut.CreateBookAsync(dto);

        Assert.NotNull(captured);
        var evt = Assert.IsType<PopularityEnrichmentRequested>(captured!(createdBook.Id));
        Assert.Equal(11, evt.BookId);
    }

    [Fact]
    public async Task UpdateBookAsync_WritesPopularityOutboxEvent_WhenBookExists()
    {
        var dto = new UpdateBookDto("Refactoring", 3);
        var updatedBook = new Book { Id = 5, Title = "Refactoring", AuthorId = 3, Author = new Author { Id = 3, FullName = "Martin Fowler" } };

        Func<int, IntegrationEvent>? captured = null;
        _repositoryMock
            .Setup(r => r.UpdateAsync(It.IsAny<Book>(), It.IsAny<Func<int, IntegrationEvent>>()))
            .Callback<Book, Func<int, IntegrationEvent>>((_, factory) => captured = factory)
            .ReturnsAsync(updatedBook);

        await _sut.UpdateBookAsync(5, dto, Version);

        Assert.NotNull(captured);
        var evt = Assert.IsType<PopularityEnrichmentRequested>(captured!(5));
        Assert.Equal(5, evt.BookId);
    }

    [Fact]
    public async Task UpdateBookAsync_ReturnsMappedDto_WhenBookExists()
    {
        var dto = new UpdateBookDto("Refactoring", 3);
        var updatedBook = new Book { Id = 5, Title = "Refactoring", AuthorId = 3, Author = new Author { Id = 3, FullName = "Martin Fowler" } };
        // Il token atteso (If-Match) dev'essere instradato al repo come Book.RowVersion (concurrency token).
        _repositoryMock
            .Setup(r => r.UpdateAsync(
                It.Is<Book>(b => b.Id == 5 && b.Title == dto.Title && b.AuthorId == dto.AuthorId && b.RowVersion == Version),
                It.IsAny<Func<int, IntegrationEvent>>()))
            .ReturnsAsync(updatedBook);

        var result = await _sut.UpdateBookAsync(5, dto, Version);

        Assert.NotNull(result);
        Assert.Equal(5, result.Id);
        Assert.Equal("Refactoring", result.Title);
        Assert.Equal("Martin Fowler", result.AuthorFullName);
    }

    [Fact]
    public async Task UpdateBookAsync_ReturnsNull_WhenBookNotFound()
    {
        _repositoryMock
            .Setup(r => r.UpdateAsync(It.IsAny<Book>(), It.IsAny<Func<int, IntegrationEvent>>()))
            .ReturnsAsync((Book?)null);

        var result = await _sut.UpdateBookAsync(999, new UpdateBookDto("X", 1), Version);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteBookAsync_ReturnsTrue_WhenBookExists()
    {
        _repositoryMock.Setup(r => r.DeleteAsync(1, Version)).ReturnsAsync(true);

        var result = await _sut.DeleteBookAsync(1, Version);

        Assert.True(result);
        _repositoryMock.Verify(r => r.DeleteAsync(1, Version), Times.Once);
    }

    [Fact]
    public async Task DeleteBookAsync_ReturnsFalse_WhenBookNotFound()
    {
        _repositoryMock.Setup(r => r.DeleteAsync(999, It.IsAny<byte[]>())).ReturnsAsync(false);

        var result = await _sut.DeleteBookAsync(999, Version);

        Assert.False(result);
    }

    [Fact]
    public async Task GetBookByIdAsync_ProjectsRowVersion_AsBase64Version()
    {
        // Il token di concorrenza (rowversion) è esposto come Version base64 (poi → ETag nell'API).
        var book = new Book
        {
            Id = 1, Title = "Clean Code", AuthorId = 1,
            Author = new Author { Id = 1, FullName = "Robert C. Martin" },
            RowVersion = Version,
        };
        _repositoryMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(book);

        var result = await _sut.GetBookByIdAsync(1);

        Assert.Equal(VersionBase64, result!.Version);
    }

    [Fact]
    public async Task GetBookDetailsByIdAsync_ProjectsRowVersion_AsBase64Version()
    {
        var book = new Book
        {
            Id = 1, Title = "Clean Code", AuthorId = 7,
            Author = new Author { Id = 7, FullName = "Robert C. Martin" },
            RowVersion = Version,
        };
        _repositoryMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(book);

        var result = await _sut.GetBookDetailsByIdAsync(1);

        Assert.Equal(VersionBase64, result!.Version);
    }

    [Fact]
    public async Task GetBooksAsync_MapsAuthorFullName_WhenAuthorIsNull()
    {
        var books = new List<Book>
        {
            new() { Id = 1, Title = "Orphan Book", AuthorId = 1, Author = null }
        };
        _repositoryMock
            .Setup(r => r.GetPagedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BookSortField>(), It.IsAny<SortDirection>()))
            .ReturnsAsync((books, 1));

        var result = await _sut.GetBooksAsync(Query());

        Assert.Equal(string.Empty, result.Items.First().AuthorFullName);
    }

    // --- v2: stessa fetch del repository, proiezione con autore annidato (AuthorDto) ---

    [Fact]
    public async Task GetBookDetailsByIdAsync_MapsNestedAuthor_WhenBookExists()
    {
        var book = new Book { Id = 1, Title = "Clean Code", AuthorId = 7, Author = new Author { Id = 7, FullName = "Robert C. Martin" } };
        _repositoryMock.Setup(r => r.GetByIdAsync(1)).ReturnsAsync(book);

        var result = await _sut.GetBookDetailsByIdAsync(1);

        Assert.NotNull(result);
        Assert.Equal(1, result.Id);
        Assert.Equal("Clean Code", result.Title);
        // La differenza con v1: l'autore è un oggetto annidato con Id + FullName.
        Assert.Equal(7, result.Author.Id);
        Assert.Equal("Robert C. Martin", result.Author.FullName);
    }

    [Fact]
    public async Task GetBookDetailsByIdAsync_ReturnsNull_WhenBookNotFound()
    {
        _repositoryMock.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((Book?)null);

        var result = await _sut.GetBookDetailsByIdAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetBooksDetailedAsync_MapsNestedAuthors_AndReusesSamePagedFetch()
    {
        var books = new List<Book>
        {
            new() { Id = 1, Title = "Clean Code", AuthorId = 7, Author = new Author { Id = 7, FullName = "Robert C. Martin" } },
        };
        _repositoryMock
            .Setup(r => r.GetPagedAsync(1, 20, BookSortField.Id, SortDirection.Ascending))
            .ReturnsAsync((books, 1));

        var result = await _sut.GetBooksDetailedAsync(Query());

        Assert.Single(result.Items);
        Assert.Equal(7, result.Items.First().Author.Id);
        Assert.Equal("Robert C. Martin", result.Items.First().Author.FullName);
        // Stessa firma di repository di v1 (DRY): cambia solo la proiezione finale.
        _repositoryMock.Verify(r => r.GetPagedAsync(1, 20, BookSortField.Id, SortDirection.Ascending), Times.Once);
    }
}
