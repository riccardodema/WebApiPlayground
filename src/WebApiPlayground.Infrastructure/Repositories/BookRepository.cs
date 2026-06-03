using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Querying;
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

    public async Task<(IReadOnlyList<Book> Items, int TotalCount)> GetPagedAsync(
        int pageNumber, int pageSize, BookSortField sortBy, SortDirection direction)
    {
        _logger.LogDebug(
            "Executing paged query: page {PageNumber} (size {PageSize}), sort {SortBy} {SortDir}",
            pageNumber, pageSize, sortBy, direction);

        IQueryable<Book> source = _context.Books.Include(b => b.Author);

        var ordered = sortBy switch
        {
            BookSortField.Title => OrderByWithIdTiebreaker(source, b => b.Title, direction),
            BookSortField.Author => OrderByWithIdTiebreaker(source, b => b.Author!.FullName, direction),
            _ => OrderById(source, direction),
        };

        var totalCount = await ordered.CountAsync();
        var items = await ordered
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        _logger.LogDebug(
            "Paged query returned {BookCount} of {TotalCount} book(s)", items.Count, totalCount);
        return (items, totalCount);
    }

    // ORDER BY <chiave> con tiebreaker deterministico sulla PK (Id): senza, l'OFFSET non è
    // ripetibile quando si ordina per colonne non univoche (Title, Author.FullName). Vedi [L07].
    private static IOrderedQueryable<Book> OrderByWithIdTiebreaker<TKey>(
        IQueryable<Book> source, Expression<Func<Book, TKey>> keySelector, SortDirection direction)
    {
        var ordered = direction == SortDirection.Descending
            ? source.OrderByDescending(keySelector)
            : source.OrderBy(keySelector);
        return ordered.ThenBy(b => b.Id);
    }

    // Ordinamento per PK: già univoco, nessun tiebreaker necessario.
    private static IOrderedQueryable<Book> OrderById(IQueryable<Book> source, SortDirection direction) =>
        direction == SortDirection.Descending
            ? source.OrderByDescending(b => b.Id)
            : source.OrderBy(b => b.Id);

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
