namespace WebApiPlayground.Api.Observability;

/// <summary>
/// Opzioni OpenTelemetry (sezione <c>OpenTelemetry</c>). Config-gated come Cache/Redis: con
/// <see cref="OtlpEndpoint"/> vuoto la telemetria è solo raccolta (nessun export); valorizzandolo si
/// esportano traces + metrics + logs via OTLP verso un collector/backend. <see cref="ConsoleExporter"/>
/// stampa la telemetria su console (utile in locale, senza collector). Vedi <c>.claude/context/opentelemetry.md</c>.
/// </summary>
public sealed class OpenTelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    /// <summary>Endpoint OTLP (es. <c>http://localhost:4317</c>, gRPC). Vuoto ⇒ nessun export OTLP.</summary>
    public string OtlpEndpoint { get; set; } = string.Empty;

    /// <summary>Nome del servizio (resource attribute <c>service.name</c>) nelle trace/metriche/log.</summary>
    public string ServiceName { get; set; } = "WebApiPlayground.Api";

    /// <summary>Se <c>true</c>, aggiunge il console exporter per traces e metrics (telemetria visibile in locale).</summary>
    public bool ConsoleExporter { get; set; }
}
