using System.Reflection;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    // Hold existing admission to deterministically populate the queue, without
    // adding a collection delay or a production-only test hook.
    private static SemaphoreSlim PartitionAdmission(DatabaseKernel kernel) =>
        (SemaphoreSlim)typeof(DatabaseKernel).GetField("_localWriteAuthority", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;

    private static async Task<(IAsyncDisposable Database, DatabaseKernel Kernel)> OpenCohortStore(
        DatabaseProfileId profile, string path, IRedoDurabilityObserver observer)
    {
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var db = await GraphDatabase.OpenLocalAsync(new(path)
            {
                VersioningMode = DatabaseVersioningMode.PartitionLocal
            }, observer, null);
            return (db, ((GraphDatabaseEngine)db).PartitionKernel);
        }
        var rdf = await RdfDatabase.OpenLocalAsync(new(path)
        {
            VersioningMode = DatabaseVersioningMode.PartitionLocal
        }, observer, null);
        return (rdf, ((RdfDatabaseEngine)rdf).PartitionKernel);
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Partition_cohort_shares_prepare_and_commit_forces_and_preserves_replay_and_retention(DatabaseProfileId profile)
    {
        string path = Path.Combine(_root, "cohort");
        var observer = new PartitionWalObserver();
        Guid[] ids = Enumerable.Range(0, 4).Select(_ => EntityOperationIds.New(DateTimeOffset.UtcNow.AddMinutes(-5))).ToArray();
        Guid[] subjects = ids.Select(_ => Guid.NewGuid()).ToArray();
        var opened = await OpenCohortStore(profile, path, observer);
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            Guid seed = Guid.NewGuid();
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects, null, true));
            var gate = PartitionAdmission(kernel);
            int forces = observer.Forces;
            await gate.WaitAsync();
            Task[] preparations;
            try
            {
                preparations = ids.Select((id, index) => kernel.PartitionTransactions.PrepareAsync(id, ReservationDescriptor,
                new(1), ReservationPayload(profile, id, [subjects[index]], new(1))).AsTask()).ToArray();
            }
            finally
            {
                gate.Release();
            }
            await Task.WhenAll(preparations).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(forces + 1, observer.Forces, "One force must cover all four durable votes.");
            await gate.WaitAsync();
            Task<DatabaseCommitReceipt?>[] completions;
            try
            {
                completions = ids.Select(id => kernel.PartitionTransactions.CompleteAsync(id, ReservationDescriptor, true).AsTask()).ToArray();
            }
            finally
            {
                gate.Release();
            }
            var receipts = await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.AreEqual(forces + 2, observer.Forces, "One native force must cover all four commits.");
            CollectionAssert.AreEqual(new long[] { 2, 3, 4, 5 }, receipts.Select(receipt => receipt!.Version.Value).ToArray());
            CollectionAssert.AreEqual(ids, receipts.Select(receipt => receipt!.TransactionId).ToArray());
            Assert.IsTrue((await kernel.PartitionTransactions.ReadAsync()).All(entry => entry.State == DurablePartitionTransactionState.Committed));
        }
        opened = await OpenCohortStore(profile, path, observer);
        await using (opened.Database)
        {
            foreach (var id in ids)
            {
                var receipt = await opened.Kernel.PartitionTransactions.CompleteAsync(id, ReservationDescriptor, true);
                Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, receipt!.Outcome);
                Assert.AreEqual(id, receipt.TransactionId);
            }
            await opened.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.AreEqual(0, (await opened.Kernel.PartitionTransactions.ReadAsync()).Count);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Partition_cohort_uncertain_force_retains_every_member_for_recovery(DatabaseProfileId profile, bool commit)
    {
        string path = Path.Combine(_root, "uncertain-cohort");
        var observer = new PartitionWalObserver();
        Guid[] ids = [EntityOperationIds.New(), EntityOperationIds.New()];
        Guid[] subjects = [Guid.NewGuid(), Guid.NewGuid()];
        var opened = await OpenCohortStore(profile, path, observer);
        await using (opened.Database)
        {
            var kernel = opened.Kernel;
            Guid seed = Guid.NewGuid();
            await kernel.CommitAsync(seed, ReservationPayload(profile, seed, subjects, null, true));
            if (commit)
            {
                for (int index = 0; index < ids.Length; index++)
                {
                    await kernel.PartitionTransactions.PrepareAsync(ids[index], ReservationDescriptor, new(1),
                        ReservationPayload(profile, ids[index], [subjects[index]], new(1)));
                }
            }

            var gate = PartitionAdmission(kernel);
            await gate.WaitAsync();
            Task[] work;
            try
            {
                observer.Fail = true;
                work = ids.Select((id, index) => commit
                    ? (Task)kernel.PartitionTransactions.CompleteAsync(id, ReservationDescriptor, true).AsTask()
                    : kernel.PartitionTransactions.PrepareAsync(id, ReservationDescriptor, new(1),
                        ReservationPayload(profile, id, [subjects[index]], new(1))).AsTask()).ToArray();
            }
            finally
            {
                gate.Release();
            }
            try
            {
                await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Fail("The forced group reply was lost.");
            }
            catch (Exception) when (work.All(task => task.IsFaulted))
            {

            }
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            await Assert.ThrowsAsync<EntityForgeDbRecoveryRequiredException>(() =>
                kernel.PartitionTransactions.ReadSelectedDescriptorsAsync(ids).AsTask());
        }
        opened = await OpenCohortStore(profile, path, observer);
        await using (opened.Database)
        {
            var entries = await opened.Kernel.PartitionTransactions.ReadAsync();
            Assert.AreEqual(ids.Length, entries.Count);
            Assert.AreEqual(ids.Length, (await opened.Kernel.PartitionTransactions.ReadSelectedDescriptorsAsync(ids)).Count);
            Assert.IsTrue(entries.All(entry => entry.State == (commit ? DurablePartitionTransactionState.Committed : DurablePartitionTransactionState.Prepared)));
            foreach (var id in ids)
            {
                await opened.Kernel.PartitionTransactions.CompleteAsync(id, ReservationDescriptor, commit);
            }

            Assert.IsTrue((await opened.Kernel.PartitionTransactions.ReadAsync()).All(entry => entry.State != DurablePartitionTransactionState.Prepared));
        }
    }

    [TestMethod]
    public async Task Partition_cohort_rejects_overlapping_member_and_cancelled_queue_entry_without_losing_peers()
    {
        var observer = new PartitionWalObserver();
        var opened = await OpenCohortStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "conflicting-cohort"), observer);
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        Guid subject = Guid.NewGuid(), seed = Guid.NewGuid();
        await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [subject], null, true));
        Guid a = EntityOperationIds.New(), b = EntityOperationIds.New(), c = EntityOperationIds.New();
        using var cancelled = new CancellationTokenSource();
        var gate = PartitionAdmission(kernel);
        await gate.WaitAsync();
        Task first, conflict, withdrawn;
        try
        {
            first = kernel.PartitionTransactions.PrepareAsync(a, ReservationDescriptor, new(1), ReservationPayload(DatabaseProfileId.PropertyGraph, a, [subject], new(1))).AsTask();
            conflict = kernel.PartitionTransactions.PrepareAsync(b, ReservationDescriptor, new(1), ReservationPayload(DatabaseProfileId.PropertyGraph, b, [subject], new(1))).AsTask();
            withdrawn = kernel.PartitionTransactions.PrepareAsync(c, ReservationDescriptor, new(1), ReservationPayload(DatabaseProfileId.PropertyGraph, c, [subject], new(1)), cancelled.Token).AsTask();
            cancelled.Cancel();
        }
        finally
        {
            gate.Release();
        }
        await first;
        await Assert.ThrowsExactlyAsync<DatabasePartitionConflictException>(() => conflict);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => withdrawn);
        Assert.AreEqual(a, (await kernel.PartitionTransactions.ReadAsync()).Single().Operation);
        await kernel.PartitionTransactions.CompleteAsync(a, ReservationDescriptor, false);
        Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
    }

    private sealed class PausedCohortForce : IRedoDurabilityObserver, IDisposable
    {
        internal bool Pause;

        internal TaskCompletionSource ReachedForce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ManualResetEventSlim Release { get; } = new();

        public void Reached(RedoDurabilityStage stage)
        {
            if (!Pause || stage != RedoDurabilityStage.AfterWalFlush)
            {
                return;
            }

            Pause = false;
            ReachedForce.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Test did not release the forced group.");
            }
        }

        public void Dispose() => Release.Dispose();
    }

    [TestMethod]
    public async Task Partition_queue_capacity_rejection_and_cancellation_leave_no_durable_obligation()
    {
        var opened = await OpenReservationStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "queue-capacity"));
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        Guid seed = Guid.NewGuid(), subject = Guid.NewGuid();
        await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [subject], null, true));
        using var cancellation = new CancellationTokenSource();
        var gate = PartitionAdmission(kernel);
        await gate.WaitAsync();
        List<Task> queued = [];
        try
        {
            const int QueueCapacity = 256;
            for (int index = 0; index < QueueCapacity; index++)
            {
                var operation = EntityOperationIds.New();
                queued.Add(kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, new(1),
                    ReservationPayload(DatabaseProfileId.PropertyGraph, operation, [subject], new(1)), cancellation.Token).AsTask());
            }
            var overflow = EntityOperationIds.New();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => kernel.PartitionTransactions.PrepareAsync(overflow,
                ReservationDescriptor, new(1), ReservationPayload(DatabaseProfileId.PropertyGraph, overflow, [subject], new(1))).AsTask());
            cancellation.Cancel();
        }
        finally
        {
            gate.Release();
        }
        try
        {
            await Task.WhenAll(queued);
            Assert.Fail("Queued work should be cancelled.");
        }
        catch (OperationCanceledException)
        {

        }
        Assert.IsTrue(queued.All(task => task.IsCanceled));
        Assert.AreEqual(0, (await kernel.PartitionTransactions.ReadAsync()).Count);
        Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
    }

    [TestMethod]
    public async Task Scoped_topology_completions_share_one_commit_force()
    {
        var observer = new PartitionWalObserver();
        var opened = await OpenCohortStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "group-fallback"), observer);
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        Guid[] ids = [EntityOperationIds.New(), EntityOperationIds.New()];
        var gate = PartitionAdmission(kernel);
        await gate.WaitAsync();
        Task[] preparations;
        try
        {
            preparations = ids.Select(id =>
            {
                var vertex = GraphVertexId.New();
                return kernel.PartitionTransactions.PrepareAsync(id, ReservationDescriptor, new(0),
                    GraphTransactionCodec.Encode(new(id, [new CreateVertex(vertex, [])], new(0), [new VertexRead(vertex)]))).AsTask();
            }).ToArray();
        }
        finally
        {
            gate.Release();
        }
        await Task.WhenAll(preparations);
        await gate.WaitAsync();
        Task<DatabaseCommitReceipt?>[] completions;
        try
        {
            completions = ids.Select(id => kernel.PartitionTransactions.CompleteAsync(id, ReservationDescriptor, true).AsTask()).ToArray();
        }
        finally
        {
            gate.Release();
        }
        var receipts = await Task.WhenAll(completions);
        Assert.AreEqual(2, observer.Forces, "One preparation group and one ordinary topology commit group.");
        Assert.AreEqual(2L, kernel.CurrentVersion.Value);
        Assert.IsTrue(receipts.All(receipt => receipt!.Outcome == DatabaseCommitOutcome.AppliedWithChanges));
        Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
    }

    [TestMethod]
    public async Task Selected_partition_cohort_owns_cancellation_and_kernel_shutdown_drains_it()
    {
        using var observer = new PausedCohortForce();
        var opened = await OpenCohortStore(DatabaseProfileId.PropertyGraph, Path.Combine(_root, "drained-cohort"), observer);
        await using var db = opened.Database;
        var kernel = opened.Kernel;
        Guid seed = Guid.NewGuid(), subject = Guid.NewGuid(), id = EntityOperationIds.New();
        await kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, [subject], null, true));
        using var cancellation = new CancellationTokenSource();
        observer.Pause = true;
        var work = kernel.PartitionTransactions.PrepareAsync(id, ReservationDescriptor, new(1),
            ReservationPayload(DatabaseProfileId.PropertyGraph, id, [subject], new(1)), cancellation.Token).AsTask();
        Task? closing = null;
        try
        {
            await observer.ReachedForce.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.IsFalse(work.IsCompleted, "Cancellation cannot abandon the selected durable vote.");
            closing = kernel.DisposeAsync().AsTask();
            Assert.IsFalse(closing.IsCompleted, "Shutdown must join selected durability and publication.");
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => kernel.PartitionTransactions.PrepareAsync(
                EntityOperationIds.New(), ReservationDescriptor, new(1),
                ReservationPayload(DatabaseProfileId.PropertyGraph, id, [subject], new(1))).AsTask());
        }
        finally
        {
            observer.Release.Set();
        }
        await work.WaitAsync(TimeSpan.FromSeconds(10));
        await closing!.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
