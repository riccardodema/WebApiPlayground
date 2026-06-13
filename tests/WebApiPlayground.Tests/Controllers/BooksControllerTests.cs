using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebApiPlayground.Api.Controllers;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using Xunit;

namespace WebApiPlayground.Tests.Controllers;

/// <summary>
/// Contratto HTTP delle action v1 (service mockato): status code e shape del risultato per i casi
/// trovato/non-trovato, il 201 con la rotta del nuovo libro, e il guard <c>If-Match</c> delle
/// scritture condizionali (mancante → 428, malformato → 400, via <see cref="PreconditionException"/>).
/// </summary>
public class BooksControllerTests
{
    private const string ValidIfMatch = "\"AAAAAAAAAAE=\""; // base64 tra virgolette: ETag strong valido

    private readonly Mock<IBooksService> _service = new();
    private readonly BooksController _sut;

    public BooksControllerTests()
    {
        _sut = new BooksController(_service.Object, NullLogger<BooksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private void SetIfMatch(string? value)
    {
        if (value is not null)
            _sut.HttpContext.Request.Headers.IfMatch = value;
    }

    // ---- Letture -----------------------------------------------------------------

    [Fact]
    public async Task GetBooks_returns_200_with_the_page_from_the_service()
    {
        var page = new PagedResult<BookDto>([new BookDto(1, "Dune", "Frank Herbert")], 1, 20, 1);
        _service.Setup(s => s.GetBooksAsync(It.IsAny<BooksQueryParameters>())).ReturnsAsync(page);

        var result = await _sut.GetBooks(new BooksQueryParameters());

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(page, ok.Value);
    }

    [Fact]
    public async Task GetBookById_returns_200_when_found_and_404_when_missing()
    {
        _service.Setup(s => s.GetBookByIdAsync(1)).ReturnsAsync(new BookDto(1, "Dune", "Frank Herbert"));
        _service.Setup(s => s.GetBookByIdAsync(999)).ReturnsAsync((BookDto?)null);

        Assert.IsType<OkObjectResult>(await _sut.GetBookById(1));
        Assert.IsType<NotFoundResult>(await _sut.GetBookById(999));
    }

    // ---- Create --------------------------------------------------------------------

    [Fact]
    public async Task CreateBook_returns_201_pointing_at_the_new_resource()
    {
        var created = new BookDto(42, "Dune", "Frank Herbert");
        _service.Setup(s => s.CreateBookAsync(It.IsAny<CreateBookDto>())).ReturnsAsync(created);

        var result = await _sut.CreateBook(new CreateBookDto("Dune", 7));

        var createdAt = Assert.IsType<CreatedAtActionResult>(result);
        Assert.Equal(nameof(BooksController.GetBookById), createdAt.ActionName);
        Assert.Equal(42, createdAt.RouteValues!["id"]); // la Location punta al libro APPENA creato
        Assert.Same(created, createdAt.Value);
    }

    // ---- Scritture condizionali: guard If-Match --------------------------------------

    [Fact]
    public async Task UpdateBook_passes_the_parsed_version_token_to_the_service()
    {
        SetIfMatch(ValidIfMatch);
        var updated = new BookDto(1, "Dune (rev)", "Frank Herbert");
        _service.Setup(s => s.UpdateBookAsync(1, It.IsAny<UpdateBookDto>(), It.IsAny<byte[]>())).ReturnsAsync(updated);

        var result = await _sut.UpdateBook(1, new UpdateBookDto("Dune (rev)", 7));

        Assert.IsType<OkObjectResult>(result);
        // Il token passato al service è il base64 DECODIFICATO dell'If-Match: il confronto di
        // versione avviene sui byte del rowversion, non sulla stringa dell'header.
        _service.Verify(s => s.UpdateBookAsync(1, It.IsAny<UpdateBookDto>(),
            It.Is<byte[]>(v => Convert.ToBase64String(v) == "AAAAAAAAAAE=")), Times.Once);
    }

    [Fact]
    public async Task UpdateBook_returns_404_when_the_service_finds_nothing()
    {
        SetIfMatch(ValidIfMatch);
        _service.Setup(s => s.UpdateBookAsync(1, It.IsAny<UpdateBookDto>(), It.IsAny<byte[]>()))
            .ReturnsAsync((BookDto?)null);

        Assert.IsType<NotFoundResult>(await _sut.UpdateBook(1, new UpdateBookDto("x", 7)));
    }

    [Fact]
    public async Task UpdateBook_without_if_match_throws_precondition_required_before_touching_the_service()
    {
        await Assert.ThrowsAsync<PreconditionException>(() => _sut.UpdateBook(1, new UpdateBookDto("x", 7)));

        _service.Verify(s => s.UpdateBookAsync(It.IsAny<int>(), It.IsAny<UpdateBookDto>(), It.IsAny<byte[]>()), Times.Never);
    }

    [Fact]
    public async Task UpdateBook_with_malformed_if_match_throws_before_touching_the_service()
    {
        SetIfMatch("not-a-quoted-base64-etag");

        await Assert.ThrowsAsync<PreconditionException>(() => _sut.UpdateBook(1, new UpdateBookDto("x", 7)));

        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeleteBook_returns_204_when_deleted_and_404_when_missing()
    {
        SetIfMatch(ValidIfMatch);
        _service.Setup(s => s.DeleteBookAsync(1, It.IsAny<byte[]>())).ReturnsAsync(true);
        _service.Setup(s => s.DeleteBookAsync(999, It.IsAny<byte[]>())).ReturnsAsync(false);

        Assert.IsType<NoContentResult>(await _sut.DeleteBook(1));
        Assert.IsType<NotFoundResult>(await _sut.DeleteBook(999));
    }

    [Fact]
    public async Task DeleteBook_without_if_match_throws_precondition_required()
    {
        await Assert.ThrowsAsync<PreconditionException>(() => _sut.DeleteBook(1));

        _service.Verify(s => s.DeleteBookAsync(It.IsAny<int>(), It.IsAny<byte[]>()), Times.Never);
    }
}
