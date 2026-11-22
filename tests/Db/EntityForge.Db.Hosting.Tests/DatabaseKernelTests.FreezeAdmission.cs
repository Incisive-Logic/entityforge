using EntityForge.Db.Kernel;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class DatabaseKernelTests
{
    [TestMethod]
    public async Task Frozen_reads_acquire_profile_admission_before_excluding_native_writes()
    {
        string path = CreatePath();
        var runtime = new FreezeAdmissionRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), runtime,
            checkpointStore: new FileDatabaseCheckpointStore(Path.Combine(_directory!, "freeze.checkpoint")),
            durableDatabaseIncarnation: Guid.NewGuid(), versioningMode: DatabaseVersioningMode.PartitionLocal);
        await kernel.RecoverAsync();
        var pending = kernel.PartitionTransactions.FreezeAsync().AsTask();
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            // Models the maintenance owner that already owns profile admission
            // and needs native write admission to finish and release it.
            await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 1 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            runtime.Continue.TrySetResult();
        }
        await using var freeze = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, runtime.Released);
        using var cancel = new CancellationTokenSource();
        var blocked = kernel.CommitAsync(Guid.NewGuid(), new byte[] { 2 }, cancel.Token).AsTask();
        Assert.IsFalse(blocked.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => blocked);
        await freeze.DisposeAsync();
        await freeze.DisposeAsync();
        Assert.AreEqual(1, runtime.Released);
        await kernel.CommitAsync(Guid.NewGuid(), new byte[] { 3 });
    }

    private sealed class FreezeAdmissionRuntime : CheckpointRuntime, IDatabaseProfileSnapshotPin
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Released;

        public async ValueTask<IDisposable?> EnterSnapshotPinAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Continue.Task.WaitAsync(token);
            return new Admission(this);
        }

        public IDisposable PinSnapshot(DatabaseVersion version) => throw new NotSupportedException();

        private sealed class Admission(FreezeAdmissionRuntime runtime) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref runtime.Released);
        }
    }
}
