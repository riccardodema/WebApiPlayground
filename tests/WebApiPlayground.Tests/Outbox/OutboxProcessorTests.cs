using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Outbox;
using WebApiPlayground.Tests.Persistence;
using Xunit;

namespace WebApiPlayground.Tests.Outbox;

/// <summary>
/// Semantica at-least-once dell'unità di lavoro outbox, su un DB relazionale reale (SQLite):
/// <c>ProcessedAt</c> SOLO a successo, fallimenti isolati per-messaggio (Attempts/Error), poison
/// fuori dal giro dopo MaxAttempts, batch FIFO per Id e progresso persistito per-messaggio.
/// </summary>
public sealed class OutboxProcessorTests : IDisposable
{
    private sealed class RecordingPublisher : IIntegrationEventPublisher
    {
        public readonly List<IntegrationEvent> Published = [];
        public Func<IntegrationEvent, Exception?>? FailWith { get; set; }

        public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            if (FailWith?.Invoke(integrationEvent) is { } failure)
                throw failure;
            Published.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlitePlaygroundDb _db = SqlitePlaygroundDb.Create();
    private readonly RecordingPublisher _publisher = new();

    public void Dispose() => _db.Dispose();

    private OutboxProcessor BuildProcessor(int batchSize = 20, int maxAttempts = 5) =>
        new(_db.Context, _publisher, new FixedTimeProvider(Now),
            Options.Create(new OutboxOptions { BatchSize = batchSize, MaxAttempts = maxAttempts }),
            NullLogger<OutboxProcessor>.Instance);

    private async Task<OutboxMessage> SeedMessageAsync(
        int bookId = 1, int attempts = 0, DateTimeOffset? processedAt = null, string? type = null)
    {
        var message = new OutboxMessage
        {
            Type = type ?? PopularityEnrichmentRequested.TypeName,
            Payload = IntegrationEventSerialization.Serialize(new PopularityEnrichmentRequested(bookId, null)),
            OccurredAt = Now.AddMinutes(-1),
            Attempts = attempts,
            ProcessedAt = processedAt,
        };
        _db.Context.OutboxMessages.Add(message);
        await _db.Context.SaveChangesAsync();
        _db.Context.ChangeTracker.Clear();
        return message;
    }

