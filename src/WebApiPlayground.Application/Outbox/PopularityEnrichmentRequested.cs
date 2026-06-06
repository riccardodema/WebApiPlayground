using System.Text.Json.Serialization;

namespace WebApiPlayground.Application.Outbox;

/// <summary>
/// Evento di integrazione emesso alla create/update di un libro: chiede di arricchirne la popolarità in
/// background (chiamata esterna fuori dal path di scrittura). Volutamente <b>minimale</b> — porta solo il
/// <see cref="BookId"/>; il consumer ricarica titolo/autore freschi dal DB. Porta inoltre il
/// <see cref="TraceParent"/> (traceparent W3C catturato all'enqueue) così lo span del consumer si aggancia
/// alla trace della write oltre il confine durevole. Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
/// <param name="BookId">Id del libro da arricchire.</param>
/// <param name="TraceParent">Traceparent W3C della richiesta che ha prodotto l'evento (<c>null</c> se nessuna trace attiva).</param>
public sealed record PopularityEnrichmentRequested(int BookId, string? TraceParent) : IntegrationEvent
{
    /// <summary>Discriminatore stabile (contratto col dispatcher). Non rinominare senza migrare le righe outbox esistenti.</summary>
    public const string TypeName = nameof(PopularityEnrichmentRequested);

    [JsonIgnore]
    public override string EventType => TypeName;
}
