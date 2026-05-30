using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Interfaces;

public interface IBooksService
{
    Task<ICollection<BookDto>> GetAllBooksAsync();
    Task<BookDto?> GetBookByIdAsync(int id);
    Task<BookDto> CreateBookAsync(CreateBookDto dto);
    Task<bool> DeleteBookAsync(int id);
}
