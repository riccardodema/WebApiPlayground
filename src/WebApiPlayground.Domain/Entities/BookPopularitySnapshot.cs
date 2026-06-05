using System.ComponentModel.DataAnnotations;

namespace WebApiPlayground.Domain.Entities;

/// <summary>
/// Fotografia <b>durevole</b> della popolarità di un libro, scritta in background dal worker di arricchimento
/// (chiamata esterna fuori dal path di scrittura). Non è la fonte normale del read — quella resta la cache
/// (calda) con ricaduta sulla chiamata live — ma il <b>fallback d'outage</b>: con la dipendenza esterna giù e
/// la cache fail-safe vuota (es. dopo un restart) si serve l'ultimo valore noto invece di un 503.
/// <see cref="RetrievedAt"/>/<see cref="Source"/> rendono esplicite freschezza e provenienza. POCO: nessuna
/// dipendenza di persistenza (mappata in Infrastructure). Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
public class BookPopularitySnapshot
{
    /// <summary>Chiave primaria <i>e</i> foreign key verso <see cref="Book"/> (relazione 1:1, store-not-generated).</summary>
    [Key]
    public int BookId { get; set; }

    public double? AverageRating { get; set; }
    public int? RatingsCount { get; set; }
    public int? WantToReadCount { get; set; }
    public int? CurrentlyReadingCount { get; set; }
    public int? AlreadyReadCount { get; set; }
    public int? ReadingLogCount { get; set; }

    /// <summary>Provenienza leggibile dei dati (es. "Open Library").</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Quando il valore è stato effettivamente recuperato dalla fonte (as-of dei dati).</summary>
    public DateTimeOffset RetrievedAt { get; set; }
}
