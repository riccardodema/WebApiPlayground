using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Popularity;

namespace WebApiPlayground.Application.Services;

/// <summary>
/// Compone i dati del nostro dominio (libro dal DB) con l'arricchimento esterno (popolarità). Il repository
/// dà titolo/autore; il client esterno dà i segnali di popolarità. La resilienza è invisibile qui: vive nella
/// pipeline Polly attorno al client (Infrastructure). Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
public class BookPopularityService : IBookPopularityService
{
    private readonly IBookRepository _repository;
    private readonly IBookPopularityClient _popularityClient;
    private readonly IBookPopularitySnapshotRepository _snapshots;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BookPopularityService> _logger;

    public BookPopularityService(
        IBookRepository repository,
        IBookPopularityClient popularityClient,
        IBookPopularitySnapshotRepository snapshots,
        TimeProvider timeProvider,
        ILogger<BookPopularityService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(popularityClient);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _popularityClient = popularityClient;
        _snapshots = snapshots;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<BookPopularityDto?> GetBookPopularityAsync(int bookId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Resolving popularity for book {BookId}", bookId);

        var book = await _repository.GetByIdAsync(bookId);
        if (book is null)
        {
            _logger.LogDebug("Book {BookId} not found — no popularity to resolve", bookId);
            return null;
        }

        var author = book.Author?.FullName;

        // Percorso felice: cache → live (il client governa resilienza e cache). Niente snapshot qui: il read
        // normale serve sempre il valore fresco/cachato.
        BookPopularity? popularity;
        string source;
        DateTimeOffset retrievedAt;

        try
        {
            popularity = await _popularityClient.GetPopularityAsync(book.Title, author, cancellationToken);
            source = _popularityClient.SourceName;
            retrievedAt = _timeProvider.GetUtcNow();

            if (popularity is null)
                _logger.LogInformation(
                    "No popularity match for book {BookId} ('{BookTitle}') in {Source}",
                    bookId, book.Title, source);
        }
        catch (ExternalServiceUnavailableException)
        {
            // Outage E fail-safe della cache vuoto (es. cache fredda dopo un restart): invece del 503,
            // serviamo l'ultimo snapshot durevole (last-known-good) con la SUA freschezza/provenienza.
            // (Niente await in un filtro catch → lookup qui dentro con rethrow se non c'è fallback.)
            var snapshot = await _snapshots.GetByBookIdAsync(bookId, cancellationToken);
            if (snapshot is null)
                throw; // nessuno snapshot → si propaga → 503

            _logger.LogWarning(
                "{Source} unavailable — serving durable snapshot for book {BookId} (retrieved {RetrievedAt:o})",
                _popularityClient.SourceName, bookId, snapshot.RetrievedAt);

            popularity = new BookPopularity(
                snapshot.AverageRating,
                snapshot.RatingsCount,
                snapshot.WantToReadCount,
                snapshot.CurrentlyReadingCount,
                snapshot.AlreadyReadCount,
                snapshot.ReadingLogCount);
            source = snapshot.Source;
            retrievedAt = snapshot.RetrievedAt;
        }

        return new BookPopularityDto(
            book.Id,
            book.Title,
            author ?? string.Empty,
            popularity?.AverageRating,
            popularity?.RatingsCount,
            popularity?.WantToReadCount,
            popularity?.CurrentlyReadingCount,
            popularity?.AlreadyReadCount,
            popularity?.ReadingLogCount,
            source,
            retrievedAt);
    }
}
