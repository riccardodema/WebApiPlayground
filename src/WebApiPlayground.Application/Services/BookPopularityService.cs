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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BookPopularityService> _logger;

    public BookPopularityService(
        IBookRepository repository,
        IBookPopularityClient popularityClient,
        TimeProvider timeProvider,
        ILogger<BookPopularityService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(popularityClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _popularityClient = popularityClient;
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

        // Una sola chiamata esterna, avvolta dalla resilienza nel client. Un esaurimento si propaga come
        // ExternalServiceUnavailableException (→ 503): non lo catturiamo qui per non mascherare il guasto.
        var popularity = await _popularityClient.GetPopularityAsync(book.Title, author, cancellationToken);

        if (popularity is null)
            _logger.LogInformation(
                "No popularity match for book {BookId} ('{BookTitle}') in {Source}",
                bookId, book.Title, _popularityClient.SourceName);

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
            _popularityClient.SourceName,
            _timeProvider.GetUtcNow());
    }
}
