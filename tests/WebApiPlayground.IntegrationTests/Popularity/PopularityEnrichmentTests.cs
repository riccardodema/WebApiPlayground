using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.Infrastructure.Popularity;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Popularity;

/// <summary>
/// Arricchimento popolarità asincrono end-to-end: una <c>POST /books</c> accoda un work item, il
/// <c>PopularityEnrichmentWorker</c> (hosted service reale, avviato dalla factory) chiama il client (stub) e
/// persiste lo snapshot durevole. Si verifica anche il <b>fallback d'outage</b>: con la dipendenza giù e cache
/// fredda, lo snapshot serve un 200 last-known-good invece del 503. Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
[Collection("Integration")]
public class PopularityEnrichmentTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _readClient;
    private readonly HttpClient _writeClient;

    public PopularityEnrichmentTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _readClient = factory.CreateClientWithScope(BooksPermissions.ScopeRead);
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

    // Il worker è asincrono: si fa polling sullo store finché lo snapshot compare (o scade il timeout).
    private async Task<BookPopularitySnapshot?> WaitForSnapshotAsync(int bookId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
            var snapshot = await db.BookPopularitySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.BookId == bookId);
            if (snapshot is not null)
                return snapshot;
            await Task.Delay(100);
        }

        return null;
    }

    [Fact]
    public async Task PostBook_TriggersBackgroundEnrichment_PersistingASnapshot()
    {
        var authorId = await SeedAuthorAsync();

        var create = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Dune", authorId));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);

        var snapshot = await WaitForSnapshotAsync(created!.Id, TimeSpan.FromSeconds(10));

        Assert.NotNull(snapshot);
        Assert.Equal(created.Id, snapshot!.BookId);
        Assert.Equal("Open Library", snapshot.Source);
        // Lo stub di successo (factory base) risponde i segnali noti: il worker li ha persistiti.
        Assert.Equal(4.5, snapshot.AverageRating);
        Assert.Equal(155, snapshot.ReadingLogCount);

        // L'endpoint serve regolarmente 200 (cache calda o live).
        var read = await _readClient.GetAsync($"/api/v1/books/{created.Id}/popularity");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task GetPopularity_ServesDurableSnapshot_WhenDependencyUnavailable()
    {
        var authorId = await SeedAuthorAsync();
        var create = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Dune", authorId));
        var created = await create.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);

        // Aspetta che il worker (factory base, stub di successo) abbia persistito lo snapshot.
        var snapshot = await WaitForSnapshotAsync(created!.Id, TimeSpan.FromSeconds(10));
        Assert.NotNull(snapshot);

        // Host separato con dipendenza giù e cache fredda (FusionCache per-host): live fallisce, ma lo snapshot
        // durevole (stesso DB) regge → 200 last-known-good invece del 503.
        using var unavailableFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BookPopularity:Resilience:Retry:MaxRetryAttempts"] = "1",
                ["BookPopularity:Resilience:Retry:BaseDelay"] = "00:00:00.001",
                ["BookPopularity:Resilience:AttemptTimeout"] = "00:00:02",
            }));
            builder.ConfigureServices(services =>
                services.AddHttpClient<OpenLibraryPopularityClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => PopularityHttpStub.Always(HttpStatusCode.ServiceUnavailable)));
        });

        var client = unavailableFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, BooksPermissions.ScopeRead);

        var response = await client.GetAsync($"/api/v1/books/{created.Id}/popularity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<BookPopularityDto>();
        Assert.NotNull(dto);
        Assert.Equal(4.5, dto!.AverageRating); // proviene dallo snapshot durevole
    }
}
