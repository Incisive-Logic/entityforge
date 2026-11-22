using EntityForge.Db.Kernel;
using EntityForge.Db.Durability;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class DatabaseKernelTests
{
    [TestMethod]
    [DataRow(true, true, false, true, false)]
    [DataRow(false, true, false, false, false)]
    [DataRow(true, false, false, false, false)]
    [DataRow(true, true, true, false, false)]
    [DataRow(true, true, false, false, true)]
    public async Task Local_checkpoint_reuses_retired_pages_only_with_exclusive_supported_admission(
        bool debt, bool localSupported, bool preserveSnapshots, bool expected, bool partition)
    {
        string path = CreatePath();
        var profile = new LocalReuseRuntime(debt, localSupported);
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), profile, checkpointStore: new FileDatabaseCheckpointStore(path + ".checkpoint"),
            durableDatabaseIncarnation: Guid.NewGuid(), versioningMode: partition
                ? DatabaseVersioningMode.PartitionLocal : DatabaseVersioningMode.LocalOrAuthoritative);
        await kernel.RecoverAsync();
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 7 });
        if (preserveSnapshots)
        {
            await kernel.CaptureCheckpointImageAsync(_ => ValueTask.FromResult(0), CancellationToken.None);
        }
        else
        {
            await kernel.CreateCheckpointAsync();
        }
        Assert.AreEqual(expected, profile.Authorized);
        Assert.AreEqual(expected ? 3 : 0, profile.MaintenancePasses);
        Assert.IsFalse(profile.Admitted);
        Assert.AreEqual(0, profile.RetentionCaptures, "Physical reuse must not expire operation history.");
        Assert.AreEqual(0, profile.Pruned.Count);
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 8 });
        CollectionAssert.AreEqual(new byte[] { 7, 8 }, profile.State.ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Local_checkpoint_cannot_authorize_page_reuse_before_both_publications_succeed(int failure)
    {
        string path = CreatePath();
        var profile = new LocalReuseRuntime(true, true);
        var checkpoints = new ReuseFailingStore(failure);
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path),
            Changes(path), profile, checkpointStore: checkpoints, durableDatabaseIncarnation: Guid.NewGuid());
        await kernel.RecoverAsync();
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 7 });
        await Assert.ThrowsAsync<IOException>(async () => await kernel.CreateCheckpointAsync());
        Assert.IsFalse(profile.Authorized);
        Assert.IsFalse(profile.Admitted);
        Assert.AreEqual(failure, checkpoints.Saves);
        Assert.AreEqual(failure == 1 ? 0 : 3, profile.MaintenancePasses);
    }

    private sealed class ReuseFailingStore(int failure) : IDatabaseCheckpointStore
    {
        internal int Saves;

        public ValueTask SaveAsync(DurableCheckpointEnvelope checkpoint, CancellationToken cancellationToken = default) =>
            ++Saves == failure ? ValueTask.FromException(new IOException("Injected checkpoint publication failure.")) : ValueTask.CompletedTask;

        public ValueTask<DurableCheckpointEnvelope?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DurableCheckpointEnvelope?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LocalReuseRuntime(bool debt, bool localSupported) : CheckpointRuntime,
        IDatabaseProfileCheckpointPageReuse
    {
        public bool SupportsLocalRetentionMaintenance => localSupported;

        public bool RequiresCheckpointMaintenance => debt;

        internal bool Admitted, Authorized;
        internal int MaintenancePasses;

        public IDisposable EnterMaintenance()
        {
            Assert.IsFalse(Admitted);
            Admitted = true;
            return new ReuseAdmission(this);
        }

        public ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(
            Guid id, DatabaseVersion version, bool compact, CancellationToken cancellationToken)
        {
            Assert.IsTrue(Admitted);
            Assert.IsFalse(compact, "Ordinary checkpoints must not force full index vacuum.");
            MaintenancePasses++;
            return ValueTask.FromResult<IReadOnlyList<DatabasePhysicalPageMutation>>([]);
        }

        public ValueTask PublishMaintenanceAsync(Guid id, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask AbandonMaintenanceAsync(Guid id) => ValueTask.CompletedTask;

        public ValueTask CompleteMaintenanceAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void AuthorizeCheckpointPageReuse(bool compacted)
        {
            Assert.IsTrue(Admitted);
            Assert.IsFalse(compacted);
            Assert.AreEqual(1, PublishedCheckpoints);
            Authorized = true;
        }

        private sealed class ReuseAdmission(LocalReuseRuntime owner) : IDisposable
        {
            public void Dispose() => owner.Admitted = false;
        }
    }
}
