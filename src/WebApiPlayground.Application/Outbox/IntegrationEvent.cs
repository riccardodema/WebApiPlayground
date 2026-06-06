using System.Text.Json.Serialization;

namespace WebApiPlayground.Application.Outbox;

/// <summary>
/// Base degli <b>eventi di integrazione</b> accodati nella outbox transazionale: dati immutabili e
/// <b>serializzabili</b> (JSON) che descrivono un fatto avvenuto in una write di business e da elaborare
/// in modo asincrono/durevole. Vive in Application e usa solo primitive BCL: il meccanismo di persistenza
/// (outbox/EF) e di consegna (dispatcher/broker) resta in Infrastructure. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public abstract record IntegrationEvent
{
    /// <summary>
    /// Discriminatore stabile del tipo, usato come <c>Type</c> della riga outbox e per il routing nel
    /// dispatcher. Escluso dal payload (è metadato, non dato dell'evento).
    /// </summary>
    [JsonIgnore]
    public abstract string EventType { get; }
}
