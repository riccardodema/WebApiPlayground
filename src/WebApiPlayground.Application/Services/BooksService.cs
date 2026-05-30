using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Services;

public class BooksService : IBooksService
{
    private readonly IBookRepository _repository;
    private readonly ILogger<BooksService> _logger;

    public BooksService(IBookRepository repository, ILogger<BooksService> logger)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(logger);
        _repository = repository;
        _logger = logger;
    }

    public async Task<ICollection<BookDto>> GetAllBooksAsync()
    {
        _logger.LogDebug("Retrieving all books from repository");

        var books = await _repository.GetAllAsync();
        var dtos = books.Select(MapToDto).ToList();

        _logger.LogDebug("Mapped {BookCount} book(s) to DTO", dtos.Count);
        return dtos;
    }

    public async Task<BookDto?> GetBookByIdAsync(int id)
    {
        _logger.LogDebug("Looking up book {BookId} in repository", id);

        var book = await _repository.GetByIdAsync(id);

        if (book is null)
        {
            _logger.LogDebug("Repository returned no book for ID {BookId}", id);
            return null;
        }

        _logger.LogDebug("Book {BookId} retrieved: '{BookTitle}' by {AuthorName}",
            id, book.Title, book.Author?.FullName ?? "unknown author");

        return MapToDto(book);
    }

    public async Task<BookDto> CreateBookAsync(CreateBookDto dto)
    {
        _logger.LogDebug(
            "Building book entity — Title: '{BookTitle}', AuthorId: {AuthorId}",
            dto.Title, dto.AuthorId);

        var book = new Book { Title = dto.Title, AuthorId = dto.AuthorId };
        var created = await _repository.CreateAsync(book);

        _logger.LogDebug(
            "Book entity persisted with ID {BookId}, author resolved as '{AuthorName}'",
            created.Id, created.Author?.FullName ?? "unknown");

        return MapToDto(created);
    }

    public async Task<bool> DeleteBookAsync(int id)
    {
        _logger.LogDebug("Requesting deletion of book {BookId} from repository", id);

        var deleted = await _repository.DeleteAsync(id);

        if (!deleted)
            _logger.LogDebug("Repository reported book {BookId} does not exist — nothing deleted", id);
        else
            _logger.LogDebug("Repository confirmed book {BookId} has been deleted", id);

        return deleted;
    }

    private static BookDto MapToDto(Book book) =>
        new(book.Id, book.Title, book.Author?.FullName ?? string.Empty);
}
