using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.Diagnostics;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Outbox;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// <b>Routing + esecuzione</b> di un evento di integrazione: instrada sul tipo ed esegue il lavoro tramite
/// l'astrazione riusabile (<see cref="IPopularityEnricher"/>). È il "consumatore" logico, condiviso dai due
/// trasporti così la logica di gestione esiste in un <b>unico</b> posto:
/// <list type="bullet">
///   <item><c>InProcessIntegrationEventPublisher</c> lo invoca <i>subito</i> (consegna in-process, default);</item>
///   <item><c>ServiceBusIntegrationEventConsumer</c> lo invoca quando <i>riceve</i> il messaggio dal broker.</item>
/// </list>
/// Scoped (un <see cref="IPopularityEnricher"/>/DbContext per gestione). Lo span "Popularity.Enrich" si aggancia
/// al <see cref="PopularityEnrichmentRequested.TraceParent"/> catturato all'enqueue → la trace della write segue
/// l'evento <b>oltre il confine durevole</b> (e oltre il broker, in PR-2). Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
internal sealed class IntegrationEventHandler
{
    private const string EnrichActivityName = "Popularity.Enrich";

    private readonly IPopularityEnricher _enricher;
    private readonly ILogger<IntegrationEventHandler> _logger;

    public IntegrationEventHandler(IPopularityEnricher enricher, ILogger<IntegrationEventHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(enricher);
        ArgumentNullException.ThrowIfNull(logger);
        _enricher = enricher;
        _logger = logger;
    }

    /// <summary>Instrada l'evento sul tipo ed esegue il lavoro. Propaga l'eccezione: il chiamante decide retry/isolamento.</summary>
    public async Task HandleAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        switch (integrationEvent)
        {
            case PopularityEnrichmentRequested enrichment:
                // Span agganciato alla trace della write che ha prodotto l'evento (correlazione oltre il confine durevole).
                using (var activity = StartEnrichActivity(enrichment.TraceParent))
                {
                    activity?.SetTag("book.id", enrichment.BookId);
                    await _enricher.EnrichAsync(enrichment.BookId, cancellationToken);
                }

                break;

            default:
                // I tipi noti sono enumerati in IntegrationEventSerialization.Deserialize: arrivare qui significa
                // un evento aggiunto al serializzatore ma non al routing → fail-fast esplicito (bug di programmazione).
                throw new InvalidOperationException(
                    $"No handler registered for integration event '{integrationEvent.EventType}'");
        }

        _logger.LogDebug("Integration event {EventType} handled", integrationEvent.EventType);
    }

    private static Activity? StartEnrichActivity(string? traceParent) =>
        ActivityContext.TryParse(traceParent, null, out var parent)
            ? BackgroundProcessingDiagnostics.StartProcessActivity(EnrichActivityName, parent)
            : BackgroundProcessingDiagnostics.ActivitySource.StartActivity(EnrichActivityName);
}
