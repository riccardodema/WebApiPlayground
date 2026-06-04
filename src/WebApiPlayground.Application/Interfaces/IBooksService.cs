using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Interfaces;

public interface IBooksService
{
    Task<PagedResult<BookDto>> GetBooksAsync(BooksQueryParameters query);
    Task<BookDto?> GetBookByIdAsync(int id);
    Task<BookDto> CreateBookAsync(CreateBookDto dto);
    Task<BookDto?> UpdateBookAsync(int id, UpdateBookDto dto);
    Task<bool> DeleteBookAsync(int id);

    // Letture in forma v2 (autore annidato). Stessa fetch di v1, diversa proiezione (vedi BooksService).
    Task<PagedResult<BookDetailsDto>> GetBooksDetailedAsync(BooksQueryParameters query);
    Task<BookDetailsDto?> GetBookDetailsByIdAsync(int id);
}
