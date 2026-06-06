using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.Concurrency;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Application.Querying;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Outbox;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Infrastructure.Repositories;

public class BookRepository : IBookRepository
{
    private readonly PlaygroundDbContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BookRepository> _logger;

    public BookRepository(PlaygroundDbContext context, TimeProvider timeProvider, ILogger<BookRepository> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
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

    public async Task<Book> CreateAsync(Book book, Func<int, IntegrationEvent> outboxEvent)
    {
        _logger.LogDebug(
            "Inserting book into database — Title: '{BookTitle}', AuthorId: {AuthorId}",
            book.Title, book.AuthorId);

        _context.Books.Add(book);

        // Transactional outbox: libro + riga outbox committano insieme. L'Id è IDENTITY (noto solo dopo
        // l'INSERT), quindi prima si salva il libro, poi si materializza l'evento con l'Id appena assegnato,
        // il tutto in un'unica transazione esplicita → atomicità (crash prima del commit = rollback di entrambi).
        await using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            await _context.SaveChangesAsync();
            EnqueueOutbox(outboxEvent(book.Id));
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
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

    public async Task<Book?> UpdateAsync(Book book, Func<int, IntegrationEvent> outboxEvent)
    {
        _logger.LogDebug("Looking up book {BookId} for update", book.Id);

        var existing = await _context.Books.FindAsync(book.Id);

        if (existing is null)
        {
            // Nessun libro → niente da aggiornare e nessun evento outbox da scrivere.
            _logger.LogDebug("Book {BookId} not found in database — update skipped", book.Id);
            return null;
        }

        existing.Title = book.Title;
        existing.AuthorId = book.AuthorId;

        // Optimistic concurrency: forziamo l'OriginalValue del token alla versione ATTESA dal client
        // (arrivata in book.RowVersion via If-Match). Senza questo, EF userebbe la versione appena letta
        // dal FindAsync e l'UPDATE non rileverebbe mai un conflitto. Stale → 0 righe → DbUpdateConcurrencyException.
        _context.Entry(existing).Property(b => b.RowVersion).OriginalValue = book.RowVersion;

        // Transactional outbox: l'UPDATE del libro e la riga outbox committano insieme (o rollback insieme).
        // Se la versione è stale il SaveChanges lancia → rollback → nessun evento scritto.
        await using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            await _context.SaveChangesAsync();
            EnqueueOutbox(outboxEvent(existing.Id));
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex,
                "Concurrency conflict updating book {BookId}: the supplied version is stale", book.Id);
            throw new ConcurrencyConflictException(book.Id, ex);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex,
                "Database error while updating book {BookId} (Title '{BookTitle}', AuthorId {AuthorId})",
                book.Id, book.Title, book.AuthorId);
            throw;
        }

        var updated = await _context.Books
            .Include(b => b.Author)
            .FirstAsync(b => b.Id == existing.Id);

        _logger.LogDebug("Book {BookId} updated in database: '{BookTitle}'", updated.Id, updated.Title);
        return updated;
    }

    // Aggiunge la riga outbox al change tracker: verrà persistita nel SaveChanges della transazione corrente.
    private void EnqueueOutbox(IntegrationEvent message) =>
        _context.OutboxMessages.Add(OutboxMessageFactory.Create(message, _timeProvider.GetUtcNow()));

    public async Task<bool> DeleteAsync(int id, byte[] expectedVersion)
    {
        _logger.LogDebug("Looking up book {BookId} for deletion", id);

        var book = await _context.Books.FindAsync(id);

        if (book is null)
        {
            _logger.LogDebug("Book {BookId} not found in database — deletion skipped", id);
            return false;
        }

        // Stessa storia dell'update: il DELETE è condizionato alla versione attesa (If-Match).
        _context.Entry(book).Property(b => b.RowVersion).OriginalValue = expectedVersion;
        _context.Books.Remove(book);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex,
                "Concurrency conflict deleting book {BookId}: the supplied version is stale", id);
            throw new ConcurrencyConflictException(id, ex);
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
