using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly HttpClient _client;

    public BooksControllerTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
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

    // --- GET /api/books ---

    [Fact]
    public async Task GetBooks_WhenEmpty_Returns200WithEmptyList()
    {
        var response = await _client.GetAsync("/api/books");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var books = await response.Content.ReadFromJsonAsync<List<BookDto>>();
        Assert.NotNull(books);
        Assert.Empty(books);
    }

    [Fact]
    public async Task GetBooks_WhenBooksExist_Returns200WithAllBooks()
    {
        var author = await SeedAuthorAsync("George Orwell");
        await SeedBookAsync(author.Id, "1984");
        await SeedBookAsync(author.Id, "Animal Farm");

        var response = await _client.GetAsync("/api/books");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var books = await response.Content.ReadFromJsonAsync<List<BookDto>>();
        Assert.NotNull(books);
        Assert.Equal(2, books.Count);
        Assert.Contains(books, b => b.Title == "1984" && b.AuthorFullName == "George Orwell");
        Assert.Contains(books, b => b.Title == "Animal Farm" && b.AuthorFullName == "George Orwell");
    }

    // --- GET /api/books/{id} ---

    [Fact]
    public async Task GetBookById_WhenExists_Returns200WithCorrectData()
    {
        var author = await SeedAuthorAsync("J.K. Rowling");
        var book = await SeedBookAsync(author.Id, "Harry Potter");

        var response = await _client.GetAsync($"/api/books/{book.Id}");

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
        var response = await _client.GetAsync("/api/books/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- POST /api/books ---

    [Fact]
    public async Task CreateBook_Returns201AndPersistsBookInDb()
    {
        var author = await SeedAuthorAsync("Fyodor Dostoevsky");
        var dto = new CreateBookDto("Crime and Punishment", author.Id);

        var response = await _client.PostAsJsonAsync("/api/books", dto);

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

        var response = await _client.PostAsJsonAsync("/api/books", dto);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var created = await response.Content.ReadFromJsonAsync<BookDto>();
        Assert.NotNull(created);
        Assert.EndsWith($"/api/books/{created.Id}", response.Headers.Location!.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateBook_IncreasesBookCountInDb()
    {
        var author = await SeedAuthorAsync();
        await SeedBookAsync(author.Id, "Existing Book");

        var dto = new CreateBookDto("Brand New Book", author.Id);
        await _client.PostAsJsonAsync("/api/books", dto);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        Assert.Equal(2, await db.Books.CountAsync());
    }

    // --- DELETE /api/books/{id} ---

    [Fact]
    public async Task DeleteBook_WhenExists_Returns204AndRemovesBookFromDb()
    {
        var author = await SeedAuthorAsync();
        var book = await SeedBookAsync(author.Id, "To Delete");

        var response = await _client.DeleteAsync($"/api/books/{book.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var dbBook = await FindBookInDbAsync(book.Id);
        Assert.Null(dbBook);
    }

    [Fact]
    public async Task DeleteBook_WhenNotFound_Returns404()
    {
        var response = await _client.DeleteAsync("/api/books/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteBook_DoesNotAffectOtherBooks()
    {
        var author = await SeedAuthorAsync();
        var bookToDelete = await SeedBookAsync(author.Id, "Delete Me");
        var bookToKeep = await SeedBookAsync(author.Id, "Keep Me");

        await _client.DeleteAsync($"/api/books/{bookToDelete.Id}");

        var dbBook = await FindBookInDbAsync(bookToKeep.Id);
        Assert.NotNull(dbBook);
        Assert.Equal("Keep Me", dbBook.Title);
    }
}
