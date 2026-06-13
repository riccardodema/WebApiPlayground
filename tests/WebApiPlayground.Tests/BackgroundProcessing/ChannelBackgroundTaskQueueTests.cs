using Microsoft.Extensions.Options;
using WebApiPlayground.Infrastructure.BackgroundProcessing;
using Xunit;

namespace WebApiPlayground.Tests.BackgroundProcessing;

/// <summary>
/// Unit della coda bounded su <c>Channel</c>: capacità/backpressure (TryEnqueue best-effort), ordine FIFO,
/// rispetto della cancellation, profondità coerente. Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
public class ChannelBackgroundTaskQueueTests
{
    private static ChannelBackgroundTaskQueue<int> CreateQueue(int capacity) =>
        new(Options.Create(new BackgroundProcessingOptions { QueueCapacity = capacity }));

    [Fact]
    public void TryEnqueue_ReturnsFalse_WhenQueueIsFull()
    {
        var queue = CreateQueue(capacity: 2);

        Assert.True(queue.TryEnqueue(1));
        Assert.True(queue.TryEnqueue(2));
        Assert.False(queue.TryEnqueue(3)); // piena → best-effort drop, niente blocco

        Assert.Equal(2, queue.Depth);
    }

    [Fact]
    public async Task DequeueAsync_ReturnsItems_InFifoOrder()
    {
        var queue = CreateQueue(capacity: 10);
        queue.TryEnqueue(1);
        queue.TryEnqueue(2);
        queue.TryEnqueue(3);

        Assert.Equal(1, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(2, await queue.DequeueAsync(CancellationToken.None));
        Assert.Equal(3, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Depth_DecrementsAfterDequeue()
    {
        var queue = CreateQueue(capacity: 10);
        queue.TryEnqueue(1);
        queue.TryEnqueue(2);
        Assert.Equal(2, queue.Depth);

        await queue.DequeueAsync(CancellationToken.None);

        Assert.Equal(1, queue.Depth);
    }

    [Fact]
    public async Task DequeueAsync_Throws_WhenTokenAlreadyCancelled()
    {
        var queue = CreateQueue(capacity: 1);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await queue.DequeueAsync(cts.Token));
    }

    [Fact]
    public void Depth_increments_on_successful_enqueue()
    {
        var queue = CreateQueue(capacity: 10);

        Assert.Equal(0, queue.Depth);
        queue.TryEnqueue(1);
        Assert.Equal(1, queue.Depth); // l'enqueue, non solo il dequeue, muove la profondità
    }

    [Fact]
    public void Dropped_enqueue_does_not_change_depth()
    {
        var queue = CreateQueue(capacity: 1);
        queue.TryEnqueue(1);

        Assert.False(queue.TryEnqueue(2)); // drop
        Assert.Equal(1, queue.Depth);      // un drop NON deve contare come accodato
    }

    [Theory]
    [InlineData(0)]   // capacità non valida → floor a 1, niente crash
    [InlineData(-5)]
    public void Non_positive_capacity_is_floored_to_one(int capacity)
    {
        var queue = CreateQueue(capacity);

        Assert.True(queue.TryEnqueue(1));   // almeno un posto c'è
        Assert.False(queue.TryEnqueue(2));  // ma uno solo: il floor è 1, non illimitato
    }

    [Fact]
    public void Constructor_rejects_null_options() =>
        Assert.Throws<ArgumentNullException>(() => new ChannelBackgroundTaskQueue<int>(null!));
}
