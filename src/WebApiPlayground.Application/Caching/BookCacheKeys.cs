using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Caching;

/// <summary>
/// Vocabolario centralizzato di chiavi e tag della cache dei libri (no magic string:
/// stessa filosofia del sort in <c>Application/Querying</c>). Le chiavi sono stabili e
/// deterministiche; tutte le entry portano il tag <see cref="Books"/> così un singolo
/// <c>RemoveByTagAsync(Books)</c> invalida sia i singoli libri sia tutte le pagine di lista.
/// </summary>
public static class BookCacheKeys
{
    /// <summary>Tag applicato a ogni entry "libro": invalidato in blocco su create/update/delete.</summary>
    public const string Books = "books";

    /// <summary>Array riusabile (i metodi HybridCache vogliono un <c>IEnumerable&lt;string&gt;</c>).</summary>
    public static readonly string[] BooksTag = [Books];

    /// <summary>Chiave del singolo libro per Id: <c>books:id:{id}</c>.</summary>
    public static string ById(int id) => $"books:id:{id}";

    /// <summary>
    /// Chiave di una pagina di lista: <c>books:list:{page}:{size}:{sortBy}:{sortDir}</c>.
    /// Include tutti i parametri che cambiano il risultato, così pagine/ordinamenti diversi
    /// non si sovrascrivono. Sort normalizzato a minuscolo per non moltiplicare le chiavi.
    /// </summary>
    public static string ForList(BooksQueryParameters query) =>
        $"books:list:{query.PageNumber}:{query.PageSize}:" +
        $"{query.SortBy.ToLowerInvariant()}:{query.SortDir.ToLowerInvariant()}";

    // La rappresentazione v2 (autore annidato) ha una FORMA diversa: chiavi distinte da v1 per non
    // mescolare DTO diversi sotto la stessa chiave. Stesso tag Books → le scritture le invalidano comunque.

    /// <summary>Chiave del singolo libro (forma v2) per Id: <c>books:v2:id:{id}</c>.</summary>
    public static string ByIdDetailed(int id) => $"books:v2:id:{id}";

    /// <summary>Chiave di una pagina di lista (forma v2): <c>books:v2:list:{page}:{size}:{sortBy}:{sortDir}</c>.</summary>
    public static string ForListDetailed(BooksQueryParameters query) =>
        $"books:v2:list:{query.PageNumber}:{query.PageSize}:" +
        $"{query.SortBy.ToLowerInvariant()}:{query.SortDir.ToLowerInvariant()}";
}
