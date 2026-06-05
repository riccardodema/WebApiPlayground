using System.Diagnostics;

namespace WebApiPlayground.Application.BackgroundProcessing;

/// <summary>
/// Work item accodato alla create/update di un libro: chiede al worker di arricchirne la popolarità in
/// background. Volutamente <b>minimale</b> — porta solo l'<see cref="BookId"/>; il worker ricarica titolo/
/// autore freschi dal DB nel proprio scope (niente dati potenzialmente stale nel messaggio). Porta inoltre
/// l'<see cref="ParentContext"/> della trace della richiesta che lo ha generato, così lo span del worker si
/// <b>aggancia alla stessa trace</b> oltre il confine asincrono (correlazione end-to-end con la write).
/// Vedi <c>.claude/context/background-processing.md</c> e <c>.claude/context/opentelemetry.md</c>.
/// </summary>
/// <param name="BookId">Id del libro da arricchire.</param>
/// <param name="ParentContext">Contesto della trace al momento dell'enqueue (<c>default</c> se nessuna trace attiva).</param>
public sealed record PopularityEnrichmentRequest(int BookId, ActivityContext ParentContext);
