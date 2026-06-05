using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;
using WebApiPlayground.Application.Popularity;

namespace WebApiPlayground.Infrastructure.Popularity;

/// <summary>
/// Implementazione di <see cref="IBookPopularityClient"/> verso <c>openlibrary.org/search.json</c> come
/// <b>HttpClient tipizzato</b>. La <b>resilienza</b> (retry/circuit-breaker/timeout) è agganciata fuori, nella
/// pipeline registrata in <see cref="BookPopularityRegistration"/>: qui ci si concentra su come costruire la
/// richiesta, mappare la risposta e <b>tradurre</b> gli errori di trasporto/Polly in
/// <see cref="ExternalServiceUnavailableException"/> — così l'eccezione d'infrastruttura non risale oltre il
/// suo layer (stesso principio della <c>ConcurrencyConflictException</c>). Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
public sealed class OpenLibraryPopularityClient : IBookPopularityClient
{
    // Solo i campi che ci servono → payload minimo (banda, memoria, parsing).
    private const string Fields =
        "ratings_average,ratings_count,want_to_read_count,currently_reading_count,already_read_count,readinglog_count";

    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenLibraryPopularityClient> _logger;

    public OpenLibraryPopularityClient(HttpClient httpClient, ILogger<OpenLibraryPopularityClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _logger = logger;
    }

    public string SourceName => "Open Library";

    public async Task<BookPopularity?> GetPopularityAsync(string title, string? author, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null; // Senza un titolo non c'è nulla da cercare.

        var requestUri = BuildQuery(title, author);

        try
        {
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Qui arrivano i fallimenti già "esauriti" dalla pipeline (es. 5xx ritentati a vuoto): per il
                // chiamante la popolarità è indisponibile → 503. Non esponiamo lo status upstream al client.
                _logger.LogWarning(
                    "{Source} returned {StatusCode} for title '{Title}'", SourceName, (int)response.StatusCode, title);
                throw new ExternalServiceUnavailableException(SourceName, RetryAfterFrom(response));
            }

            var payload = await response.Content.ReadFromJsonAsync<OpenLibrarySearchResponse>(cancellationToken);
            var doc = payload?.Docs is { Count: > 0 } docs ? docs[0] : null;

            return doc is null ? null : Map(doc);
        }
        catch (BrokenCircuitException ex)
        {
            // Circuito aperto: fail-fast senza nemmeno toccare la rete (protegge noi e l'upstream in difficoltà).
            // RetryAfter = null → l'handler 503 ricade sulla BreakDuration configurata (Polly 8.x non espone qui
            // quanto manca alla riapertura del circuito).
            _logger.LogWarning(ex, "{Source} circuit is open — failing fast", SourceName);
            throw new ExternalServiceUnavailableException(SourceName, innerException: ex);
        }
        catch (TimeoutRejectedException ex)
        {
            _logger.LogWarning(ex, "{Source} request timed out", SourceName);
            throw new ExternalServiceUnavailableException(SourceName, innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "{Source} request failed at transport level", SourceName);
            throw new ExternalServiceUnavailableException(SourceName, innerException: ex);
        }
        catch (JsonException ex)
        {
            // Risposta 2xx ma corpo non interpretabile: è un problema dell'upstream, non del client → 503.
            _logger.LogWarning(ex, "{Source} returned an unparsable body", SourceName);
            throw new ExternalServiceUnavailableException(SourceName, innerException: ex);
        }
    }

    // Host fisso da BaseAddress; title/author URL-encoded come query string (mai host/schema/path) → niente SSRF.
    private static string BuildQuery(string title, string? author)
    {
        var query = $"/search.json?fields={Fields}&limit=1&title={Uri.EscapeDataString(title)}";
        if (!string.IsNullOrWhiteSpace(author))
            query += $"&author={Uri.EscapeDataString(author)}";
        return query;
    }

    private static BookPopularity Map(OpenLibraryDoc doc) => new(
        doc.RatingsAverage,
        doc.RatingsCount,
        doc.WantToReadCount,
        doc.CurrentlyReadingCount,
        doc.AlreadyReadCount,
        doc.ReadingLogCount);

    private static TimeSpan? RetryAfterFrom(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;
        if (retryAfter.Delta is { } delta)
            return delta;
        if (retryAfter.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }
}
