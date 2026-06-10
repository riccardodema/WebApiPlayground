using System.Text.Json;
using WebApiPlayground.Application.Outbox;

namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Sorgente unica (DRY) per serializzare/deserializzare gli <see cref="IntegrationEvent"/> e per la mappa
/// <c>EventType</c> → tipo concreto. Confina il dettaglio "JSON" in Infrastructure ed è condivisa da TUTTI i punti
/// che attraversano il confine durevole con lo stesso contratto:
/// <list type="bullet">
///   <item>scrittura della riga outbox (<see cref="OutboxMessageFactory"/>);</item>
///   <item>lettura della riga outbox per la pubblicazione (<c>OutboxProcessor</c>);</item>
///   <item>serializzazione nel body del messaggio Service Bus (publisher ASB) e deserializzazione lato consumer.</item>
/// </list>
/// Un solo posto in cui aggiungere un nuovo tipo di evento → impossibile che producer e consumer divergano.
/// </summary>
internal static class IntegrationEventSerialization
{
    /// <summary>Opzioni condivise producer↔consumer: camelCase + case-insensitive (default "Web"). Contratto stabile.</summary>
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializza l'evento nel suo payload JSON (usa il tipo runtime → serializza i campi del concreto).</summary>
    public static string Serialize(IntegrationEvent integrationEvent) =>
        JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), Options);

    /// <summary>
    /// Ricostruisce l'evento concreto dal suo <paramref name="eventType"/> (discriminatore) e dal payload JSON.
    /// Tipo sconosciuto o payload vuoto → <see cref="InvalidOperationException"/> (il chiamante isola e logga).
    /// </summary>
    public static IntegrationEvent Deserialize(string eventType, string payload) => eventType switch
    {
        PopularityEnrichmentRequested.TypeName =>
            JsonSerializer.Deserialize<PopularityEnrichmentRequested>(payload, Options)
                ?? throw new InvalidOperationException($"Integration event '{eventType}' has an empty payload"),
        _ => throw new InvalidOperationException($"Unknown integration event type '{eventType}'"),
    };
}
