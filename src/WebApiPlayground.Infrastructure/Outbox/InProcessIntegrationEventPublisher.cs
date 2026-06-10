using WebApiPlayground.Application.Outbox;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Trasporto <b>in-process</b> (default, quando Service Bus non è configurato): "pubblicare" significa gestire
/// l'evento <i>subito</i> nello stesso processo, instradandolo all'<see cref="IntegrationEventHandler"/>. Il
/// <c>OutboxProcessor</c> marca il messaggio processato solo se questa ritorna senza errori → at-least-once
/// durevole in-process (il comportamento di PR-1, qui solo spostato dietro l'astrazione del trasporto).
/// Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
internal sealed class InProcessIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly IntegrationEventHandler _handler;

    public InProcessIntegrationEventPublisher(IntegrationEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
    }

    public Task PublishAsync(IntegrationEvent integrationEvent, CancellationToken cancellationToken) =>
        _handler.HandleAsync(integrationEvent, cancellationToken);
}
