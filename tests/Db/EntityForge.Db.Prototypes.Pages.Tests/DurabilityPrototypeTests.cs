using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class DurabilityPrototypeTests
{
    private const int PageSize = 8 * 1024;
    private string? _directory;

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [DataTestMethod]
    [DataRow(DurabilityStrategyKind.RedoWal)]
    [DataRow(DurabilityStrategyKind.NoOverwrite)]
    public async Task Durable_but_unpublished_transaction_is_recovered(DurabilityStrategyKind strategy)
    {
        _directory = NewDirectory();
        DurabilityTransaction transaction = Transaction(1, 7);
        await using (IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize))
        {
            await Assert.ThrowsExactlyAsync<InjectedDurabilityFaultException>(async () =>
                await store.ApplyAsync(transaction,
                    DurabilityFaultPoint.AfterDurableCommitBeforePublication));
            Assert.AreEqual(0, store.CurrentPages.Count);
        }
        await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize);
        CollectionAssert.AreEqual(transaction.Mutations[0].Image, recovered.CurrentPages[7]);
    }

    [DataTestMethod]
    [DataRow(DurabilityStrategyKind.RedoWal)]
    [DataRow(DurabilityStrategyKind.NoOverwrite)]
    public async Task Pre_durability_fault_is_not_recovered(DurabilityStrategyKind strategy)
    {
        _directory = NewDirectory();
        await using (IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize))
        {
            await Assert.ThrowsExactlyAsync<InjectedDurabilityFaultException>(async () =>
                await store.ApplyAsync(Transaction(1, 7), DurabilityFaultPoint.BeforeDurableCommit));
        }

        await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize);
        Assert.AreEqual(0, recovered.CurrentPages.Count);
    }

    [DataTestMethod]
    [DataRow(DurabilityStrategyKind.RedoWal)]
    [DataRow(DurabilityStrategyKind.NoOverwrite)]
    public async Task Published_but_unacknowledged_transaction_remains_durable(DurabilityStrategyKind strategy)
    {
        _directory = NewDirectory();
        DurabilityTransaction transaction = Transaction(2, 11);
        await using (IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize))
        {
            await Assert.ThrowsExactlyAsync<InjectedDurabilityFaultException>(async () =>
                await store.ApplyAsync(transaction,
                    DurabilityFaultPoint.AfterPublicationBeforeAcknowledgement));
            CollectionAssert.AreEqual(transaction.Mutations[0].Image, store.CurrentPages[11]);
        }
        await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize);
        CollectionAssert.AreEqual(transaction.Mutations[0].Image, recovered.CurrentPages[11]);
    }

    [TestMethod]
    public async Task Strategies_recover_the_same_logical_digest_for_each_page_size()
    {
        DurabilityExperimentResult result = await new DurabilityExperiment().RunAsync(
            new DurabilityExperimentOptions([PageSize, 16 * 1024], 20, 3, 32, 4, 1234));
        foreach (IGrouping<int, DurabilityExperimentCaseResult> pageSize in result.Results.GroupBy(item => item.PageSize))
        {
            Assert.AreEqual(1, pageSize.Select(item => item.LogicalDigest).Distinct().Count());
        }
    }

    [DataTestMethod]
    [DataRow(DurabilityStrategyKind.RedoWal)]
    [DataRow(DurabilityStrategyKind.NoOverwrite)]
    public async Task Grouped_transactions_share_flushes_and_recover_individual_mutations(
        DurabilityStrategyKind strategy)
    {
        _directory = NewDirectory();
        DurabilityTransaction[] transactions = [Transaction(1, 3), Transaction(2, 5), Transaction(3, 7)];
        await using (IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize))
        {
            await store.ApplyBatchAsync(transactions, DurabilityFaultPoint.None);
            Assert.AreEqual(transactions.Length, store.Metrics.AcknowledgedTransactions);
            Assert.AreEqual(strategy == DurabilityStrategyKind.RedoWal ? 1 : 2, store.Metrics.FlushCount);
        }
        await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize);
        foreach (DurabilityTransaction transaction in transactions)
        {
            CollectionAssert.AreEqual(transaction.Mutations[0].Image,
                recovered.CurrentPages[transaction.Mutations[0].PageId]);
        }
    }

    [DataTestMethod]
    [DataRow(DurabilityStrategyKind.RedoWal)]
    [DataRow(DurabilityStrategyKind.NoOverwrite)]
    public async Task Checkpoint_reclaims_covered_history_and_replays_the_retained_tail(
        DurabilityStrategyKind strategy)
    {
        _directory = NewDirectory();
        DurabilityTransaction checkpointed = Transaction(1, 13);
        DurabilityTransaction tail = Transaction(2, 17);
        await using (IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize))
        {
            await store.ApplyAsync(checkpointed, DurabilityFaultPoint.None);
            await store.CreateCheckpointAsync();
            long checkpointBytes = store.Metrics.CheckpointBytes;
            Assert.IsTrue(checkpointBytes > 0);
            await store.ApplyAsync(tail, DurabilityFaultPoint.None);
        }
        await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize);
        CollectionAssert.AreEqual(checkpointed.Mutations[0].Image, recovered.CurrentPages[13]);
        CollectionAssert.AreEqual(tail.Mutations[0].Image, recovered.CurrentPages[17]);
        Assert.AreEqual(2, recovered.CurrentPages.Count);
    }

    [DataTestMethod]
    [DataRow(DurabilityStrategyKind.RedoWal, CheckpointFaultPoint.BeforePublication)]
    [DataRow(DurabilityStrategyKind.RedoWal, CheckpointFaultPoint.AfterPublicationBeforeReclamation)]
    [DataRow(DurabilityStrategyKind.RedoWal, CheckpointFaultPoint.AfterReclamation)]
    [DataRow(DurabilityStrategyKind.NoOverwrite, CheckpointFaultPoint.BeforePublication)]
    [DataRow(DurabilityStrategyKind.NoOverwrite, CheckpointFaultPoint.AfterPublicationBeforeReclamation)]
    [DataRow(DurabilityStrategyKind.NoOverwrite, CheckpointFaultPoint.AfterReclamation)]
    public async Task Checkpoint_faults_preserve_the_same_restart_state(DurabilityStrategyKind strategy,
        CheckpointFaultPoint faultPoint)
    {
        _directory = NewDirectory();
        DurabilityTransaction first = Transaction(1, 19);
        DurabilityTransaction second = Transaction(2, 23);
        string expectedDigest;
        await using (IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize))
        {
            await store.ApplyBatchAsync([first, second], DurabilityFaultPoint.None);
            expectedDigest = DurabilityExperiment.Digest(store.CurrentPages);
            await Assert.ThrowsExactlyAsync<InjectedCheckpointFaultException>(async () =>
                await store.CreateCheckpointAsync(faultPoint));
        }
        await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
            _directory, PageSize);
        Assert.AreEqual(expectedDigest, DurabilityExperiment.Digest(recovered.CurrentPages));
    }

    private string NewDirectory() => Path.Combine(Path.GetTempPath(), $"entityforge-durability-test-{Guid.NewGuid():N}");

    private static DurabilityTransaction Transaction(long transactionId, long pageId) =>
        new(transactionId, [new DurabilityMutation(pageId, Enumerable.Repeat((byte)transactionId, PageSize).ToArray())]);
}
