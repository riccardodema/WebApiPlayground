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
/// Sotto-risorsa di <b>popolarità</b> di un libro (<c>/api/v{version}/books/{bookId}/popularity</c>): arricchisce
/// il libro del nostro DB con i segnali letti da una <b>dipendenza esterna</b> (Open Library) — il miglior proxy
/// gratuito di domanda/vendite (i dati di vendita reali non sono pubblici). La chiamata esterna è protetta da una
/// <b>pipeline di resilienza</b> (retry/circuit-breaker/timeout): se la dipendenza è indisponibile, l'endpoint
/// risponde <b>503</b> (ProblemDetails RFC 7807) con <c>Retry-After</c>, invece di propagare un 500 opaco. Stessa
/// auth e stesso rate limiting di lettura degli altri GET. Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
[ApiController]
[ApiVersion(ApiVersions.V1)]
[Route(ApiRoutes.Books + "/{bookId:int}/popularity")]
[Authorize]
[Produces("application/json")]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
public class BookPopularityController : ControllerBase
{
    private readonly IBookPopularityService _popularityService;
    private readonly ILogger<BookPopularityController> _logger;

    public BookPopularityController(IBookPopularityService popularityService, ILogger<BookPopularityController> logger)
    {
        ArgumentNullException.ThrowIfNull(popularityService);
        ArgumentNullException.ThrowIfNull(logger);
        _popularityService = popularityService;
        _logger = logger;
    }

    /// <summary>Recupera i segnali di popolarità di un libro dalla dipendenza esterna.</summary>
    /// <remarks>
    /// Carica il libro dal DB (per titolo/autore), poi interroga Open Library. Le metriche sono nullable: se la
    /// fonte non ha un match per quel titolo, il libro viene comunque restituito con metriche a <c>null</c>
    /// (200). Se la dipendenza esterna è indisponibile (circuito aperto / retry esauriti / timeout), la risposta
    /// è <c>503</c> con l'header <c>Retry-After</c> — un guasto a valle non diventa un 500 del nostro servizio.
    /// </remarks>
    /// <param name="bookId">Id del libro (intero positivo).</param>
    /// <param name="cancellationToken">Token di cancellazione propagato alla chiamata esterna.</param>
    /// <response code="200">Segnali di popolarità (eventualmente con metriche a null se nessun match upstream).</response>
    /// <response code="404">Nessun libro con l'Id indicato.</response>
    /// <response code="503">La dipendenza esterna è indisponibile; riprovare dopo i secondi in <c>Retry-After</c>.</response>
    [HttpGet]
    [MapToApiVersion(ApiVersions.V1)]
    [Authorize(Policy = AuthorizationPolicies.ReadBooks)]
    [EnableRateLimiting(RateLimitingOptions.PolicyNames.Read)]
    [ProducesResponseType(typeof(BookPopularityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetBookPopularity([FromRoute] int bookId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetching popularity for book {BookId}", bookId);

        var popularity = await _popularityService.GetBookPopularityAsync(bookId, cancellationToken);

        if (popularity is null)
        {
            _logger.LogWarning("Cannot fetch popularity for book {BookId}: book not found", bookId);
            return NotFound();
        }

        _logger.LogInformation(
            "Popularity for book {BookId} resolved from {Source} (avgRating {AverageRating})",
            bookId, popularity.Source, popularity.AverageRating);
        return Ok(popularity);
    }
}
