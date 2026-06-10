using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Outbox;

/// <summary>
/// Outbox transazionale end-to-end: una <c>POST /books</c> scrive il libro e la riga outbox <b>nella stessa
/// transazione</b>; il processore (<c>OutboxProcessor</c>, guidato qui via <c>DrainOutboxAsync</c> invece del
/// dispatcher in background) la consegna marcandola <c>ProcessedAt</c> e persiste lo snapshot. Si verifica anche
/// la <b>durabilità</b>: una riga non processata inserita a mano (come dopo un crash/restart) viene comunque
/// consegnata. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
[Collection("Integration")]
public class OutboxProcessingTests : IAsyncLifetime
{
    // camelCase + case-insensitive: stesse opzioni del producer/consumer (IntegrationEventSerialization.Options).
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _writeClient;

    public OutboxProcessingTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedAuthorAsync(string fullName = "Frank Herbert")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = fullName };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return author.Id;
    }

    private async Task<int> SeedBookAsync(string title = "Dune")
    {
        var authorId = await SeedAuthorAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var book = new Book { Title = title, AuthorId = authorId };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    private async Task<OutboxMessage?> GetOutboxMessageAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        return await db.OutboxMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Type == PopularityEnrichmentRequested.TypeName);
    }

    private async Task<BookPopularitySnapshot?> GetSnapshotAsync(int bookId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        return await db.BookPopularitySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.BookId == bookId);
    }

    [Fact]
    public async Task PostBook_WritesPopularityOutboxMessage_Transactionally()
    {
        var authorId = await SeedAuthorAsync();

        var create = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Dune", authorId));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);

        // La riga outbox è stata committata insieme al libro (dispatcher disattivato nei test → resta da
        // processare): la troviamo col payload che riferisce il libro appena creato.
        var message = await GetOutboxMessageAsync();
        Assert.NotNull(message);
        Assert.Null(message!.ProcessedAt);
        var payload = JsonSerializer.Deserialize<PopularityEnrichmentRequested>(message.Payload, SerializerOptions);
        Assert.NotNull(payload);
        Assert.Equal(created!.Id, payload!.BookId);
    }

    [Fact]
    public async Task Processor_ProcessesUnprocessedMessage_AndMarksItProcessed()
    {
        // Durabilità: una riga outbox non processata (come sopravvissuta a un restart) deve essere consegnata.
        var bookId = await SeedBookAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Type = PopularityEnrichmentRequested.TypeName,
                Payload = JsonSerializer.Serialize(new PopularityEnrichmentRequested(bookId, null), SerializerOptions),
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var processed = await _factory.DrainOutboxAsync();
        Assert.True(processed >= 1);

        // Il processore ha arricchito → lo snapshot durevole esiste…
        var snapshot = await GetSnapshotAsync(bookId);
        Assert.NotNull(snapshot);
        Assert.Equal("Open Library", snapshot!.Source);

        // …e la riga outbox è marcata processata (at-least-once: marcata solo a successo).
        var message = await GetOutboxMessageAsync();
        Assert.NotNull(message);
        Assert.NotNull(message!.ProcessedAt);
    }

    [Fact]
    public async Task Processor_IsolatesFailure_LeavesMessageUnprocessed_AndCountsAttempt()
    {
        // Un messaggio "velenoso" (tipo sconosciuto) non deve fermare il processore: resta non-processato
        // (verrà riprovato) e conta il tentativo. Oltre MaxAttempts smetterà di riprovarlo (poison).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Type = "UnknownEventType",
                Payload = "{}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var processed = await _factory.DrainOutboxAsync();
        Assert.Equal(1, processed); // esaminato…

        using var readScope = _factory.Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var message = await readDb.OutboxMessages.AsNoTracking().FirstAsync(m => m.Type == "UnknownEventType");
        Assert.Null(message.ProcessedAt);   // …ma non marcato consegnato
        Assert.Equal(1, message.Attempts);  // tentativo contato
        Assert.NotNull(message.Error);
    }
}
