using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class TimestampRecoveryMatrixTests
{
    private sealed class Clock(Guid domain) : IDatabaseBoundedClock
    {
        internal bool Healthy = true;

        public Guid Domain => domain;

        public DatabaseClockInterval Read()
        {
            if (!Healthy)
            {
                throw new InvalidOperationException("Injected clock source loss.");
            }

            long now = DateTime.UtcNow.Ticks;
            return new(now - TimeSpan.TicksPerMillisecond, now + TimeSpan.TicksPerMillisecond);
        }
    }

    private sealed class CheckpointFault(DatabaseCheckpointPublicationStage selected) : IDatabaseCheckpointPublicationObserver
    {
        internal bool Armed;

        public void Reached(DatabaseCheckpointPublicationStage stage)
        {
            if (Armed && stage == selected)
            {
                Armed = false;
                throw new IOException("Injected checkpoint interruption.");
            }
        }
    }

    [TestMethod]
    [DataRow(DatabaseCheckpointPublicationStage.BeforeReplacement)]
    [DataRow(DatabaseCheckpointPublicationStage.AfterReplacement)]
    public async Task Interrupted_checkpoint_recovers_committed_data_and_read_promise(DatabaseCheckpointPublicationStage stage)
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-timestamp-checkpoint-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock(Guid.NewGuid());
        var fault = new CheckpointFault(stage);
        Guid operation = Guid.NewGuid();
        var id = GraphVertexId.New();
        long promise;
        try
        {
            await using (var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            }, null, fault))
            {
                var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
                await kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation, [new CreateVertex(id, [])])));
                using (var pin = await kernel.PartitionTransactions.CaptureSnapshotAsync())
                {
                    await kernel.PartitionTransactions.CaptureTimestampProjectionAsync(pin, DatabaseTimestampReadClock.Read(clock.Domain, clock), null);
                }

                promise = kernel.TimestampCostState!.ReservedReadThrough;
                fault.Armed = true;
                var failure = await Assert.ThrowsExceptionAsync<EntityForgeDbCheckpointException>(async () => await graph.CreateCheckpointAsync());
                Assert.IsInstanceOfType<IOException>(failure.InnerException);
            }
            await using var recovered = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = clock
            });
            Assert.IsNotNull(await recovered.GetVertexAsync(id, recovered.CurrentVersion));
            Assert.IsTrue(((GraphDatabaseEngine)recovered).PartitionKernel.TimestampCostState!.ReservedReadThrough >= promise);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [TestMethod]
    public async Task Partial_timestamp_application_and_clock_loss_complete_exactly_after_owner_reopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "ef-timestamp-partial-" + Guid.NewGuid().ToString("N"));
        var firstClock = new Clock(Guid.NewGuid());
        var secondClock = new Clock(firstClock.Domain);
        Guid operation = Guid.NewGuid();
        string descriptor = new('A', 64);
        var left = GraphVertexId.New();
        var right = GraphVertexId.New();
        try
        {
            await using var first = await GraphDatabase.OpenLocalAsync(new(Path.Combine(root, "a"))
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = firstClock
            });
            var a = ((GraphDatabaseEngine)first).PartitionKernel;
            DurableTimestampState decision;
            await using (var second = await GraphDatabase.OpenLocalAsync(new(Path.Combine(root, "b"))
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = secondClock
            }))
            {
                var b = ((GraphDatabaseEngine)second).PartitionKernel;
                await a.PartitionTransactions.PrepareAsync(operation, descriptor, a.CurrentVersion,
                    GraphTransactionCodec.Encode(new(operation, [new CreateVertex(left, [])], ReadVersion: a.CurrentVersion)));
                await b.PartitionTransactions.PrepareAsync(operation, descriptor, b.CurrentVersion,
                    GraphTransactionCodec.Encode(new(operation, [new CreateVertex(right, [])], ReadVersion: b.CurrentVersion)));
                decision = await a.PartitionTransactions.ChooseTimestampAsync([
                    (await a.PartitionTransactions.ReadTimestampVoteAsync(operation))!,
                    (await b.PartitionTransactions.ReadTimestampVoteAsync(operation))!]);
                await a.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, true, decision);
                secondClock.Healthy = false;
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                    await b.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, true, decision));
                secondClock.Healthy = true;
            }
            await using var reopened = await GraphDatabase.OpenLocalAsync(new(Path.Combine(root, "b"))
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal,
                TimestampClock = secondClock
            });
            var recovered = ((GraphDatabaseEngine)reopened).PartitionKernel;
            await recovered.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, true, decision);
            await recovered.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, true, decision);
            Assert.IsNotNull(await first.GetVertexAsync(left, first.CurrentVersion));
            Assert.IsNotNull(await reopened.GetVertexAsync(right, reopened.CurrentVersion));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
