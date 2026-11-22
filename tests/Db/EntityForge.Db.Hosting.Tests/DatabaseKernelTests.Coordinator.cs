using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class DatabaseKernelTests
{
    [TestMethod]
    public async Task Concurrent_native_home_proposals_reuse_one_whole_winner_and_terminal_race_keeps_it()
    {
        string path = CreatePath();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            checkpointStore: new FileDatabaseCheckpointStore(Path.Combine(_directory!, "concurrent-home.checkpoint")),
            durableDatabaseIncarnation: Guid.NewGuid(), versioningMode: DatabaseVersioningMode.PartitionLocal);
        await kernel.RecoverAsync();
        Guid operation = EntityOperationIds.New();
        var selections = new List<Task<DurablePartitionTransaction>>();
        await using (await kernel.PartitionTransactions.FreezeAsync())
        {
            for (int index = 0; index < 8; index++)
            {
                selections.Add(kernel.PartitionTransactions.SelectCoordinatorAsync(operation, (index + 1).ToString("X64"),
                    new(0), new byte[] { (byte)(index + 1) }, new("home:test", new('F', 64), new byte[] { (byte)index })).AsTask());
            }
        }

        var winners = await Task.WhenAll(selections);
        Assert.IsTrue(winners.All(entry => entry.Descriptor == winners[0].Descriptor && entry.Coordinator!.Matches(winners[0].Coordinator!)));
        async Task Compete(bool commit)
        {
            try
            {
                await kernel.PartitionTransactions.CompleteAsync(operation, winners[0].Descriptor, commit);
            }
            catch (InvalidDataException)
            {
                /* The opposite terminal winner is immutable. */
            }
        }
        await Task.WhenAll(Compete(true), Compete(false));
        var terminal = (await kernel.PartitionTransactions.ReadCoordinatorAsync(operation))!;
        Assert.AreNotEqual(DurablePartitionTransactionState.Prepared, terminal.State);
        Assert.AreEqual(winners[0].Descriptor, terminal.Descriptor);
        Assert.AreEqual(DatabaseKernelState.Ready, kernel.State);
        Assert.AreEqual(1, (await kernel.PartitionTransactions.ReadAsync()).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Native_home_ambiguous_force_never_reports_absence_and_reopens_exact_evidence(bool deciding)
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "uncertain-home.checkpoint");
        Guid incarnation = Guid.NewGuid(), id = EntityOperationIds.New();
        var home = new DurablePartitionCoordinator("home:domain:a:v1", new('B', 64), new byte[] { 1 });
        var fault = new HomeForceObserver();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path,
            new(maximumGroupDelay: TimeSpan.Zero), fault), Changes(path), new CheckpointRuntime(),
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal))
        {
            await kernel.RecoverAsync();
            if (deciding)
            {
                await kernel.PartitionTransactions.SelectCoordinatorAsync(id, new('A', 64), new(0), new byte[] { 7 }, home);
            }

            fault.Armed = true;
            try
            {
                if (deciding)
                {
                    await kernel.PartitionTransactions.CompleteAsync(id, new('A', 64), true);
                }
                else
                {
                    await kernel.PartitionTransactions.SelectCoordinatorAsync(id, new('A', 64), new(0), new byte[] { 7 }, home);
                }

                Assert.Fail("The post-force failure was not observed.");
            }
            catch (DatabaseCommitLogRecoveryRequiredException)
            {

            }
            catch (EntityForgeDbCommitException failure) when (failure.InnerException is DatabaseCommitLogRecoveryRequiredException)
            {

            }
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryRequiredException>(() => kernel.PartitionTransactions.ReadCoordinatorAsync(id).AsTask());
        }
        await using var recovered = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await recovered.RecoverAsync();
        var entry = (await recovered.PartitionTransactions.ReadCoordinatorAsync(id))!;
        Assert.IsTrue(home.Matches(entry.Coordinator!));
        Assert.AreEqual(deciding ? DurablePartitionTransactionState.Committed : DurablePartitionTransactionState.Prepared, entry.State);
        if (!deciding)
        {
            await recovered.PartitionTransactions.CompleteAsync(id, entry.Descriptor, false);
        }
        else
        {
            Assert.AreEqual(id, (await recovered.PartitionTransactions.ResolveAsync(id, entry.Descriptor))!.TransactionId);
        }
    }

    private sealed class HomeForceObserver : IRedoDurabilityObserver
    {
        internal bool Armed;

        public void Reached(RedoDurabilityStage stage)
        {
            if (Armed && stage == RedoDurabilityStage.AfterWalFlush)
            {
                Armed = false;
                throw new IOException("Injected lost home durability acknowledgement.");
            }
        }
    }

    [TestMethod]
    public async Task Native_home_preserves_commit_across_publication_failure_and_refuses_live_absence()
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "home.checkpoint");
        Guid incarnation = Guid.NewGuid(), id = EntityOperationIds.New();
        var home = new DurablePartitionCoordinator("home:domain:a:v1", new('B', 64), new byte[] { 1 });
        var runtime = new CheckpointRuntime();
        await using (var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), runtime,
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal))
        {
            await kernel.RecoverAsync();
            await kernel.PartitionTransactions.SelectCoordinatorAsync(id, new('A', 64), new(0), new byte[] { 7 }, home);
            runtime.FailNextApply = true;
            var committed = await Assert.ThrowsExactlyAsync<EntityOperationCommittedException<DatabaseCommitReceipt>>(
                () => kernel.PartitionTransactions.CompleteAsync(id, new('A', 64), true).AsTask());
            Assert.AreEqual(id, committed.Receipt.TransactionId);
            Assert.AreEqual(DatabaseKernelState.RecoveryRequired, kernel.State);
            await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryRequiredException>(() => kernel.PartitionTransactions.ReadCoordinatorAsync(id).AsTask());
            await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryRequiredException>(() => kernel.PartitionTransactions.ReadCoordinatorAsync(EntityOperationIds.New()).AsTask());
        }
        await using var recovered = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
            checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await recovered.RecoverAsync();
        var decision = (await recovered.PartitionTransactions.ReadCoordinatorAsync(id))!;
        Assert.AreEqual(DurablePartitionTransactionState.Committed, decision.State);
        Assert.IsTrue(home.Matches(decision.Coordinator!));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Native_home_selection_and_decision_survive_wal_and_checkpoint(bool checkpointed)
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "home.checkpoint");
        Guid incarnation = Guid.NewGuid(), id = EntityOperationIds.New();
        var home = new DurablePartitionCoordinator("home:domain:a:v1", new('B', 64), new byte[] { 10, 20, 30 });
        async Task<DatabaseKernel> Open()
        {
            var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
                checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
                versioningMode: DatabaseVersioningMode.PartitionLocal);
            await kernel.RecoverAsync();
            return kernel;
        }
        await using (var kernel = await Open())
        {
            var selected = await kernel.PartitionTransactions.SelectCoordinatorAsync(id, new('A', 64), new(0), new byte[] { 7 }, home);
            Assert.AreEqual(DurablePartitionTransactionState.Prepared, selected.State);
            // Same application arbitration reuses the ENTIRE winner, not a retry's replanned member.
            var winner = await kernel.PartitionTransactions.SelectCoordinatorAsync(id, new('C', 64), new(0), new byte[] { 8 }, home);
            Assert.AreEqual(selected.Descriptor, winner.Descriptor);
            CollectionAssert.AreEqual(selected.Payload.ToArray(), winner.Payload.ToArray());
            await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(() => kernel.PartitionTransactions.SelectCoordinatorAsync(
                id, new('A', 64), new(0), new byte[] { 7 }, new("home:domain:a:v1", new('D', 64), new byte[] { 1 })).AsTask());
            if (checkpointed)
            {
                await kernel.PartitionTransactions.CheckpointForTransferAsync();
            }
        }
        await using (var kernel = await Open())
        {
            var selected = await kernel.PartitionTransactions.ReadCoordinatorAsync(id);
            Assert.IsTrue(home.Matches(selected!.Coordinator!));
            await kernel.PartitionTransactions.CompleteAsync(id, selected.Descriptor, true);
            Assert.AreEqual(DurablePartitionTransactionState.Committed, (await kernel.PartitionTransactions.ReadCoordinatorAsync(id))!.State);
            if (checkpointed)
            {
                await kernel.PartitionTransactions.CheckpointForTransferAsync();
            }
        }
        await using (var kernel = await Open())
        {
            var selected = (await kernel.PartitionTransactions.ReadCoordinatorAsync(id))!;
            Assert.AreEqual(DurablePartitionTransactionState.Committed, selected.State);
            Assert.IsTrue(home.Matches(selected.Coordinator!));
            Assert.AreEqual(1L, selected.LocalVersion!.Value.Value);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => kernel.PartitionTransactions.CompleteAsync(id, selected.Descriptor, false).AsTask());
        }
    }

    [TestMethod]
    public async Task Native_home_rejected_selection_is_durably_aborted_without_a_reservation()
    {
        string path = CreatePath(), checkpoint = Path.Combine(_directory!, "home.checkpoint");
        Guid incarnation = Guid.NewGuid(), id = EntityOperationIds.New();
        var home = new DurablePartitionCoordinator("home:domain:a:v1", new('B', 64), new byte[] { 1 });
        async Task<DatabaseKernel> Open()
        {
            var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(path), Changes(path), new CheckpointRuntime(),
                checkpointStore: new FileDatabaseCheckpointStore(checkpoint), durableDatabaseIncarnation: incarnation,
                versioningMode: DatabaseVersioningMode.PartitionLocal);
            await kernel.RecoverAsync();
            return kernel;
        }
        await using (var kernel = await Open())
        {
            await kernel.CommitAsync(EntityOperationIds.New(), new byte[] { 1 });
            var rejected = await kernel.PartitionTransactions.SelectCoordinatorAsync(id, new('A', 64), new(0), new byte[] { 7 }, home);
            Assert.AreEqual(DurablePartitionTransactionState.Aborted, rejected.State);
            await kernel.CommitAsync(EntityOperationIds.New(), new byte[] { 2 });
        }
        await using (var kernel = await Open())
        {
            Assert.AreEqual(DurablePartitionTransactionState.Aborted, (await kernel.PartitionTransactions.ReadCoordinatorAsync(id))!.State);
            Assert.AreEqual(DurablePartitionTransactionState.Aborted, (await kernel.PartitionTransactions.SelectCoordinatorAsync(
                id, new('A', 64), new(2), new byte[] { 7 }, home)).State);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => kernel.PartitionTransactions.CompleteAsync(id, new('A', 64), true).AsTask());
        }
    }
}
