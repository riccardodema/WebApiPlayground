using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using WebApiPlayground.Application.Outbox;

namespace WebApiPlayground.Infrastructure.Outbox.ServiceBus;

/// <summary>
/// Trasporto <b>Azure Service Bus</b> (attivo se configurato): "pubblicare" significa inviare l'evento sulla coda
/// in modo durevole. Quando <see cref="ServiceBusSender.SendMessageAsync(ServiceBusMessage, CancellationToken)"/>
/// ritorna, il broker ha accettato il messaggio → il <c>OutboxProcessor</c> può marcare la riga processata
/// (l'outbox ha passato la consegna al broker, che ora garantisce il recapito al consumer). L'arricchimento vero
/// avviene <b>fuori</b> da questo path, nel <see cref="ServiceBusIntegrationEventConsumer"/>.
///
/// Il contratto del messaggio:
/// <list type="bullet">
///   <item><b>Body</b> = payload JSON dell'evento (stesse opzioni della riga outbox);</item>
///   <item><b>Subject</b> = <see cref="IntegrationEvent.EventType"/> → discriminatore per il routing lato consumer;</item>
///   <item><b>ContentType</b> = <c>application/json</c>.</item>
/// </list>
/// Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
internal sealed class ServiceBusIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly ServiceBusSender _sender;
    private readonly ILogger<ServiceBusIntegrationEventPublisher> _logger;

    public ServiceBusIntegrationEventPublisher(
        ServiceBusSender sender, ILogger<ServiceBusIntegrationEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(logger);
        _sender = sender;
        _logger = logger;
    }

    public async Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        var message = new ServiceBusMessage(IntegrationEventSerialization.Serialize(integrationEvent))
        {
            Subject = integrationEvent.EventType, // discriminatore per il routing lato consumer
            ContentType = "application/json",
        };

        // Un fallimento di invio propaga: il processore NON marca la riga e la riprova (at-least-once).
        await _sender.SendMessageAsync(message, cancellationToken);
        _logger.LogDebug(
            "Published integration event {EventType} to Service Bus queue {Queue}",
            integrationEvent.EventType, _sender.EntityPath);
    }
}
