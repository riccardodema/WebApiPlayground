using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace WebApiPlayground.Application.Diagnostics;

/// <summary>
/// Punto unico (DRY, coerente con "no magic strings" di <c>conventions.md</c>) per la strumentazione
/// OpenTelemetry <b>custom</b> del dominio Books. Usa solo primitive BCL
/// (<see cref="System.Diagnostics.ActivitySource"/> / <see cref="System.Diagnostics.Metrics.Meter"/>):
/// l'SDK OpenTelemetry e gli exporter vivono <b>solo</b> nella composition root (layer Api), che si limita
/// a registrare questi nomi via <c>AddSource</c>/<c>AddMeter</c>. Così il codice di business resta
/// strumentabile senza dipendere dall'SDK — le regole di layering (NetArchTest) restano rispettate, come
/// già accade con <c>Microsoft.Extensions.Logging.Abstractions</c>. Vedi <c>.claude/context/opentelemetry.md</c>.
/// </summary>
public static class BooksDiagnostics
{
    /// <summary>Nome dell'<see cref="ActivitySource"/> custom. Contratto stabile: le dashboard ci si agganciano.</summary>
    public const string ActivitySourceName = "WebApiPlayground.Books";

    /// <summary>Nome del <see cref="Meter"/> custom. Contratto stabile come sopra.</summary>
    public const string MeterName = "WebApiPlayground.Books";

    /// <summary>Nome dello span di business sulla creazione di un libro.</summary>
    public const string CreateBookActivityName = "Books.Create";

    /// <summary>Nome della metrica: numero di libri creati con successo.</summary>
    public const string BooksCreatedCounterName = "books.created";

    // Versione dell'assembly (es. "1.0.0"): diventa la versione dello scope OTel (instrumentation scope).
    private static readonly string Version =
        typeof(BooksDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BooksDiagnostics).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Source delle trace custom. L'Api la abilita con <c>.AddSource(ActivitySourceName)</c>.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, Version);

    private static readonly Meter Meter = new(MeterName, Version);

    private static readonly Counter<long> BooksCreated = Meter.CreateCounter<long>(
        BooksCreatedCounterName,
        unit: "{book}",
        description: "Numero di libri creati con successo.");

    /// <summary>
    /// Avvia lo span di business sulla creazione di un libro. Restituisce <c>null</c> se nessun listener è
    /// in ascolto (overhead ~zero quando OTel non è configurato) — il chiamante lo usa in <c>using</c>.
    /// </summary>
    public static Activity? StartCreateBookActivity() =>
        ActivitySource.StartActivity(CreateBookActivityName, ActivityKind.Internal);

    /// <summary>Registra la creazione di un libro avvenuta con successo (incrementa <c>books.created</c>).</summary>
    public static void RecordBookCreated() => BooksCreated.Add(1);
}
