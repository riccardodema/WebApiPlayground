namespace WebApiPlayground.Application.Interfaces;

/// <summary>
/// Logica di arricchimento popolarità di un libro, <b>riusabile</b> da qualunque trasporto asincrono
/// (oggi il dispatcher dell'outbox; domani un consumer Azure Service Bus). Carica il libro, chiama il
/// client esterno resiliente+cachato (scaldando la cache) e <b>upserta lo snapshot durevole</b>. Idempotente:
/// lo snapshot è keyed su <c>BookId</c>, quindi rielaborare lo stesso evento (at-least-once) è sicuro.
/// L'astrazione vive in Application; l'implementazione (client/EF) in Infrastructure. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public interface IPopularityEnricher
{
    /// <summary>Arricchisce la popolarità del libro indicato; no-op se il libro non esiste più.</summary>
    Task EnrichAsync(int bookId, CancellationToken cancellationToken);
}
