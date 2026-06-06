namespace WebApiPlayground.Domain.Entities;

/// <summary>
/// Riga della <b>outbox transazionale</b>: un evento di integrazione persistito nella stessa transazione
/// della write di business (es. la create/update di un <see cref="Book"/>), così messaggio e dato sono
/// atomici — o committano insieme, o rollback insieme. Un dispatcher in background la drena e la marca
/// <see cref="ProcessedAt"/> solo a esito riuscito (consegna <b>at-least-once</b>, durevole oltre i restart).
/// POCO: nessuna dipendenza di persistenza (mappata in Infrastructure). Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public class OutboxMessage
{
    /// <summary>Chiave store-generated (IDENTITY): dà anche l'ordine FIFO di consegna.</summary>
    public long Id { get; set; }

    /// <summary>Discriminatore dell'evento (es. "PopularityEnrichmentRequested"): il dispatcher ci instrada sopra.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Evento serializzato (JSON): il dispatcher lo deserializza nel tipo concreto di <see cref="Type"/>.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Quando l'evento è stato prodotto (timestamp del producer).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary><c>null</c> = ancora da processare; valorizzato dal dispatcher quando la consegna riesce.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Tentativi falliti: il dispatcher smette di riprovare oltre un massimo (poison message).</summary>
    public int Attempts { get; set; }

    /// <summary>Ultimo errore osservato (diagnostica dei retry); <c>null</c> finché non fallisce.</summary>
    public string? Error { get; set; }
}
