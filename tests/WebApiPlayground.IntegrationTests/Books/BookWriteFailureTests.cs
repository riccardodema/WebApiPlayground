using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Books;

/// <summary>
/// Percorsi di FALLIMENTO della persistenza in scrittura: un <c>AuthorId</c> sintatticamente valido
/// (&gt; 0, passa la validazione) ma inesistente viola la FK → <c>DbUpdateException</c> → il
/// GlobalExceptionHandler la traduce in <b>500 ProblemDetails</b> (con correlationId), senza far
/// trapelare dettagli del DB. Copre i rami catch del repository su create e update.
/// </summary>
[Collection("Integration")]
public class BookWriteFailureTests : IAsyncLifetime
{
    private const int MissingAuthorId = 999_999;

    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _writeClient;

    public BookWriteFailureTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_with_nonexistent_author_returns_500_problem_details()
    {
        var response = await _writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto("Dune", MissingAuthorId));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.True(problem!.Extensions.ContainsKey("correlationId"));
        // Niente leak dei dettagli del DB (nomi di constraint/tabelle) nel corpo della risposta.
        Assert.DoesNotContain("FOREIGN KEY", problem.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_pointing_to_nonexistent_author_returns_500_problem_details()
    {
        int bookId;
        string etag;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
            var book = new Book { Title = "Dune", Author = new Author { FullName = "Frank Herbert" } };
            db.Books.Add(book);
            await db.SaveChangesAsync();
            bookId = book.Id;
        }

        // ETag corrente (token di concorrenza): l'update richiede If-Match (vedi optimistic-concurrency.md).
        var current = await _factory.CreateClientWithScope(BooksPermissions.ScopeRead)
            .GetAsync($"/api/v1/books/{bookId}");
        etag = current.Headers.ETag!.Tag;

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/books/{bookId}")
        {
            Content = JsonContent.Create(new UpdateBookDto("Dune (rev)", MissingAuthorId)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);

        var response = await _writeClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.True(problem!.Extensions.ContainsKey("correlationId"));
    }
}
