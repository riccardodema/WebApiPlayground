using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Interfaces;

public interface IBooksService
{
    Task<PagedResult<BookDto>> GetBooksAsync(BooksQueryParameters query);
    Task<BookDto?> GetBookByIdAsync(int id);
    Task<BookDto> CreateBookAsync(CreateBookDto dto);

    /// <summary>Aggiorna un libro con optimistic concurrency: <paramref name="expectedVersion"/> è il
    /// token (rowversion) atteso dal client (header <c>If-Match</c>). <c>null</c> = libro inesistente;
    /// lancia <see cref="Concurrency.ConcurrencyConflictException"/> se la versione è stale.</summary>
    Task<BookDto?> UpdateBookAsync(int id, UpdateBookDto dto, byte[] expectedVersion);

    /// <summary>Elimina un libro con optimistic concurrency. <c>false</c> = libro inesistente; lancia
    /// <see cref="Concurrency.ConcurrencyConflictException"/> se la versione è stale.</summary>
    Task<bool> DeleteBookAsync(int id, byte[] expectedVersion);

    // Letture in forma v2 (autore annidato). Stessa fetch di v1, diversa proiezione (vedi BooksService).
    Task<PagedResult<BookDetailsDto>> GetBooksDetailedAsync(BooksQueryParameters query);
    Task<BookDetailsDto?> GetBookDetailsByIdAsync(int id);
}
