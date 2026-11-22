using System.Diagnostics.Metrics;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BackupTelemetryLifetimeTests
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ef-backup-telemetry-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, "success")]
    [DataRow(DatabaseProfileId.Rdf, "success")]
    [DataRow(DatabaseProfileId.PropertyGraph, "missing")]
    [DataRow(DatabaseProfileId.Rdf, "missing")]
    [DataRow(DatabaseProfileId.PropertyGraph, "corrupt")]
    [DataRow(DatabaseProfileId.Rdf, "corrupt")]
    [DataRow(DatabaseProfileId.PropertyGraph, "cancelled")]
    [DataRow(DatabaseProfileId.Rdf, "cancelled")]
    public async Task Backup_checkpoint_inspection_releases_owned_metric_instruments(DatabaseProfileId profile, string outcome)
    {
        Directory.CreateDirectory(_directory);
        string checkpoint = Path.Combine(_directory, "checkpoint");
        Guid incarnation = Guid.NewGuid();
        if (outcome is "success" or "cancelled")
        {
            await using var writer = new FileDatabaseCheckpointStore(checkpoint);
            await writer.SaveAsync(new(incarnation, new(7), new(profile, "test", 1, new byte[] { 1 })));
        }
        else if (outcome == "corrupt")
        {
            await File.WriteAllBytesAsync(checkpoint, new byte[] { 1, 2, 3 });
        }

        var active = new HashSet<Instrument>();
        var ownedMeters = new HashSet<Meter>();
        object sync = new();
        bool capture = false;
        int published = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            lock (sync)
            {
                // Ignore instruments published before this operation's listener started.
                if (!capture || instrument.Meter.Name != DatabaseStorageTelemetry.MeterName)
                {
                    return;
                }
                active.Add(instrument);
                ownedMeters.Add(instrument.Meter);
                published++;
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.MeasurementsCompleted = (instrument, _) =>
        {
            lock (sync)
            {
                active.Remove(instrument);
            }
        };
        listener.Start();
        capture = true;
        try
        {
            var manager = new LocalDatabaseBackupManager();
            for (int repeat = 0; repeat < 3; repeat++)
            {
                if (outcome == "success")
                {
                    var info = await manager.InspectCheckpointAsync(checkpoint);
                    Assert.AreEqual(incarnation, info.DatabaseIncarnation);
                    Assert.AreEqual(profile, info.Profile);
                    Assert.AreEqual(new DatabaseVersion(7), info.CheckpointVersion);
                    Assert.AreEqual(String.Empty, info.Sha256);
                    Assert.AreEqual(0L, info.CheckpointBytes + info.PageBytes + info.ChangeJournalBytes);
                }
                else if (outcome == "missing")
                {
                    await Assert.ThrowsExactlyAsync<InvalidDataException>(() => manager.InspectCheckpointAsync(checkpoint).AsTask());
                }
                else if (outcome == "corrupt")
                {
                    await Assert.ThrowsExactlyAsync<EntityForgeDbCheckpointException>(() => manager.InspectCheckpointAsync(checkpoint).AsTask());
                }
                else
                {
                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => manager.InspectCheckpointAsync(checkpoint, cancelled.Token).AsTask());
                }
                lock (sync)
                {
                    Assert.IsTrue(published > 0, "The listener must observe the operation's metric instruments.");
                    Assert.AreEqual(0, active.Count, "Completed or failed checkpoint inspection must not retain owned metric instruments.");
                }
            }
        }
        finally
        {
            // Keep a failing regression from leaking its own observed meters into later tests.
            foreach (var meter in ownedMeters)
            {
                meter.Dispose();
            }
        }
    }
}
