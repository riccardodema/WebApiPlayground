using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Versioning;

/// <summary>
/// API versioning end-to-end. Stessa risorsa sotto <c>/api/v1/books</c> e <c>/api/v2/books</c>: la v2
/// evolve la forma di lettura (autore annidato) mentre le scritture sono condivise. Si verifica:
/// stesso libro letto in v1 (autore piatto) e v2 (autore annidato); header <c>api-supported-versions</c>;
/// versione inesistente → 400; scrittura condivisa funzionante su entrambe le versioni.
/// </summary>
[Collection("Integration")]
public class ApiVersioningTests : IAsyncLifetime
{
    private const string SupportedVersionsHeader = "api-supported-versions";

    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _readClient;
    private readonly HttpClient _writeClient;

    public ApiVersioningTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _readClient = factory.CreateClientWithScope(BooksPermissions.ScopeRead);
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(int AuthorId, string AuthorName)> SeedAuthorAsync(string fullName = "Robert C. Martin")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = fullName };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return (author.Id, author.FullName);
    }

    private async Task<int> SeedBookAsync(int authorId, string title = "Clean Code")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var book = new Book { Title = title, AuthorId = authorId };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    [Fact]
    public async Task SameBook_HasFlatAuthorInV1_AndNestedAuthorInV2()
    {
        var (authorId, authorName) = await SeedAuthorAsync();
        var bookId = await SeedBookAsync(authorId, "Clean Code");

        var v1 = await _readClient.GetFromJsonAsync<JsonElement>($"{ApiTestRoutes.BooksV1}/{bookId}");
        var v2 = await _readClient.GetFromJsonAsync<JsonElement>($"{ApiTestRoutes.BooksV2}/{bookId}");

        // v1: autore come stringa piatta.
        Assert.Equal("Clean Code", v1.GetProperty("title").GetString());
        Assert.Equal(authorName, v1.GetProperty("authorFullName").GetString());
        Assert.False(v1.TryGetProperty("author", out _), "v1 non deve avere l'oggetto author annidato.");

        // v2: autore come oggetto annidato { id, fullName }.
        Assert.Equal("Clean Code", v2.GetProperty("title").GetString());
        var author = v2.GetProperty("author");
        Assert.Equal(authorId, author.GetProperty("id").GetInt32());
        Assert.Equal(authorName, author.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task EveryResponse_ReportsSupportedVersionsHeader()
    {
        var response = await _readClient.GetAsync(ApiTestRoutes.BooksV1);

        Assert.True(response.Headers.Contains(SupportedVersionsHeader),
            "ReportApiVersions deve esporre l'header api-supported-versions.");
        var supported = string.Join(",", response.Headers.GetValues(SupportedVersionsHeader));
        Assert.Contains("1.0", supported);
        Assert.Contains("2.0", supported);
    }

    [Fact]
    public async Task UnknownVersion_Returns404_BecauseVersionIsPartOfTheRoute()
    {
        // Con il versioning per SEGMENTO URL la versione fa parte della rotta: una versione
        // inesistente (v3) non corrisponde ad alcun endpoint → 404 (non 400). Con i reader a
        // header/query si otterrebbe invece 400 + api-supported-versions. Vedi api-versioning.md.
        var response = await _readClient.GetAsync("/api/v3/books");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Writes_AreShared_CreateWorksUnderBothVersions()
    {
        var (authorId, _) = await SeedAuthorAsync();

        // La stessa create (contratto invariato) è servita sia da v1 sia da v2.
        var v1Created = await _writeClient.PostAsJsonAsync(ApiTestRoutes.BooksV1, new CreateBookDto("Dune", authorId));
        var v2Created = await _writeClient.PostAsJsonAsync(ApiTestRoutes.BooksV2, new CreateBookDto("Hyperion", authorId));

        Assert.Equal(HttpStatusCode.Created, v1Created.StatusCode);
        Assert.Equal(HttpStatusCode.Created, v2Created.StatusCode);
        Assert.NotNull(v1Created.Headers.Location);
        Assert.NotNull(v2Created.Headers.Location);
    }
}
