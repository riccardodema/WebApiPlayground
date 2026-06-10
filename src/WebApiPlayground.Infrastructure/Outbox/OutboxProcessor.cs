using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.Diagnostics;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Unità di lavoro dell'outbox: processa un batch di messaggi non consegnati (FIFO per Id) e <b>pubblica</b>
/// ciascuno tramite <see cref="IIntegrationEventPublisher"/> (trasporto in-process o Azure Service Bus, scelto
/// nella composition root), marcandolo <c>ProcessedAt</c> <b>solo a successo</b> → consegna at-least-once durevole.
/// Un fallimento del singolo messaggio è isolato (incrementa <c>Attempts</c>/<c>Error</c> e si riprova al giro
/// dopo). È <b>separata dal loop di hosting</b> (<see cref="OutboxDispatcher"/>) così è guidabile in modo
/// deterministico nei test (un <c>ProcessPendingAsync</c> esplicito, senza polling). Scoped: un
/// <see cref="PlaygroundDbContext"/> per scope. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public sealed class OutboxProcessor
{
    private readonly PlaygroundDbContext _db;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        PlaygroundDbContext db,
        IIntegrationEventPublisher publisher,
        TimeProvider timeProvider,
        IOptions<OutboxOptions> options,
        ILogger<OutboxProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Processa un batch di messaggi non consegnati; ritorna quanti ne ha esaminati (0 se vuoto).</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var batch = await _db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.Attempts < _options.MaxAttempts)
            .OrderBy(m => m.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
            return 0;

        foreach (var message in batch)
        {
            try
            {
                await PublishAsync(message, cancellationToken);
                message.ProcessedAt = _timeProvider.GetUtcNow(); // at-least-once: marcato consegnato solo a successo
                BackgroundProcessingDiagnostics.RecordProcessed();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Isolamento del singolo messaggio: resta non-processato (verrà riprovato), conta il tentativo.
                message.Attempts++;
                message.Error = ex.Message;
                BackgroundProcessingDiagnostics.RecordFailed();
                _logger.LogWarning(ex,
                    "Outbox message {Id} ({Type}) failed (attempt {Attempt}/{Max}) — will retry",
                    message.Id, message.Type, message.Attempts, _options.MaxAttempts);
            }

            // Persistenza per-messaggio: un fallimento più avanti nel batch non perde il progresso dei precedenti.
            await _db.SaveChangesAsync(cancellationToken);
        }

        return batch.Count;
    }

    /// <summary>Deserializza la riga nel suo evento concreto e la consegna al trasporto configurato.</summary>
    private Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // Type sconosciuto o payload vuoto → lancia → il chiamante isola il messaggio (Attempts++) e prosegue.
        var integrationEvent = IntegrationEventSerialization.Deserialize(message.Type, message.Payload);
        return _publisher.PublishAsync(integrationEvent, cancellationToken);
    }
}
