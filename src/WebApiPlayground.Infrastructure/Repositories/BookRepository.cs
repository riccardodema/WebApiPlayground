using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Infrastructure.Repositories;

public class BookRepository : IBookRepository
{
    private readonly PlaygroundDbContext _context;
    private readonly ILogger<BookRepository> _logger;

    public BookRepository(PlaygroundDbContext context, ILogger<BookRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<ICollection<Book>> GetAllAsync()
    {
        _logger.LogDebug("Executing query: SELECT all books with authors");

        var books = await _context.Books
            .Include(b => b.Author)
            .ToListAsync();

        _logger.LogDebug("Query returned {BookCount} book(s)", books.Count);
        return books;
    }

    public async Task<Book?> GetByIdAsync(int id)
    {
        _logger.LogDebug("Executing query: SELECT book by ID {BookId} with author", id);

        var book = await _context.Books
            .Include(b => b.Author)
            .FirstOrDefaultAsync(b => b.Id == id);

        if (book is null)
            _logger.LogDebug("No book found with ID {BookId}", id);
        else
            _logger.LogDebug("Book {BookId} found in database: '{BookTitle}'", id, book.Title);

        return book;
    }

    public async Task<Book> CreateAsync(Book book)
    {
        _logger.LogDebug(
            "Inserting book into database — Title: '{BookTitle}', AuthorId: {AuthorId}",
            book.Title, book.AuthorId);

        _context.Books.Add(book);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex,
                "Database error while inserting book '{BookTitle}' for author {AuthorId}",
                book.Title, book.AuthorId);
            throw;
        }

        var created = await _context.Books
            .Include(b => b.Author)
            .FirstAsync(b => b.Id == book.Id);

        _logger.LogDebug(
            "Book inserted successfully — ID: {BookId}, Title: '{BookTitle}'",
            created.Id, created.Title);

        return created;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        _logger.LogDebug("Looking up book {BookId} for deletion", id);

        var book = await _context.Books.FindAsync(id);

        if (book is null)
        {
            _logger.LogDebug("Book {BookId} not found in database — deletion skipped", id);
            return false;
        }

        _context.Books.Remove(book);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Database error while deleting book {BookId}", id);
            throw;
        }

        _logger.LogDebug("Book {BookId} deleted from database", id);
        return true;
    }
}
