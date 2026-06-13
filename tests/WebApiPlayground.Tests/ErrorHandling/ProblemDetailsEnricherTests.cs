using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Api.Middleware;
using Xunit;

namespace WebApiPlayground.Tests.ErrorHandling;

/// <summary>
/// L'arricchimento dei ProblemDetails che chiude il cerchio di correlazione log↔risposta:
/// <c>correlationId</c> dagli Items (se il middleware l'ha messo) e <c>traceId</c>
/// dall'Activity corrente, con fallback al TraceIdentifier della richiesta.
/// </summary>
public class ProblemDetailsEnricherTests
{
    [Fact]
    public void Correlation_id_from_items_lands_in_the_problem_body()
    {
        var context = new DefaultHttpContext();
        context.Items[CorrelationIdMiddleware.ItemKey] = "corr-1";
        var problem = new ProblemDetails();

        ProblemDetailsEnricher.Enrich(context, problem);

        Assert.Equal("corr-1", problem.Extensions["correlationId"]);
    }

    [Fact]
    public void Without_correlation_id_the_extension_is_absent_not_empty()
    {
        var problem = new ProblemDetails();

        ProblemDetailsEnricher.Enrich(new DefaultHttpContext(), problem);

        Assert.False(problem.Extensions.ContainsKey("correlationId"));
    }

    [Fact]
    public void Trace_id_comes_from_the_current_activity_when_present()
    {
        using var activity = new Activity("test").Start();
        var problem = new ProblemDetails();

        ProblemDetailsEnricher.Enrich(new DefaultHttpContext(), problem);

        Assert.Equal(activity.Id, problem.Extensions["traceId"]);
    }

    [Fact]
    public void Trace_id_falls_back_to_the_request_identifier_without_an_activity()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        try
        {
            var context = new DefaultHttpContext { TraceIdentifier = "req-42" };
            var problem = new ProblemDetails();

            ProblemDetailsEnricher.Enrich(context, problem);

            Assert.Equal("req-42", problem.Extensions["traceId"]);
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}