    private async Task<OutboxMessage> ReloadAsync(long id)
    {
        using var fresh = _db.CreateFreshContext();
        return await fresh.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    // ---- Successo: handoff durevole --------------------------------------------

    [Fact]
    public async Task Pending_message_is_published_and_marked_processed_at_the_clock_time()
    {
        var message = await SeedMessageAsync(bookId: 42);

        var processed = await BuildProcessor().ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        var published = Assert.IsType<PopularityEnrichmentRequested>(Assert.Single(_publisher.Published));
        Assert.Equal(42, published.BookId); // il payload è stato DESERIALIZZATO, non inoltrato a stringa

        var reloaded = await ReloadAsync(message.Id);
        Assert.Equal(Now, reloaded.ProcessedAt); // timestamp dal TimeProvider, non da DateTime.UtcNow
        Assert.Equal(0, reloaded.Attempts);
    }

    [Fact]
    public async Task Already_processed_messages_are_never_republished()
    {
        await SeedMessageAsync(processedAt: Now.AddMinutes(-5));

        var processed = await BuildProcessor().ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, processed);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task Empty_outbox_is_a_cheap_no_op()
    {
        Assert.Equal(0, await BuildProcessor().ProcessPendingAsync(CancellationToken.None));
    }

    // ---- Fallimenti: isolamento e retry -------------------------------------------

    [Fact]
    public async Task Publish_failure_keeps_the_message_pending_and_counts_the_attempt()
    {
        var message = await SeedMessageAsync();
        _publisher.FailWith = _ => new InvalidOperationException("broker down");

        await BuildProcessor().ProcessPendingAsync(CancellationToken.None);

        var reloaded = await ReloadAsync(message.Id);
        Assert.Null(reloaded.ProcessedAt);              // NON consegnato → verrà riprovato
        Assert.Equal(1, reloaded.Attempts);
        Assert.Equal("broker down", reloaded.Error);    // diagnostica del perché
    }

    [Fact]
    public async Task One_failing_message_does_not_block_the_rest_of_the_batch()
    {
        var poison = await SeedMessageAsync(bookId: 1);
        var healthy = await SeedMessageAsync(bookId: 2);
        _publisher.FailWith = e => ((PopularityEnrichmentRequested)e).BookId == 1
            ? new InvalidOperationException("boom") : null;

        await BuildProcessor().ProcessPendingAsync(CancellationToken.None);

        Assert.Null((await ReloadAsync(poison.Id)).ProcessedAt);
        Assert.NotNull((await ReloadAsync(healthy.Id)).ProcessedAt); // il sano è passato comunque
    }

    [Fact]
    public async Task Unknown_event_type_is_isolated_like_any_other_failure()
    {
        var message = await SeedMessageAsync(type: "NoSuchEvent");

        await BuildProcessor().ProcessPendingAsync(CancellationToken.None);

        Assert.Empty(_publisher.Published);
        var reloaded = await ReloadAsync(message.Id);
        Assert.Equal(1, reloaded.Attempts);
        Assert.Contains("NoSuchEvent", reloaded.Error);
    }

    // ---- Poison: oltre MaxAttempts esce dal giro ------------------------------------

    [Fact]
    public async Task Messages_at_max_attempts_are_skipped_entirely()
    {
        await SeedMessageAsync(attempts: 5);

        var processed = await BuildProcessor(maxAttempts: 5).ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, processed);          // né pubblicato né contato: è poison
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task Message_one_attempt_below_the_limit_still_gets_its_last_chance()
    {
        var message = await SeedMessageAsync(attempts: 4); // boundary: MaxAttempts-1

        var processed = await BuildProcessor(maxAttempts: 5).ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.NotNull((await ReloadAsync(message.Id)).ProcessedAt);
    }

    // ---- Telemetria: i contatori raccontano il lavoro fatto -----------------------------

    [Fact]
    public async Task Success_and_failure_each_increment_their_own_counter()
    {
        using var processed = new Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long>(
            null, Application.Diagnostics.BackgroundProcessingDiagnostics.MeterName,
            Application.Diagnostics.BackgroundProcessingDiagnostics.ProcessedCounterName);
        using var failed = new Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long>(
            null, Application.Diagnostics.BackgroundProcessingDiagnostics.MeterName,
            Application.Diagnostics.BackgroundProcessingDiagnostics.FailedCounterName);

        await SeedMessageAsync(bookId: 1);
        await SeedMessageAsync(bookId: 2);
        _publisher.FailWith = e => ((PopularityEnrichmentRequested)e).BookId == 2
            ? new InvalidOperationException("boom") : null;

        await BuildProcessor().ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, processed.GetMeasurementSnapshot().EvaluateAsCounter());
        Assert.Equal(1, failed.GetMeasurementSnapshot().EvaluateAsCounter());
    }

    [Fact]
    public void Constructor_rejects_missing_dependencies()
    {
        var options = Options.Create(new OutboxOptions());
        var logger = NullLogger<OutboxProcessor>.Instance;

        Assert.Throws<ArgumentNullException>(() => new OutboxProcessor(null!, _publisher, TimeProvider.System, options, logger));
        Assert.Throws<ArgumentNullException>(() => new OutboxProcessor(_db.Context, null!, TimeProvider.System, options, logger));
        Assert.Throws<ArgumentNullException>(() => new OutboxProcessor(_db.Context, _publisher, null!, options, logger));
        Assert.Throws<ArgumentNullException>(() => new OutboxProcessor(_db.Context, _publisher, TimeProvider.System, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new OutboxProcessor(_db.Context, _publisher, TimeProvider.System, options, null!));
    }

    // ---- Batch: dimensione e ordine ---------------------------------------------------

    [Fact]
    public async Task Batch_respects_size_and_fifo_order_by_id()
    {
        var first = await SeedMessageAsync(bookId: 1);
        var second = await SeedMessageAsync(bookId: 2);
        var third = await SeedMessageAsync(bookId: 3);

        var processed = await BuildProcessor(batchSize: 2).ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(2, processed);
        // FIFO: i due più VECCHI (Id più basso) prima — l'ordine di pubblicazione preserva la causalità.
        Assert.Equal([1, 2], _publisher.Published.Cast<PopularityEnrichmentRequested>().Select(e => e.BookId));
        Assert.Null((await ReloadAsync(third.Id)).ProcessedAt);

        Assert.Equal(1, await BuildProcessor(batchSize: 2).ProcessPendingAsync(CancellationToken.None));
        Assert.NotNull((await ReloadAsync(third.Id)).ProcessedAt); // il giro dopo completa la coda
        _ = first; _ = second;
    }
}
