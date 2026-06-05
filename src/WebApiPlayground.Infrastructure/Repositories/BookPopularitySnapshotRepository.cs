using Microsoft.EntityFrameworkCore;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Infrastructure.Repositories;

/// <summary>
/// Store EF Core degli snapshot di popolarità (tabella <c>BookPopularitySnapshots</c>, 1:1 col libro).
/// Lettura <c>AsNoTracking</c> (sola consultazione come fallback); <see cref="UpsertAsync"/> insert-or-update
/// per <c>BookId</c>. Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
public sealed class BookPopularitySnapshotRepository : IBookPopularitySnapshotRepository
{
    private readonly PlaygroundDbContext _context;

    public BookPopularitySnapshotRepository(PlaygroundDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public async Task<BookPopularitySnapshot?> GetByBookIdAsync(int bookId, CancellationToken cancellationToken) =>
        await _context.BookPopularitySnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.BookId == bookId, cancellationToken);

    public async Task UpsertAsync(BookPopularitySnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var existing = await _context.BookPopularitySnapshots
            .FirstOrDefaultAsync(s => s.BookId == snapshot.BookId, cancellationToken);

        if (existing is null)
        {
            _context.BookPopularitySnapshots.Add(snapshot);
        }
        else
        {
            existing.AverageRating = snapshot.AverageRating;
            existing.RatingsCount = snapshot.RatingsCount;
            existing.WantToReadCount = snapshot.WantToReadCount;
            existing.CurrentlyReadingCount = snapshot.CurrentlyReadingCount;
            existing.AlreadyReadCount = snapshot.AlreadyReadCount;
            existing.ReadingLogCount = snapshot.ReadingLogCount;
            existing.Source = snapshot.Source;
            existing.RetrievedAt = snapshot.RetrievedAt;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }
}
