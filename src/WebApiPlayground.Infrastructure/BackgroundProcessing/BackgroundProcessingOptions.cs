namespace WebApiPlayground.Infrastructure.BackgroundProcessing;

/// <summary>
/// Configurazione del processamento in background (sezione <c>BackgroundProcessing</c>). Vedi
/// <c>.claude/context/background-processing.md</c>.
/// </summary>
public sealed class BackgroundProcessingOptions
{
    public const string SectionName = "BackgroundProcessing";

    /// <summary>
    /// Capacità massima della coda <b>bounded</b> (backpressure): oltre questo numero di item in attesa,
    /// <c>TryEnqueue</c> scarta (best-effort) invece di far crescere la memoria all'infinito.
    /// </summary>
    public int QueueCapacity { get; set; } = 100;
}
