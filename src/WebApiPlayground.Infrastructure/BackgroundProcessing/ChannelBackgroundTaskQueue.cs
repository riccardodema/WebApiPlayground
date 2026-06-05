using System.Threading.Channels;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.BackgroundProcessing;
using WebApiPlayground.Application.Diagnostics;

namespace WebApiPlayground.Infrastructure.BackgroundProcessing;

/// <summary>
/// Implementazione di <see cref="IBackgroundTaskQueue{T}"/> su <c>System.Threading.Channels</c> con un canale
/// <b>bounded</b> (backpressure): la coda non cresce illimitata. <c>TryEnqueue</c> è non bloccante
/// (best-effort) e i contatori enqueued/dropped rendono osservabile la pressione. Confinata in Infrastructure:
/// Application vede solo l'astrazione (regola NetArchTest). Vedi <c>.claude/context/background-processing.md</c>.
/// </summary>
public sealed class ChannelBackgroundTaskQueue<T> : IBackgroundTaskQueue<T>
{
    private readonly Channel<T> _channel;
    private int _depth;

    public ChannelBackgroundTaskQueue(IOptions<BackgroundProcessingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var capacity = Math.Max(1, options.Value.QueueCapacity);

        // FullMode.Wait è irrilevante per TryWrite (ritorna false quando piena, senza bloccare). SingleReader:
        // c'è un solo consumer (il worker), così il canale ottimizza. SingleWriter false: più producer (write + ...).
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public bool TryEnqueue(T item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _depth);
            BackgroundProcessingDiagnostics.RecordEnqueued();
            return true;
        }

        BackgroundProcessingDiagnostics.RecordDropped();
        return false;
    }

    public async ValueTask<T> DequeueAsync(CancellationToken cancellationToken)
    {
        var item = await _channel.Reader.ReadAsync(cancellationToken);
        Interlocked.Decrement(ref _depth);
        return item;
    }

    public int Depth => Volatile.Read(ref _depth);
}
