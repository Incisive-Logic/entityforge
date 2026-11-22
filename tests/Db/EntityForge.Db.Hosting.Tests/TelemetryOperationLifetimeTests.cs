using System.Diagnostics.Metrics;
using EntityForge.Db.Telemetry;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TelemetryOperationLifetimeTests
{
    [TestMethod]
    [DataRow("applied", false, 1, 0, 0)]
    [DataRow("idempotent_replay", false, 1, 0, 0)]
    [DataRow("conflict", false, 0, 1, 0)]
    [DataRow("error", true, 0, 0, 1)]
    public async Task Repeated_concurrent_disposal_records_one_operation(
        string outcome, bool error, int expectedCommits, int expectedConflicts, int expectedFailures)
    {
        int durations = 0;
        long commits = 0, conflicts = 0, failures = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == EntityForgeDbTelemetry.MeterName)
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) =>
        {
            if (instrument.Name == "entityforge.db.operation.duration")
            {
                Interlocked.Increment(ref durations);
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            switch (instrument.Name)
            {
                case "entityforge.db.commits":
                    Interlocked.Add(ref commits, value);
                    break;
                case "entityforge.db.write_conflicts":
                    Interlocked.Add(ref conflicts, value);
                    break;
                case "entityforge.db.failures":
                    Interlocked.Add(ref failures, value);
                    break;
            }
        });
        listener.Start();
        using var telemetry = new EntityForgeDbTelemetry();
        var operation = telemetry.StartOperation("db.commit");
        operation.SetOutcome(outcome);
        if (error)
        {
            operation.RecordException(new IOException("Injected operation failure"), outcome);
        }
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(operation.Dispose)));
        operation.Dispose();
        Assert.AreEqual(1, durations);
        Assert.AreEqual((long)expectedCommits, commits);
        Assert.AreEqual((long)expectedConflicts, conflicts);
        Assert.AreEqual((long)expectedFailures, failures);
    }
}
