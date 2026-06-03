using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Caching;

/// <summary>
/// HTTP caching end-to-end: i GET espongono ETag + Cache-Control; un conditional GET con
/// If-None-Match combaciante riceve 304 senza body; dopo una scrittura l'ETag cambia (la cache
/// server-side è invalidata e la rappresentazione è nuova).
/// </summary>
[Collection("Integration")]
public class ETagCachingTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _readClient;
    private readonly HttpClient _writeClient;

    public ETagCachingTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _readClient = factory.CreateClientWithScope(BooksPermissions.ScopeRead);
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Book> SeedBookAsync(string title = "Test Book")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = "Test Author" };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        var book = new Book { Title = title, AuthorId = author.Id };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book;
    }

    [Fact]
    public async Task GetById_SetsETagAndCacheControlHeaders()
    {
        var book = await SeedBookAsync();

        var response = await _readClient.GetAsync($"/api/books/{book.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.True(response.Headers.CacheControl!.Private);
        Assert.True(response.Headers.CacheControl.NoCache);
    }

    [Fact]
    public async Task GetById_WithMatchingIfNoneMatch_Returns304WithoutBody()
    {
        var book = await SeedBookAsync();

        var first = await _readClient.GetAsync($"/api/books/{book.Id}");
        var etag = first.Headers.ETag!;

        var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/books/{book.Id}");
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await _readClient.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Empty(body);
        // Il 304 ribadisce l'ETag corrente.
        Assert.Equal(etag, second.Headers.ETag);
    }

    [Fact]
    public async Task GetList_SetsETagHeader()
    {
        await SeedBookAsync();

        var response = await _readClient.GetAsync("/api/books");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task GetById_AfterUpdate_ProducesDifferentETag()
    {
        var book = await SeedBookAsync(title: "Original");

        var before = await _readClient.GetAsync($"/api/books/{book.Id}");
        var etagBefore = before.Headers.ETag;

        var update = await _writeClient.PutAsJsonAsync(
            $"/api/books/{book.Id}", new UpdateBookDto("Updated", book.AuthorId));
        update.EnsureSuccessStatusCode();

        var after = await _readClient.GetAsync($"/api/books/{book.Id}");
        var dto = await after.Content.ReadFromJsonAsync<BookDto>();

        Assert.Equal("Updated", dto!.Title);                 // cache server-side invalidata
        Assert.NotEqual(etagBefore, after.Headers.ETag);     // rappresentazione cambiata → ETag diverso
    }
}
