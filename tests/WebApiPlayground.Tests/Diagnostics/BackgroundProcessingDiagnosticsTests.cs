using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using WebApiPlayground.Application.Diagnostics;
using Xunit;

namespace WebApiPlayground.Tests.Diagnostics;

/// <summary>
/// I NOMI delle metriche del background processing sono contratto osservabile (dashboard/alert li
/// referenziano per stringa): ogni Record* deve incrementare ESATTAMENTE il counter col suo nome.
/// Il collector ascolta per nome → se il nome muta, nessuna misura arriva e il test fallisce.
/// </summary>
public class BackgroundProcessingDiagnosticsTests
{
    private static MetricCollector<long> Collect(string instrumentName) =>
        new(null, BackgroundProcessingDiagnostics.MeterName, instrumentName);

    [Fact]
    public void Each_record_method_increments_its_own_counter_by_one()
    {
        using var enqueued = Collect(BackgroundProcessingDiagnostics.EnqueuedCounterName);
        using var dropped = Collect(BackgroundProcessingDiagnostics.DroppedCounterName);
        using var processed = Collect(BackgroundProcessingDiagnostics.ProcessedCounterName);
        using var failed = Collect(BackgroundProcessingDiagnostics.FailedCounterName);

        BackgroundProcessingDiagnostics.RecordEnqueued();
        BackgroundProcessingDiagnostics.RecordDropped();
        BackgroundProcessingDiagnostics.RecordProcessed();
        BackgroundProcessingDiagnostics.RecordFailed();

        Assert.Equal(1, enqueued.GetMeasurementSnapshot().EvaluateAsCounter());
        Assert.Equal(1, dropped.GetMeasurementSnapshot().EvaluateAsCounter());
        Assert.Equal(1, processed.GetMeasurementSnapshot().EvaluateAsCounter());
        Assert.Equal(1, failed.GetMeasurementSnapshot().EvaluateAsCounter());
    }

    [Fact]
    public void Counter_names_follow_the_otel_naming_convention()
    {
        // Contratto verso dashboard/alert: dotted lowercase, namespace 'background.tasks'.
        Assert.Equal("background.tasks.enqueued", BackgroundProcessingDiagnostics.EnqueuedCounterName);
        Assert.Equal("background.tasks.dropped", BackgroundProcessingDiagnostics.DroppedCounterName);
        Assert.Equal("background.tasks.processed", BackgroundProcessingDiagnostics.ProcessedCounterName);
        Assert.Equal("background.tasks.failed", BackgroundProcessingDiagnostics.FailedCounterName);
    }

    [Fact]
    public void Meter_and_activity_source_share_the_same_name_for_correlation()
    {
        Assert.Equal(BackgroundProcessingDiagnostics.ActivitySourceName, BackgroundProcessingDiagnostics.MeterName);
        Assert.Equal("WebApiPlayground.BackgroundProcessing", BackgroundProcessingDiagnostics.MeterName);
    }

    [Fact]
    public void Counters_declare_unit_and_a_description_for_the_dashboards()
    {
        var instruments = new Dictionary<string, Instrument>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BackgroundProcessingDiagnostics.MeterName)
                {
                    instruments[instrument.Name] = instrument;
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.Start();
        BackgroundProcessingDiagnostics.RecordEnqueued(); // forza la pubblicazione degli instrument

        foreach (var name in new[]
                 {
                     BackgroundProcessingDiagnostics.EnqueuedCounterName,
                     BackgroundProcessingDiagnostics.DroppedCounterName,
                     BackgroundProcessingDiagnostics.ProcessedCounterName,
                     BackgroundProcessingDiagnostics.FailedCounterName,
                 })
        {
            var instrument = instruments[name];
            Assert.Equal("{task}", instrument.Unit); // unità OTel: annotazione, non unità di misura
            Assert.False(string.IsNullOrWhiteSpace(instrument.Description));
        }
    }

    [Fact]
    public void Process_activity_is_linked_to_the_producer_context()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackgroundProcessingDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        var parent = new ActivityContext(
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

        using var activity = BackgroundProcessingDiagnostics.StartProcessActivity("Test.Process", parent);

        Assert.NotNull(activity);
        Assert.Equal(parent.TraceId, activity!.TraceId); // stesso trace del produttore: correlazione end-to-end
    }
}
