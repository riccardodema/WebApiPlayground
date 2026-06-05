using System.Net;
using System.Text;

namespace WebApiPlayground.IntegrationTests.Infrastructure;

/// <summary>
/// Primary handler finto per il client di popolarità: i test d'integrazione <b>non</b> devono toccare la rete
/// reale (Open Library) — sarebbe lento, flaky e dipendente da un servizio esterno. La factory base installa lo
/// stub di <b>successo</b>; i test di indisponibilità installano (via <c>WithWebHostBuilder</c>) lo stub che
/// fallisce sempre, esercitando la pipeline di resilienza fino al 503. Vedi <c>.claude/context/resilience.md</c>.
/// </summary>
public sealed class PopularityHttpStub : HttpMessageHandler
{
    // Corpo search.json con un match (gli stessi campi richiesti dal client via fields=).
    private const string MatchJson =
        """{"docs":[{"ratings_average":4.5,"ratings_count":10,"want_to_read_count":100,"currently_reading_count":5,"already_read_count":50,"readinglog_count":155}]}""";

    private readonly HttpStatusCode _status;

    private PopularityHttpStub(HttpStatusCode status) => _status = status;

    /// <summary>Stub che risponde 200 con un match (happy path).</summary>
    public static PopularityHttpStub AlwaysOk() => new(HttpStatusCode.OK);

    /// <summary>Stub che risponde sempre con uno status di errore (per esercitare la resilienza → 503).</summary>
    public static PopularityHttpStub Always(HttpStatusCode status) => new(status);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_status)
        {
            Content = new StringContent(_status == HttpStatusCode.OK ? MatchJson : "{}", Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(response);
    }
}
