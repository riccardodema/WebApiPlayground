using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using WebApiPlayground.Application.BackgroundProcessing;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Popularity;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.BackgroundProcessing;
using Xunit;

namespace WebApiPlayground.Tests.BackgroundProcessing;

/// <summary>
/// Unit del consumer concreto: carica il libro, chiama il client (resiliente+cachato), persiste lo snapshot.
/// Si guida via la coda reale + un host del worker (StartAsync/StopAsync), segnalando il completamento con un
/// <see cref="TaskCompletionSource"/> per restare deterministici. Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
public class PopularityEnrichmentWorkerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly Mock<IBookRepository> _repository = new();
    private readonly Mock<IBookPopularityClient> _client = new();
    private readonly Mock<IBookPopularitySnapshotRepository> _snapshots = new();

    public PopularityEnrichmentWorkerTests() => _client.SetupGet(c => c.SourceName).Returns("Open Library");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static Book SampleBook(int id = 7) => new()
    {
        Id = id,
        Title = "Dune",
        AuthorId = 3,
        Author = new Author { Id = 3, FullName = "Frank Herbert" },
    };

    // Costruisce un provider con i mock scoped + worker reale sulla coda reale.
    private (ChannelBackgroundTaskQueue<PopularityEnrichmentRequest> Queue, PopularityEnrichmentWorker Worker) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
        services.AddScoped(_ => _repository.Object);
        services.AddScoped(_ => _client.Object);
        services.AddScoped(_ => _snapshots.Object);
        var provider = services.BuildServiceProvider();

        var queue = new ChannelBackgroundTaskQueue<PopularityEnrichmentRequest>(
            Options.Create(new BackgroundProcessingOptions { QueueCapacity = 16 }));
        var worker = new PopularityEnrichmentWorker(
            queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<PopularityEnrichmentWorker>.Instance);
        return (queue, worker);
    }

    [Fact]
    public async Task PersistsSnapshot_FromExternalSignals_WithFixedTimestamp()
    {
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync("Dune", "Frank Herbert", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookPopularity(4.5, 10, 100, 5, 50, 155));

        var upserted = new TaskCompletionSource<BookPopularitySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots
            .Setup(s => s.UpsertAsync(It.IsAny<BookPopularitySnapshot>(), It.IsAny<CancellationToken>()))
            .Callback<BookPopularitySnapshot, CancellationToken>((s, _) => upserted.TrySetResult(s))
            .Returns(Task.CompletedTask);

        var (queue, worker) = Build();
        await ((IHostedService)worker).StartAsync(CancellationToken.None);
        queue.TryEnqueue(new PopularityEnrichmentRequest(7, default));

        var snapshot = await upserted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        Assert.Equal(7, snapshot.BookId);
        Assert.Equal(4.5, snapshot.AverageRating);
        Assert.Equal(155, snapshot.ReadingLogCount);
        Assert.Equal("Open Library", snapshot.Source);
        Assert.Equal(FixedNow, snapshot.RetrievedAt); // timestamp dal TimeProvider iniettato (testabile)
    }

    [Fact]
    public async Task SkipsUpsert_WhenBookNoLongerExists()
    {
        // L'item 99 (libro inesistente) non deve fare upsert; usiamo l'item 7 come barriera deterministica:
        // single-consumer FIFO → quando 7 viene processato, 99 è già stato gestito.
        _repository.Setup(r => r.GetByIdAsync(99)).ReturnsAsync((Book?)null);
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook());
        _client
            .Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BookPopularity(1, 1, 1, 1, 1, 1));

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots
            .Setup(s => s.UpsertAsync(It.Is<BookPopularitySnapshot>(b => b.BookId == 7), It.IsAny<CancellationToken>()))
            .Callback(() => barrier.TrySetResult())
            .Returns(Task.CompletedTask);

        var (queue, worker) = Build();
        await ((IHostedService)worker).StartAsync(CancellationToken.None);
        queue.TryEnqueue(new PopularityEnrichmentRequest(99, default));
        queue.TryEnqueue(new PopularityEnrichmentRequest(7, default));

        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        _snapshots.Verify(s => s.UpsertAsync(It.Is<BookPopularitySnapshot>(b => b.BookId == 99), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IsolatesFailure_WhenExternalThrows_NoSnapshotForThatItem()
    {
        // Item 7: il client lancia (outage) → nessuno snapshot, errore isolato dalla base. Item 8 come barriera.
        // FIFO single-consumer: la prima chiamata al client è per il 7 (lancia), la seconda per l'8 (ok).
        _repository.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(SampleBook(7));
        _repository.Setup(r => r.GetByIdAsync(8)).ReturnsAsync(SampleBook(8));

        var calls = 0;
        _client
            .Setup(c => c.GetPopularityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((_, _, _) =>
                Interlocked.Increment(ref calls) == 1
                    ? throw new ExternalServiceUnavailableException("Open Library")
                    : Task.FromResult<BookPopularity?>(new BookPopularity(2, 2, 2, 2, 2, 2)));

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _snapshots
            .Setup(s => s.UpsertAsync(It.Is<BookPopularitySnapshot>(b => b.BookId == 8), It.IsAny<CancellationToken>()))
            .Callback(() => barrier.TrySetResult())
            .Returns(Task.CompletedTask);

        var (queue, worker) = Build();
        await ((IHostedService)worker).StartAsync(CancellationToken.None);
        queue.TryEnqueue(new PopularityEnrichmentRequest(7, default)); // lancia → isolato
        queue.TryEnqueue(new PopularityEnrichmentRequest(8, default)); // ok → barriera

        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        _snapshots.Verify(s => s.UpsertAsync(It.Is<BookPopularitySnapshot>(b => b.BookId == 7), It.IsAny<CancellationToken>()), Times.Never);
    }
}
