using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Traduce un <see cref="IntegrationEvent"/> (Application) in una riga <see cref="OutboxMessage"/> (Domain) da
/// persistere nella stessa transazione della write. La (de)serializzazione è delegata a
/// <see cref="IntegrationEventSerialization"/> — unica sorgente del contratto JSON condivisa con chi rilegge la
/// riga (<c>OutboxProcessor</c>) e col trasporto Service Bus. Vive in Infrastructure: è qui che il dettaglio
/// "JSON" si confina.
/// </summary>
internal static class OutboxMessageFactory
{
    public static OutboxMessage Create(IntegrationEvent message, DateTimeOffset occurredAt) => new()
    {
        Type = message.EventType,
        Payload = IntegrationEventSerialization.Serialize(message),
        OccurredAt = occurredAt,
    };
}
