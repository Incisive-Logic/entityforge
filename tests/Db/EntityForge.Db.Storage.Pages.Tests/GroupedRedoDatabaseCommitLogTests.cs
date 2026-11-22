using EntityForge.Db.Durability;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class GroupedRedoDatabaseCommitLogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Saturated_redo_waiters_finish_without_enumeration_on_shutdown_or_writer_failure(bool group, bool fail)
    {
        var observer = new PauseReplacement(RedoDurabilityStage.BeforeWalWrite, fail);
        using var release = observer.Release;
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupCount: 1, maximumGroupDelay: TimeSpan.Zero), observer);
        var admitted = new List<Task> { log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph)).AsTask() };
        var waiting = new List<Task>();
        Task shutdown = Task.CompletedTask;
        try
        {
            await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int version = 2; version <= 4; version++)
            {
                admitted.Add(log.CommitAsync(Transaction(version, DatabaseProfileId.PropertyGraph)).AsTask());
            }
            for (int version = 5; version <= 16; version++)
            {
                var transaction = Transaction(version, DatabaseProfileId.PropertyGraph);
                waiting.Add(group
                    ? log.CommitGroupAsync([new(transaction, new UnreadableMutations())]).AsTask()
                    : log.CommitWithPhysicalMutationsAsync(transaction, new UnreadableMutations()).AsTask());
            }

            Assert.IsTrue(waiting.All(task => !task.IsCompleted));
            if (!fail)
            {
                shutdown = log.DisposeAsync().AsTask();
            }
        }
        finally
        {
            observer.Release.Set();
        }

        foreach (Task task in admitted)
        {
            if (fail)
            {
                await Assert.ThrowsAsync<DatabaseCommitLogRecoveryRequiredException>(async () =>
                    await task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        foreach (Task task in waiting)
        {
            if (fail)
            {
                await Assert.ThrowsAsync<DatabaseCommitLogRecoveryRequiredException>(async () =>
                    await task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
                    await task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
        }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await log.DisposeAsync();
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        Assert.AreEqual(fail ? 0 : 4, (await reopened.RecoverAsync()).Count);
        await reopened.CommitAsync(Transaction(17, DatabaseProfileId.PropertyGraph));
        Assert.AreEqual(fail ? 1 : 5, (await reopened.RecoverAsync()).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_mutation_materialization_releases_redo_submission_capacity(bool group)
    {
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupCount: 1, maximumGroupDelay: TimeSpan.Zero));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var transaction = Transaction(1, DatabaseProfileId.PropertyGraph);
            Task rejected = group
                ? log.CommitGroupAsync([new(transaction, new UnreadableMutations())]).AsTask()
                : log.CommitWithPhysicalMutationsAsync(transaction, new UnreadableMutations()).AsTask();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await rejected.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, (await log.RecoverAsync()).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Saturated_redo_admission_waits_before_materializing_mutations(bool group)
    {
        var observer = new PauseReplacement(RedoDurabilityStage.BeforeWalWrite);
        using var release = observer.Release;
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupCount: 1, maximumGroupDelay: TimeSpan.Zero), observer);
        var admitted = new List<Task> { log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph)).AsTask() };
        await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        try
        {
            for (int version = 2; version <= 4; version++)
            {
                admitted.Add(log.CommitAsync(Transaction(version, DatabaseProfileId.PropertyGraph)).AsTask());
            }

            var transaction = Transaction(5, DatabaseProfileId.PropertyGraph);
            Task waiting = group
                ? log.CommitGroupAsync([new(transaction, new UnreadableMutations())], cancellation.Token).AsTask()
                : log.CommitWithPhysicalMutationsAsync(transaction, new UnreadableMutations(), cancellation.Token).AsTask();
            bool wasWaiting = !waiting.IsCompleted;
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            Assert.IsTrue(wasWaiting, "Saturated admission must wait without enumerating physical mutations.");
        }
        finally
        {
            observer.Release.Set();
            await Task.WhenAll(admitted);
        }

        await log.CommitAsync(Transaction(6, DatabaseProfileId.PropertyGraph));
        var recovered = await log.RecoverAsync();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 6 }, recovered.Select(t => t.Version.Value).ToArray());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task Batched_redo_tail_repair_preserves_committed_prefix_and_allows_complete_retry(int cut)
    {
        using var stream = new MemoryStream();
        var committed = Transaction(1, DatabaseProfileId.PropertyGraph);
        await RedoWalCodec.WriteTransactionAsync(stream, 1, committed, [], default);
        await stream.WriteAsync(RedoWalCodec.GroupMarker(1, 1));
        long prefixLength = stream.Length;
        byte[] prefix = stream.ToArray();
        var interrupted = Transaction(2, DatabaseProfileId.PropertyGraph);
        var pages = CompressibleEntries(600, 8000);
        await RedoWalCodec.WriteTransactionAsync(stream, 2, interrupted, pages, default);
        long transactionEnd = stream.Length;
        int firstLength = RedoWalCodec.ReadLength(stream.GetBuffer().AsSpan((int)prefixLength, RedoWalCodec.HeaderSize));
        long firstEnd = prefixLength + firstLength;
        Assert.IsTrue(firstEnd + RedoWalCodec.HeaderSize + 10 < transactionEnd);
        long truncatedLength = cut switch
        {
            0 => firstEnd,
            1 => firstEnd + RedoWalCodec.HeaderSize - 1,
            2 => firstEnd + RedoWalCodec.HeaderSize + 10,
            _ => transactionEnd
        };
        stream.SetLength(truncatedLength);
        await Assert.ThrowsExactlyAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.ReadAsync(stream, false, default));
        Assert.AreEqual(truncatedLength, stream.Length);
        var repaired = await RedoWalRecovery.ReadAsync(stream, true, default);
        Assert.AreEqual(1L, repaired.DurableLsn);
        Assert.AreEqual(committed.TransactionId, repaired.Transactions.Single().Transaction.TransactionId);
        CollectionAssert.AreEqual(prefix, stream.ToArray());
        Assert.AreEqual(prefixLength, stream.Length);

        stream.Position = stream.Length;
        await RedoWalCodec.WriteTransactionAsync(stream, 2, interrupted, pages, default);
        await stream.WriteAsync(RedoWalCodec.GroupMarker(2, 1));
        var recovered = await RedoWalRecovery.ReadAsync(stream, false, default);
        Assert.AreEqual(2L, recovered.DurableLsn);
        Assert.AreEqual(2, recovered.Transactions.Count);
        Assert.AreEqual(interrupted.TransactionId, recovered.Transactions[1].Transaction.TransactionId);
        Assert.AreEqual(pages.Length, recovered.Transactions[1].PageEntries.Count);
        for (int index = 0; index < pages.Length; index++)
        {
            var actual = recovered.Transactions[1].PageEntries[index];
            Assert.AreEqual(pages[index].PageId, actual.PageId);
            CollectionAssert.AreEqual(pages[index].Bytes.ToArray(), actual.Bytes.ToArray());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Redo_compression_prepares_bounded_batches_and_recovers_one_transaction(bool cancel)
    {
        var transaction = Transaction(1, DatabaseProfileId.PropertyGraph);
        var entries = new ObservedPageEntries(CompressibleEntries(600, 8000));
        using var stream = new DelayedWriteStream();
        using var cancellation = new CancellationTokenSource();
        Task<long> write = RedoWalCodec.WriteTransactionAsync(stream, 1, transaction, entries, cancellation.Token).AsTask();
        int prepared;
        try
        {
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            prepared = entries.Reads;
            if (cancel)
            {
                cancellation.Cancel();
            }
        }
        finally
        {
            stream.Release.TrySetResult();
        }

        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => write);
            Assert.AreEqual(0L, stream.Length);
        }
        else
        {
            long written = await write;
            Assert.AreEqual(stream.Length, written);
            await Assert.ThrowsExactlyAsync<RedoWalCorruptionException>(async () =>
                await RedoWalRecovery.ReadAsync(stream, false, default));
            stream.Position = stream.Length;
            await stream.WriteAsync(RedoWalCodec.GroupMarker(1, 1));
            var recovered = (await RedoWalRecovery.ReadAsync(stream, false, default)).Transactions.Single();
            Assert.AreEqual(transaction.TransactionId, recovered.Transaction.TransactionId);
            Assert.AreEqual(600, recovered.PageEntries.Count);
            for (int index = 0; index < 600; index++)
            {
                Assert.AreEqual(index + 1L, recovered.PageEntries[index].PageId);
                CollectionAssert.AreEqual(entries[index].Bytes.ToArray(), recovered.PageEntries[index].Bytes.ToArray());
            }
        }

        Assert.IsTrue(prepared <= 256, $"Prepared {prepared} entries before the first write completed.");
    }

    private sealed class ObservedPageEntries(IReadOnlyList<RedoPageEntry> entries) : IReadOnlyList<RedoPageEntry>
    {
        public int Reads
        {
            get; private set;
        }

        public int Count => entries.Count;

        public RedoPageEntry this[int index]
        {
            get
            {
                Reads++;
                return entries[index];
            }
        }

        public IEnumerator<RedoPageEntry> GetEnumerator() => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public async Task Redo_compression_observes_cancellation_between_page_entries_before_writing()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new MemoryStream();
        var entries = new CancellingPageEntries(cancellation);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await RedoWalCodec.WriteTransactionAsync(stream, 1,
                Transaction(1, DatabaseProfileId.PropertyGraph), entries, cancellation.Token));
        Assert.AreEqual(1, entries.Reads);
        Assert.AreEqual(0L, stream.Length);
    }

    private sealed class CancellingPageEntries(CancellationTokenSource cancellation) : IReadOnlyList<RedoPageEntry>
    {
        public int Reads
        {
            get; private set;
        }

        public int Count => 2;

        public RedoPageEntry this[int index]
        {
            get
            {
                Reads++;
                if (index != 0)
                {
                    throw new InvalidOperationException("Compression continued after cancellation.");
                }

                cancellation.Cancel();
                return new(RedoPageEntryKind.Patch, 1, 0, new byte[4096]);
            }
        }

        public IEnumerator<RedoPageEntry> GetEnumerator() => throw new NotSupportedException();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public async Task Cancelled_group_does_not_enumerate_physical_mutations_or_poison_writer()
    {
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await log.CommitGroupAsync([new(Transaction(1, DatabaseProfileId.PropertyGraph),
                new UnreadableMutations())], cancelled.Token));
        Assert.AreEqual(0, (await log.RecoverAsync()).Count);
        await log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph));
        Assert.AreEqual(1, (await log.RecoverAsync()).Count);
    }

    private sealed class UnreadableMutations : IReadOnlyList<DatabasePhysicalPageMutation>
    {
        public int Count => 0;

        public DatabasePhysicalPageMutation this[int index] => throw new InvalidOperationException("Must not inspect cancelled work.");

        public IEnumerator<DatabasePhysicalPageMutation> GetEnumerator() =>
            throw new InvalidOperationException("Must not enumerate cancelled work.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public async Task Redo_ownership_remains_exclusive_across_reclamation_and_releases_on_disposal()
    {
        await using (var owner = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await Assert.ThrowsExactlyAsync<PhysicalPageIoException>(async () =>
                await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath));
            await owner.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
            await owner.ReclaimThroughAsync(new(1));
            await Assert.ThrowsExactlyAsync<PhysicalPageIoException>(async () =>
                await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath));
        }
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        Assert.IsTrue(File.Exists(WalPath + ".owner"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reclaimed_history_requires_a_checkpoint_covering_the_independent_redo_floor(bool retainTail)
    {
        var first = Transaction(1, DatabaseProfileId.PropertyGraph);
        DurableCheckpointEnvelope Checkpoint(long floor) => new(first.DatabaseIncarnation, new(1), first.Payload)
        {
            PhysicalRecovery = new(floor, 0, new string('0', 64))
        };
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await log.ValidateRecoveryCheckpointAsync(null);
            await log.CommitAsync(first);
            await log.ValidateRecoveryCheckpointAsync(null); // Complete history needs no checkpoint.
            if (retainTail)
            {
                await log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph));
            }
            await log.ReclaimThroughAsync(new(1));
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        await Assert.ThrowsAsync<EntityForgeDbFormatException>(async () =>
            await reopened.ValidateRecoveryCheckpointAsync(null));
        await Assert.ThrowsAsync<EntityForgeDbFormatException>(async () =>
            await reopened.ValidateRecoveryCheckpointAsync(Checkpoint(0)));
        await reopened.ValidateRecoveryCheckpointAsync(Checkpoint(1));
        await Assert.ThrowsAsync<EntityForgeDbFormatException>(async () =>
            await reopened.ValidateRecoveryCheckpointAsync(Checkpoint(3)));
        Assert.AreEqual(retainTail ? 1 : 0, (await reopened.RecoverAsync()).Count);
        // Validation must still work after the startup recovery cache was consumed.
        await Assert.ThrowsAsync<EntityForgeDbFormatException>(async () =>
            await reopened.ValidateRecoveryCheckpointAsync(null));
        await reopened.ValidateRecoveryCheckpointAsync(Checkpoint(1));
    }

    [TestMethod]
    public async Task Checkpoint_position_reclaims_nonadvancing_records_but_keeps_later_same_version_records()
    {
        var template = Transaction(2, DatabaseProfileId.PropertyGraph);
        DurableTransactionEnvelope Evidence() => new(template.DatabaseIncarnation, new(2), Guid.NewGuid(),
            template.Payload, DurableCommittedChangeKind.PartitionEvidence, null,
            versioningMode: DatabaseVersioningMode.PartitionLocal);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await log.CommitAsync(Evidence());
            long coveredPosition = log.CaptureCheckpointPosition();
            var later = Evidence();
            await log.CommitAsync(later);
            await log.ReclaimCheckpointAsync(new(1), coveredPosition);
            Assert.AreEqual(later.TransactionId, (await log.RecoverAsync()).Single().TransactionId);
            await log.ReclaimCheckpointAsync(new(1), log.CaptureCheckpointPosition());
            Assert.AreEqual(0, (await log.RecoverAsync()).Count);
        }
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        Assert.AreEqual(2L, reopened.CaptureCheckpointPosition());
        await reopened.CommitAsync(Evidence());
        Assert.AreEqual(3L, reopened.CaptureCheckpointPosition());
    }

    [TestMethod]
    public async Task Optional_reclamation_diagnostics_preserve_retained_tail_and_report_bounded_work()
    {
        var phases = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var work = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "EntityForge.Db.Storage.Pages" &&
                instrument.Name.StartsWith("entityforge.db.redo.reclamation.", StringComparison.Ordinal))
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            Assert.IsTrue(double.IsFinite(value) && value >= 0);
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.phase")
                {
                    phases.Enqueue((string)tag.Value!);
                }
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => work[instrument.Name] = value);
        listener.Start();
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupDelay: TimeSpan.Zero));
        for (int index = 1; index <= 5; index++)
        {
            await log.CommitAsync(Transaction(index, DatabaseProfileId.PropertyGraph));
        }

        Assert.AreEqual(0, phases.Count); // No append phases are enabled by this subscription.
        long sourceBytes = log.GetRedoStatus().EncodedWalBytes;
        var result = await log.ReclaimThroughAsync(new(3));
        CollectionAssert.AreEqual(new[]
        {
            "replacement_initialize", "source_flush", "source_scan_rewrite",
            "source_catchup", "replacement_commit", "replacement_publish", "replacement_reopen"
        }, phases.ToArray());
        Assert.AreEqual(sourceBytes, work["entityforge.db.redo.reclamation.source.bytes"]);
        Assert.AreEqual(3L, work["entityforge.db.redo.reclamation.removed.transactions"]);
        Assert.AreEqual(3, result.RecordsRemoved);
        CollectionAssert.AreEqual(new[] { 4L, 5L },
            (await log.RecoverAsync()).Select(item => item.Version.Value).ToArray());
    }

    [TestMethod]
    public async Task Captured_epoch_keeps_first_post_cut_page_protection_after_prefix_reclamation()
    {
        var pageId = new PhysicalPageId(0);
        byte[] before = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, pageId).Encode();
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await log.CommitWithRedoAsync(Transaction(1, DatabaseProfileId.PropertyGraph),
                [new(pageId, RedoWalTestOffsets.GenerationByte, new byte[] { 41 }, before)]);
            long cut = await log.BeginCheckpointEpochAsync();
            await log.CommitWithRedoAsync(Transaction(2, DatabaseProfileId.PropertyGraph),
                [new(pageId, RedoWalTestOffsets.GenerationByte, new byte[] { 42 }, before)]);
            await log.ReclaimCheckpointAsync(new(1), cut);
        }
        string pagePath = Path.Combine(_directory!, "torn-pages.efp");
        await File.WriteAllBytesAsync(pagePath, new byte[PhysicalPageFileOptions.DefaultPageSize]);
        await using var recovered = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        await using var pages = await PhysicalPageFile.OpenAsync(pagePath);
        await recovered.RecoverPagesAsync(pages);
        var image = await pages.ReadAsync(pageId);
        SlottedDataPage.Validate(image.Span, pageId);
        Assert.AreEqual(42, image.Span[RedoWalTestOffsets.GenerationByte]);
        Assert.AreEqual(2L, (await recovered.RecoverAsync()).Single().Version.Value);
    }

    private sealed class PauseReplacement(RedoDurabilityStage target = RedoDurabilityStage.BeforeReplacementInitialization,
        bool failAfterRelease = false) : IRedoDurabilityObserver
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new(false);

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage != target)
            {
                return;
            }

            Started.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Replacement barrier was not released.");
            }
            if (failAfterRelease)
            {
                throw new IOException("Injected writer failure after admission saturation.");
            }
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Physical_submission_snapshots_mutation_collection_before_queueing(bool explicitGroup)
    {
        var samples = new System.Collections.Concurrent.ConcurrentQueue<double>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "EntityForge.Db.Storage.Pages" &&
                instrument.Name == "entityforge.db.redo.phase.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.phase" && Equals(tag.Value, "submission_queue_wait"))
                {
                    samples.Enqueue(value);
                }
            }
        });
        listener.Start();
        var observer = new PauseReplacement(RedoDurabilityStage.BeforeWalWrite);
        string path = WalPath;
        byte[] expected = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(0), 17).Encode();
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(path,
            new(maximumGroupDelay: TimeSpan.Zero), observer))
        {
            var first = log.CommitAsync(Transaction(1, DatabaseProfileId.Semantic)).AsTask();
            await observer.Started.Task;
            Task second;
            try
            {
                DatabasePhysicalPageMutation[] mutations = [DatabasePhysicalPageMutation.CreatePage(0, expected)];
                var transaction = Transaction(2, DatabaseProfileId.Semantic);
                second = explicitGroup
                    ? log.CommitGroupAsync([new DatabasePhysicalCommit(transaction, mutations)]).AsTask()
                    : log.CommitWithPhysicalMutationsAsync(transaction, mutations).AsTask();
                mutations[0] = DatabasePhysicalPageMutation.CreatePage(0,
                    new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(0), 99).Encode());
            }
            finally
            {
                observer.Release.Set();
            }
            await first;
            await second;
        }
        Assert.AreEqual(2, samples.Count);
        Assert.IsTrue(samples.All(value => double.IsFinite(value) && value >= 0));
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(path);
        await using var pages = await PhysicalPageFile.OpenAsync(Path.Combine(_directory!, "snapshot-pages.efp"));
        await reopened.RecoverPagesAsync(pages);
        var restored = SlottedDataPage.Decode((await pages.ReadAsync(new(0))).Span, new(0));
        Assert.AreEqual(17L, restored.Generation);
        Assert.AreEqual(2L, restored.PageLsn); // Recovery stamps the durable LSN.
        Assert.AreEqual(0, restored.SlotCount);
    }

    [DataTestMethod]
    [DataRow(RedoDurabilityStage.BeforeReplacementInitialization)]
    [DataRow(RedoDurabilityStage.BeforeReclamationPrefixScan)]
    public async Task Replacement_initialization_does_not_exclude_foreground_commits_and_keeps_their_tail(RedoDurabilityStage stage)
    {
        var observer = new PauseReplacement(stage);
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath, observer: observer);
        await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
        long cut = await log.BeginCheckpointEpochAsync();
        var reclamation = Task.Run(async () => await log.ReclaimCheckpointAsync(new(1), cut));
        try
        {
            await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(reclamation.IsCompleted);
        }
        finally
        {
            observer.Release.Set();
        }
        await reclamation;
        Assert.AreEqual(2L, (await log.RecoverAsync()).Single().Version.Value);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Prefix_scan_cancellation_or_disposal_preserves_commits(bool dispose)
    {
        var observer = new PauseReplacement(RedoDurabilityStage.BeforeReclamationPrefixScan);
        var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath, observer: observer);
        using var cancellation = new CancellationTokenSource();
        await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
        var reclamation = Task.Run(async () => await log.ReclaimThroughAsync(new(1), cancellation.Token));
        Task? disposal = null;
        try
        {
            await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose)
            {
                disposal = log.DisposeAsync().AsTask();
                Assert.IsFalse(disposal.IsCompleted, "Disposal must keep the captured generation alive until reclamation drains.");
            }
            else
            {
                cancellation.Cancel();
            }
        }
        finally
        {
            observer.Release.Set();
        }
        try
        {
            if (dispose)
            {
                await reclamation.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                try
                {
                    await reclamation;
                    Assert.Fail("Cancelled reclamation must not publish.");
                }
                catch (OperationCanceledException)
                {
                    Assert.IsTrue(cancellation.IsCancellationRequested);
                }
            }
        }
        finally
        {
            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                await log.DisposeAsync();
            }
        }
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        var versions = (await reopened.RecoverAsync()).Select(item => item.Version.Value).ToArray();
        CollectionAssert.AreEqual(dispose ? new[] { 2L } : new[] { 1L, 2L }, versions);
    }

    [TestMethod]
    public async Task Reclamation_writer_gate_blocks_commit_then_preserves_recoverable_tail_and_reports_wait()
    {
        var waits = new System.Collections.Concurrent.ConcurrentQueue<(string Operation, double Milliseconds)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "EntityForge.Db.Storage.Pages" &&
                instrument.Name == "entityforge.db.redo.file_gate.wait.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.operation")
                {
                    waits.Enqueue(((string)tag.Value!, value));
                }
            }
        });
        listener.Start();
        var observer = new PauseReplacement(RedoDurabilityStage.BeforeWalReclamation);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupDelay: TimeSpan.Zero), observer))
        {
            await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
            long cut = await log.BeginCheckpointEpochAsync();
            var reclamation = Task.Run(async () => await log.ReclaimCheckpointAsync(new(1), cut));
            Task? commit = null;
            try
            {
                await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                commit = log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph)).AsTask();
                var probeInterval = TimeSpan.FromMilliseconds(100);
                await Task.Delay(probeInterval);
                Assert.IsFalse(commit.IsCompleted, "Commit must not publish while replacement owns the writer gate.");
            }
            finally
            {
                observer.Release.Set();
            }
            await reclamation.WaitAsync(TimeSpan.FromSeconds(5));
            await commit!.WaitAsync(TimeSpan.FromSeconds(5));
            CollectionAssert.AreEqual(new[] { 2L },
                (await log.RecoverAsync()).Select(item => item.Version.Value).ToArray());
        }
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        Assert.AreEqual(2L, (await reopened.RecoverAsync()).Single().Version.Value);
        Assert.AreEqual(2, waits.Count(item => item.Operation == "commit"));
        Assert.IsTrue(waits.Any(item => item.Operation == "checkpoint_epoch"));
        Assert.IsTrue(waits.Any(item => item.Operation == "reclamation"));
        Assert.IsTrue(waits.All(item => double.IsFinite(item.Milliseconds) && item.Milliseconds >= 0));
    }

    [DataTestMethod]
    [DataRow(RedoDurabilityStage.BeforeWalReclamation, "prefix_capture")]
    [DataRow(RedoDurabilityStage.AfterWalReclamation, "catchup_publish")]
    public async Task Reclamation_hold_measurement_covers_owned_gate_and_preserves_recovery(RedoDurabilityStage stage, string expectedPhase)
    {
        var holds = new System.Collections.Concurrent.ConcurrentQueue<(string Phase, double Milliseconds)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "EntityForge.Db.Storage.Pages" &&
                instrument.Name == "entityforge.db.redo.reclamation.file_gate.hold.duration")
            {
                owner.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "entityforge.db.operation")
                {
                    holds.Enqueue(((string)tag.Value!, value));
                }
            }
        });
        listener.Start();
        var observer = new PauseReplacement(stage);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupDelay: TimeSpan.Zero), observer))
        {
            await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
            long cut = await log.BeginCheckpointEpochAsync();
            var reclamation = Task.Run(async () => await log.ReclaimCheckpointAsync(new(1), cut));
            Task? commit = null;
            try
            {
                await observer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                commit = log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph)).AsTask();
                await Task.Delay(100);
                Assert.IsFalse(commit.IsCompleted);
            }
            finally
            {
                observer.Release.Set();
            }
            await reclamation.WaitAsync(TimeSpan.FromSeconds(5));
            await commit!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(2, holds.Count);
        Assert.IsTrue(holds.All(item => double.IsFinite(item.Milliseconds) && item.Milliseconds >= 0));
        Assert.IsTrue(holds.Single(item => item.Phase == expectedPhase).Milliseconds >= 75,
            "The injected 100ms owned-gate pause must appear in the hold measurement.");
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        CollectionAssert.AreEqual(new[] { 2L }, (await reopened.RecoverAsync()).Select(item => item.Version.Value).ToArray());
    }

    [TestMethod]
    public async Task Reclamation_reuses_directory_lineage_but_keeps_each_publication_barrier()
    {
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
            await log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph));
            var synchronized = new List<string>();
            DurableDirectory.BeforeSync.Value = synchronized.Add;
            await log.ReclaimThroughAsync(new(1));
            CollectionAssert.AreEqual(new[] { _directory!, _directory!, _directory! }, synchronized,
                "New candidate, selected-name replacement, and selected-file reopen must each retain their directory barrier.");
            Assert.AreEqual(2L, (await log.RecoverAsync()).Single().Version.Value);
        }
        var recoverySync = new List<string>();
        DurableDirectory.BeforeSync.Value = recoverySync.Add;
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        CollectionAssert.Contains(recoverySync, Path.GetDirectoryName(_directory!)!);
        Assert.AreEqual(2L, (await reopened.RecoverAsync()).Single().Version.Value);
    }

    [TestMethod]
    public async Task Failed_selected_name_sync_requires_recovery_and_preserves_retained_tail()
    {
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
            await log.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph));
            int syncs = 0;
            DurableDirectory.BeforeSync.Value = _ =>
            {
                if (++syncs == 2)
                {
                    throw new IOException("Injected selected-name publication failure.");
                }
            };
            await Assert.ThrowsExceptionAsync<PhysicalPageIoException>(async () => await log.ReclaimThroughAsync(new(1)));
            await Assert.ThrowsExceptionAsync<DatabaseCommitLogRecoveryRequiredException>(async () =>
                await log.CommitAsync(Transaction(3, DatabaseProfileId.PropertyGraph)));
        }
        var synchronized = new List<string>();
        DurableDirectory.BeforeSync.Value = synchronized.Add;
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        CollectionAssert.Contains(synchronized, Path.GetDirectoryName(_directory!)!);
        Assert.AreEqual(2L, (await reopened.RecoverAsync()).Single().Version.Value);
        await reopened.CommitAsync(Transaction(3, DatabaseProfileId.PropertyGraph));
        CollectionAssert.AreEqual(new[] { 2L, 3L }, (await reopened.RecoverAsync()).Select(x => x.Version.Value).ToArray());
    }

    private string? _directory;

    private string WalPath => Path.Combine(_directory!, "selected-redo.wal");

    [TestInitialize]
    public void Initialize() =>
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-redo-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        DurableDirectory.BeforeSync.Value = null;
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [DataTestMethod]
    [DataRow(CommittedChangeKind.DatasetReset)]
    [DataRow(CommittedChangeKind.IncrementalEffects)]
    public async Task Wal_recovery_preserves_committed_payload_kind(CommittedChangeKind kind)
    {
        var original = Transaction(1, DatabaseProfileId.Semantic, withEffects: true);
        var changes = new CommittedChangePayload("test-reset", 1, ReadOnlyMemory<byte>.Empty, 1, kind: kind);
        var transaction = new DurableTransactionEnvelope(original.DatabaseIncarnation, original.Version,
            original.TransactionId, original.Payload, DurableCommittedChangeKind.UserDataWithChanges, changes);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await log.CommitAsync(transaction);
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        var restored = (await reopened.RecoverAsync()).Single().CommittedChanges!;
        Assert.AreEqual(kind, restored.Kind);
        Assert.AreEqual(kind == CommittedChangeKind.DatasetReset, restored.RequiresSnapshotRefresh);
        Assert.AreEqual(changes.CodecId, restored.CodecId);
        Assert.AreEqual(changes.EffectCount, restored.EffectCount);
        Assert.AreEqual(0, restored.Content.Length);
    }

    [TestMethod]
    public async Task Prewrite_protection_failure_completes_the_admitted_caller_and_requires_reopen()
    {
        var transaction = Transaction(1, DatabaseProfileId.PropertyGraph);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath, new(maximumGroupDelay: TimeSpan.Zero)))
        {
            // A malformed producer must not strand the completion removed from the channel.
            var failure = await Assert.ThrowsExceptionAsync<DatabaseCommitLogRecoveryRequiredException>(async () =>
                await log.CommitWithRedoAsync(transaction, new PhysicalPageRedoMutation[] { null! }).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(transaction.TransactionId, failure.TransactionId);
            Assert.IsFalse(failure.OutcomeUnknown);
            await Assert.ThrowsExceptionAsync<DatabaseCommitLogRecoveryRequiredException>(async () =>
                await log.CommitAsync(transaction));
        }
        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        Assert.AreEqual(0, (await reopened.RecoverAsync()).Count);
        await reopened.CommitAsync(transaction);
        Assert.AreEqual(transaction.TransactionId, (await reopened.RecoverAsync()).Single().TransactionId);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(4096)]
    [DataRow(65000)]
    [DataRow(65536)]
    [DataRow(131072)]
    public async Task Streamed_logical_records_match_canonical_bytes_across_buffer_boundary(int size)
    {
        byte[] content = new byte[size];
        new Random(size).NextBytes(content);
        DurableTransactionEnvelope template = Transaction(1, DatabaseProfileId.PropertyGraph, withEffects: true);
        var transaction = new DurableTransactionEnvelope(template.DatabaseIncarnation, template.Version,
            template.TransactionId, new(template.Payload.Profile, template.Payload.CodecId,
                template.Payload.CodecVersion, content), template.ChangeKind, template.CommittedChanges);
        using var stream = new MemoryStream();
        long written = await RedoWalCodec.WriteTransactionAsync(stream, 1, transaction, [], default);
        byte[] expected = RedoWalCodec.Transaction(1, transaction);
        Assert.AreEqual((long)expected.Length, written);
        CollectionAssert.AreEqual(expected, stream.ToArray());
    }

    [DataTestMethod]
    [DataRow(32, false)]
    [DataRow(65000, false)]
    [DataRow(65536, true)]
    [DataRow(131072, true)]
    public async Task Streamed_envelope_and_page_redo_share_one_canonical_record(int size, bool compressible)
    {
        var template = Transaction(1, DatabaseProfileId.Semantic, withEffects: true);
        byte[] content = new byte[size];
        new Random(size).NextBytes(content);
        var transaction = new DurableTransactionEnvelope(template.DatabaseIncarnation, template.Version,
            template.TransactionId, new(template.Payload.Profile, template.Payload.CodecId,
                template.Payload.CodecVersion, content), template.ChangeKind, template.CommittedChanges);
        byte[] page = new byte[PhysicalPageFileOptions.DefaultPageSize];
        if (!compressible)
        {
            new Random(7).NextBytes(page);
        }

        RedoPageEntry[] entries = [new(RedoPageEntryKind.Patch, 7, 0, page)];
        using var stream = new CountingWriteStream();
        long written = await RedoWalCodec.WriteTransactionAsync(stream, 1, transaction, entries, default);
        byte[] canonical = RedoWalCodec.Transaction(1, transaction, entries);
        Assert.AreEqual((long)canonical.Length, written);
        CollectionAssert.AreEqual(canonical, stream.ToArray());
        Assert.AreEqual(canonical.Length <= 64 * 1024, stream.Writes == 1,
            "Only bounded small records should use the single-write path.");
        var fragment = RedoWalCodec.DecodeTransaction(RedoWalCodec.Decode(stream.ToArray()).Payload);
        Assert.AreEqual(transaction.TransactionId,
            RedoWalCodec.DecodeTransactionEnvelope(fragment.EnvelopeBytes.Span, fragment.Version).TransactionId);
        CollectionAssert.AreEqual(page, fragment.PageEntries.Single().Bytes.ToArray());
    }

    [TestMethod]
    // Keep compilation of the measurement harness out of the counted interval.
    // The codec itself retains the normal runtime compilation settings.
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining |
        System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
    public void Inspect_checksum_does_not_allocate_and_rejects_corruption()
    {
        byte[] record = RedoWalCodec.Transaction(1, Transaction(1, DatabaseProfileId.Semantic));
        _ = RedoWalCodec.Inspect(record);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            _ = RedoWalCodec.Inspect(record);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 1024,
            $"Inspect allocated {allocated} bytes across 10,000 checksum inspections.");
        record[^1] ^= 1;
        Assert.ThrowsExactly<RedoWalCorruptionException>(() => RedoWalCodec.Inspect(record));
    }

    private sealed class CountingWriteStream : MemoryStream
    {
        public int Writes;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Writes++;
            return base.WriteAsync(buffer, token);
        }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task Page_redo_buffer_limit_is_exact_and_preserves_failure_and_cancellation(int extraByte)
    {
        var template = Transaction(1, DatabaseProfileId.Semantic);
        DurableTransactionEnvelope Make(int size) => new(template.DatabaseIncarnation, template.Version,
            template.TransactionId, new(template.Payload.Profile, template.Payload.CodecId,
                template.Payload.CodecVersion, new byte[size]), template.ChangeKind, template.CommittedChanges);
        byte[] page = new byte[16384];
        new Random(17).NextBytes(page);
        RedoPageEntry[] entries = [new(RedoPageEntryKind.Patch, 7, 3, page)];
        int size = 65536 + extraByte - RedoWalCodec.Transaction(1, Make(0), entries).Length;
        var transaction = Make(size);
        byte[] expected = RedoWalCodec.Transaction(1, transaction, entries);
        Assert.AreEqual(65536 + extraByte, expected.Length);
        using var failed = new FailingWriteStream();
        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await RedoWalCodec.WriteTransactionAsync(failed, 1, transaction, entries, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new CountingWriteStream();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await RedoWalCodec.WriteTransactionAsync(stream, 1, transaction, entries, cancelled.Token));
        Assert.AreEqual(0L, stream.Length);
        stream.Writes = 0;
        await RedoWalCodec.WriteTransactionAsync(stream, 1, transaction, entries, default);
        Assert.AreEqual(extraByte == 0, stream.Writes == 1);
        CollectionAssert.AreEqual(expected, stream.ToArray());
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException(new IOException("Injected write failure."));
    }

    private static RedoPageEntry[] CompressibleEntries(int count, int entropy)
    {
        byte[] bytes = new byte[16384];
        new Random(412).NextBytes(bytes.AsSpan(0, entropy));
        bytes.AsSpan(entropy).Fill(42);
        return Enumerable.Range(0, count).Select(i => new RedoPageEntry(
            RedoPageEntryKind.FullPageProtection, i + 1, 0, bytes)).ToArray();
    }

    [TestMethod]
    public void Compressed_page_storage_does_not_allocate_per_payload_bytes()
    {
        var transaction = Transaction(1, DatabaseProfileId.Semantic);
        var small = CompressibleEntries(1, 0);
        var large = CompressibleEntries(1, 8000);
        long Measure(RedoPageEntry[] entries)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                RedoWalCodec.WriteTransactionAsync(Stream.Null, 1, transaction, entries, default).GetAwaiter().GetResult();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        _ = Measure(small);
        _ = Measure(large);
        Assert.IsTrue(Measure(large) - Measure(small) < 100 * 512);
    }

    [TestMethod]
    [DataRow(1, false)]
    [DataRow(12, false)]
    [DataRow(12, true)]
    public async Task Compressed_entry_arena_survives_delayed_writes_and_bounded_overflow(int count, bool cancel)
    {
        var transaction = Transaction(1, DatabaseProfileId.Semantic);
        var entries = CompressibleEntries(count, 8000);
        byte[] expected = RedoWalCodec.Transaction(1, transaction, entries);
        using var stream = new DelayedWriteStream();
        using var cancellation = new CancellationTokenSource();
        var pending = RedoWalCodec.WriteTransactionAsync(stream, 1, transaction, entries, cancellation.Token).AsTask();
        await stream.Entered.Task;
        Assert.IsFalse(pending.IsCompleted);
        for (int i = 0; i < 20; i++)
        {
            await RedoWalCodec.WriteTransactionAsync(Stream.Null, 2, transaction, CompressibleEntries(12, 4000), default);
        }

        if (cancel)
        {
            cancellation.Cancel();
        }

        stream.Release.TrySetResult();
        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            await pending;
            CollectionAssert.AreEqual(expected, stream.ToArray());
            var decoded = RedoWalCodec.DecodeTransaction(RedoWalCodec.Decode(stream.ToArray()).Payload);
            Assert.AreEqual(count, decoded.PageEntries.Count);
            for (int i = 0; i < count; i++)
            {
                CollectionAssert.AreEqual(entries[i].Bytes.ToArray(), decoded.PageEntries[i].Bytes.ToArray());
            }
        }
        using var retry = new MemoryStream();
        await RedoWalCodec.WriteTransactionAsync(retry, 1, transaction, entries, default);
        CollectionAssert.AreEqual(expected, retry.ToArray());
    }

    [TestMethod]
    public void Small_streamed_envelopes_do_not_allocate_payload_sized_arrays()
    {
        var template = Transaction(1, DatabaseProfileId.Semantic, withEffects: true);
        DurableTransactionEnvelope Make(int size) => new(template.DatabaseIncarnation, template.Version,
            template.TransactionId, new(template.Payload.Profile, template.Payload.CodecId,
                template.Payload.CodecVersion, new byte[size]), template.ChangeKind, template.CommittedChanges);
        var small = Make(1024);
        var large = Make(32000);
        long Measure(DurableTransactionEnvelope transaction)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                RedoWalCodec.WriteTransactionAsync(Stream.Null, 1, transaction, [], default).GetAwaiter().GetResult();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        _ = Measure(small);
        _ = Measure(large);
        Assert.IsTrue(Measure(large) - Measure(small) < 100 * 512,
            "Small streamed envelope allocation must not scale with payload size.");
    }

    [TestMethod]
    public async Task Pooled_record_buffers_remain_owned_until_async_write_completes()
    {
        var transaction = Transaction(1, DatabaseProfileId.Semantic, withEffects: true);
        using var stream = new DelayedWriteStream();
        var pending = RedoWalCodec.WriteTransactionAsync(stream, 1, transaction, [], default).AsTask();
        await stream.Entered.Task;
        Assert.IsFalse(pending.IsCompleted);
        for (int i = 2; i < 100; i++)
        {
            await RedoWalCodec.WriteTransactionAsync(Stream.Null, i, Transaction(i, DatabaseProfileId.Semantic), [], default);
        }

        stream.Release.SetResult();
        await pending;
        CollectionAssert.AreEqual(RedoWalCodec.Transaction(1, transaction), stream.ToArray());
    }

    private sealed class DelayedWriteStream : MemoryStream
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            await base.WriteAsync(buffer, token);
        }
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(63)]
    [DataRow(64)]
    [DataRow(67)]
    [DataRow(68)]
    public void Uncompressible_small_redo_entries_retain_original_bytes(int size)
    {
        byte[] bytes = new byte[size];
        new Random(42).NextBytes(bytes);
        var transaction = Transaction(1, DatabaseProfileId.Semantic);
        byte[] record = RedoWalCodec.Transaction(1, transaction, [new(RedoPageEntryKind.Patch, 7, 5, bytes)]);
        var decoded = RedoWalCodec.DecodeTransaction(RedoWalCodec.Decode(record).Payload).PageEntries.Single();
        Assert.AreEqual(RedoPageEntryKind.Patch, decoded.Kind);
        Assert.AreEqual(5, decoded.Offset);
        CollectionAssert.AreEqual(bytes, decoded.Bytes.ToArray());
    }

    [DataTestMethod]
    [DataRow(1L, false)]
    [DataRow(2L, false)]
    [DataRow(1L, true)]
    [DataRow(2L, true)]
    public async Task Valid_checksummed_groups_cannot_regress_or_reuse_an_lsn(long secondLsn, bool reclamation)
    {
        using var source = new MemoryStream();
        source.Write(RedoWalCodec.Transaction(2, Transaction(1, DatabaseProfileId.PropertyGraph)));
        source.Write(RedoWalCodec.GroupMarker(2, 1));
        source.Write(RedoWalCodec.Transaction(secondLsn, Transaction(2, DatabaseProfileId.PropertyGraph)));
        source.Write(RedoWalCodec.GroupMarker(secondLsn, 1));
        using var target = new MemoryStream();
        await Assert.ThrowsExceptionAsync<RedoWalCorruptionException>(async () =>
        {
            if (reclamation)
            {
                await RedoWalRecovery.RewriteAfterAsync(source, target, new(0), default);
            }
            else
            {
                await RedoWalRecovery.ReadAsync(source, false, default);
            }
        });
    }

    [TestMethod]
    public void Page_entry_count_is_bounded_by_the_validated_record_before_allocation()
    {
        byte[] record = RedoWalCodec.Transaction(1, Transaction(1, DatabaseProfileId.PropertyGraph));
        byte[] payload = RedoWalCodec.Decode(record).Payload.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(payload.Length - sizeof(int)), int.MaxValue);
        Assert.ThrowsException<RedoWalCorruptionException>(() => RedoWalCodec.DecodeTransaction(payload));
    }

    [TestMethod]
    public async Task Declared_envelope_larger_than_the_whole_log_is_rejected_before_allocation()
    {
        byte[] record = RedoWalCodec.Transaction(1, Transaction(1, DatabaseProfileId.PropertyGraph));
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(RedoWalCodec.HeaderSize + sizeof(long)), int.MaxValue);
        System.Security.Cryptography.SHA256.HashData(record.AsSpan(0, record.Length - RedoWalCodec.ChecksumSize))
            .CopyTo(record.AsSpan(record.Length - RedoWalCodec.ChecksumSize));
        using var source = new MemoryStream();
        source.Write(record);
        source.Write(RedoWalCodec.GroupMarker(1, 1));
        await Assert.ThrowsExceptionAsync<RedoWalCorruptionException>(async () => await RedoWalRecovery.ReadAsync(source, false, default));
    }

    [TestMethod]
    public async Task Graph_and_semantic_envelopes_and_effects_round_trip_through_selected_format()
    {
        DurableTransactionEnvelope graph = Transaction(1, DatabaseProfileId.PropertyGraph, withEffects: true);
        DurableTransactionEnvelope semantic = Transaction(2, DatabaseProfileId.Semantic, withEffects: true);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitAsync(graph);
            await log.CommitAsync(semantic);
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        IReadOnlyList<DurableTransactionEnvelope> recovered = await reopened.RecoverAsync();

        CollectionAssert.AreEqual(new[] { graph.TransactionId, semantic.TransactionId }, recovered.Select(item => item.TransactionId).ToArray());
        CollectionAssert.AreEqual(new[] { DatabaseProfileId.PropertyGraph, DatabaseProfileId.Semantic }, recovered.Select(item => item.Payload.Profile).ToArray());
        Assert.IsTrue(recovered.All(item => item.CommittedChanges!.Content.Span.SequenceEqual(new byte[] { 7, 8, 9 })));
        Assert.IsTrue(recovered.All(item =>
            item.CommittedChanges!.Routing.OverflowedKinds.SequenceEqual(["subject"])));
    }

    [TestMethod]
    public async Task Concurrent_submissions_share_bounded_durable_groups()
    {
        const int transactionCount = 32;
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupCount: transactionCount, maximumGroupBytes: RedoDurabilityOptions.DefaultMaximumGroupBytes,
                maximumGroupDelay: TimeSpan.FromMilliseconds(20)));

        await Task.WhenAll(Enumerable.Range(1, transactionCount)
            .Select(index => log.CommitAsync(Transaction(index, DatabaseProfileId.PropertyGraph)).AsTask()));

        RedoDurabilityStatus status = log.GetRedoStatus();
        Assert.AreEqual(transactionCount, status.DurableTransactions);
        Assert.IsTrue(status.DurableGroups >= 1 && status.DurableGroups < transactionCount);
        Assert.IsTrue(status.MaximumObservedGroupCount >= 2 && status.MaximumObservedGroupCount <= transactionCount);
    }

    [TestMethod]
    public async Task Incomplete_final_group_is_removed_and_later_commits_remain_recoverable()
    {
        DurableTransactionEnvelope first = Transaction(1, DatabaseProfileId.Semantic);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitAsync(first);
        }

        long committedLength = new FileInfo(WalPath).Length;
        await using (var tail = new FileStream(WalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            await tail.WriteAsync(new byte[] { 1, 2, 3, 4, 5 });
        }

        DurableTransactionEnvelope second = Transaction(2, DatabaseProfileId.Semantic);
        await using (var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            Assert.AreEqual(committedLength, new FileInfo(WalPath).Length);
            await reopened.CommitAsync(second);
        }

        await using var verified = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        CollectionAssert.AreEqual(new[] { first.TransactionId, second.TransactionId },
            (await verified.RecoverAsync()).Select(item => item.TransactionId).ToArray());
    }

    [TestMethod]
    public async Task Corruption_inside_a_committed_group_fails_closed()
    {
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
        }

        byte[] bytes = await File.ReadAllBytesAsync(WalPath);
        bytes[InitializedRedoStream.HeaderBytes + RedoWalTestOffsets.PayloadByte] ^= byte.MaxValue;
        await File.WriteAllBytesAsync(WalPath, bytes);

        await Assert.ThrowsAsync<RedoWalCorruptionException>(async () =>
            await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath));
    }

    [TestMethod]
    public void Transaction_version_prefix_must_match_the_durable_envelope()
    {
        byte[] record = RedoWalCodec.Transaction(1, Transaction(1, DatabaseProfileId.PropertyGraph));
        byte[] payload = RedoWalCodec.Decode(record).Payload;
        payload[0] = 2;

        RedoTransactionFragment fragment = RedoWalCodec.DecodeTransaction(payload);
        Assert.ThrowsExactly<RedoWalCorruptionException>(() =>
            RedoWalCodec.DecodeTransactionEnvelope(fragment.EnvelopeBytes.Span, fragment.Version));
    }

    [TestMethod]
    public void Binary_transaction_rejects_an_out_of_bounds_profile_codec()
    {
        byte[] record = RedoWalCodec.Transaction(1, Transaction(1, DatabaseProfileId.PropertyGraph));
        byte[] payload = RedoWalCodec.Decode(record).Payload;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(
            RedoWalTestOffsets.TransactionProfileCodecLength), int.MaxValue);

        RedoTransactionFragment fragment = RedoWalCodec.DecodeTransaction(payload);
        Assert.ThrowsExactly<RedoWalCorruptionException>(() =>
            RedoWalCodec.DecodeTransactionEnvelope(fragment.EnvelopeBytes.Span, fragment.Version));
    }

    [TestMethod]
    public void Binary_transaction_requires_a_supported_storage_layout()
    {
        byte[] record = RedoWalCodec.Transaction(1, Transaction(1, DatabaseProfileId.PropertyGraph));
        RedoTransactionFragment fragment = RedoWalCodec.DecodeTransaction(RedoWalCodec.Decode(record).Payload);
        byte[] envelope = fragment.EnvelopeBytes.ToArray();
        Assert.AreEqual(DatabaseStorageLayout.Current,
            RedoWalCodec.DecodeTransactionEnvelope(envelope, fragment.Version).StorageLayout);
        envelope[0] = byte.MaxValue;
        Assert.ThrowsExactly<RedoWalCorruptionException>(() =>
            RedoWalCodec.DecodeTransactionEnvelope(envelope, fragment.Version));
    }

    [TestMethod]
    public async Task Failure_before_tail_publication_reports_uncertain_and_reopens_without_the_unpublished_identity()
    {
        DurableTransactionEnvelope transaction = Transaction(1, DatabaseProfileId.Semantic);
        var observer = new ThrowOnceObserver(RedoDurabilityStage.AfterWalWrite);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero), observer))
        {
            RedoCommitUncertainException exception = await Assert.ThrowsAsync<RedoCommitUncertainException>(
                () => log.CommitAsync(transaction).AsTask());
            Assert.AreEqual(transaction.TransactionId, exception.TransactionId);
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        Assert.IsFalse((await reopened.RecoverAsync()).Any(item => item.TransactionId == transaction.TransactionId));
    }

    [DataTestMethod]
    [DataRow(RedoDurabilityStage.BeforeWalWrite, false, false)]
    [DataRow(RedoDurabilityStage.AfterWalWrite, true, false)]
    [DataRow(RedoDurabilityStage.BeforeWalFlush, true, false)]
    [DataRow(RedoDurabilityStage.AfterWalFlush, true, true)]
    [DataRow(RedoDurabilityStage.BeforeTailPublication, true, false)]
    [DataRow(RedoDurabilityStage.AfterFirstTailCopy, true, true)]
    [DataRow(RedoDurabilityStage.AfterTailPublication, true, true)]
    public async Task Every_wal_boundary_has_an_explicit_reopen_resolvable_outcome(
        RedoDurabilityStage stage, bool uncertain, bool presentAfterReopen)
    {
        DurableTransactionEnvelope transaction = Transaction(1, DatabaseProfileId.PropertyGraph);
        var observer = new ThrowOnceObserver(stage);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero), observer))
        {
            Exception failure;
            try
            {
                await log.CommitAsync(transaction);
                Assert.Fail("The injected durability boundary did not fail.");
                return;
            }
            catch (DatabaseCommitLogRecoveryRequiredException exception)
            {
                failure = exception;
            }
            Assert.AreEqual(uncertain, failure is RedoCommitUncertainException);
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        bool recovered = (await reopened.RecoverAsync()).Any(item => item.TransactionId == transaction.TransactionId);
        Assert.AreEqual(presentAfterReopen, recovered);
    }

    [DataTestMethod]
    [DataRow(RedoDurabilityStage.BeforeWalWrite, false)]
    [DataRow(RedoDurabilityStage.AfterWalFlush, true)]
    public async Task Failed_group_poisons_live_writer_until_reopen(RedoDurabilityStage stage,
        bool firstPresentAfterReopen)
    {
        DurableTransactionEnvelope first = Transaction(1, DatabaseProfileId.PropertyGraph);
        DurableTransactionEnvelope second = Transaction(1, DatabaseProfileId.PropertyGraph);
        var observer = new ThrowOnceObserver(stage);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero), observer))
        {
            await Assert.ThrowsAsync<DatabaseCommitLogRecoveryRequiredException>(
                () => log.CommitAsync(first).AsTask());
            await Assert.ThrowsAsync<DatabaseCommitLogRecoveryRequiredException>(
                () => log.CommitAsync(second).AsTask());
            await Assert.ThrowsExactlyAsync<PhysicalPageIoException>(async () =>
                await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath));
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        IReadOnlyList<DurableTransactionEnvelope> recovered = await reopened.RecoverAsync();
        Assert.AreEqual(firstPresentAfterReopen ? 1 : 0, recovered.Count);
        if (firstPresentAfterReopen)
        {
            Assert.AreEqual(first.TransactionId, recovered.Single().TransactionId);
        }
    }

    [TestMethod]
    public async Task Reclamation_keeps_only_the_checkpoint_tail()
    {
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupDelay: TimeSpan.Zero));
        for (int index = 1; index <= 5; index++)
        {
            await log.CommitAsync(Transaction(index, DatabaseProfileId.PropertyGraph));
        }

        var result = await log.ReclaimThroughAsync(new(3));

        Assert.AreEqual(3, result.RecordsRemoved);
        CollectionAssert.AreEqual(new[] { 4L, 5L }, (await log.RecoverAsync()).Select(item => item.Version.Value).ToArray());
    }

    [TestMethod]
    public async Task Empty_checkpoint_tail_retains_the_durable_lsn_floor_after_reopen()
    {
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitAsync(Transaction(1, DatabaseProfileId.PropertyGraph));
            await log.ReclaimThroughAsync(new(1));
            Assert.AreEqual(1, log.GetRedoStatus().DurableLsn);
            Assert.AreEqual(0, (await log.RecoverAsync()).Count);
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupDelay: TimeSpan.Zero));
        Assert.AreEqual(1, reopened.GetRedoStatus().DurableLsn);
        await reopened.CommitAsync(Transaction(2, DatabaseProfileId.PropertyGraph));
        Assert.AreEqual(2, reopened.GetRedoStatus().DurableLsn);
    }

    [TestMethod]
    public async Task Repeated_empty_tail_reclamation_coalesces_the_lsn_floor()
    {
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            for (int version = 1; version <= 20; version++)
            {
                await log.CommitAsync(Transaction(version, DatabaseProfileId.PropertyGraph));
                await log.ReclaimThroughAsync(new(version));
                Assert.AreEqual(version, log.GetRedoStatus().DurableLsn);
                Assert.AreEqual(RedoWalCodec.GroupMarker(version, 0).Length,
                    log.GetRedoStatus().EncodedWalBytes);
            }
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupDelay: TimeSpan.Zero));
        Assert.AreEqual(20, reopened.GetRedoStatus().DurableLsn);
        Assert.AreEqual(0, (await reopened.RecoverAsync()).Count);
        await reopened.CommitAsync(Transaction(21, DatabaseProfileId.PropertyGraph));
        Assert.AreEqual(21, reopened.GetRedoStatus().DurableLsn);
    }

    [TestMethod]
    public async Task First_page_mutation_protects_full_image_and_committed_patch_replays_idempotently()
    {
        var pageId = new PhysicalPageId(0);
        byte[] before = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, pageId).Encode();
        var mutation = new PhysicalPageRedoMutation(pageId, RedoWalTestOffsets.GenerationByte,
            new byte[] { 42 }, before);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitWithRedoAsync(Transaction(1, DatabaseProfileId.PropertyGraph), [mutation]);
        }

        string pagePath = Path.Combine(_directory!, "pages.efp");
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(pagePath))
            {
                await log.RecoverPagesAsync(pages);
                await log.RecoverPagesAsync(pages);
            }
        }
        await using PhysicalPageFile verified = await PhysicalPageFile.OpenAsync(pagePath);
        ReadOnlyMemory<byte> recovered = await verified.ReadAsync(pageId);
        Assert.AreEqual(42, recovered.Span[RedoWalTestOffsets.GenerationByte]);
        SlottedDataPage.Validate(recovered.Span, pageId);
    }

    [TestMethod]
    public async Task Newly_created_page_requires_no_blank_before_image_and_replays_from_its_after_image()
    {
        var pageId = new PhysicalPageId(0);
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, pageId);
        Assert.IsTrue(page.TryInsert(new byte[] { 4, 5, 6 }, 1, out _));
        DatabasePhysicalPageMutation mutation = DatabasePhysicalPageMutation.CreatePage(
            pageId.Value, page.Encode());
        Assert.IsFalse(mutation.PageExisted);
        Assert.AreEqual(0, mutation.BeforeImage.Length);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitWithPhysicalMutationsAsync(
                Transaction(1, DatabaseProfileId.Semantic), [mutation]);
        }

        string pagePath = Path.Combine(_directory!, "new-pages.efp");
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(pagePath))
            {
                await log.RecoverPagesAsync(pages);
            }
        }
        await using PhysicalPageFile verified = await PhysicalPageFile.OpenAsync(pagePath);
        SlottedDataPage.Validate((await verified.ReadAsync(pageId)).Span, pageId);
    }

    [TestMethod]
    public async Task Buffered_recovery_repairs_a_corrupt_page_from_a_durable_full_after_image()
    {
        var pageId = new PhysicalPageId(0);
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, pageId);
        Assert.IsTrue(page.TryInsert(new byte[] { 7, 8, 9 }, 1, out _));
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitWithPhysicalMutationsAsync(Transaction(1, DatabaseProfileId.Semantic),
                [DatabasePhysicalPageMutation.CreatePage(pageId.Value, page.Encode())]);
        }

        string pagePath = Path.Combine(_directory!, "corrupt-pages.efp");
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(pagePath))
        {
            await pages.EnsureCapacityAsync(1);
        }

        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await using (BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(pagePath))
            {
                await log.RecoverPagesAsync(pages);
            }
        }
        await using BufferedPhysicalPageStore verified = await BufferedPhysicalPageStore.OpenLocalAsync(pagePath);
        SlottedDataPage.Validate((await verified.ReadAsync(pageId)).Span, pageId);
    }

    [TestMethod]
    public async Task Compressible_page_redo_is_compact_and_recovers_exactly()
    {
        const int pageCount = 128;
        DatabasePhysicalPageMutation[] mutations = Enumerable.Range(0, pageCount)
            .Select(index => DatabasePhysicalPageMutation.CreatePage(index,
                new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(index)).Encode())).ToArray();
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitWithPhysicalMutationsAsync(Transaction(1, DatabaseProfileId.Semantic), mutations);
        }

        await using (var encoded = await InitializedRedoStream.OpenAsync(WalPath))
        {
            Assert.IsTrue(encoded.Length < (long)pageCount * PhysicalPageFileOptions.DefaultPageSize / 4);
        }

        string pagePath = Path.Combine(_directory!, "compressed-redo-pages.efp");
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath))
        {
            await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(pagePath))
            {
                await log.RecoverPagesAsync(pages);
            }
        }
        await using PhysicalPageFile verified = await PhysicalPageFile.OpenAsync(pagePath);
        for (int index = 0; index < pageCount; index++)
        {
            SlottedDataPage.Validate((await verified.ReadAsync(new(index))).Span, new(index));
        }
    }

    [TestMethod]
    public async Task Oversized_logical_transaction_is_segmented_and_recovers_once_atomically()
    {
        int pageCount = (RedoWalCodec.MaximumTransactionRecordBytes /
                         PhysicalPageFileOptions.DefaultPageSize) + 2;
        DatabasePhysicalPageMutation[] mutations = IncompressiblePageMutations(pageCount);
        DurableTransactionEnvelope transaction = Transaction(1, DatabaseProfileId.Semantic);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitWithPhysicalMutationsAsync(transaction, mutations);
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        IReadOnlyList<DurableTransactionEnvelope> recovered = await reopened.RecoverAsync();

        Assert.AreEqual(1, recovered.Count);
        Assert.AreEqual(transaction.TransactionId, recovered[0].TransactionId);
        Assert.IsTrue(new FileInfo(WalPath).Length > RedoWalCodec.MaximumTransactionRecordBytes);
    }

    [TestMethod]
    public async Task Explicit_ordered_submission_is_persisted_as_one_durable_group()
    {
        const int transactionCount = 8;
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupCount: transactionCount, maximumGroupDelay: TimeSpan.Zero));
        DatabasePhysicalCommit[] group = Enumerable.Range(1, transactionCount)
            .Select(index => new DatabasePhysicalCommit(Transaction(index, DatabaseProfileId.Semantic), []))
            .ToArray();

        await log.CommitGroupAsync(group);

        RedoDurabilityStatus status = log.GetRedoStatus();
        Assert.AreEqual(1L, status.DurableGroups);
        Assert.AreEqual(transactionCount, status.DurableTransactions);
        Assert.AreEqual(transactionCount, status.MaximumObservedGroupCount);
        CollectionAssert.AreEqual(Enumerable.Range(1, transactionCount).Select(value => (long)value).ToArray(),
            (await log.RecoverAsync()).Select(item => item.Version.Value).ToArray());
    }

    [TestMethod]
    public async Task Explicit_group_reports_every_member_as_uncertain_after_wal_write_failure()
    {
        const int transactionCount = 4;
        var observer = new ThrowOnceObserver(RedoDurabilityStage.AfterWalWrite);
        await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
            new(maximumGroupCount: transactionCount, maximumGroupDelay: TimeSpan.Zero), observer);
        DatabasePhysicalCommit[] group = Enumerable.Range(1, transactionCount)
            .Select(index => new DatabasePhysicalCommit(Transaction(index, DatabaseProfileId.PropertyGraph), []))
            .ToArray();

        await Assert.ThrowsAsync<RedoCommitUncertainException>(() => log.CommitGroupAsync(group).AsTask());
    }

    [TestMethod]
    public async Task Oversized_transaction_envelope_is_fragmented_and_recovers_once_atomically()
    {
        byte[] content = new byte[RedoWalCodec.MaximumTransactionRecordBytes];
        content[0] = 17;
        content[^1] = 29;
        var transaction = new DurableTransactionEnvelope(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new(1), GuidFrom(1),
            new(DatabaseProfileId.Semantic, "test.large-profile", 1, content),
            DurableCommittedChangeKind.System, null);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitAsync(transaction);
        }

        await using var reopened = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath);
        DurableTransactionEnvelope recovered = (await reopened.RecoverAsync()).Single();

        Assert.AreEqual(content.Length, recovered.Payload.Content.Length);
        Assert.AreEqual(17, recovered.Payload.Content.Span[0]);
        Assert.AreEqual(29, recovered.Payload.Content.Span[^1]);
        Assert.IsTrue(new FileInfo(WalPath).Length > RedoWalCodec.MaximumTransactionRecordBytes);
    }

    [TestMethod]
    public async Task Truncating_committed_initialized_capacity_is_not_repaired_as_an_empty_log()
    {
        int pageCount = (RedoWalCodec.MaximumTransactionRecordBytes /
                         PhysicalPageFileOptions.DefaultPageSize) + 2;
        DatabasePhysicalPageMutation[] mutations = IncompressiblePageMutations(pageCount);
        await using (var log = await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath,
                         new(maximumGroupDelay: TimeSpan.Zero)))
        {
            await log.CommitWithPhysicalMutationsAsync(
                Transaction(1, DatabaseProfileId.Semantic), mutations);
        }

        await using (var tail = new FileStream(WalPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            tail.SetLength(tail.Length - 1);
        }

        await Assert.ThrowsAsync<RedoWalCorruptionException>(async () =>
            await GroupedRedoDatabaseCommitLog.OpenAsync(WalPath));
        // Failed recovery must release both the selected WAL and its lease.
        using var walHandle = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var ownerHandle = new FileStream(WalPath + ".owner", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static DurableTransactionEnvelope Transaction(int version, DatabaseProfileId profile,
        bool withEffects = false)
    {
        CommittedChangePayload? effects = withEffects
            ? new("test.effects", 1, new byte[] { 7, 8, 9 }, 1,
                new([new CommittedChangeRoutingKey("profile", profile.ToString())], ["subject"]))
            : null;
        return new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new(version),
            GuidFrom(version), new(profile, "test.profile", 1, new byte[] { 1, 2, checked((byte)version) }),
            withEffects ? DurableCommittedChangeKind.UserDataWithChanges : DurableCommittedChangeKind.System,
            effects);
    }

    private static DatabasePhysicalPageMutation[] IncompressiblePageMutations(int pageCount)
    {
        var random = new Random(42);
        return Enumerable.Range(0, pageCount).Select(index =>
        {
            byte[] image = new byte[PhysicalPageFileOptions.DefaultPageSize];
            random.NextBytes(image);
            return DatabasePhysicalPageMutation.CreatePage(index, image);
        }).ToArray();
    }

    private static Guid GuidFrom(int value)
    {
        byte[] bytes = new byte[16];
        BitConverter.GetBytes(value).CopyTo(bytes, 0);
        return new(bytes);
    }

    private sealed class ThrowOnceObserver(RedoDurabilityStage target) : IRedoDurabilityObserver
    {
        private int _thrown;

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == target && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new IOException("Injected fault.");
            }
        }
    }

    private static class RedoWalTestOffsets
    {
        internal const int PayloadByte = 32;
        internal const int GenerationByte = 20;
        internal const int TransactionProfileCodecLength =
            RedoWalCodec.TransactionFragmentPrefixLength + DatabaseStorageLayout.EncodedLength + 16 + sizeof(long) + 16 + sizeof(byte);
    }
}
