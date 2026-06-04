using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Api.RateLimiting;
using WebApiPlayground.Api.Versioning;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;

namespace WebApiPlayground.Api.Controllers;

/// <summary>
/// Letture <b>v2</b> dei libri: stessa risorsa di v1 (stessa rotta <c>/api/v{version}/books</c>,
/// selezionata da <c>version=2</c>) ma rappresentazione evoluta — l'autore è un oggetto annidato
/// (<see cref="BookDetailsDto"/>) invece del nome piatto di v1. Le scritture restano in
/// <see cref="BooksController"/>, condivise tra le versioni (contratto invariato). Stessa auth,
/// stesso rate limiting di lettura. Vedi <c>.claude/context/api-versioning.md</c>.
/// </summary>
[ApiController]
[ApiVersion(ApiVersions.V2)]
[Route(ApiRoutes.Books)]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
public class BooksV2Controller : ControllerBase
{
    private readonly IBooksService _booksService;
    private readonly ILogger<BooksV2Controller> _logger;

    public BooksV2Controller(IBooksService booksService, ILogger<BooksV2Controller> logger)
    {
        ArgumentNullException.ThrowIfNull(booksService);
        ArgumentNullException.ThrowIfNull(logger);
        _booksService = booksService;
        _logger = logger;
    }

    /// <summary>Elenca i libri in modo paginato e ordinato (v2: autore annidato).</summary>
    /// <remarks>
    /// Stessa paginazione/ordinamento di v1 (vedi <c>BooksController.GetBooks</c>); cambia solo la
    /// forma di ogni elemento: <c>author</c> è un oggetto <c>{ id, fullName }</c> invece del nome piatto.
    /// </remarks>
    /// <response code="200">Pagina di libri (forma v2) con i metadati di paginazione.</response>
    /// <response code="400">Parametri di paginazione fuori range (es. <c>pageSize=0</c> o &gt; 100).</response>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.ReadBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Read)]
    [ProducesResponseType(typeof(PagedResult<BookDetailsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBooks([FromQuery] BooksQueryParameters query)
    {
        _logger.LogInformation(
            "Fetching books (v2) — page {PageNumber} (size {PageSize}), sort {SortBy} {SortDir}",
            query.PageNumber, query.PageSize, query.SortBy, query.SortDir);

        var page = await _booksService.GetBooksDetailedAsync(query);

        _logger.LogInformation(
            "Successfully retrieved {BookCount} of {TotalCount} book(s) (v2) — page {PageNumber}/{TotalPages}",
            page.Items.Count, page.TotalCount, page.PageNumber, page.TotalPages);
        return Ok(page);
    }

    /// <summary>Recupera un singolo libro per Id (v2: autore annidato).</summary>
    /// <param name="id">Id del libro (intero positivo).</param>
    /// <response code="200">Il libro richiesto (forma v2, autore annidato).</response>
    /// <response code="404">Nessun libro con l'Id indicato.</response>
    [HttpGet("{id:int}")]
    [Authorize(Policy = AuthorizationPolicies.ReadBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Read)]
    [ProducesResponseType(typeof(BookDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBookById([FromRoute] int id)
    {
        _logger.LogDebug("Fetching book {BookId} (v2 shape)", id);

        var book = await _booksService.GetBookDetailsByIdAsync(id);

        if (book is null)
        {
            _logger.LogWarning("Book with ID {BookId} was not found (v2)", id);
            return NotFound();
        }

        _logger.LogDebug("Book {BookId} found (v2): '{BookTitle}'", id, book.Title);
        return Ok(book);
    }
}
