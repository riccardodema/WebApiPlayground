using Microsoft.Extensions.Configuration;
using WebApiPlayground.Api.Observability;
using Xunit;

namespace WebApiPlayground.Tests.Observability;

/// <summary>
/// I default sono "collect-only" (nessun export finché non si configura un endpoint), e la sezione
/// <c>OpenTelemetry</c> si lega correttamente — lo stesso percorso usato da <c>AddApiObservability</c>.
/// Specchio di <c>RateLimitingOptionsTests</c>.
/// </summary>
public class OpenTelemetryOptionsTests
{
    [Fact]
    public void Defaults_AreCollectOnly_NoExport()
    {
        var options = new OpenTelemetryOptions();

        // Senza configurazione: nessun endpoint OTLP (no export) e console exporter spento.
        Assert.Equal(string.Empty, options.OtlpEndpoint);
        Assert.False(options.ConsoleExporter);
        Assert.Equal("WebApiPlayground.Api", options.ServiceName);
    }

    [Fact]
    public void Binds_FromConfigurationSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:OtlpEndpoint"] = "http://localhost:4317",
                ["OpenTelemetry:ServiceName"] = "custom-service",
                ["OpenTelemetry:ConsoleExporter"] = "true",
            })
            .Build();

        var options = config.GetSection(OpenTelemetryOptions.SectionName).Get<OpenTelemetryOptions>();

        Assert.NotNull(options);
        Assert.Equal("http://localhost:4317", options!.OtlpEndpoint);
        Assert.Equal("custom-service", options.ServiceName);
        Assert.True(options.ConsoleExporter);
    }
}
