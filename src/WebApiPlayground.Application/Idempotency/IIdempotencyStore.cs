namespace WebApiPlayground.Application.Idempotency;

/// <summary>
/// Store delle risposte idempotenti: associa una storage key (derivata dalla
/// <c>Idempotency-Key</c>) alla risposta della prima esecuzione. Astrazione nel layer Application;
/// l'implementazione (memoria ora, Redis quando configurato) vive in Infrastructure.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Restituisce il record memorizzato per la chiave, oppure <c>null</c> se assente.</summary>
    Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Memorizza il record per la chiave con la scadenza indicata (TTL).</summary>
    Task SaveAsync(string key, IdempotencyRecord record, TimeSpan ttl, CancellationToken cancellationToken = default);
}
