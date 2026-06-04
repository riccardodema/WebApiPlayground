namespace WebApiPlayground.Application.Idempotency;

/// <summary>
/// Opzioni dell'idempotency, bindate dalla sezione <c>Idempotency</c> della configurazione.
/// Default sensati: il middleware funziona anche senza configurazione esplicita.
/// </summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>Nome dell'header che porta la chiave di idempotenza.</summary>
    public string HeaderName { get; init; } = "Idempotency-Key";

    /// <summary>Per quanto si conserva una risposta memorizzata (finestra entro cui un retry la rigioca).</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Lunghezza massima accettata per la chiave (oltre → 400).</summary>
    public int MaxKeyLength { get; init; } = 255;
}
