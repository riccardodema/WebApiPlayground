namespace WebApiPlayground.Application.DTOs;

/// <summary>
/// Envelope di una pagina di risultati: gli elementi della finestra corrente più
/// i metadati di navigazione che il frontend usa per i controlli di pagina.
/// </summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPrevious => PageNumber > 1;
    public bool HasNext => PageNumber < TotalPages;
}
