using Microsoft.EntityFrameworkCore;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Repositories;
using WebApiPlayground.Tests.Persistence;
using Xunit;

namespace WebApiPlayground.Tests.Repositories;

/// <summary>
/// Comportamento dello store dello snapshot di popolarità su un DB reale (SQLite): lookup per
/// BookId (trovato/assente), e l'UPSERT — insert sulla prima scrittura, UPDATE in place sulla
/// seconda (1:1 col libro). È l'idempotenza che rende sicura la redelivery del consumer.
/// </summary>
public sealed class BookPopularitySnapshotRepositoryTests : IDisposable
{
    private readonly SqlitePlaygroundDb _db = SqlitePlaygroundDb.Create();
    private readonly BookPopularitySnapshotRepository _sut;

    public BookPopularitySnapshotRepositoryTests()
    {
        _sut = new BookPopularitySnapshotRepository(_db.Context);
    }

    public void Dispose() => _db.Dispose();

    private async Task<int> SeedBookAsync()
    {
        var book = new Book { Title = "Dune", Author = new Author { FullName = "Herbert" } };
        _db.Context.Books.Add(book);
        await _db.Context.SaveChangesAsync();
        _db.Context.ChangeTracker.Clear();
        return book.Id;
    }

    private static BookPopularitySnapshot Snapshot(int bookId, double rating, int ratings, string source = "Open Library") => new()
    {
        BookId = bookId,
        AverageRating = rating,
        RatingsCount = ratings,
        WantToReadCount = 10,
        CurrentlyReadingCount = 2,
        AlreadyReadCount = 5,
        ReadingLogCount = 17,
        Source = source,
        RetrievedAt = new DateTimeOffset(2026, 6, 13, 10, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task GetByBookId_returns_null_when_no_snapshot_exists()
    {
        var bookId = await SeedBookAsync();

        Assert.Null(await _sut.GetByBookIdAsync(bookId, CancellationToken.None));
    }

    [Fact]
    public async Task GetByBookId_returns_the_snapshot_for_the_right_book()
    {
        var bookId = await SeedBookAsync();
        await _sut.UpsertAsync(Snapshot(bookId, 4.2, 50), CancellationToken.None);

        var found = await _sut.GetByBookIdAsync(bookId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(bookId, found!.BookId);
        Assert.Equal(4.2, found.AverageRating);
        Assert.Equal(50, found.RatingsCount);
    }

    [Fact]
    public async Task First_upsert_inserts_a_new_row()
    {
        var bookId = await SeedBookAsync();

        await _sut.UpsertAsync(Snapshot(bookId, 4.0, 10), CancellationToken.None);

        using var fresh = _db.CreateFreshContext();
        Assert.Equal(1, await fresh.BookPopularitySnapshots.CountAsync());
    }

    [Fact]
    public async Task Second_upsert_updates_in_place_keeping_one_row()
    {
        var bookId = await SeedBookAsync();
        await _sut.UpsertAsync(Snapshot(bookId, 4.0, 10, source: "Open Library"), CancellationToken.None);

        // Scope/context separato: deve TROVARE la riga sul DB e aggiornarla, non inserirne una seconda.
        using (var second = _db.CreateFreshContext())
        {
            var repo = new BookPopularitySnapshotRepository(second);
            await repo.UpsertAsync(Snapshot(bookId, 4.9, 99, source: "Refreshed"), CancellationToken.None);
        }

        using var fresh = _db.CreateFreshContext();
        var row = Assert.Single(await fresh.BookPopularitySnapshots.ToListAsync());
        Assert.Equal(4.9, row.AverageRating);    // tutti i campi aggiornati...
        Assert.Equal(99, row.RatingsCount);
        Assert.Equal("Refreshed", row.Source);
    }

    [Fact]
    public async Task Snapshots_for_different_books_are_independent()
    {
        var first = await SeedBookAsync();
        var secondBook = new Book { Title = "Foundation", Author = new Author { FullName = "Asimov" } };
        _db.Context.Books.Add(secondBook);
        await _db.Context.SaveChangesAsync();

        await _sut.UpsertAsync(Snapshot(first, 4.0, 10), CancellationToken.None);
        await _sut.UpsertAsync(Snapshot(secondBook.Id, 3.0, 5), CancellationToken.None);

        Assert.Equal(4.0, (await _sut.GetByBookIdAsync(first, CancellationToken.None))!.AverageRating);
        Assert.Equal(3.0, (await _sut.GetByBookIdAsync(secondBook.Id, CancellationToken.None))!.AverageRating);
    }

    [Fact]
    public void Constructor_rejects_a_null_context() =>
        Assert.Throws<ArgumentNullException>(() => new BookPopularitySnapshotRepository(null!));
}
