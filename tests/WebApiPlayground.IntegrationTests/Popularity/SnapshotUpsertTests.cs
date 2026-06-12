using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Popularity;

/// <summary>
/// Upsert dello snapshot di popolarità contro il DB reale: il secondo upsert per lo stesso libro
/// deve AGGIORNARE la riga esistente (1:1 col libro), non crearne un'altra — è l'idempotenza che
/// rende sicura la redelivery at-least-once del consumer (vedi outbox.md).
/// </summary>
[Collection("Integration")]
public class SnapshotUpsertTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;

    public SnapshotUpsertTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static BookPopularitySnapshot Snapshot(int bookId, double rating, int ratings) => new()
    {
        BookId = bookId,
        AverageRating = rating,
        RatingsCount = ratings,
        WantToReadCount = 10,
        CurrentlyReadingCount = 2,
        AlreadyReadCount = 5,
        ReadingLogCount = 17,
        Source = "Open Library",
        RetrievedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Second_upsert_updates_the_existing_row_instead_of_inserting()
    {
        int bookId;
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
            var book = new Book { Title = "Dune", Author = new Author { FullName = "Frank Herbert" } };
            db.Books.Add(book);
            await db.SaveChangesAsync();
            bookId = book.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IBookPopularitySnapshotRepository>();
            await repository.UpsertAsync(Snapshot(bookId, rating: 4.0, ratings: 10), CancellationToken.None);
        }

        // Scope separato: il secondo upsert deve TROVARE la riga sul DB, non nel change tracker.
        using (var scope = _factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IBookPopularitySnapshotRepository>();
            await repository.UpsertAsync(Snapshot(bookId, rating: 4.8, ratings: 99), CancellationToken.None);
        }

        using var assertScope = _factory.Services.CreateScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var rows = await assertDb.BookPopularitySnapshots.AsNoTracking()
            .Where(s => s.BookId == bookId).ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(4.8, row.AverageRating);
        Assert.Equal(99, row.RatingsCount);
    }
}
