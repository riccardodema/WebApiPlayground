using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Loop di hosting dell'outbox: polla periodicamente (intervallo da <see cref="OutboxOptions"/>) e delega il
/// lavoro a un <see cref="OutboxProcessor"/> risolto in uno <b>scope per giro</b> (DbContext non catturato nel
/// singleton). Marca i messaggi solo a successo → consegna <b>at-least-once</b> durevole. Errori a livello di
/// batch (es. DB momentaneamente giù) sono isolati: si logga, si attende, si riprova — nessuna perdita. In PR-2
/// il processore pubblicherà su Azure Service Bus dietro la stessa astrazione. La logica vive nel processore
/// (testabile deterministicamente); qui solo il loop. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory, IOptions<OutboxOptions> options, ILogger<OutboxDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxDispatcher started (polling every {Interval}, batch {BatchSize}, max {MaxAttempts} attempts)",
            _options.PollingInterval, _options.BatchSize, _options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int processed;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                    processed = await processor.ProcessPendingAsync(stoppingToken);
                }

                // Batch pieno = probabilmente c'è altro arretrato: rifà subito; altrimenti attende il prossimo giro.
                if (processed < _options.BatchSize)
                    await Task.Delay(_options.PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // shutdown richiesto: usciamo puliti
            }
            catch (Exception ex)
            {
                // Errore a livello di batch (es. DB momentaneamente irraggiungibile): logga, attendi, riprova.
                // I messaggi non marcati restano non-processati → nessuna perdita.
                _logger.LogError(ex, "OutboxDispatcher batch failed — retrying after {Interval}", _options.PollingInterval);
                try { await Task.Delay(_options.PollingInterval, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }

        _logger.LogInformation("OutboxDispatcher stopping");
    }
}
