using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Api.Http;
using WebApiPlayground.Api.RateLimiting;
using WebApiPlayground.Api.Versioning;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;

namespace WebApiPlayground.Api.Controllers;

/// <summary>
/// CRUD sui libri, versionato per segmento URL (<c>/api/v{version}/books</c>). Le <b>scritture</b>
/// (POST/PUT/DELETE) servono <b>entrambe</b> le versioni (contratto di richiesta invariato); le
/// <b>letture v1</b> (<c>BookDto</c>, autore piatto) vivono qui, mentre le letture v2 (autore annidato)
/// stanno in <see cref="BooksV2Controller"/>. Tutti gli endpoint richiedono un access token (Entra ID)
/// e una policy read/write. Errori come <c>application/problem+json</c> (RFC 7807): 400 input non
/// valido, 401/403 auth, 404 risorsa assente, 429 rate limit. Vedi
/// <c>.claude/context/api-versioning.md</c>.
/// </summary>
[ApiController]
[ApiVersion(ApiVersions.V1)]
[ApiVersion(ApiVersions.V2)]
[Route(ApiRoutes.Books)]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
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

    /// <summary>Elenca i libri in modo paginato e ordinato.</summary>
    /// <remarks>
    /// Paginazione offset: <c>pageNumber</c> &gt;= 1 (default 1), <c>pageSize</c> tra 1 e 100
    /// (default 20). Ordinamento: <c>sortBy</c> ∈ {<c>id</c>, <c>title</c>, <c>author</c>} e
    /// <c>sortDir</c> ∈ {<c>asc</c>, <c>desc</c>}; valori non riconosciuti ricadono sui default.
    /// La risposta è un envelope <c>PagedResult</c> con metadati di paginazione.
    /// </remarks>
    /// <response code="200">Pagina di libri con i metadati di paginazione.</response>
    /// <response code="400">Parametri di paginazione fuori range (es. <c>pageSize=0</c> o &gt; 100).</response>
    [HttpGet]
    [MapToApiVersion(ApiVersions.V1)]
    [Authorize(Policy = AuthorizationPolicies.ReadBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Read)]
    [ProducesResponseType(typeof(PagedResult<BookDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBooks([FromQuery] BooksQueryParameters query)
    {
        _logger.LogInformation(
            "Fetching books — page {PageNumber} (size {PageSize}), sort {SortBy} {SortDir}",
            query.PageNumber, query.PageSize, query.SortBy, query.SortDir);

        var page = await _booksService.GetBooksAsync(query);

        _logger.LogInformation(
            "Successfully retrieved {BookCount} of {TotalCount} book(s) — page {PageNumber}/{TotalPages}",
            page.Items.Count, page.TotalCount, page.PageNumber, page.TotalPages);
        return Ok(page);
    }

    /// <summary>Recupera un singolo libro per Id.</summary>
    /// <param name="id">Id del libro (intero positivo).</param>
    /// <response code="200">Il libro richiesto.</response>
    /// <response code="404">Nessun libro con l'Id indicato.</response>
    [HttpGet("{id:int}")]
    [MapToApiVersion(ApiVersions.V1)]
    [Authorize(Policy = AuthorizationPolicies.ReadBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Read)]
    [ProducesResponseType(typeof(BookDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
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

    /// <summary>Crea un nuovo libro.</summary>
    /// <remarks>
    /// Validazione del body (FluentValidation): <c>title</c> obbligatorio, 1–100 caratteri;
    /// <c>authorId</c> intero positivo (&gt; 0). In caso di violazione → 400 con i dettagli
    /// per campo nella proprietà <c>errors</c>.
    /// </remarks>
    /// <response code="201">Libro creato; l'header <c>Location</c> punta alla nuova risorsa.</response>
    /// <response code="400">Body non valido (vedi <c>errors</c> per i campi e come correggerli).</response>
    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.WriteBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Write)]
    [ProducesResponseType(typeof(BookDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
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

    /// <summary>Aggiorna un libro esistente (sostituzione completa della risorsa).</summary>
    /// <remarks>
    /// Validazione del body (FluentValidation): <c>title</c> obbligatorio, 1–100 caratteri;
    /// <c>authorId</c> intero positivo (&gt; 0). In caso di violazione → 400 con i dettagli
    /// per campo nella proprietà <c>errors</c>.
    ///
    /// <b>Optimistic concurrency</b>: richiede l'header <c>If-Match</c> con l'ETag ottenuto da una GET
    /// precedente. Assente → 428; ETag stale (la risorsa è cambiata nel frattempo) → 412; in caso di
    /// successo la risposta porta il nuovo <c>ETag</c>.
    /// </remarks>
    /// <param name="id">Id del libro da aggiornare.</param>
    /// <param name="dto">Nuovi valori della risorsa.</param>
    /// <response code="200">Il libro aggiornato (header <c>ETag</c> con la nuova versione).</response>
    /// <response code="400">Body non valido, oppure <c>If-Match</c> malformato.</response>
    /// <response code="404">Nessun libro con l'Id indicato.</response>
    /// <response code="412">L'ETag in <c>If-Match</c> è stale: la risorsa è stata modificata da un'altra richiesta.</response>
    /// <response code="428">Header <c>If-Match</c> mancante (richiesto per le scritture).</response>
    [HttpPut("{id:int}")]
    [Authorize(Policy = AuthorizationPolicies.WriteBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Write)]
    [ProducesResponseType(typeof(BookDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> UpdateBook([FromRoute] int id, [FromBody] UpdateBookDto dto)
    {
        var expectedVersion = RequireIfMatch();

        _logger.LogInformation(
            "Updating book {BookId} — Title: '{BookTitle}', AuthorId: {AuthorId}",
            id, dto.Title, dto.AuthorId);

        var updated = await _booksService.UpdateBookAsync(id, dto, expectedVersion);

        if (updated is null)
        {
            _logger.LogWarning("Cannot update book with ID {BookId}: book not found", id);
            return NotFound();
        }

        _logger.LogInformation("Book with ID {BookId} updated successfully", id);
        return Ok(updated);
    }

    /// <summary>Elimina un libro per Id.</summary>
    /// <remarks>
    /// <b>Optimistic concurrency</b>: come la PUT, richiede l'header <c>If-Match</c> con l'ETag corrente
    /// (delete-if-unchanged). Assente → 428; ETag stale → 412.
    /// </remarks>
    /// <param name="id">Id del libro da eliminare.</param>
    /// <response code="204">Libro eliminato.</response>
    /// <response code="400"><c>If-Match</c> malformato.</response>
    /// <response code="404">Nessun libro con l'Id indicato.</response>
    /// <response code="412">L'ETag in <c>If-Match</c> è stale: la risorsa è stata modificata da un'altra richiesta.</response>
    /// <response code="428">Header <c>If-Match</c> mancante (richiesto per le scritture).</response>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = AuthorizationPolicies.WriteBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Write)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> DeleteBook([FromRoute] int id)
    {
        var expectedVersion = RequireIfMatch();

        _logger.LogInformation("Deleting book with ID {BookId}", id);

        var deleted = await _booksService.DeleteBookAsync(id, expectedVersion);

        if (!deleted)
        {
            _logger.LogWarning("Cannot delete book with ID {BookId}: book not found", id);
            return NotFound();
        }

        _logger.LogInformation("Book with ID {BookId} deleted successfully", id);
        return NoContent();
    }

    /// <summary>
    /// Legge l'header <c>If-Match</c> (obbligatorio sulle scritture) e ne estrae il token di versione
    /// (byte della rowversion). Assente → <see cref="PreconditionException.Required"/> (428); presente
    /// ma non un ETag strong valido → <see cref="PreconditionException.MalformedIfMatch"/> (400). Le
    /// eccezioni sono mappate a ProblemDetails da <see cref="PreconditionExceptionHandler"/>.
    /// </summary>
    private byte[] RequireIfMatch()
    {
        var ifMatch = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
            throw PreconditionException.Required();
        if (!ETag.TryParseToken(ifMatch, out var token))
            throw PreconditionException.MalformedIfMatch();
        return token;
    }
}
