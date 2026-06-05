namespace WebApiPlayground.Application.Popularity;

/// <summary>
/// Astrazione della dipendenza esterna di popolarità (implementata in Infrastructure da un
/// <c>HttpClient</c> tipizzato verso Open Library, avvolto da una pipeline di resilienza Polly).
/// Application dipende <b>solo</b> da questa interfaccia: i package HTTP/Polly restano confinati a
/// Infrastructure (regola architetturale auto-validata, come per la cache). Vedi
/// <c>.claude/context/resilience.md</c>.
/// </summary>
public interface IBookPopularityClient
{
    /// <summary>Nome leggibile della fonte (es. <c>"Open Library"</c>), esposto dall'implementazione concreta
    /// così il service può etichettare la risposta senza conoscere il provider (che resta in Infrastructure).</summary>
    string SourceName { get; }

    /// <summary>
    /// Recupera i segnali di popolarità per un libro identificato da titolo (+ autore opzionale, per
    /// disambiguare gli omonimi). Restituisce <c>null</c> se la fonte non ha alcun match; lancia
    /// <see cref="ExternalServiceUnavailableException"/> se la resilienza è esaurita (circuito aperto,
    /// retry finiti o timeout) — l'errore di trasporto non risale mai oltre Infrastructure.
    /// </summary>
    Task<BookPopularity?> GetPopularityAsync(string title, string? author, CancellationToken cancellationToken);
}
