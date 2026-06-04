namespace WebApiPlayground.Api.RateLimiting;

/// <summary>
/// Opzioni del rate limiting, bindate dalla sezione <c>RateLimiting</c> della configurazione.
/// Due policy distinte — letture generose, scritture strette — perché le scritture mutano stato,
/// colpiscono il DB e invalidano la cache (superficie più sensibile/abusabile). Default sensati:
/// il rate limiter funziona anche senza configurazione esplicita.
/// Vedi <c>.claude/context/rate-limiting.md</c>.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Policy per le letture (GET). Default 100/60s ≈ 1.6 req/s sostenute: ben sopra qualunque
    /// navigazione UI umana (liste + dettaglio, Scalar), ma taglia lo scraping scriptato.
    /// </summary>
    public SlidingWindowPolicyOptions Read { get; init; } = new()
    {
        PermitLimit = 100,
        WindowSeconds = 60,
        SegmentsPerWindow = 6,
        QueueLimit = 0,
    };

    /// <summary>
    /// Policy per le scritture (POST/PUT/DELETE). Default 20/60s ≈ 1 scrittura ogni 3s: sopra l'uso
    /// interattivo legittimo, ma blocca i bulk-insert abusivi. Si sposa con l'idempotency: un
    /// retry-storm con la stessa key è assorbito dall'idempotency, uno con payload diversi è tagliato qui.
    /// </summary>
    public SlidingWindowPolicyOptions Write { get; init; } = new()
    {
        PermitLimit = 20,
        WindowSeconds = 60,
        SegmentsPerWindow = 6,
        QueueLimit = 0,
    };

    /// <summary>Nomi delle policy, referenziati da <c>[EnableRateLimiting]</c> sui controller.</summary>
    public static class PolicyNames
    {
        public const string Read = "read";
        public const string Write = "write";
    }
}

/// <summary>Parametri di una sliding window (finestra scorrevole a segmenti), per partizione.</summary>
public sealed class SlidingWindowPolicyOptions
{
    /// <summary>Numero massimo di richieste consentite nella finestra, per partizione (utente/IP).</summary>
    public int PermitLimit { get; init; }

    /// <summary>Ampiezza della finestra in secondi.</summary>
    public int WindowSeconds { get; init; }

    /// <summary>
    /// Segmenti in cui è suddivisa la finestra: più segmenti = scorrimento più fluido (la finestra
    /// avanza a passi più piccoli, niente conteggio a scatti) al costo di un po' di memoria in più.
    /// </summary>
    public int SegmentsPerWindow { get; init; }

    /// <summary>
    /// Richieste accodate oltre il limite. 0 = nessun accodamento: si rifiuta subito con 429
    /// (backpressure immediato e deterministico, niente latenza nascosta dietro una coda).
    /// </summary>
    public int QueueLimit { get; init; }
}
