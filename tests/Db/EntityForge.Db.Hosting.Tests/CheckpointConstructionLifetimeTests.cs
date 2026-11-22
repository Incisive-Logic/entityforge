using System.Diagnostics.Metrics;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CheckpointConstructionLifetimeTests
{
    [TestMethod]
    [DataRow("page-without-floor")]
    [DataRow("floor-without-page")]
    [DataRow("write-without-flush")]
    [DataRow("flush-without-write")]
    public void Rejected_checkpoint_configuration_does_not_retain_owned_metric_instruments(string scenario)
    {
        var instruments = new List<Instrument>();
        var active = new HashSet<Instrument>();
        bool capture = false;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (capture && instrument.Meter.Name == DatabaseStorageTelemetry.MeterName)
            {
                instruments.Add(instrument);
                active.Add(instrument);
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.MeasurementsCompleted = (instrument, _) => active.Remove(instrument);
        listener.Start();
        capture = true;
        try
        {
            string checkpoint = Path.Combine(Path.GetTempPath(), "ef-constructor-" + Guid.NewGuid().ToString("N"));
            string? page = scenario == "page-without-floor" ? checkpoint + ".pages" : null;
            Func<long>? floor = scenario == "floor-without-page" ? () => 0 : null;
            Func<CancellationToken, ValueTask>? write = scenario == "write-without-flush" ? _ => ValueTask.CompletedTask : null;
            Func<CancellationToken, ValueTask>? flush = scenario == "flush-without-write" ? _ => ValueTask.CompletedTask : null;
            Assert.ThrowsExactly<ArgumentException>(() => new FileDatabaseCheckpointStore(checkpoint,
                pagePath: page, walFloor: floor, writeCheckpointPages: write, flushCheckpointPages: flush));
            Assert.AreEqual(0, active.Count, "Rejected configuration must not leave owned telemetry registered.");
            Assert.IsFalse(File.Exists(checkpoint));
            Assert.IsFalse(File.Exists(checkpoint + ".pages"));
        }
        finally
        {
            // The old implementation leaks these meters; contain the red regression's resources.
            foreach (var meter in instruments.Select(instrument => instrument.Meter).Distinct())
            {
                meter.Dispose();
            }
        }
    }
}
