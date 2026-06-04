using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Idempotency;

/// <summary>
/// Idempotency end-to-end sul POST: la stessa <c>Idempotency-Key</c> rigioca la prima risposta senza
/// creare duplicati; la stessa chiave con payload diverso è respinta (422); senza chiave ogni POST
/// crea una risorsa. Chiavi GUID fresche per test (lo store memoria è condiviso dalla factory).
/// </summary>
[Collection("Integration")]
public class IdempotencyTests : IAsyncLifetime
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _writeClient;

    public IdempotencyTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedAuthorAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = "Test Author" };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return author.Id;
    }

    private async Task<int> CountBooksAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        return await db.Books.CountAsync();
    }

    private HttpRequestMessage PostBook(string idempotencyKey, CreateBookDto dto)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/books")
        {
            Content = JsonContent.Create(dto),
        };
        request.Headers.Add(IdempotencyKeyHeader, idempotencyKey);
        return request;
    }

    [Fact]
    public async Task SameKeyAndBody_ReplaysFirstResponse_AndCreatesExactlyOnce()
    {
        var authorId = await SeedAuthorAsync();
        var key = Guid.NewGuid().ToString();
        var dto = new CreateBookDto("Dune", authorId);

        var first = await _writeClient.SendAsync(PostBook(key, dto));
        var second = await _writeClient.SendAsync(PostBook(key, dto));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // La prima è eseguita, la seconda è un replay marcato.
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        Assert.True(second.Headers.Contains("Idempotency-Replayed"));

        // Stessa risorsa (Location) e stesso body.
        Assert.Equal(first.Headers.Location, second.Headers.Location);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());

        // Effetto exactly-once: un solo libro creato nonostante i due POST.
        Assert.Equal(1, await CountBooksAsync());
    }

    [Fact]
    public async Task SameKeyDifferentBody_Returns422()
    {
        var authorId = await SeedAuthorAsync();
        var key = Guid.NewGuid().ToString();

        var first = await _writeClient.SendAsync(PostBook(key, new CreateBookDto("Original", authorId)));
        var second = await _writeClient.SendAsync(PostBook(key, new CreateBookDto("Different", authorId)));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(1, await CountBooksAsync());
    }

    [Fact]
    public async Task WithoutKey_CreatesOnEachRequest()
    {
        var authorId = await SeedAuthorAsync();
        var dto = new CreateBookDto("Solaris", authorId);

        var first = await _writeClient.PostAsJsonAsync("/api/v1/books", dto);
        var second = await _writeClient.PostAsJsonAsync("/api/v1/books", dto);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2, await CountBooksAsync());
    }
}
