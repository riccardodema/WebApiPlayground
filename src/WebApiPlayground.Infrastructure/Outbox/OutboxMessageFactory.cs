using System.Text.Json;
using WebApiPlayground.Application.Outbox;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Traduce un <see cref="IntegrationEvent"/> (Application) in una riga <see cref="OutboxMessage"/> (Domain)
/// e viceversa per la serializzazione. Vive in Infrastructure: è qui che il dettaglio "JSON" si confina.
/// Le stesse <see cref="SerializerOptions"/> vanno usate dal dispatcher in deserializzazione (contratto).
/// </summary>
internal static class OutboxMessageFactory
{
    /// <summary>Opzioni condivise producer↔consumer: camelCase + case-insensitive (default "Web").</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static OutboxMessage Create(IntegrationEvent message, DateTimeOffset occurredAt) => new()
    {
        Type = message.EventType,
        Payload = JsonSerializer.Serialize(message, message.GetType(), SerializerOptions),
        OccurredAt = occurredAt,
    };
}
