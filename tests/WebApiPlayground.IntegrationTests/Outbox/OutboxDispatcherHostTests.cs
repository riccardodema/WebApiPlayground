using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// Factory dedicata che tiene <b>attivo</b> il vero <c>OutboxDispatcher</c> (hosted service), su un
/// <b>container isolato</b> (proprio <c>MsSqlContainer</c>, ereditato dalla base) così il polling continuo non
/// interferisce con gli altri test. Polling stretto per consegne rapide. Usata solo da
/// <see cref="OutboxDispatcherHostTests"/>.
/// </summary>
public sealed class DispatcherEnabledApiFactory : PlaygroundApiFactory
{
    protected override bool DisableOutboxDispatcher => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Polling stretto: il loop reale consegna in fretta → il test attende poco (con timeout generoso).
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Outbox:PollingInterval"] = "00:00:00.200",
        }));
        base.ConfigureWebHost(builder);
    }
}

/// <summary>
/// Testa il <b>loop di hosting reale</b> dell'<c>OutboxDispatcher</c> (non l'unità di lavoro guidata a mano):
/// nessun <c>DrainOutboxAsync</c>, è il <c>BackgroundService</c> che polla da solo e consegna. Deterministico
/// senza essere a tempo: ambiente <b>isolato</b> (container dedicato → nessuna interferenza) + attesa dell'esito
/// con timeout generoso (l'esito <i>avverrà</i>, tipicamente in &lt;1s). Sta nella collection "Integration" così
/// resta <b>serializzato</b> rispetto ai test col listener OTel globale [L18]. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
[Collection("Integration")]
public class OutboxDispatcherHostTests : IClassFixture<DispatcherEnabledApiFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly DispatcherEnabledApiFactory _factory;
    private readonly HttpClient _writeClient;

    public OutboxDispatcherHostTests(DispatcherEnabledApiFactory factory)
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

    private async Task<int> SeedBookAsync()
    {
        var authorId = await SeedAuthorAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var book = new Book { Title = "Dune", AuthorId = authorId };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    // Attende che il vero dispatcher abbia persistito lo snapshot (polling con timeout generoso → niente flakiness).
    private async Task<BookPopularitySnapshot?> WaitForSnapshotAsync(int bookId)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
                var snapshot = await db.BookPopularitySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.BookId == bookId);
                if (snapshot is not null)
                    return snapshot;
            }
            await Task.Delay(150);
        }

        return null;
    }

    private async Task<bool> AllOutboxProcessedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        return !await db.OutboxMessages.AsNoTracking().AnyAsync(m => m.ProcessedAt == null);
    }

    [Fact]
    public async Task RealDispatcher_AutomaticallyEnrichesPostedBook()
    {
        var authorId = await SeedAuthorAsync();

        var create = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Dune", authorId));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);

        // NESSUN drain manuale: è il vero OutboxDispatcher (hosted, polling 200ms) che consegna da solo.
        var snapshot = await WaitForSnapshotAsync(created!.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(created.Id, snapshot!.BookId);
        Assert.Equal("Open Library", snapshot.Source);

        Assert.True(await AllOutboxProcessedAsync()); // la riga outbox è stata marcata processata dal loop reale
    }

    [Fact]
    public async Task RealDispatcher_ProcessesPreexistingUnprocessedMessage()
    {
        // Durabilità attraverso il loop reale: una riga non processata (come sopravvissuta a un restart) viene
        // raccolta e consegnata dal dispatcher al primo giro, senza alcun intervento esterno.
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

        var snapshot = await WaitForSnapshotAsync(bookId);
        Assert.NotNull(snapshot);
        Assert.Equal("Open Library", snapshot!.Source);

        Assert.True(await AllOutboxProcessedAsync());
    }
}
