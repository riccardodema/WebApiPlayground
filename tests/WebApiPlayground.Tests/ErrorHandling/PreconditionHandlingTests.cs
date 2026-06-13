using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Application.Concurrency;
using Xunit;

namespace WebApiPlayground.Tests.ErrorHandling;

/// <summary>
/// La semantica delle PRECONDIZIONI HTTP (optimistic concurrency): 428 quando manca If-Match,
/// 400 quando è malformato, 412 quando l'ETag è stale — ognuna col suo type RFC e un detail
/// che dice al client COME rimediare. Status e messaggi sono contratto, si asseriscono per esteso.
/// </summary>
public class PreconditionHandlingTests
{
    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? Written { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.CompletedTask;
        }
    }

    // ---- Le factory dell'eccezione: status + guida al rimedio ---------------------

    [Fact]
    public void Required_is_428_and_tells_the_client_to_GET_first()
    {
        var ex = PreconditionException.Required();

        Assert.Equal(StatusCodes.Status428PreconditionRequired, ex.StatusCode);
        Assert.Equal("Precondition Required", ex.ProblemTitle);
        Assert.Contains("If-Match", ex.Message);
        Assert.Contains("GET the resource first", ex.Message); // il rimedio, non solo il divieto
    }

    [Fact]
    public void MalformedIfMatch_is_400_and_describes_the_expected_format()
    {
        var ex = PreconditionException.MalformedIfMatch();

        Assert.Equal(StatusCodes.Status400BadRequest, ex.StatusCode);
        Assert.Equal("Invalid If-Match header", ex.ProblemTitle);
        Assert.Contains("quoted token", ex.Message);
    }

    // ---- L'handler: mappa eccezione → status/type RFC -----------------------------

    private static async Task<(bool Handled, DefaultHttpContext Context, CapturingProblemDetailsService Service)>
        HandleAsync(Exception exception)
    {
        var service = new CapturingProblemDetailsService();
        var handler = new PreconditionExceptionHandler(service);
        var context = new DefaultHttpContext();
        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        return (handled, context, service);
    }

    [Fact]
    public async Task Stale_etag_concurrency_conflict_maps_to_412()
    {
        var (handled, context, service) = await HandleAsync(new ConcurrencyConflictException(42));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, context.Response.StatusCode);
        var problem = service.Written!.ProblemDetails;
        Assert.Equal("Precondition Failed", problem.Title);
        Assert.Contains("15.5.13", problem.Type); // type RFC del 412
    }

    [Fact]
    public async Task Missing_if_match_maps_to_428_with_the_rfc6585_type()
    {
        var (handled, context, service) = await HandleAsync(PreconditionException.Required());

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, context.Response.StatusCode);
        Assert.Contains("rfc6585", service.Written!.ProblemDetails.Type); // il 428 ha il SUO RFC, non il 9110
    }

    [Fact]
    public async Task Malformed_if_match_maps_to_400_with_the_bad_request_type()
    {
        var (handled, context, service) = await HandleAsync(PreconditionException.MalformedIfMatch());

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Contains("15.5.1", service.Written!.ProblemDetails.Type);
    }

    [Fact]
    public async Task Unrelated_exceptions_are_left_to_the_next_handler()
    {
        var (handled, context, service) = await HandleAsync(new InvalidOperationException("boom"));

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Null(service.Written);
    }
}
