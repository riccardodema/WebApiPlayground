namespace WebApiPlayground.Application.Querying;

/// <summary>
/// Campi ammessi per l'ordinamento dei libri: è la whitelist <i>type-safe</i>.
/// Le stringhe grezze del contratto HTTP (<c>?sortBy=</c>) vengono mappate qui da
/// <see cref="BookSortParser"/>; il resto dell'applicazione ragiona solo su questo enum.
/// </summary>
public enum BookSortField
{
    Id,
    Title,
    Author,
}
