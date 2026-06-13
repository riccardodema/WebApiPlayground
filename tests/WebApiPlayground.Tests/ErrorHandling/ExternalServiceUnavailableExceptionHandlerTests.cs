using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Api.ErrorHandling;
using WebApiPlayground.Application.Popularity;
using Xunit;

namespace WebApiPlayground.Tests.ErrorHandling;

/// <summary>
/// Traduzione dell'indisponibilità di una dipendenza esterna in <b>503 + Retry-After</b>:
/// l'hint dell'eccezione vince (arrotondato in alto, mai sotto 1s), senza hint si usa il
/// fallback; le altre eccezioni passano oltre (competenza del GlobalExceptionHandler).
/// </summary>
public class ExternalServiceUnavailableExceptionHandlerTests
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

    private static async Task<(bool Handled, DefaultHttpContext Context, CapturingProblemDetailsService Service)>
        HandleAsync(Exception exception)
    {
        var service = new CapturingProblemDetailsService();
        var handler = new ExternalServiceUnavailableExceptionHandler(service);
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        return (handled, context, service);
    }

    [Fact]
    public async Task Other_exceptions_are_not_handled_and_response_is_untouched()
    {
        var (handled, context, service) = await HandleAsync(new InvalidOperationException("boom"));

        Assert.False(handled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Null(service.Written);
    }

    [Fact]
    public async Task Unavailable_dependency_becomes_503_problem_details_naming_the_service()
    {
        var (handled, context, service) = await HandleAsync(
            new ExternalServiceUnavailableException("Open Library", TimeSpan.FromSeconds(30)));

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var problem = service.Written!.ProblemDetails;
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
        Assert.Contains("Open Library", problem.Detail);
    }

    [Theory]
    [InlineData(30, "30")]   // hint esatto
    [InlineData(2.3, "3")]   // frazioni arrotondate IN ALTO (mai invitare a ritentare troppo presto)
    [InlineData(0.1, "1")]   // boundary: mai sotto 1 secondo
    public async Task Retry_after_hint_is_ceiling_rounded_and_never_below_one_second(double seconds, string expected)
    {
        var (_, context, _) = await HandleAsync(
            new ExternalServiceUnavailableException("Open Library", TimeSpan.FromSeconds(seconds)));

        Assert.Equal(expected, context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Missing_hint_falls_back_to_ten_seconds()
    {
        var (_, context, _) = await HandleAsync(new ExternalServiceUnavailableException("Open Library"));

        Assert.Equal("10", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Non_positive_hint_falls_back_too()
    {
        // Un Retry-After calcolato da una data passata può venire negativo: non va propagato.
        var (_, context, _) = await HandleAsync(
            new ExternalServiceUnavailableException("Open Library", TimeSpan.FromSeconds(-5)));

        Assert.Equal("10", context.Response.Headers.RetryAfter);
    }
}
