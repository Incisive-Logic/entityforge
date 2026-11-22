using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class RedoQueueDiagnosticsTests
{
    [TestMethod]
    public void Queue_duration_is_listener_gated_and_uses_bounded_phase_tags()
    {
        using var telemetry = new RedoDurabilityTelemetry();
        Assert.AreEqual(default, telemetry.QueueTimestamp());
        var measurements = new List<(double Ms, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Name == RedoDurabilityTelemetry.PhaseDurationInstrument)
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            var copy = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                copy.Add(tag.Key, tag.Value);
            }

            measurements.Add((value, copy));
        });
        listener.Start();
        var current = telemetry.QueueTimestamp();
        Assert.IsTrue(current > 0);
        telemetry.QueueWait(current - Stopwatch.Frequency);
        var sample = measurements.Single();
        Assert.IsTrue(double.IsFinite(sample.Ms) && sample.Ms >= 1000);
        Assert.AreEqual(1, sample.Tags.Count);
        Assert.AreEqual("submission_queue_wait", sample.Tags[RedoDurabilityTelemetry.PhaseTag]);
        telemetry.QueueWait(default);
        Assert.AreEqual(1, measurements.Count);
    }
}
