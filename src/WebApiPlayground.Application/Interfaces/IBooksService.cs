using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Interfaces;

public interface IBooksService
{
    Task<PagedResult<BookDto>> GetBooksAsync(BooksQueryParameters query);
    Task<BookDto?> GetBookByIdAsync(int id);
    Task<BookDto> CreateBookAsync(CreateBookDto dto);
    Task<bool> DeleteBookAsync(int id);
}
