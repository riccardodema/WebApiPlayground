namespace WebApiPlayground.Application.Popularity;

/// <summary>
/// Sollevata quando una chiamata a una dipendenza esterna fallisce in modo <b>non recuperabile</b> dopo che
/// la pipeline di resilienza si è esaurita: circuito aperto (<c>BrokenCircuitException</c>), retry finiti,
/// o timeout (<c>TimeoutRejectedException</c>) / errore di rete (<c>HttpRequestException</c>). Stesso ruolo
/// di <see cref="Concurrency.ConcurrencyConflictException"/>: l'implementazione (Infrastructure) traduce qui
/// le eccezioni di trasporto/Polly, così non risalgono oltre il loro layer. Il layer HTTP la mappa su
/// <b>503 Service Unavailable</b> (RFC 7807) con header <c>Retry-After</c>. Vedi
/// <c>.claude/context/resilience.md</c>.
/// </summary>
public sealed class ExternalServiceUnavailableException : Exception
{
    /// <summary>Nome leggibile della dipendenza esterna (es. <c>"Open Library"</c>), per log e diagnostica.</summary>
    public string ServiceName { get; }

    /// <summary>Suggerimento di attesa prima di riprovare, proiettato nell'header <c>Retry-After</c>.
    /// <c>null</c> = nessun suggerimento (l'handler ricade su un default).</summary>
    public TimeSpan? RetryAfter { get; }

    public ExternalServiceUnavailableException(string serviceName, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base($"The external dependency '{serviceName}' is currently unavailable.", innerException)
    {
        ServiceName = serviceName;
        RetryAfter = retryAfter;
    }
}
