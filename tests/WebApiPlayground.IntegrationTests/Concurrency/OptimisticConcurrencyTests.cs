using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Concurrency;

/// <summary>
/// Optimistic concurrency end-to-end: il singolo libro espone un <c>ETag</c> (token di versione =
/// rowversion); le scritture (PUT/DELETE) richiedono <c>If-Match</c>. ETag corrente → 200/204 e nuovo
/// ETag; ETag stale → 412; If-Match assente → 428; malformato → 400. Lo scenario "due client"
/// dimostra la prevenzione del <b>lost update</b>.
/// </summary>
[Collection("Integration")]
public class OptimisticConcurrencyTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _readClient;
    private readonly HttpClient _writeClient;

    public OptimisticConcurrencyTests(PlaygroundApiFactory factory)
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

    private async Task<EntityTagHeaderValue> GetETagAsync(int id)
    {
        var response = await _readClient.GetAsync($"/api/v1/books/{id}");
        Assert.NotNull(response.Headers.ETag);
        return response.Headers.ETag!;
    }

    private async Task<HttpResponseMessage> PutAsync(int id, UpdateBookDto dto, EntityTagHeaderValue? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/books/{id}")
        {
            Content = JsonContent.Create(dto),
        };
        if (ifMatch is not null)
            request.Headers.IfMatch.Add(ifMatch);
        return await _writeClient.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeleteAsync(int id, EntityTagHeaderValue ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/books/{id}");
        request.Headers.IfMatch.Add(ifMatch);
        return await _writeClient.SendAsync(request);
    }

    [Fact]
    public async Task GetById_ExposesStrongETag()
    {
        var book = await SeedBookAsync();

        var response = await _readClient.GetAsync($"/api/v1/books/{book.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak); // strong: byte-identici alla versione corrente
    }

    [Fact]
    public async Task Put_WithCurrentETag_Succeeds_AndReturnsNewETag()
    {
        var book = await SeedBookAsync(title: "Original");
        var etag = await GetETagAsync(book.Id);

        var response = await PutAsync(book.Id, new UpdateBookDto("Updated", book.AuthorId), etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // La risposta di scrittura porta il NUOVO ETag (versione aggiornata) → niente GET intermedia.
        Assert.NotNull(response.Headers.ETag);
        Assert.NotEqual(etag, response.Headers.ETag);

        var dto = await response.Content.ReadFromJsonAsync<BookDto>();
        Assert.Equal("Updated", dto!.Title);
    }

    [Fact]
    public async Task Put_WithStaleETag_Returns412()
    {
        var book = await SeedBookAsync(title: "Original");
        var staleEtag = await GetETagAsync(book.Id);

        // Prima scrittura: consuma l'ETag e fa avanzare la versione.
        var first = await PutAsync(book.Id, new UpdateBookDto("First", book.AuthorId), staleEtag);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Seconda scrittura con l'ETag ormai vecchio → conflitto.
        var second = await PutAsync(book.Id, new UpdateBookDto("Second", book.AuthorId), staleEtag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Put_WithoutIfMatch_Returns428()
    {
        var book = await SeedBookAsync();

        var response = await PutAsync(book.Id, new UpdateBookDto("X", book.AuthorId), ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithMalformedIfMatch_Returns400()
    {
        var book = await SeedBookAsync();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/books/{book.Id}")
        {
            Content = JsonContent.Create(new UpdateBookDto("X", book.AuthorId)),
        };
        // ETag non quotato / non base64: malformato.
        request.Headers.TryAddWithoutValidation("If-Match", "\"not base64!\"");
        var response = await _writeClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_WithStaleETag_Returns412()
    {
        var book = await SeedBookAsync();
        var staleEtag = await GetETagAsync(book.Id);

        // Cambia la versione con una PUT, poi prova a cancellare con l'ETag vecchio.
        var update = await PutAsync(book.Id, new UpdateBookDto("Changed", book.AuthorId), staleEtag);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var delete = await DeleteAsync(book.Id, staleEtag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, delete.StatusCode);
        // La risorsa è ancora lì (la cancellazione condizionale è stata rifiutata).
        var stillThere = await _readClient.GetAsync($"/api/v1/books/{book.Id}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task TwoClients_LostUpdateIsPrevented()
    {
        var book = await SeedBookAsync(title: "Shared");

        // A e B leggono la stessa versione.
        var etagForA = await GetETagAsync(book.Id);
        var etagForB = etagForA; // stesso token: entrambi partono dalla stessa rappresentazione

        // A scrive per primo: successo, la versione avanza.
        var writeA = await PutAsync(book.Id, new UpdateBookDto("Edited by A", book.AuthorId), etagForA);
        Assert.Equal(HttpStatusCode.OK, writeA.StatusCode);

        // B scrive con l'ETag ormai stale: niente sovrascrittura silenziosa → 412.
        var writeB = await PutAsync(book.Id, new UpdateBookDto("Edited by B", book.AuthorId), etagForB);
        Assert.Equal(HttpStatusCode.PreconditionFailed, writeB.StatusCode);

        // La modifica di A sopravvive (quella di B non l'ha cancellata).
        var current = await _readClient.GetAsync($"/api/v1/books/{book.Id}");
        var dto = await current.Content.ReadFromJsonAsync<BookDto>();
        Assert.Equal("Edited by A", dto!.Title);
    }

    [Fact]
    public async Task Conflict412_ProblemDetailsCarriesCorrelationId()
    {
        var book = await SeedBookAsync();
        var staleEtag = await GetETagAsync(book.Id);
        await PutAsync(book.Id, new UpdateBookDto("First", book.AuthorId), staleEtag);

        var conflict = await PutAsync(book.Id, new UpdateBookDto("Second", book.AuthorId), staleEtag);

        Assert.Equal(HttpStatusCode.PreconditionFailed, conflict.StatusCode);
        Assert.True(conflict.Headers.TryGetValues("X-Correlation-Id", out var headerValues));
        var correlationHeader = headerValues!.First();

        using var doc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var correlationProp));
        Assert.Equal(correlationHeader, correlationProp.GetString());
    }
}
