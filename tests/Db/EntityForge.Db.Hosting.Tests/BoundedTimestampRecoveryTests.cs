using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class BoundedTimestampRecoveryTests
{
    private sealed class Clock : IDatabaseBoundedClock
    {
        public Guid Domain { get; init; } = Guid.NewGuid();

        internal bool Healthy = true;

        public DatabaseClockInterval Read()
        {
            if (!Healthy)
            {
                throw new InvalidOperationException("Clock monitor lost synchronisation.");
            }

            long now = DateTime.UtcNow.Ticks;
            return new(now - TimeSpan.TicksPerMillisecond, now + TimeSpan.TicksPerMillisecond);
        }
    }

    private sealed class LoseClockAfterForce(Clock clock) : IRedoDurabilityObserver
    {
        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == RedoDurabilityStage.AfterWalFlush)
            {
                clock.Healthy = false;
            }
        }
    }

    [TestMethod]
    public async Task Clock_loss_after_durability_preserves_commit_and_reopen_under_same_domain()
    {
        string path = Path.Combine(Path.GetTempPath(), "ef-clock-recovery-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        var prior = DatabaseTimestampCostOptions.QualificationOverride.Value;
        DatabaseTimestampCostOptions.QualificationOverride.Value = DatabaseTimestampCostOptions.FromClock(clock);
        var vertex = GraphVertexId.New();
        Guid operation = Guid.NewGuid();
        try
        {
            await using (var graph = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            }, new LoseClockAfterForce(clock), null))
            {
                var kernel = ((GraphDatabaseEngine)graph).PartitionKernel;
                var failure = await Assert.ThrowsExactlyAsync<EntityOperationCommittedException<DatabaseCommitReceipt>>(async () =>
                    await kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation, [new CreateVertex(vertex, [])]))));
                Assert.AreEqual(operation, failure.Receipt.TransactionId);
                Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            }
            // A new process/monitor can supply the same UTC interpretation. No boot
            // identity is substituted and the original durable operation is retained.
            clock = new Clock { Domain = clock.Domain };
            DatabaseTimestampCostOptions.QualificationOverride.Value = DatabaseTimestampCostOptions.FromClock(clock);
            await using var recovered = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            });
            Assert.IsNotNull(await recovered.GetVertexAsync(vertex, recovered.CurrentVersion));
        }
        finally
        {
            DatabaseTimestampCostOptions.QualificationOverride.Value = prior;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
