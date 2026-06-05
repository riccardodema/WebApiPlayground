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
