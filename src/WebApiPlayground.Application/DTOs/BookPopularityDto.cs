namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Risposta dell'endpoint <c>GET /api/v{n}/books/{id}/popularity</c>: combina l'identità del libro del
/// nostro DB (<see cref="BookId"/>/<see cref="Title"/>/<see cref="Author"/>) con i segnali di popolarità
/// recuperati dalla dipendenza esterna. Le metriche sono nullable: la fonte può non avere un match per quel
/// titolo o non esporre una metrica. <see cref="Source"/> rende esplicita la provenienza (es. "Open Library")
/// e <see cref="RetrievedAt"/> quando è stata letta (i dati esterni non sono "verità" del nostro dominio).
/// </summary>
public record BookPopularityDto(
    int BookId,
    string Title,
    string Author,
    double? AverageRating,
    int? RatingsCount,
    int? WantToReadCount,
    int? CurrentlyReadingCount,
    int? AlreadyReadCount,
    int? ReadingLogCount,
    string Source,
    DateTimeOffset RetrievedAt);
