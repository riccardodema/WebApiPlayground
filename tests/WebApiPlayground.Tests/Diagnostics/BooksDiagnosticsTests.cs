using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using WebApiPlayground.Application.Diagnostics;
using Xunit;

namespace WebApiPlayground.Tests.Diagnostics;

/// <summary>
/// I nomi di <see cref="System.Diagnostics.ActivitySource"/>/<see cref="System.Diagnostics.Metrics.Meter"/>
/// e della metrica sono <b>contratto</b> verso le dashboard/gli exporter: un rename è breaking, quindi è
/// pinnato. La metrica <c>books.created</c> è verificata con <c>MetricCollector&lt;long&gt;</c> (best
/// practice .NET per testare gli strumenti di <c>System.Diagnostics.Metrics</c>).
/// </summary>
public class BooksDiagnosticsTests
{
    [Fact]
    public void TelemetryNames_AreStable()
    {
        Assert.Equal("WebApiPlayground.Books", BooksDiagnostics.ActivitySourceName);
        Assert.Equal("WebApiPlayground.Books", BooksDiagnostics.MeterName);
        Assert.Equal("books.created", BooksDiagnostics.BooksCreatedCounterName);
        Assert.Equal("Books.Create", BooksDiagnostics.CreateBookActivityName);

        // La source espone il nome dichiarato: gli exporter ci si agganciano via AddSource(name).
        Assert.Equal(BooksDiagnostics.ActivitySourceName, BooksDiagnostics.ActivitySource.Name);
    }

    [Fact]
    public void RecordBookCreated_EmitsMeasurementsOfOne()
    {
        // Forza l'inizializzazione statica (Meter + Counter) prima di agganciare il collector.
        _ = BooksDiagnostics.ActivitySource;

        using var collector = new MetricCollector<long>(
            meterScope: null, BooksDiagnostics.MeterName, BooksDiagnostics.BooksCreatedCounterName);

        BooksDiagnostics.RecordBookCreated();
        BooksDiagnostics.RecordBookCreated();

        // Il contatore è un singleton di processo (production code): altri test possono incrementarlo in
        // parallelo. Si asserisce ciò che è deterministico — ogni misura vale 1 e le nostre due ci sono.
        var measurements = collector.GetMeasurementSnapshot();
        Assert.True(measurements.Count >= 2);
        Assert.All(measurements, m => Assert.Equal(1, m.Value));
    }
}
