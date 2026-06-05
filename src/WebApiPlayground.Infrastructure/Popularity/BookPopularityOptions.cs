namespace WebApiPlayground.Infrastructure.Popularity;

/// <summary>
/// Configurazione della dipendenza esterna di popolarità (sezione <c>BookPopularity</c>). Default sensati
/// out-of-the-box: <see cref="BaseAddress"/> punta a Open Library (key-less, nessun segreto) e i knob di
/// resilienza hanno valori di produzione ragionevoli. I test li sovrascrivono con valori minuscoli per
/// esercitare retry/circuit-breaker/timeout in fretta. Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
public sealed class BookPopularityOptions
{
    public const string SectionName = "BookPopularity";

    /// <summary>Base address (solo host/schema, fisso da config → niente SSRF: l'input utente è solo query string).</summary>
    public string BaseAddress { get; set; } = "https://openlibrary.org";

    public ResilienceSettings Resilience { get; set; } = new();

    public CacheSettings Cache { get; set; } = new();

    /// <summary>
    /// Cache della risposta esterna. NB: la popolarità si muove lentamente (giorni) ed è il candidato
    /// ideale alla cache. Si usa <c>IFusionCache</c> (non l'astrazione <c>HybridCache</c>) perché servono
    /// entry options che l'astrazione non espone: <b>factory timeout infiniti</b> (così il budget di timeout
    /// lo governa la pipeline di resilienza, non il <c>FactoryHardTimeout=2s</c> globale tarato sui books) e
    /// <b>fail-safe</b> esteso (degrade-to-stale durante un'outage). Vedi <c>.claude/context/resilience.md</c>.
    /// </summary>
    public sealed class CacheSettings
    {
        /// <summary>Master switch: <c>false</c> = nessuna cache, ogni richiesta passa per la pipeline (config-gated).</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>TTL di freschezza: per quanto un'entry è "fresca" prima di rinfrescarla.</summary>
        public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Finestra di <b>degrade-to-stale</b>: con Open Library giù e l'entry scaduta, per quanto
        /// si serve l'ultimo valore buono invece di propagare il 503.</summary>
        public TimeSpan FailSafeMaxDuration { get; set; } = TimeSpan.FromHours(24);

        /// <summary>Negative caching: se <c>true</c>, anche il "no match" upstream viene cachato (risparmia
        /// chiamate per libri che Open Library non conosce, presenza stabile nel tempo).</summary>
        public bool CacheNotFound { get; set; } = true;
    }

    public sealed class ResilienceSettings
    {
        /// <summary>Timeout del <b>singolo tentativo</b> (innermost): taglia una richiesta lenta prima del retry.</summary>
        public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>Timeout <b>totale</b> (outermost): cappa l'intera sequenza retry → evita che i retry sommati
        /// diventino un'attesa lunghissima per il chiamante.</summary>
        public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(10);

        public RetrySettings Retry { get; set; } = new();
        public CircuitBreakerSettings CircuitBreaker { get; set; } = new();
    }

    public sealed class RetrySettings
    {
        /// <summary>Numero massimo di <b>ritentativi</b> (oltre al primo tentativo).</summary>
        public int MaxRetryAttempts { get; set; } = 3;

        /// <summary>Ritardo base del backoff esponenziale (con jitter): il ritardo cresce ~esponenzialmente per tentativo.</summary>
        public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    }

    public sealed class CircuitBreakerSettings
    {
        /// <summary>Frazione di fallimenti (0–1) nella finestra oltre la quale il circuito si apre.</summary>
        public double FailureRatio { get; set; } = 0.5;

        /// <summary>Finestra di campionamento su cui si calcola la <see cref="FailureRatio"/>.</summary>
        public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Numero minimo di chiamate nella finestra perché il breaker possa aprirsi (evita aperture su pochi campioni).</summary>
        public int MinimumThroughput { get; set; } = 10;

        /// <summary>Quanto resta aperto il circuito (fail-fast) prima di passare a half-open e ritentare una sonda.</summary>
        public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(15);
    }
}
