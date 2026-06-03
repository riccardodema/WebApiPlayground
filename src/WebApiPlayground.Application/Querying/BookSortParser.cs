namespace WebApiPlayground.Application.Querying;

/// <summary>
/// Unico punto in cui vivono le "magic strings" del contratto pubblico di ordinamento dei libri
/// (i valori che il frontend passa in <c>?sortBy=</c> e <c>?sortDir=</c>). Mappa le stringhe grezze
/// sugli enum type-safe <see cref="BookSortField"/> / <see cref="SortDirection"/> usati dal resto
/// dell'applicazione. Centralizzando qui il vocabolario, service e repository restano senza literal.
/// </summary>
public static class BookSortParser
{
    /// <summary>Valori ammessi per <c>?sortBy=</c> (per documentazione / messaggi).</summary>
    public const string AllowedSortByValues = "id | title | author";

    /// <summary>Valori ammessi per <c>?sortDir=</c> (per documentazione / messaggi).</summary>
    public const string AllowedSortDirValues = "asc | desc";

    /// <summary>Campo di ordinamento usato quando l'input è assente o non riconosciuto.</summary>
    public const BookSortField DefaultField = BookSortField.Id;

    /// <summary>
    /// Mappa <c>?sortBy=</c> sull'enum. Ritorna <c>false</c> (e <paramref name="field"/> =
    /// <see cref="DefaultField"/>) se il valore non è in whitelist: il chiamante decide se loggare.
    /// </summary>
    public static bool TryParseField(string? value, out BookSortField field)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "id":
                field = BookSortField.Id;
                return true;
            case "title":
                field = BookSortField.Title;
                return true;
            case "author":
                field = BookSortField.Author;
                return true;
            default:
                field = DefaultField;
                return false;
        }
    }

    /// <summary>
    /// Mappa <c>?sortDir=</c> sull'enum. Qualsiasi valore diverso da <c>desc</c> (case-insensitive)
    /// è trattato come ascendente, così l'input invalido degrada al default senza errori.
    /// </summary>
    public static SortDirection ParseDirection(string? value) =>
        string.Equals(value?.Trim(), "desc", StringComparison.OrdinalIgnoreCase)
            ? SortDirection.Descending
            : SortDirection.Ascending;
}
