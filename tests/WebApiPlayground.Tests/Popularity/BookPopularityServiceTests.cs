using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Application.Services;
using WebApiPlayground.Domain.Entities;
using Xunit;

namespace WebApiPlayground.Tests.Popularity;

/// <summary>
/// Unit del <see cref="BookPopularityService"/>: composizione "libro dal DB + arricchimento esterno", con
/// repository e client esterno mockati (nessun DB, nessuna rete). La resilienza non è testata qui: vive nella
/// pipeline attorno al client (vedi <see cref="PopularityResiliencePipelineTests"/>).
/// </summary>
public class BookPopularityServiceTests
{
    private readonly Mock<IBookRepository> _repository = new();
    private readonly Mock<IBookPopularityClient> _client = new();
    private readonly Mock<IBookPopularitySnapshotRepository> _snapshots = new();

    public BookPopularityServiceTests() => _client.SetupGet(c => c.SourceName).Returns("Open Library");

    private BookPopularityService CreateSut() =>
        new(_repository.Object, _client.Object, _snapshots.Object, TimeProvider.System, NullLogger<BookPopularityService>.Instance);

    private static Book SampleBook() => new()
    {
        Id = 7,
        Title = "Dune",
        AuthorId = 3,
        Author = new Author { Id = 3, FullName = "Frank Herbert" },
    };

    [Fact]
    public async Task ReturnsNull_AndSkipsExternalCall_WhenBookNotFound()
    {
        _repository.Setup(r => r.GetByIdAsync(99)).ReturnsAsync((Book?)null);

        var result = await CreateSut().GetBookPopularityAsync(99, CancellationToken.None);

        Assert.Null(result);
        _client.Verify(
            c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MapsSignals_AndQueriesByTitleAndAuthor_WhenMatchFound()
    {
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync("Dune", "Frank Herbert", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookPopularity(4.5, 10, 100, 5, 50, 155));

        var before = DateTimeOffset.UtcNow;
        var result = await CreateSut().GetBookPopularityAsync(7, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7, result!.BookId);
        Assert.Equal("Dune", result.Title);
        Assert.Equal("Frank Herbert", result.Author);
        Assert.Equal(4.5, result.AverageRating);
        Assert.Equal(10, result.RatingsCount);
        Assert.Equal(100, result.WantToReadCount);
        Assert.Equal(5, result.CurrentlyReadingCount);
        Assert.Equal(50, result.AlreadyReadCount);
        Assert.Equal(155, result.ReadingLogCount);
        Assert.Equal("Open Library", result.Source);
        Assert.InRange(result.RetrievedAt, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ReturnsDtoWithNullMetrics_WhenNoUpstreamMatch()
    {
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookPopularity?)null);

        var result = await CreateSut().GetBookPopularityAsync(7, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7, result!.BookId);
        Assert.Equal("Dune", result.Title);
        Assert.Null(result.AverageRating);
        Assert.Null(result.RatingsCount);
        Assert.Null(result.ReadingLogCount);
        Assert.Equal("Open Library", result.Source);
    }

    [Fact]
    public async Task PropagatesUnavailableException_WhenNoSnapshotFallback_SoItMapsTo503()
    {
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceUnavailableException("Open Library"));
        // Nessuno snapshot durevole (default del mock = null) → niente fallback → si propaga → 503.
        _snapshots.Setup(s => s.GetByBookIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync((BookPopularitySnapshot?)null);

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => CreateSut().GetBookPopularityAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task ServesDurableSnapshot_WhenExternalUnavailable_AndSnapshotExists()
    {
        // Outage della dipendenza + snapshot durevole presente → 200 last-known-good (NON 503), con la
        // freschezza/provenienza dello snapshot. È il valore aggiunto della persistenza durevole.
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceUnavailableException("Open Library"));

        var retrievedAt = DateTimeOffset.UtcNow.AddHours(-3);
        _snapshots
            .Setup(s => s.GetByBookIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookPopularitySnapshot
            {
                BookId = 7,
                AverageRating = 4.2,
                RatingsCount = 99,
                ReadingLogCount = 321,
                Source = "Open Library (snapshot)",
                RetrievedAt = retrievedAt,
            });

        var result = await CreateSut().GetBookPopularityAsync(7, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(7, result!.BookId);
        Assert.Equal(4.2, result.AverageRating);
        Assert.Equal(99, result.RatingsCount);
        Assert.Equal(321, result.ReadingLogCount);
        // Provenienza e as-of vengono dallo snapshot, non dal "now".
        Assert.Equal("Open Library (snapshot)", result.Source);
        Assert.Equal(retrievedAt, result.RetrievedAt);
    }

    [Fact]
    public async Task UsesEmptyAuthor_WhenBookHasNoAuthor()
    {
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(new Book { Id = 7, Title = "Orphan", Author = null });
        _client
            .Setup(c => c.GetPopularityAsync("Orphan", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookPopularity(null, null, null, null, null, null));

        var result = await CreateSut().GetBookPopularityAsync(7, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result!.Author);
        _client.Verify(c => c.GetPopularityAsync("Orphan", null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
