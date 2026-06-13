using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WebApiPlayground.Api.ErrorHandling;
using Xunit;

namespace WebApiPlayground.Tests.ErrorHandling;

/// <summary>
/// Il fallback per le eccezioni non gestite: SEMPRE 500 ProblemDetails RFC 7807, ma il
/// <c>Detail</c> col messaggio dell'eccezione è esposto SOLO in Development — fuori è null
/// (niente info leak). L'handler accetta sempre la responsabilità (ultimo della catena).
/// </summary>
public class GlobalExceptionHandlerTests
{
    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetails? Written { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Written = context.ProblemDetails;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written = context.ProblemDetails;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static async Task<(bool Handled, DefaultHttpContext Context, CapturingProblemDetailsService Service)>
        HandleAsync(Exception exception, string environment)
    {
        var service = new CapturingProblemDetailsService();
        var handler = new GlobalExceptionHandler(
            service, new StubEnvironment(environment), NullLogger<GlobalExceptionHandler>.Instance);
        var context = new DefaultHttpContext();
        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);
        return (handled, context, service);
    }

    [Fact]
    public async Task Any_exception_becomes_a_500_problem_details()
    {
        var (handled, context, service) = await HandleAsync(new InvalidOperationException("boom"), Environments.Production);

        Assert.True(handled); // è l'ultimo handler: prende sempre in carico
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal(StatusCodes.Status500InternalServerError, service.Written!.Status);
        Assert.Equal("An unexpected error occurred.", service.Written.Title);
        Assert.Contains("15.6.1", service.Written.Type); // type RFC del 500
    }

    [Fact]
    public async Task In_development_the_detail_carries_the_exception_message()
    {
        var (_, _, service) = await HandleAsync(new InvalidOperationException("sensitive boom"), Environments.Development);

        Assert.Equal("sensitive boom", service.Written!.Detail);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_development_the_detail_is_null_to_avoid_info_leak(string environment)
    {
        var (_, _, service) = await HandleAsync(new InvalidOperationException("sensitive boom"), environment);

        Assert.Null(service.Written!.Detail); // mai il messaggio interno fuori da dev
    }
}
