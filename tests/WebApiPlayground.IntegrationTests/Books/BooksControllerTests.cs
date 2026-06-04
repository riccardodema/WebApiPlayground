using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Books;

[Collection("Integration")]
public class BooksControllerTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;

    // Token delegato (claim "scp") con permesso di sola lettura.
    private readonly HttpClient _readClient;

    // Token delegato (claim "scp") con permesso di scrittura (read/write).
    private readonly HttpClient _writeClient;

    public BooksControllerTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _readClient = factory.CreateClientWithScope(BooksPermissions.ScopeRead);
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // --- helpers ---

    private async Task<Author> SeedAuthorAsync(string fullName = "Test Author")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = fullName };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return author;
    }

    private async Task<Book> SeedBookAsync(int authorId, string title = "Test Book")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var book = new Book { Title = title, AuthorId = authorId };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book;
    }

    private async Task<Book?> FindBookInDbAsync(int id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        return await db.Books.FindAsync(id);
    }

    // GET corrente per ottenere l'ETag (token di concorrenza) da rimandare in If-Match sulle scritture.
    private async Task<EntityTagHeaderValue> GetCurrentETagAsync(int id)
    {
        var response = await _readClient.GetAsync($"/api/v1/books/{id}");
        Assert.NotNull(response.Headers.ETag);
        return response.Headers.ETag!;
    }

    // ETag dummy ben formato (base64 valido): supera il check di presenza/parsing dell'If-Match,
    // così i test "not found" raggiungono il 404 invece del 428/400.
    private static readonly EntityTagHeaderValue DummyETag = EntityTagHeaderValue.Parse("\"AAAAAAAAB9E=\"");

    private async Task<HttpResponseMessage> PutWithIfMatchAsync(
        HttpClient client, int id, UpdateBookDto dto, EntityTagHeaderValue ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/books/{id}")
        {
            Content = JsonContent.Create(dto),
        };
        request.Headers.IfMatch.Add(ifMatch);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> DeleteWithIfMatchAsync(
        HttpClient client, int id, EntityTagHeaderValue ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/books/{id}");
        request.Headers.IfMatch.Add(ifMatch);
        return await client.SendAsync(request);
    }

    // --- GET /api/v1/books ---

    [Fact]
    public async Task GetBooks_WhenEmpty_Returns200WithEmptyPage()
    {
        var response = await _readClient.GetAsync("/api/v1/books");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookDto>>();
        Assert.NotNull(page);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
        Assert.False(page.HasNext);
        Assert.False(page.HasPrevious);
    }

    [Fact]
    public async Task GetBooks_WhenBooksExist_Returns200WithAllBooks()
    {
        var author = await SeedAuthorAsync("George Orwell");
        await SeedBookAsync(author.Id, "1984");
        await SeedBookAsync(author.Id, "Animal Farm");

        var response = await _readClient.GetAsync("/api/v1/books");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookDto>>();
        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, b => b.Title == "1984" && b.AuthorFullName == "George Orwell");
        Assert.Contains(page.Items, b => b.Title == "Animal Farm" && b.AuthorFullName == "George Orwell");
    }

    [Fact]
    public async Task GetBooks_RespectsPageSizeAndReportsMetadata()
    {
        var author = await SeedAuthorAsync();
        await SeedBookAsync(author.Id, "A");
        await SeedBookAsync(author.Id, "B");
        await SeedBookAsync(author.Id, "C");

        var response = await _readClient.GetAsync("/api/v1/books?pageNumber=1&pageSize=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookDto>>();
        Assert.NotNull(page);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.True(page.HasNext);
        Assert.False(page.HasPrevious);
    }

    [Fact]
    public async Task GetBooks_SortByTitleDesc_OrdersResultsDescending()
    {
        var author = await SeedAuthorAsync();
        await SeedBookAsync(author.Id, "Alpha");
        await SeedBookAsync(author.Id, "Zulu");
        await SeedBookAsync(author.Id, "Mike");

        var response = await _readClient.GetAsync("/api/v1/books?sortBy=title&sortDir=desc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<BookDto>>();
        Assert.NotNull(page);
        Assert.Equal(new[] { "Zulu", "Mike", "Alpha" }, page.Items.Select(b => b.Title).ToArray());
    }

    [Theory]
    [InlineData("/api/v1/books?pageSize=0")]
    [InlineData("/api/v1/books?pageNumber=0")]
    [InlineData("/api/v1/books?pageSize=101")]
    public async Task GetBooks_WithInvalidPagingParams_Returns400(string url)
    {
        var response = await _readClient.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- GET /api/v1/books/{id} ---

    [Fact]
    public async Task GetBookById_WhenExists_Returns200WithCorrectData()
    {
        var author = await SeedAuthorAsync("J.K. Rowling");
        var book = await SeedBookAsync(author.Id, "Harry Potter");

        var response = await _readClient.GetAsync($"/api/v1/books/{book.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(dto);
        Assert.Equal(book.Id, dto.Id);
        Assert.Equal("Harry Potter", dto.Title);
        Assert.Equal("J.K. Rowling", dto.AuthorFullName);
    }

    [Fact]
    public async Task GetBookById_WhenNotFound_Returns404()
    {
        var response = await _readClient.GetAsync("/api/v1/books/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- POST /api/v1/books ---

    [Fact]
    public async Task CreateBook_Returns201AndPersistsBookInDb()
    {
        var author = await SeedAuthorAsync("Fyodor Dostoevsky");
        var dto = new CreateBookDto("Crime and Punishment", author.Id);

        var response = await _writeClient.PostAsJsonAsync("/api/v1/books", dto);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);
        Assert.True(created.Id > 0);
        Assert.Equal("Crime and Punishment", created.Title);
        Assert.Equal("Fyodor Dostoevsky", created.AuthorFullName);

        var dbBook = await FindBookInDbAsync(created.Id);
        Assert.NotNull(dbBook);
        Assert.Equal("Crime and Punishment", dbBook.Title);
        Assert.Equal(author.Id, dbBook.AuthorId);
    }

    [Fact]
    public async Task CreateBook_Returns201WithLocationHeaderPointingToNewResource()
    {
        var author = await SeedAuthorAsync();
        var dto = new CreateBookDto("New Book", author.Id);

        var response = await _writeClient.PostAsJsonAsync("/api/v1/books", dto);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var created = await response.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);
        Assert.EndsWith($"/api/v1/books/{created.Id}", response.Headers.Location!.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateBook_IncreasesBookCountInDb()
    {
        var author = await SeedAuthorAsync();
        await SeedBookAsync(author.Id, "Existing Book");

        var dto = new CreateBookDto("Brand New Book", author.Id);
        await _writeClient.PostAsJsonAsync("/api/v1/books", dto);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        Assert.Equal(2, await db.Books.CountAsync());
    }

    // --- POST /api/v1/books: validazione input (400 ProblemDetails) ---

    [Fact]
    public async Task CreateBook_WithEmptyTitle_Returns400ProblemDetailsWithFieldError()
    {
        var response = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("", 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.True(problem.Errors.ContainsKey("Title"));
        Assert.Contains(problem.Errors["Title"], m => m.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateBook_WithNonPositiveAuthorId_Returns400()
    {
        var response = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Valid Title", 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem.Errors.ContainsKey("AuthorId"));
    }

    [Fact]
    public async Task CreateBook_WithTooLongTitle_Returns400()
    {
        var dto = new CreateBookDto(new string('a', 101), 1);

        var response = await _writeClient.PostAsJsonAsync("/api/v1/books", dto);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem.Errors.ContainsKey("Title"));
    }

    [Fact]
    public async Task CreateBook_WithInvalidInput_ProblemDetailsCarriesCorrelationId()
    {
        var response = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("", 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-Id", out var headerValues));
        var correlationHeader = headerValues!.First();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out var correlationProp));
        Assert.Equal(correlationHeader, correlationProp.GetString());
    }

    [Fact]
    public async Task CreateBook_WithInvalidInput_IsNotPersisted()
    {
        await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("", 0));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        Assert.Equal(0, await db.Books.CountAsync());
    }

    // --- PUT /api/v1/books/{id} ---

    [Fact]
    public async Task UpdateBook_WhenExists_Returns200AndPersistsChanges()
    {
        var author = await SeedAuthorAsync("Original Author");
        var book = await SeedBookAsync(author.Id, "Original Title");
        var newAuthor = await SeedAuthorAsync("New Author");

        var etag = await GetCurrentETagAsync(book.Id);
        var response = await PutWithIfMatchAsync(
            _writeClient, book.Id, new UpdateBookDto("Updated Title", newAuthor.Id), etag);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(updated);
        Assert.Equal(book.Id, updated.Id);
        Assert.Equal("Updated Title", updated.Title);
        Assert.Equal("New Author", updated.AuthorFullName);

        var dbBook = await FindBookInDbAsync(book.Id);
        Assert.NotNull(dbBook);
        Assert.Equal("Updated Title", dbBook.Title);
        Assert.Equal(newAuthor.Id, dbBook.AuthorId);
    }

    [Fact]
    public async Task UpdateBook_WhenNotFound_Returns404()
    {
        var author = await SeedAuthorAsync();

        // If-Match ben formato (dummy): supera precondizione/parsing → si arriva al 404, non al 428/400.
        var response = await PutWithIfMatchAsync(
            _writeClient, 99999, new UpdateBookDto("Whatever", author.Id), DummyETag);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBook_WithoutIfMatch_Returns428()
    {
        var author = await SeedAuthorAsync();
        var book = await SeedBookAsync(author.Id, "Title");

        var response = await _writeClient.PutAsJsonAsync(
            $"/api/v1/books/{book.Id}", new UpdateBookDto("Updated", author.Id));

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UpdateBook_WithInvalidInput_Returns400AndDoesNotChangeDb()
    {
        var author = await SeedAuthorAsync();
        var book = await SeedBookAsync(author.Id, "Keep This Title");

        var response = await _writeClient.PutAsJsonAsync(
            $"/api/v1/books/{book.Id}", new UpdateBookDto("", 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem.Errors.ContainsKey("Title"));
        Assert.True(problem.Errors.ContainsKey("AuthorId"));

        var dbBook = await FindBookInDbAsync(book.Id);
        Assert.NotNull(dbBook);
        Assert.Equal("Keep This Title", dbBook.Title);
    }

    [Fact]
    public async Task UpdateBook_WithReadScopeOnly_Returns403()
    {
        var response = await _readClient.PutAsJsonAsync("/api/v1/books/1", new UpdateBookDto("X", 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UpdateBook_WithoutToken_Returns401()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.PutAsJsonAsync("/api/v1/books/1", new UpdateBookDto("X", 1));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- DELETE /api/v1/books/{id} ---

    [Fact]
    public async Task DeleteBook_WhenExists_Returns204AndRemovesBookFromDb()
    {
        var author = await SeedAuthorAsync();
        var book = await SeedBookAsync(author.Id, "To Delete");

        var etag = await GetCurrentETagAsync(book.Id);
        var response = await DeleteWithIfMatchAsync(_writeClient, book.Id, etag);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var dbBook = await FindBookInDbAsync(book.Id);
        Assert.Null(dbBook);
    }

    [Fact]
    public async Task DeleteBook_WhenNotFound_Returns404()
    {
        var response = await DeleteWithIfMatchAsync(_writeClient, 99999, DummyETag);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBook_WithoutIfMatch_Returns428()
    {
        var author = await SeedAuthorAsync();
        var book = await SeedBookAsync(author.Id, "Title");

        var response = await _writeClient.DeleteAsync($"/api/v1/books/{book.Id}");

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBook_DoesNotAffectOtherBooks()
    {
        var author = await SeedAuthorAsync();
        var bookToDelete = await SeedBookAsync(author.Id, "Delete Me");
        var bookToKeep = await SeedBookAsync(author.Id, "Keep Me");

        var etag = await GetCurrentETagAsync(bookToDelete.Id);
        await DeleteWithIfMatchAsync(_writeClient, bookToDelete.Id, etag);

        var dbBook = await FindBookInDbAsync(bookToKeep.Id);
        Assert.NotNull(dbBook);
        Assert.Equal("Keep Me", dbBook.Title);
    }

    // --- Autorizzazione: nessun token → 401 ---

    [Fact]
    public async Task GetBooks_WithoutToken_Returns401()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync("/api/v1/books");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateBook_WithoutToken_Returns401()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/v1/books", new CreateBookDto("X", 1));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBook_WithoutToken_Returns401()
    {
        var client = _factory.CreateAnonymousClient();

        var response = await client.DeleteAsync("/api/v1/books/1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Autorizzazione: token valido ma permesso insufficiente → 403 ---

    [Fact]
    public async Task CreateBook_WithReadScopeOnly_Returns403()
    {
        var response = await _readClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("X", 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBook_WithReadScopeOnly_Returns403()
    {
        var response = await _readClient.DeleteAsync("/api/v1/books/1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Autorizzazione: flusso application / macchina→macchina (claim "roles") ---

    [Fact]
    public async Task GetBooks_WithReadAppPermission_Returns200()
    {
        var client = _factory.CreateClientWithAppRoles(BooksPermissions.AppRead);

        var response = await client.GetAsync("/api/v1/books");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateBook_WithWriteAppPermission_Returns201()
    {
        var author = await SeedAuthorAsync();
        var client = _factory.CreateClientWithAppRoles(BooksPermissions.AppReadWrite);

        var response = await client.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Daemon Book", author.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateBook_WithReadAppPermissionOnly_Returns403()
    {
        var client = _factory.CreateClientWithAppRoles(BooksPermissions.AppRead);

        var response = await client.PostAsJsonAsync("/api/v1/books", new CreateBookDto("X", 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
