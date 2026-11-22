using EntityForge.Db.Kernel;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class DatabaseKernelTests
{
    [TestMethod]
    public async Task Only_partition_admission_uses_unsubmitted_physical_cleanup()
    {
        string path = CreatePath();
        var runtime = new AdmissionCleanupRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), runtime, checkpointStore: new FileDatabaseCheckpointStore(path + ".checkpoint"), durableDatabaseIncarnation: Guid.NewGuid(),
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await kernel.RecoverAsync();
        Guid operation = Guid.NewGuid();
        string descriptor = new('A', 64);
        await kernel.PreparePartitionTransactionAsync(operation, descriptor, new(0), new byte[] { 1 });
        Assert.AreEqual(1, runtime.Unsubmitted);
        Assert.AreEqual(0, runtime.Ordinary);
        await kernel.CompletePartitionTransactionAsync(operation, descriptor, true);
        Assert.AreEqual(1, runtime.Published);
        Assert.AreEqual(1, runtime.Unsubmitted);
        runtime.FailPreparation = true;
        await Assert.ThrowsAsync<EntityForgeDbCommitException>(async () =>
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 }));
        Assert.AreEqual(1, runtime.Ordinary);
        Assert.AreEqual(1, runtime.Unsubmitted);
    }

    private sealed class AdmissionCleanupRuntime : CheckpointRuntime, IDatabaseProfilePhysicalMutationPlanner
    {
        public int Unsubmitted, Ordinary, Published;
        public bool FailPreparation;

        public ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
            DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => FailPreparation
                ? ValueTask.FromException<IReadOnlyList<DatabasePhysicalPageMutation>>(new InvalidOperationException("Injected planning failure."))
                : ValueTask.FromResult<IReadOnlyList<DatabasePhysicalPageMutation>>([]);

        public ValueTask PublishPhysicalMutationsAsync(Guid transactionId, CancellationToken cancellationToken = default)
        {
            Published++;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonPhysicalMutationsAsync(Guid transactionId, CancellationToken cancellationToken = default)
        {
            Ordinary++;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonUnsubmittedPhysicalMutationsAsync(Guid transactionId, CancellationToken cancellationToken = default)
        {
            Unsubmitted++;
            return ValueTask.CompletedTask;
        }
    }
}
