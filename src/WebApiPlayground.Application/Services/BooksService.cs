using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Services;

public class BooksService : IBooksService
{
    private readonly IBookRepository _repository;

    public BooksService(IBookRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<ICollection<BookDto>> GetAllBooksAsync()
    {
        var books = await _repository.GetAllAsync();
        return books.Select(MapToDto).ToList();
    }

    public async Task<BookDto?> GetBookByIdAsync(int id)
    {
        var book = await _repository.GetByIdAsync(id);
        return book is null ? null : MapToDto(book);
    }

    public async Task<BookDto> CreateBookAsync(CreateBookDto dto)
    {
        var book = new Book { Title = dto.Title, AuthorId = dto.AuthorId };
        var created = await _repository.CreateAsync(book);
        return MapToDto(created);
    }

    public async Task<bool> DeleteBookAsync(int id)
    {
        return await _repository.DeleteAsync(id);
    }

    private static BookDto MapToDto(Book book) =>
        new(book.Id, book.Title, book.Author?.FullName ?? string.Empty);
}
