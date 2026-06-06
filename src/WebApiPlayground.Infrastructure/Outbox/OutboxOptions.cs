namespace WebApiPlayground.Infrastructure.Outbox;

/// <summary>
/// Configurazione del dispatcher dell'outbox (sezione <c>Outbox</c>). Vedi <c>.claude/context/outbox.md</c>.
/// </summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Intervallo di polling quando la coda è vuota (latenza max della consegna a regime).</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Quanti messaggi non processati elaborare per giro (FIFO per Id).</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>
    /// Tentativi massimi per messaggio prima di considerarlo "poison" e smettere di riprovarlo (evita di
    /// martellare all'infinito un messaggio che fallisce sempre). La riga resta in tabella per diagnostica.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;
}
