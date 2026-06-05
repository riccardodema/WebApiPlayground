using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// L'endpoint <c>GET /api/v1/books/{id}/popularity</c> end-to-end. La dipendenza esterna (Open Library) è
/// sempre uno stub: la factory base risponde successo, un host separato (<c>WithWebHostBuilder</c>) risponde
/// fallimento per esercitare la resilienza fino al <b>503</b>. Si verifica: 200 con i segnali, 404 libro
/// assente, 401/403 auth, 503 ProblemDetails (RFC 7807) con <c>correlationId</c>/<c>traceId</c> + Retry-After.
/// </summary>
[Collection("Integration")]
public class BookPopularityEndpointTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _readClient;

    // Host con lo stub esterno che fallisce sempre + resilienza "veloce" (retry minimi, backoff ~0): la
    // pipeline si esaurisce in fretta → 503 senza rallentare il test.
    private readonly WebApplicationFactory<Program> _unavailableFactory;

    public BookPopularityEndpointTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _readClient = factory.CreateClientWithScope(BooksPermissions.ScopeRead);

        _unavailableFactory = factory.WithWebHostBuilder(builder =>
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
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedBookAsync(string title = "Dune", string author = "Frank Herbert")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var authorEntity = new Author { FullName = author };
        db.Authors.Add(authorEntity);
        await db.SaveChangesAsync();
        var book = new Book { Title = title, AuthorId = authorEntity.Id };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    private HttpClient UnavailableReadClient()
    {
        var client = _unavailableFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, BooksPermissions.ScopeRead);
        return client;
    }

    [Fact]
    public async Task Returns200WithSignals_ForExistingBook()
    {
        var bookId = await SeedBookAsync();

        var response = await _readClient.GetAsync($"/api/v1/books/{bookId}/popularity");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<BookPopularityDto>();
        Assert.NotNull(dto);
        Assert.Equal(bookId, dto!.BookId);
        Assert.Equal("Dune", dto.Title);
        Assert.Equal("Frank Herbert", dto.Author);
        Assert.Equal(4.5, dto.AverageRating);
        Assert.Equal(155, dto.ReadingLogCount);
        Assert.Equal("Open Library", dto.Source);
    }

    [Fact]
    public async Task Returns404_WhenBookDoesNotExist()
    {
        var response = await _readClient.GetAsync("/api/v1/books/999999/popularity");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Returns401_WhenAnonymous()
    {
        var anonymous = _factory.CreateAnonymousClient();

        var response = await anonymous.GetAsync("/api/v1/books/1/popularity");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Returns403_WhenScopeLacksRead()
    {
        var client = _factory.CreateClientWithScope("Unrelated.Scope");

        var response = await client.GetAsync("/api/v1/books/1/popularity");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Caching_ReducesUpstreamCalls_ForRepeatedRequests()
    {
        var bookId = await SeedBookAsync();

        // Host dedicato con uno stub di cui tratteniamo il riferimento (per leggere il contatore) e cache
        // fredda (FusionCache per-host): la 1ª GET è una miss (1 chiamata a OL), la 2ª una hit (0 chiamate).
        var stub = PopularityHttpStub.AlwaysOk();
        using var cachingFactory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHttpClient<OpenLibraryPopularityClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => stub)));

        var client = cachingFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, BooksPermissions.ScopeRead);

        var first = await client.GetAsync($"/api/v1/books/{bookId}/popularity");
        var second = await client.GetAsync($"/api/v1/books/{bookId}/popularity");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, stub.Invocations); // la 2ª risposta è servita dalla cache → niente nuova chiamata a OL
    }

    [Fact]
    public async Task Returns503ProblemDetails_WithRetryAfter_WhenDependencyUnavailable()
    {
        var bookId = await SeedBookAsync();

        var response = await UnavailableReadClient().GetAsync($"/api/v1/books/{bookId}/popularity");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.RetryAfter is not null, "La 503 deve includere l'header Retry-After.");

        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(503, problem.RootElement.GetProperty("status").GetInt32());
        Assert.True(problem.RootElement.TryGetProperty("correlationId", out _),
            "Il ProblemDetails 503 deve includere correlationId (correlazione coi log).");
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _),
            "Il ProblemDetails 503 deve includere traceId (correlazione con le trace).");
    }
}
