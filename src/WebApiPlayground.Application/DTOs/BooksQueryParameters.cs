using System.ComponentModel.DataAnnotations;

namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Parametri di query per <c>GET /api/books</c> (binding/validation model, non un DTO dato:
/// per questo è una classe con validazione e non un record). <c>[ApiController]</c> trasforma
/// le violazioni di <see cref="RangeAttribute"/> in un 400 ProblemDetails automatico.
/// <c>SortBy</c>/<c>SortDir</c> non riconosciuti vengono normalizzati ai default nel service.
/// </summary>
public class BooksQueryParameters
{
    public const int MaxPageSize = 100;

    [Range(1, int.MaxValue, ErrorMessage = "pageNumber deve essere >= 1.")]
    public int PageNumber { get; init; } = 1;

    [Range(1, MaxPageSize, ErrorMessage = "pageSize deve essere compreso tra 1 e 100.")]
    public int PageSize { get; init; } = 20;

    /// <summary>Whitelist: <c>id</c> | <c>title</c> | <c>author</c> (default: <c>id</c>).</summary>
    public string SortBy { get; init; } = "id";

    /// <summary>Direzione: <c>asc</c> | <c>desc</c> (default: <c>asc</c>).</summary>
    public string SortDir { get; init; } = "asc";
}
