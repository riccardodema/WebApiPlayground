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
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.RateLimiting;

/// <summary>
/// Rate limiting end-to-end. Una factory dedicata abbassa i limiti (read=3, write=2) così il 429
/// scatta in poche richieste; ogni test usa un'identità distinta (header <c>X-Test-User</c>) per
/// isolare la partizione, dato che il limiter in-memory è un singleton condiviso. Si verifica:
/// 429 come ProblemDetails (RFC 7807) con <c>Retry-After</c>; le policy read/write sono bucket
/// indipendenti; client diversi non condividono la quota.
/// </summary>
[Collection("Integration")]
public class RateLimitingTests : IAsyncLifetime
{
    private const int ReadLimit = 3;
    private const int WriteLimit = 2;

    private readonly PlaygroundApiFactory _factory;
    private readonly WebApplicationFactory<Program> _tinyFactory;

    public RateLimitingTests(PlaygroundApiFactory factory)
    {
        _factory = factory;

        // Host separato (limiter in-memory pulito) con limiti minuscoli: l'override di config vince
        // sui valori altissimi impostati dalla factory base.
        _tinyFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimiting:Read:PermitLimit"] = ReadLimit.ToString(),
                ["RateLimiting:Read:WindowSeconds"] = "60",
                ["RateLimiting:Read:SegmentsPerWindow"] = "1",
                ["RateLimiting:Read:QueueLimit"] = "0",
                ["RateLimiting:Write:PermitLimit"] = WriteLimit.ToString(),
                ["RateLimiting:Write:WindowSeconds"] = "60",
                ["RateLimiting:Write:SegmentsPerWindow"] = "1",
                ["RateLimiting:Write:QueueLimit"] = "0",
            })));
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient ClientFor(string scope, string userId)
    {
        var client = _tinyFactory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, scope);
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        return client;
    }

    private async Task<int> SeedAuthorAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = "Rate Limit Author" };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return author.Id;
    }

    [Fact]
    public async Task Write_OverLimit_Returns429_AsProblemDetails_WithRetryAfter()
    {
        var authorId = await SeedAuthorAsync();
        var client = ClientFor(BooksPermissions.ScopeReadWrite, userId: Guid.NewGuid().ToString());
        var dto = new CreateBookDto("Dune", authorId);

        // Le prime WriteLimit scritture passano (non 429).
        for (var i = 0; i < WriteLimit; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/books", dto);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, ok.StatusCode);
        }

        // La successiva supera il limite → 429.
        var rejected = await client.PostAsJsonAsync("/api/books", dto);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType?.MediaType);
        Assert.True(rejected.Headers.Contains("Retry-After"), "La 429 deve esporre l'header Retry-After.");

        var problem = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal("Too many requests.", problem.GetProperty("title").GetString());
        // Coerenza con gli altri errori: correlationId/traceId per correlare risposta e log.
        Assert.True(problem.TryGetProperty("correlationId", out _), "Il ProblemDetails deve includere il correlationId.");
        Assert.True(problem.TryGetProperty("traceId", out _), "Il ProblemDetails deve includere il traceId.");
    }

    [Fact]
    public async Task Read_OverLimit_Returns429()
    {
        var client = ClientFor(BooksPermissions.ScopeRead, userId: Guid.NewGuid().ToString());

        for (var i = 0; i < ReadLimit; i++)
        {
            var ok = await client.GetAsync("/api/books");
            Assert.NotEqual(HttpStatusCode.TooManyRequests, ok.StatusCode);
        }

        var rejected = await client.GetAsync("/api/books");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task WriteExhausted_DoesNotConsume_ReadQuota_ForSamePartition()
    {
        var authorId = await SeedAuthorAsync();
        var client = ClientFor(BooksPermissions.ScopeReadWrite, userId: Guid.NewGuid().ToString());
        var dto = new CreateBookDto("Solaris", authorId);

        // Esaurisci le scritture fino al 429 (write=2 → la terza è respinta).
        for (var i = 0; i < WriteLimit; i++)
            await client.PostAsJsonAsync("/api/books", dto);
        var writeRejected = await client.PostAsJsonAsync("/api/books", dto);
        Assert.Equal(HttpStatusCode.TooManyRequests, writeRejected.StatusCode);

        // Le letture restano disponibili: la policy read è un bucket separato dalla write.
        var read = await client.GetAsync("/api/books");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task DifferentClients_DoNotShareTheSameBucket()
    {
        var authorId = await SeedAuthorAsync();
        var dto = new CreateBookDto("Hyperion", authorId);

        var clientA = ClientFor(BooksPermissions.ScopeReadWrite, userId: "client-A-" + Guid.NewGuid());
        var clientB = ClientFor(BooksPermissions.ScopeReadWrite, userId: "client-B-" + Guid.NewGuid());

        // A esaurisce la propria quota di scrittura fino al 429.
        for (var i = 0; i < WriteLimit; i++)
            await clientA.PostAsJsonAsync("/api/books", dto);
        var aRejected = await clientA.PostAsJsonAsync("/api/books", dto);
        Assert.Equal(HttpStatusCode.TooManyRequests, aRejected.StatusCode);

        // B ha una quota piena: la sua prima richiesta non è limitata.
        var bFirst = await clientB.PostAsJsonAsync("/api/books", dto);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, bFirst.StatusCode);
    }
}
