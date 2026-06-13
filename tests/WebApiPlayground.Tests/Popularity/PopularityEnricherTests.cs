using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Popularity;
using Xunit;

namespace WebApiPlayground.Tests.Popularity;

/// <summary>
/// Unit della logica di arricchimento riusabile (<see cref="PopularityEnricher"/>, estratta dal vecchio worker
/// su canale): carica il libro, chiama il client (resiliente+cachato) e persiste lo snapshot. Sincrona e
/// deterministica (niente host/coda): l'orchestrazione asincrona è del dispatcher (testato a parte).
/// Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public class PopularityEnricherTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly Mock<IBookRepository> _repository = new();
    private readonly Mock<IBookPopularityClient> _client = new();
    private readonly Mock<IBookPopularitySnapshotRepository> _snapshots = new();
    private readonly PopularityEnricher _sut;

    public PopularityEnricherTests()
    {
        _client.SetupGet(c => c.SourceName).Returns("Open Library");
        _sut = new PopularityEnricher(
            _repository.Object, _client.Object, _snapshots.Object,
            new FixedTimeProvider(FixedNow), NullLogger<PopularityEnricher>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Book SampleBook(int id = 7) => new()
    {
        Id = id,
        Title = "Dune",
        AuthorId = 3,
        Author = new Author { Id = 3, FullName = "Frank Herbert" },
    };

    [Fact]
    public async Task PersistsSnapshot_FromExternalSignals_WithInjectedTimestamp()
    {
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync("Dune", "Frank Herbert", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookPopularity(4.5, 10, 100, 5, 50, 155));

        BookPopularitySnapshot? saved = null;
        _snapshots
            .Setup(s => s.UpsertAsync(It.IsAny<BookPopularitySnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<BookPopularitySnapshot, CancellationToken>((s, _) => saved = s)
            .Returns(Task.CompletedTask);

        await _sut.EnrichAsync(7, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(7, saved!.BookId);
        Assert.Equal(4.5, saved.AverageRating);
        Assert.Equal(155, saved.ReadingLogCount);
        Assert.Equal("Open Library", saved.Source);
        Assert.Equal(FixedNow, saved.RetrievedAt); // timestamp dal TimeProvider iniettato (testabile)
    }

    [Fact]
    public async Task SkipsUpsert_WhenBookNoLongerExists()
    {
        // Il libro può essere stato cancellato tra enqueue ed elaborazione: niente da arricchire, nessun upsert.
        _repository.Setup(r => r.GetByIdAsync(99)).ReturnsAsync((Book?)null);

        await _sut.EnrichAsync(99, CancellationToken.None);

        _snapshots.Verify(
            s => s.UpsertAsync(It.IsAny<BookPopularitySnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
        _client.Verify(
            c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PropagatesFailure_WhenExternalThrows_NoSnapshotPersisted()
    {
        // Outage: il client lancia → l'eccezione propaga (il dispatcher la isola e riproverà), nessuno snapshot.
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ExternalServiceUnavailableException("Open Library"));

        await Assert.ThrowsAsync<ExternalServiceUnavailableException>(
            () => _sut.EnrichAsync(7, CancellationToken.None));

        _snapshots.Verify(
            s => s.UpsertAsync(It.IsAny<BookPopularitySnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        var log = NullLogger<PopularityEnricher>.Instance;
        var time = TimeProvider.System;
        Assert.Throws<ArgumentNullException>(() => new PopularityEnricher(null!, _client.Object, _snapshots.Object, time, log));
        Assert.Throws<ArgumentNullException>(() => new PopularityEnricher(_repository.Object, null!, _snapshots.Object, time, log));
        Assert.Throws<ArgumentNullException>(() => new PopularityEnricher(_repository.Object, _client.Object, null!, time, log));
        Assert.Throws<ArgumentNullException>(() => new PopularityEnricher(_repository.Object, _client.Object, _snapshots.Object, null!, log));
        Assert.Throws<ArgumentNullException>(() => new PopularityEnricher(_repository.Object, _client.Object, _snapshots.Object, time, null!));
    }
}
