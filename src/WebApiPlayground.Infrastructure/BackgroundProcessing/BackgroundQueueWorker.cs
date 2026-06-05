using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.BackgroundProcessing;
using WebApiPlayground.Application.Diagnostics;

namespace WebApiPlayground.Infrastructure.BackgroundProcessing;

/// <summary>
/// Base riusabile per i consumer di una <see cref="IBackgroundTaskQueue{T}"/>. Concentra in un solo punto
/// (testato una volta sola) i pitfall del <see cref="BackgroundService"/>:
/// <list type="bullet">
/// <item><b>Isolamento delle eccezioni</b>: un'eccezione che esce da <c>ExecuteAsync</c> fermerebbe l'intero
/// host (.NET 6+: <c>BackgroundServiceExceptionBehavior.StopHost</c>). Qui ogni item è in try/catch → un item
/// velenoso viene loggato/contato e il loop prosegue.</item>
/// <item><b>Scope per item</b>: i servizi scoped (DbContext, repository) si risolvono in uno scope dedicato
/// per ogni item — mai catturati nel singleton del worker (captive dependency).</item>
/// <item><b>Shutdown graceful</b>: si rispetta lo <c>stoppingToken</c>; l'item in volo finisce, la coda
/// residua viene abbandonata (at-most-once, debolezza voluta → Outbox).</item>
/// </list>
/// Vedi <c>.claude/context/background-processing.md</c> e <c>.claude/lessons.md</c> [L21].
/// </summary>
public abstract class BackgroundQueueWorker<T> : BackgroundService
{
    private readonly IBackgroundTaskQueue<T> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    protected BackgroundQueueWorker(
        IBackgroundTaskQueue<T> queue, IServiceScopeFactory scopeFactory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Nome del worker per i log (tipicamente <c>nameof(...)</c>).</summary>
    protected abstract string WorkerName { get; }

    /// <summary>Elabora un singolo work item dentro lo scope <paramref name="services"/> creato dalla base.</summary>
    protected abstract Task ProcessAsync(IServiceProvider services, T item, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started", WorkerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            T item;
            try
            {
                item = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutdown richiesto: usciamo puliti, niente eccezione fuori da ExecuteAsync
            }

            try
            {
                // Scope per item: le dipendenze scoped (DbContext/repository) non si catturano nel singleton.
                using var scope = _scopeFactory.CreateScope();
                await ProcessAsync(scope.ServiceProvider, item, stoppingToken);
                BackgroundProcessingDiagnostics.RecordProcessed();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Isolamento: l'errore di UN item non deve abbattere il worker (né l'host). Logga, conta, prosegui.
                BackgroundProcessingDiagnostics.RecordFailed();
                _logger.LogError(ex, "{Worker} failed to process a work item — skipping it and continuing", WorkerName);
            }
        }

        _logger.LogInformation("{Worker} stopping", WorkerName);
    }
}
