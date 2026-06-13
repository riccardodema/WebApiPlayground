using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using WebApiPlayground.Api.Middleware;
using Xunit;

namespace WebApiPlayground.Tests.Middleware;

/// <summary>
/// Correlazione delle richieste: l'id fornito dal client viene RIUSATO (tracing cross-service),
/// quello assente viene generato; in entrambi i casi finisce in <c>HttpContext.Items</c> (per i
/// ProblemDetails) e nell'header di risposta (per il client).
/// </summary>
public class CorrelationIdMiddlewareTests
{
    /// <summary>Feature di risposta che permette di scatenare manualmente i callback OnStarting.</summary>
    private sealed class StartableResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];

        public override void OnStarting(Func<object, Task> callback, object state) =>
            _onStarting.Add((callback, state));

        public Task FireOnStartingAsync() =>
            Task.WhenAll(_onStarting.Select(entry => entry.Callback(entry.State)));
    }

    private static (DefaultHttpContext Context, StartableResponseFeature Response) BuildContext()
    {
        var context = new DefaultHttpContext();
        var feature = new StartableResponseFeature();
        context.Features.Set<IHttpResponseFeature>(feature);
        return (context, feature);
    }

    [Fact]
    public async Task Client_supplied_id_is_reused_in_items_and_response_header()
    {
        var (context, response) = BuildContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "corr-from-client";
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
        await response.FireOnStartingAsync();

        Assert.Equal("corr-from-client", context.Items[CorrelationIdMiddleware.ItemKey]);
        Assert.Equal("corr-from-client", context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task Missing_id_is_generated_and_exposed_the_same_way()
    {
        var (context, response) = BuildContext();
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
        await response.FireOnStartingAsync();

        var generated = Assert.IsType<string>(context.Items[CorrelationIdMiddleware.ItemKey]);
        Assert.Equal(32, generated.Length); // Guid "N": 32 hex, niente trattini
        Assert.Equal(generated, context.Response.Headers[CorrelationIdMiddleware.HeaderName]);
    }

    [Fact]
    public async Task Each_request_without_id_gets_a_distinct_one()
    {
        var middleware = new CorrelationIdMiddleware(_ => Task.CompletedTask);
        var (first, _) = BuildContext();
        var (second, _) = BuildContext();

        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        Assert.NotEqual(first.Items[CorrelationIdMiddleware.ItemKey], second.Items[CorrelationIdMiddleware.ItemKey]);
    }

    [Fact]
    public async Task Downstream_pipeline_sees_the_id_already_in_items()
    {
        var (context, _) = BuildContext();
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "corr-1";
        object? seenByNext = null;
        var middleware = new CorrelationIdMiddleware(ctx =>
        {
            seenByNext = ctx.Items[CorrelationIdMiddleware.ItemKey];
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal("corr-1", seenByNext); // disponibile DENTRO la pipeline, non solo dopo
    }
}
