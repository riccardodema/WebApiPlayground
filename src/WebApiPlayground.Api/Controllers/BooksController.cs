using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;

namespace WebApiPlayground.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BooksController : ControllerBase
{
    private readonly IBooksService _booksService;
    private readonly ILogger<BooksController> _logger;

    public BooksController(IBooksService booksService, ILogger<BooksController> logger)
    {
        ArgumentNullException.ThrowIfNull(booksService);
        ArgumentNullException.ThrowIfNull(logger);
        _booksService = booksService;
        _logger = logger;
    }

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ReadBooks)]
    public async Task<IActionResult> GetBooks()
    {
        _logger.LogInformation("Fetching all books");

        var books = await _booksService.GetAllBooksAsync();

        _logger.LogInformation("Successfully retrieved {BookCount} book(s)", books.Count);
        return Ok(books);
    }

    [HttpGet("{id:int}")]
    [Authorize(Policy = AuthorizationPolicies.ReadBooks)]
    public async Task<IActionResult> GetBookById([FromRoute] int id)
    {
        _logger.LogDebug("Fetching book with ID {BookId}", id);

        var book = await _booksService.GetBookByIdAsync(id);

        if (book is null)
        {
            _logger.LogWarning("Book with ID {BookId} was not found", id);
            return NotFound();
        }

        _logger.LogDebug("Book with ID {BookId} found: '{BookTitle}'", id, book.Title);
        return Ok(book);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WriteBooks)]
    public async Task<IActionResult> CreateBook([FromBody] CreateBookDto dto)
    {
        _logger.LogInformation(
            "Creating new book — Title: '{BookTitle}', AuthorId: {AuthorId}",
            dto.Title, dto.AuthorId);

        var created = await _booksService.CreateBookAsync(dto);

        _logger.LogInformation(
            "Book created successfully — ID: {BookId}, Title: '{BookTitle}'",
            created.Id, created.Title);

        return CreatedAtAction(nameof(GetBookById), new { id = created.Id }, created);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Policy = AuthorizationPolicies.WriteBooks)]
    public async Task<IActionResult> DeleteBook([FromRoute] int id)
    {
        _logger.LogInformation("Deleting book with ID {BookId}", id);

        var deleted = await _booksService.DeleteBookAsync(id);

        if (!deleted)
        {
            _logger.LogWarning("Cannot delete book with ID {BookId}: book not found", id);
            return NotFound();
        }

        _logger.LogInformation("Book with ID {BookId} deleted successfully", id);
        return NoContent();
    }
}
