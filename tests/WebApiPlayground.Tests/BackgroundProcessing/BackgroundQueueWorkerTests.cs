using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.BackgroundProcessing;
using WebApiPlayground.Infrastructure.BackgroundProcessing;
using Xunit;

namespace WebApiPlayground.Tests.BackgroundProcessing;

/// <summary>
/// Test-chiave della base <see cref="BackgroundQueueWorker{T}"/>: concentra i pitfall del BackgroundService.
/// Verifica <b>isolamento delle eccezioni</b> (un item velenoso non ferma il loop), <b>scope per item</b>
/// (servizi scoped distinti per ogni item) e <b>stop graceful</b>. Vedi <c>.claude/lessons.md</c> [L21].
/// </summary>
public class BackgroundQueueWorkerTests
{
    private static ChannelBackgroundTaskQueue<int> CreateQueue(int capacity = 16) =>
        new(Options.Create(new BackgroundProcessingOptions { QueueCapacity = capacity }));

    // Worker di test: delega ProcessAsync a una lambda così ogni test decide il comportamento per item.
    private sealed class DelegatingWorker : BackgroundQueueWorker<int>
    {
        private readonly Func<IServiceProvider, int, Task> _onProcess;

        public DelegatingWorker(
            IBackgroundTaskQueue<int> queue, IServiceScopeFactory scopeFactory, Func<IServiceProvider, int, Task> onProcess)
            : base(queue, scopeFactory, NullLogger.Instance) => _onProcess = onProcess;

        protected override string WorkerName => nameof(DelegatingWorker);

        protected override Task ProcessAsync(IServiceProvider services, int item, CancellationToken cancellationToken) =>
            _onProcess(services, item);
    }

    private sealed class ScopeMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    [Fact]
    public async Task ThrowingItem_DoesNotStopTheLoop_NextItemIsStillProcessed()
    {
        var queue = CreateQueue();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var processed = new ConcurrentQueue<int>();
        var secondProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = new DelegatingWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), (_, item) =>
        {
            if (item == 1)
                throw new InvalidOperationException("poison item");

            processed.Enqueue(item);
            if (item == 2)
                secondProcessed.TrySetResult();
            return Task.CompletedTask;
        });

        await ((IHostedService)worker).StartAsync(CancellationToken.None);
        queue.TryEnqueue(1); // lancia → deve essere isolato
        queue.TryEnqueue(2); // deve comunque essere processato

        await secondProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        Assert.Contains(2, processed);
        Assert.DoesNotContain(1, processed);
    }

    [Fact]
    public async Task CreatesAFreshScope_PerItem()
    {
        var queue = CreateQueue();
        var services = new ServiceCollection();
        services.AddScoped<ScopeMarker>();
        await using var provider = services.BuildServiceProvider();

        var markers = new ConcurrentBag<Guid>();
        var twoSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = new DelegatingWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), (sp, _) =>
        {
            markers.Add(sp.GetRequiredService<ScopeMarker>().Id);
            if (markers.Count == 2)
                twoSeen.TrySetResult();
            return Task.CompletedTask;
        });

        await ((IHostedService)worker).StartAsync(CancellationToken.None);
        queue.TryEnqueue(1);
        queue.TryEnqueue(2);

        await twoSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((IHostedService)worker).StopAsync(CancellationToken.None);

        // Due item → due scope distinti → due istanze scoped diverse.
        Assert.Equal(2, markers.Distinct().Count());
    }

    [Fact]
    public async Task StopAsync_CompletesGracefully_WhenIdle()
    {
        var queue = CreateQueue();
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var worker = new DelegatingWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), (_, _) => Task.CompletedTask);

        await ((IHostedService)worker).StartAsync(CancellationToken.None);
        // Nessun item in coda: lo stop deve tornare pulito (loop in attesa → cancellazione → uscita).
        await ((IHostedService)worker).StopAsync(CancellationToken.None);
    }
}
