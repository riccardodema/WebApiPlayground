using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;

namespace WebApiPlayground.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BooksController : ControllerBase
{
    private readonly IBooksService _booksService;

    public BooksController(IBooksService booksService)
    {
        ArgumentNullException.ThrowIfNull(booksService);
        _booksService = booksService;
    }

    [HttpGet]
    public async Task<IActionResult> GetBooks()
    {
        return Ok(await _booksService.GetAllBooksAsync());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetBookById([FromRoute] int id)
    {
        var book = await _booksService.GetBookByIdAsync(id);
        return book is not null ? Ok(book) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> CreateBook([FromBody] CreateBookDto dto)
    {
        var created = await _booksService.CreateBookAsync(dto);
        return CreatedAtAction(nameof(GetBookById), new { id = created.Id }, created);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteBook([FromRoute] int id)
    {
        var deleted = await _booksService.DeleteBookAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
