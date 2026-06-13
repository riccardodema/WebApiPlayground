using System.Net;
using System.Text;

namespace WebApiPlayground.Tests.Popularity;

/// <summary>
/// Primary handler finto e <b>programmabile</b> per esercitare la pipeline di resilienza reale senza rete:
/// risponde in funzione del numero del tentativo (1-based) e può ritardare la risposta per scatenare i timeout.
/// Conta le invocazioni (thread-safe) per asserire retry vs fail-fast del circuit breaker. Vedi
/// <c>.claude/context/resilience.md</c>.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    // Corpo search.json minimale con un match (i campi sono quelli richiesti via fields=).
    public const string MatchJson =
        """{"docs":[{"ratings_average":4.5,"ratings_count":10,"want_to_read_count":100,"currently_reading_count":5,"already_read_count":50,"readinglog_count":155}]}""";

    // Corpo search.json senza alcun match (docs vuoto).
    public const string NoMatchJson = """{"docs":[]}""";

    private readonly Func<int, HttpResponseMessage> _responder;
    private readonly TimeSpan _delay;
    private int _invocations;

    /// <summary>Numero di volte in cui il transport è stato realmente colpito (un retry incrementa; un
    /// fail-fast del circuito aperto NO → distingue retry da circuit breaker).</summary>
    public int Invocations => Volatile.Read(ref _invocations);

    /// <summary>URI dell'ultima richiesta ricevuta: per asserire la query string costruita dal client.</summary>
    public Uri? LastRequestUri { get; private set; }

    public StubHttpMessageHandler(Func<int, HttpResponseMessage> responder, TimeSpan? delay = null)
    {
        _responder = responder;
        _delay = delay ?? TimeSpan.Zero;
    }

    /// <summary>Risponde sempre con lo stesso status (corpo "match" se 200).</summary>
    public static StubHttpMessageHandler Always(HttpStatusCode status, TimeSpan? delay = null) =>
        new(_ => Build(status), delay);

    /// <summary>Risponde con la sequenza data, poi ripete l'ultimo elemento.</summary>
    public static StubHttpMessageHandler Sequence(params HttpStatusCode[] statuses) =>
        new(attempt => Build(statuses[Math.Min(attempt, statuses.Length) - 1]));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _invocations);
        LastRequestUri = request.RequestUri;

        // Task.Delay onora il token: il timeout (per-tentativo o totale) lo cancella → Polly emette TimeoutRejectedException.
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken);

        return _responder(attempt);
    }

    public static HttpResponseMessage Build(HttpStatusCode status) => new(status)
    {
        Content = new StringContent(status == HttpStatusCode.OK ? MatchJson : "{}", Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}
