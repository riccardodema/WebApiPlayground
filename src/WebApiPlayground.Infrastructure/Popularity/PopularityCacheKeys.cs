namespace WebApiPlayground.Infrastructure.Popularity;

/// <summary>
/// Vocabolario centralizzato di chiave e tag della cache di popolarità (no magic string, come
/// <c>BookCacheKeys</c>). La chiave deriva da titolo+autore <b>normalizzati</b> (la query upstream è per
/// titolo+autore, non per Id): normalizzare massimizza le hit fra varianti banali ("  Dune " vs "dune").
/// Tutte le entry portano il tag <see cref="Tag"/> così il reset dei test (e un'eventuale invalidazione)
/// le colpisce in blocco. Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
public static class PopularityCacheKeys
{
    /// <summary>Tag applicato a ogni entry di popolarità (flush in blocco nei test, come il tag "books").</summary>
    public const string Tag = "popularity";

    /// <summary>Array riusabile (le API FusionCache vogliono un <c>IEnumerable&lt;string&gt;</c>).</summary>
    public static readonly string[] Tags = [Tag];

    /// <summary>Chiave deterministica: <c>popularity:{titolo}|{autore}</c>, normalizzata (trim, lowercase,
    /// whitespace collassato) per non moltiplicare le chiavi su differenze irrilevanti.</summary>
    public static string For(string title, string? author) =>
        $"{Tag}:{Normalize(title)}|{Normalize(author)}";

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}
