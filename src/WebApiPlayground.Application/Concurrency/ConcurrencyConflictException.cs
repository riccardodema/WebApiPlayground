namespace WebApiPlayground.Application.Concurrency;

/// <summary>
/// Sollevata quando una scrittura ottimisticamente concorrente fallisce: il token di versione atteso
/// dal client (<c>If-Match</c>) non corrisponde più alla versione corrente nel DB — qualcun altro ha
/// modificato (o cancellato) la risorsa nel frattempo. Il repository la lancia traducendo la
/// <c>DbUpdateConcurrencyException</c> di EF Core, così l'eccezione di infrastruttura non risale oltre
/// il suo layer. Il layer HTTP la mappa su <b>412 Precondition Failed</b>.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>Id della risorsa in conflitto.</summary>
    public int ResourceId { get; }

    public ConcurrencyConflictException(int resourceId, Exception? innerException = null)
        : base($"The resource (id {resourceId}) was modified by another request; the supplied version is stale.", innerException)
    {
        ResourceId = resourceId;
    }
}
