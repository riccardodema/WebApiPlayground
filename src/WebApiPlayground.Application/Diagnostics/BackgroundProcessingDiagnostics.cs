using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace WebApiPlayground.Application.Diagnostics;

/// <summary>
/// Punto unico (DRY) per la strumentazione OpenTelemetry <b>custom</b> del processamento in background. Come
/// <see cref="BooksDiagnostics"/>, usa solo primitive BCL (<see cref="ActivitySource"/>/<see cref="Meter"/>):
/// l'SDK e gli exporter vivono solo nell'Api, che registra questi nomi via <c>AddSource</c>/<c>AddMeter</c>.
/// I contatori sono <b>generici</b> (qualunque coda/worker) così l'astrazione resta riusabile. Lo span di
/// processing si aggancia al <c>ParentContext</c> del work item → correlazione con la richiesta che l'ha
/// generato. Vedi <c>.claude/context/background-processing.md</c> e <c>.claude/context/opentelemetry.md</c>.
/// </summary>
public static class BackgroundProcessingDiagnostics
{
    /// <summary>Nome dell'<see cref="ActivitySource"/>/<see cref="Meter"/> custom. Contratto stabile per le dashboard.</summary>
    public const string ActivitySourceName = "WebApiPlayground.BackgroundProcessing";

    /// <summary>Nome del <see cref="Meter"/> custom (uguale al source name, come per <see cref="BooksDiagnostics"/>).</summary>
    public const string MeterName = ActivitySourceName;

    public const string EnqueuedCounterName = "background.tasks.enqueued";
    public const string DroppedCounterName = "background.tasks.dropped";
    public const string ProcessedCounterName = "background.tasks.processed";
    public const string FailedCounterName = "background.tasks.failed";

    private static readonly string Version =
        typeof(BackgroundProcessingDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BackgroundProcessingDiagnostics).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Source delle trace custom. L'Api la abilita con <c>.AddSource(ActivitySourceName)</c>.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    private static readonly Meter Meter = new(MeterName, Version);

    private static readonly Counter<long> Enqueued = Meter.CreateCounter<long>(
        EnqueuedCounterName, unit: "{task}", description: "Work item accodati con successo.");

    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>(
        DroppedCounterName, unit: "{task}", description: "Work item scartati perché la coda era piena (backpressure).");

    private static readonly Counter<long> Processed = Meter.CreateCounter<long>(
        ProcessedCounterName, unit: "{task}", description: "Work item processati con successo dal worker.");

    private static readonly Counter<long> Failed = Meter.CreateCounter<long>(
        FailedCounterName, unit: "{task}", description: "Work item la cui elaborazione ha lanciato (isolati, non fermano il worker).");

    /// <summary>
    /// Avvia lo span di processing agganciato alla trace della richiesta originaria (<paramref name="parentContext"/>),
    /// così il lavoro asincrono compare nello stesso albero della write. <c>null</c> se nessun listener è in ascolto.
    /// </summary>
    public static Activity? StartProcessActivity(string activityName, ActivityContext parentContext) =>
        ActivitySource.StartActivity(activityName, ActivityKind.Internal, parentContext);

    public static void RecordEnqueued() => Enqueued.Add(1);
    public static void RecordDropped() => Dropped.Add(1);
    public static void RecordProcessed() => Processed.Add(1);
    public static void RecordFailed() => Failed.Add(1);
}
