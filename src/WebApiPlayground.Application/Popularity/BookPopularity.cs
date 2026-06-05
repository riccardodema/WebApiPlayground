namespace WebApiPlayground.Application.Popularity;

/// <summary>
/// Segnali di popolarità di un libro restituiti dalla dipendenza esterna (Open Library), modellati come
/// tipo <b>neutro</b> di Application: nessun riferimento al contratto JSON upstream (che vive in
/// Infrastructure) né a tipi HTTP. È il miglior proxy <i>gratuito</i> della domanda/vendite — i dati di
/// vendita reali (Nielsen BookScan ecc.) non sono pubblici. Tutti i campi sono nullable: la fonte può non
/// avere un match o non esporre una metrica. Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
/// <param name="AverageRating">Voto medio (0–5) aggregato dagli utenti della fonte.</param>
/// <param name="RatingsCount">Numero di voti che compongono la media.</param>
/// <param name="WantToReadCount">Quanti utenti hanno il libro nella lista "da leggere".</param>
/// <param name="CurrentlyReadingCount">Quanti utenti lo stanno leggendo ora.</param>
/// <param name="AlreadyReadCount">Quanti utenti lo hanno già letto.</param>
/// <param name="ReadingLogCount">Totale delle presenze nei reading-log (somma delle scaffalature).</param>
public record BookPopularity(
    double? AverageRating,
    int? RatingsCount,
    int? WantToReadCount,
    int? CurrentlyReadingCount,
    int? AlreadyReadCount,
    int? ReadingLogCount);
