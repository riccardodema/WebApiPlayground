using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.BackgroundProcessing;
using WebApiPlayground.Application.Diagnostics;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Infrastructure.BackgroundProcessing;

/// <summary>
/// Consumer della coda di arricchimento popolarità. Per ogni libro: chiama il client resiliente <b>già
/// esistente</b> (che scalda la cache <c>(title,author)</c> → primo read caldo) e <b>persiste lo snapshot</b>
/// durevole (fallback d'outage). La chiamata esterna è così fuori dal path di scrittura. Se la dipendenza è
/// giù, il client lancia <see cref="ExternalServiceUnavailableException"/>: la base la isola (item saltato,
/// snapshot precedente intatto). Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
public sealed class PopularityEnrichmentWorker : BackgroundQueueWorker<PopularityEnrichmentRequest>
{
    private const string EnrichActivityName = "Popularity.Enrich";

    public PopularityEnrichmentWorker(
        IBackgroundTaskQueue<PopularityEnrichmentRequest> queue,
        IServiceScopeFactory scopeFactory,
        ILogger<PopularityEnrichmentWorker> logger)
        : base(queue, scopeFactory, logger)
    {
    }

    protected override string WorkerName => nameof(PopularityEnrichmentWorker);

    protected override async Task ProcessAsync(
        IServiceProvider services, PopularityEnrichmentRequest item, CancellationToken cancellationToken)
    {
        // Span agganciato alla trace della write che ha generato il work item (correlazione oltre il confine async).
        using var activity = BackgroundProcessingDiagnostics.StartProcessActivity(EnrichActivityName, item.ParentContext);
        activity?.SetTag("book.id", item.BookId);

        var repository = services.GetRequiredService<IBookRepository>();
        var client = services.GetRequiredService<IBookPopularityClient>();
        var snapshots = services.GetRequiredService<IBookPopularitySnapshotRepository>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILogger<PopularityEnrichmentWorker>>();

        var book = await repository.GetByIdAsync(item.BookId);
        if (book is null)
        {
            // Il libro può essere stato cancellato tra l'enqueue e l'elaborazione: niente da arricchire.
            logger.LogDebug("Book {BookId} no longer exists — skipping popularity enrichment", item.BookId);
            return;
        }

        // Riusa il client resiliente + cachato: questa chiamata scalda anche la cache (title,author).
        var popularity = await client.GetPopularityAsync(book.Title, book.Author?.FullName, cancellationToken);

        var snapshot = new BookPopularitySnapshot
        {
            BookId = book.Id,
            AverageRating = popularity?.AverageRating,
            RatingsCount = popularity?.RatingsCount,
            WantToReadCount = popularity?.WantToReadCount,
            CurrentlyReadingCount = popularity?.CurrentlyReadingCount,
            AlreadyReadCount = popularity?.AlreadyReadCount,
            ReadingLogCount = popularity?.ReadingLogCount,
            Source = client.SourceName,
            RetrievedAt = timeProvider.GetUtcNow(),
        };

        await snapshots.UpsertAsync(snapshot, cancellationToken);
        logger.LogDebug("Popularity snapshot upserted for book {BookId} from {Source}", book.Id, client.SourceName);
    }
}
