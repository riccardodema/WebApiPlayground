using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.Diagnostics;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;
using WebApiPlayground.IntegrationTests.Infrastructure;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Observability;

/// <summary>
/// Rete di salvataggio per la pipeline OpenTelemetry: un <see cref="ActivityListener"/> in-process (lo
/// stesso processo del TestServer) cattura gli span prodotti dalla pipeline <b>reale</b>. Verifica che
/// l'auto-instrumentation ASP.NET Core e lo span di business custom (<c>Books.Create</c>) siano cablati e
/// propaghino il contesto (stessa trace). Un futuro refactoring che spegne la strumentazione rompe qui.
/// I test della collection "Integration" girano in serie, quindi la finestra del listener globale non si
/// sovrappone ad altri test (vedi <c>.claude/lessons.md</c> [L18]).
/// </summary>
[Collection("Integration")]
public class OpenTelemetryTracingTests : IAsyncLifetime
{
    private readonly PlaygroundApiFactory _factory;
    private readonly HttpClient _readClient;
    private readonly HttpClient _writeClient;

    public OpenTelemetryTracingTests(PlaygroundApiFactory factory)
    {
        _factory = factory;
        _readClient = factory.CreateClientWithScope(BooksPermissions.ScopeRead);
        _writeClient = factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
    }

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Aggancia un listener (campiona tutto) alla source custom + a quelle del framework/instrumentation,
    // così gli span vengono creati e catturati a prescindere dall'exporter configurato nell'app.
    private static (ActivityListener Listener, ConcurrentBag<Activity> Activities) CaptureSpans()
    {
        var activities = new ConcurrentBag<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == BooksDiagnostics.ActivitySourceName
                || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || source.Name.StartsWith("OpenTelemetry.Instrumentation", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, activities);
    }

    private async Task<Author> SeedAuthorAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        var author = new Author { FullName = "Telemetry Author" };
        db.Authors.Add(author);
        await db.SaveChangesAsync();
        return author;
    }

    [Fact]
    public async Task GetBooks_ProducesAspNetCoreServerSpan()
    {
        var (listener, activities) = CaptureSpans();
        using (listener)
        {
            var response = await _readClient.GetAsync("/api/v1/books");
            response.EnsureSuccessStatusCode();
        }

        // Auto-instrumentation ASP.NET Core: uno span server per la richiesta HTTP.
        Assert.Contains(activities, a =>
            a.Kind == ActivityKind.Server && (a.GetTagItem("http.request.method") as string) == "GET");
    }

    [Fact]
    public async Task CreateBook_ProducesCustomBusinessSpan_InSameTrace()
    {
        var author = await SeedAuthorAsync();

        var (listener, activities) = CaptureSpans();
        using (listener)
        {
            var response = await _writeClient.PostAsJsonAsync(
                "/api/v1/books", new CreateBookDto("Observability in Action", author.Id));
            response.EnsureSuccessStatusCode();
        }

        // Span di business custom presente e annidato nello stesso trace dello span server (contesto propagato).
        var custom = Assert.Single(activities, a => a.OperationName == BooksDiagnostics.CreateBookActivityName);
        Assert.NotNull(custom.Parent);

        var server = activities.FirstOrDefault(a => a.Kind == ActivityKind.Server);
        Assert.NotNull(server);
        Assert.Equal(server!.TraceId, custom.TraceId);

        // Instrumentation EF Core (beta): la query SQL appare come span Client nello stesso trace.
        Assert.Contains(activities, a => a.Kind == ActivityKind.Client && a.TraceId == custom.TraceId);
    }
}
