using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Books;

/// <summary>
/// Endpoint di lettura <b>v2</b> (autore annidato): lista paginata/ordinata (incluso l'ordinamento
/// per autore, che attraversa il ramo dedicato del repository) e 404 sul singolo libro inesistente.
/// La differenza di forma v1/v2 sullo stesso libro è già coperta da <c>ApiVersioningTests</c>.
/// </summary>
[Collection("Integration")]
public class BooksV2EndpointsTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _readClient;

    public BooksV2EndpointsTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _readClient = factory.CreateClientWithScope(BooksPermissions.ScopeRead);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedCatalogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var buzzati = new Author { FullName = "Dino Buzzati" };
        var asimov = new Author { FullName = "Isaac Asimov" };
        db.Authors.AddRange(buzzati, asimov);
        db.Books.AddRange(
            new Book { Title = "Il deserto dei tartari", Author = buzzati },
            new Book { Title = "Fondazione", Author = asimov },
            new Book { Title = "Io, robot", Author = asimov });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task V2_list_returns_nested_authors_with_paging_metadata()
    {
        await SeedCatalogAsync();

        var response = await _readClient.GetAsync("/api/v2/books?pageNumber=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, root.GetProperty("items").GetArrayLength());
        // Forma v2: author è un oggetto { id, fullName }, non una stringa piatta.
        var author = root.GetProperty("items")[0].GetProperty("author");
        Assert.True(author.GetProperty("id").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(author.GetProperty("fullName").GetString()));
    }

    [Fact]
    public async Task V2_list_sorted_by_author_orders_across_the_join()
    {
        await SeedCatalogAsync();

        // sortBy=author attraversa il ramo di ordinamento sull'entità correlata (Author.FullName).
        var response = await _readClient.GetAsync("/api/v2/books?sortBy=author&sortDir=asc&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = json.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("author").GetProperty("fullName").GetString())
            .ToList();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
        Assert.Equal("Dino Buzzati", names.First());
    }

    [Fact]
    public async Task V2_get_by_id_returns_404_for_missing_book()
    {
        var response = await _readClient.GetAsync("/api/v2/books/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
