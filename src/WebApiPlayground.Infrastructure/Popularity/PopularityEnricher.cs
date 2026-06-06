using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Infrastructure.Popularity;

/// <summary>
/// Arricchimento popolarità riusabile (estratto dal vecchio <c>PopularityEnrichmentWorker</c> su canale):
/// carica il libro, chiama il client resiliente+cachato (che scalda la cache <c>(title,author)</c>) e
/// <b>upserta lo snapshot durevole</b> (fallback d'outage). Se la dipendenza è giù il client lancia
/// <see cref="ExternalServiceUnavailableException"/>: il chiamante (dispatcher) la isola e riproverà
/// (at-least-once). L'upsert è idempotente (1:1 col libro). Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public sealed class PopularityEnricher : IPopularityEnricher
{
    private readonly IBookRepository _repository;
    private readonly IBookPopularityClient _client;
    private readonly IBookPopularitySnapshotRepository _snapshots;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PopularityEnricher> _logger;

    public PopularityEnricher(
        IBookRepository repository,
        IBookPopularityClient client,
        IBookPopularitySnapshotRepository snapshots,
        TimeProvider timeProvider,
        ILogger<PopularityEnricher> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _client = client;
        _snapshots = snapshots;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task EnrichAsync(int bookId, CancellationToken cancellationToken)
    {
        var book = await _repository.GetByIdAsync(bookId);
        if (book is null)
        {
            // Il libro può essere stato cancellato tra l'enqueue e l'elaborazione: niente da arricchire.
            _logger.LogDebug("Book {BookId} no longer exists — skipping popularity enrichment", bookId);
            return;
        }

        // Riusa il client resiliente + cachato: questa chiamata scalda anche la cache (title,author).
        var popularity = await _client.GetPopularityAsync(book.Title, book.Author?.FullName, cancellationToken);

        var snapshot = new BookPopularitySnapshot
        {
            BookId = book.Id,
            AverageRating = popularity?.AverageRating,
            RatingsCount = popularity?.RatingsCount,
            WantToReadCount = popularity?.WantToReadCount,
            CurrentlyReadingCount = popularity?.CurrentlyReadingCount,
            AlreadyReadCount = popularity?.AlreadyReadCount,
            ReadingLogCount = popularity?.ReadingLogCount,
            Source = _client.SourceName,
            RetrievedAt = _timeProvider.GetUtcNow(),
        };

        await _snapshots.UpsertAsync(snapshot, cancellationToken);
        _logger.LogDebug("Popularity snapshot upserted for book {BookId} from {Source}", book.Id, _client.SourceName);
    }
}
