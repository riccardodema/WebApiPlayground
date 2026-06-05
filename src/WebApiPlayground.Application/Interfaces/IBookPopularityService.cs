using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Interfaces;

public interface IBookPopularityService
{
    /// <summary>
    /// Recupera i segnali di popolarità di un libro: prima lo carica dal nostro DB (per titolo/autore), poi
    /// interroga la dipendenza esterna. Restituisce <c>null</c> se il libro non esiste localmente (→ 404).
    /// Se il libro esiste ma la fonte non ha un match, il DTO ha le metriche a <c>null</c> (resta 200).
    /// Propaga <see cref="Popularity.ExternalServiceUnavailableException"/> se la resilienza è esaurita (→ 503).
    /// </summary>
    Task<BookPopularityDto?> GetBookPopularityAsync(int bookId, CancellationToken cancellationToken);
}
