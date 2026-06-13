using WebApiPlayground.Application.Concurrency;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Idempotency;
using WebApiPlayground.Application.Popularity;
using Xunit;

namespace WebApiPlayground.Tests.Querying;

/// <summary>
/// Contratti di default e messaggi che il resto del sistema dà per scontati: i default di
/// paginazione/sort (un client che non passa parametri ottiene pagina 1/20 ordinata per id asc),
/// il TTL di default dell'idempotency, e i messaggi/proprietà delle eccezioni di dominio (la loro
/// stringa finisce in ProblemDetails e nei log — è osservabile).
/// </summary>
public class QueryAndExceptionContractTests
{
    [Fact]
    public void Query_parameters_have_sensible_defaults()
    {
        var query = new BooksQueryParameters();

        Assert.Equal(1, query.PageNumber);
        Assert.Equal(20, query.PageSize);
        Assert.Equal("id", query.SortBy);   // default stabile: cambiarlo cambierebbe l'ordinamento di tutte le liste
        Assert.Equal("asc", query.SortDir);
        Assert.Equal(100, BooksQueryParameters.MaxPageSize);
    }

    [Fact]
    public void Idempotency_default_ttl_is_24_hours()
    {
        var options = new IdempotencyOptions();

        Assert.Equal(TimeSpan.FromHours(24), options.Ttl);
        Assert.Equal("Idempotency-Key", options.HeaderName);
        Assert.Equal(255, options.MaxKeyLength);
    }

    [Fact]
    public void Concurrency_conflict_carries_the_resource_id_and_a_talking_message()
    {
        var inner = new InvalidOperationException("ef");
        var ex = new ConcurrencyConflictException(42, inner);

        Assert.Equal(42, ex.ResourceId);
        Assert.Contains("42", ex.Message);
        Assert.Contains("stale", ex.Message);     // il client deve capire che la SUA versione è vecchia
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void External_unavailable_carries_service_name_retry_after_and_message()
    {
        var ex = new ExternalServiceUnavailableException("Open Library", TimeSpan.FromSeconds(7));

        Assert.Equal("Open Library", ex.ServiceName);
        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
        Assert.Contains("Open Library", ex.Message);
        Assert.Contains("unavailable", ex.Message);
    }

    [Fact]
    public void External_unavailable_retry_after_is_optional()
    {
        var ex = new ExternalServiceUnavailableException("Open Library");

        Assert.Null(ex.RetryAfter); // assente = l'handler 503 userà il fallback
    }
}
