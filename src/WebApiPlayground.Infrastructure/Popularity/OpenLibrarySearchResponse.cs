using System.Text.Json.Serialization;

namespace WebApiPlayground.Infrastructure.Popularity;

/// <summary>
/// Contratto JSON di <c>openlibrary.org/search.json</c>, <b>confinato a Infrastructure</b>: è la forma
/// dell'upstream, non un tipo di dominio. Mappato in <see cref="WebApiPlayground.Application.Popularity.BookPopularity"/>
/// dal client, così i layer superiori non vedono mai questa rappresentazione. Solo i campi richiesti via
/// <c>fields=</c> (payload minimo). Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
internal sealed record OpenLibrarySearchResponse(
    [property: JsonPropertyName("docs")] IReadOnlyList<OpenLibraryDoc>? Docs);

internal sealed record OpenLibraryDoc(
    [property: JsonPropertyName("ratings_average")] double? RatingsAverage,
    [property: JsonPropertyName("ratings_count")] int? RatingsCount,
    [property: JsonPropertyName("want_to_read_count")] int? WantToReadCount,
    [property: JsonPropertyName("currently_reading_count")] int? CurrentlyReadingCount,
    [property: JsonPropertyName("already_read_count")] int? AlreadyReadCount,
    [property: JsonPropertyName("readinglog_count")] int? ReadingLogCount);
