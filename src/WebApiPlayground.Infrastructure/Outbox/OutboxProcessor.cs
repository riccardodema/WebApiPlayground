using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebApiPlayground.Application.Diagnostics;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Unità di lavoro dell'outbox: processa un batch di messaggi non consegnati (FIFO per Id), instrada ciascuno
/// sul tipo ed esegue il lavoro tramite l'astrazione riusabile (<see cref="IPopularityEnricher"/>), marcandolo
/// <c>ProcessedAt</c> <b>solo a successo</b> (consegna at-least-once durevole). Un fallimento del singolo
/// messaggio è isolato (incrementa <c>Attempts</c>/<c>Error</c> e si riprova al giro dopo). È <b>separata dal
/// loop di hosting</b> (<see cref="OutboxDispatcher"/>) così è guidabile in modo deterministico nei test
/// (un <c>ProcessPendingAsync</c> esplicito, senza polling). Scoped: un <see cref="PlaygroundDbContext"/> per
/// scope. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public sealed class OutboxProcessor
{
    private const string EnrichActivityName = "Popularity.Enrich";

    private readonly PlaygroundDbContext _db;
    private readonly IPopularityEnricher _enricher;
    private readonly TimeProvider _timeProvider;
    private readonly OutboxOptions _options;
    private readonly ILogger<OutboxProcessor> _logger;

    public OutboxProcessor(
        PlaygroundDbContext db,
        IPopularityEnricher enricher,
        TimeProvider timeProvider,
        IOptions<OutboxOptions> options,
        ILogger<OutboxProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(enricher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _db = db;
        _enricher = enricher;
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
                await DispatchAsync(message, cancellationToken);
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

    /// <summary>Instrada il messaggio sul tipo (routing) ed esegue il lavoro.</summary>
    private async Task DispatchAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case PopularityEnrichmentRequested.TypeName:
                var evt = JsonSerializer.Deserialize<PopularityEnrichmentRequested>(
                              message.Payload, OutboxMessageFactory.SerializerOptions)
                          ?? throw new InvalidOperationException($"Outbox message {message.Id} has an empty payload");

                // Span agganciato alla trace della write che ha prodotto l'evento (correlazione oltre il confine durevole).
                using (var activity = StartEnrichActivity(evt.TraceParent))
                {
                    activity?.SetTag("book.id", evt.BookId);
                    await _enricher.EnrichAsync(evt.BookId, cancellationToken);
                }

                break;

            default:
                throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'");
        }
    }

    private static Activity? StartEnrichActivity(string? traceParent) =>
        ActivityContext.TryParse(traceParent, null, out var parent)
            ? BackgroundProcessingDiagnostics.StartProcessActivity(EnrichActivityName, parent)
            : BackgroundProcessingDiagnostics.ActivitySource.StartActivity(EnrichActivityName);
}
