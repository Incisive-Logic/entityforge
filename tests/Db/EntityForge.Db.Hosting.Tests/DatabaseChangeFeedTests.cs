using EntityForge.Db.Kernel;
using EntityForge.Db;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class DatabaseChangeFeedTests
{
    private const string ProfileCodec = "entityforge.test.changes";
    private const string EffectsCodec = "entityforge.test.effects";
    private const int CodecVersion = 1;
    private const int PageSize = 1;
    private string? _directory;

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Receipts_distinguish_changes_noop_and_idempotent_replay()
    {
        await using DatabaseKernel kernel = await OpenAsync();
        Guid changedId = Guid.NewGuid();
        DatabaseCommitReceipt changed = await kernel.CommitWithReceiptAsync(changedId, new byte[] { 7 });
        DatabaseCommitReceipt noChange = await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 0 });
        DatabaseCommitReceipt replay = await kernel.CommitWithReceiptAsync(changedId, new byte[] { 7 });

        Assert.AreEqual(DatabaseCommitOutcome.AppliedWithChanges, changed.Outcome);
        Assert.IsNotNull(changed.ChangeCursor);
        Assert.AreEqual(1, changed.EffectCount);
        Assert.AreEqual(DatabaseCommitOutcome.AppliedNoChange, noChange.Outcome);
        Assert.IsNull(noChange.ChangeCursor);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
        Assert.AreEqual(changed.ChangeCursor, replay.ChangeCursor);
        Assert.AreEqual(changed.DatabaseIncarnation, replay.DatabaseIncarnation);
    }

    [TestMethod]
    public async Task Feed_pages_in_commit_order_and_filters_by_routing_summary()
    {
        await using DatabaseKernel kernel = await OpenAsync();
        await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 1 });
        await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 2 });
        await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 3 });

        var selector = new DatabaseChangeSelector(DatabaseProfileId.PropertyGraph,
            [new("graph.subject", "subject:2")]);
        DatabaseChangePage selected = await kernel.ReadPageAfterAsync(null, selector, PageSize);
        Assert.AreEqual(1, selected.Changes.Count);
        Assert.AreEqual(2L, selected.Changes[0].Version.Value);
        Assert.IsFalse(selected.HasMore);
        Assert.AreEqual((await kernel.GetBoundsAsync()).Current, selected.NextCursor);

        DatabaseChangePage first = await kernel.ReadPageAfterAsync(null, new(), PageSize);
        DatabaseChangePage second = await kernel.ReadPageAfterAsync(first.NextCursor, new(), PageSize);
        Assert.AreEqual(1L, first.Changes.Single().Version.Value);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual(2L, second.Changes.Single().Version.Value);
        Assert.IsTrue(second.HasMore);
    }

    [TestMethod]
    public async Task Cursor_and_database_incarnation_survive_restart()
    {
        string logPath = CreatePath();
        DatabaseCommitReceipt first;
        await using (var kernel = await OpenAsync(logPath))
        {
            first = await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 1 });
            await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 2 });
        }

        await using DatabaseKernel reopened = await OpenAsync(logPath);
        DatabaseChangePage page = await reopened.ReadPageAfterAsync(first.ChangeCursor, new(), PageSize);
        Assert.AreEqual(first.DatabaseIncarnation, reopened.DatabaseIncarnation);
        Assert.AreEqual(2L, page.Changes.Single().Version.Value);
    }

    [TestMethod]
    public async Task Independent_journal_preserves_cursor_and_replay_receipt_after_wal_reclamation()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "kernel.checkpoint");
        Guid transactionId = Guid.NewGuid();
        DatabaseCommitReceipt original;
        await using (var kernel = await OpenAsync(logPath, checkpointPath))
        {
            original = await kernel.CommitWithReceiptAsync(transactionId, new byte[] { 4 });
            await kernel.CreateCheckpointAsync();
            DatabaseChangePage retained = await kernel.ReadPageAfterAsync(null, new(), PageSize);
            Assert.AreEqual(original.ChangeCursor, retained.Changes.Single().Cursor);
        }

        await using DatabaseKernel reopened = await OpenAsync(logPath, checkpointPath);
        DatabaseCommitReceipt replay = await reopened.CommitWithReceiptAsync(transactionId, new byte[] { 4 });
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Outcome);
        Assert.AreEqual(original.ChangeCursor, replay.ChangeCursor);
        Assert.AreEqual(original.EffectCount, replay.EffectCount);
    }

    [TestMethod]
    public async Task Subscription_waits_without_polling_and_wakes_for_matching_commit()
    {
        await using DatabaseKernel kernel = await OpenAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selector = new DatabaseChangeSelector(DatabaseProfileId.PropertyGraph,
            [new("graph.subject", "subject:8")]);
        await using IAsyncEnumerator<CommittedChangeSet> subscription = kernel.SubscribeAsync(null, selector,
            new(PageSize), cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        Assert.IsFalse(waiting.IsCompleted);
        await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 7 });
        Assert.IsFalse(waiting.IsCompleted);
        await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 8 });

        Assert.IsTrue(await waiting);
        Assert.AreEqual((byte)8, subscription.Current.Payload.Content.Span[0]);
    }

    [TestMethod]
    public async Task Subscription_does_not_observe_a_materialized_change_before_profile_publication()
    {
        string logPath = CreatePath();
        var runtime = new BlockingPublicationRuntime();
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            new SegmentedDatabaseCommittedChangeStore(logPath + ".changes"), runtime);
        await kernel.RecoverAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<CommittedChangeSet> subscription = kernel.SubscribeAsync(null, new(),
            new(PageSize), cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        Task<DatabaseCommitReceipt> commit = kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 5 })
            .AsTask();
        await runtime.ApplyStarted.Task.WaitAsync(cancellation.Token);

        Assert.IsFalse(waiting.IsCompleted,
            "A durably materialized change must remain invisible until its profile state is published.");
        runtime.AllowApply.TrySetResult();
        await commit;

        Assert.IsTrue(await waiting);
        Assert.IsTrue(runtime.Published);
        Assert.AreEqual(1L, subscription.Current.Version.Value);
    }

    [TestMethod]
    public async Task Subscription_does_not_deliver_a_change_before_kernel_publication_completes()
    {
        string logPath = CreatePath();
        var store = new BlockingNotificationChangeStore(
            new SegmentedDatabaseCommittedChangeStore(logPath + ".changes"));
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            store, new ChangeRuntime());
        await kernel.RecoverAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<CommittedChangeSet> subscription = kernel.SubscribeAsync(null, new(),
            new(PageSize), cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        Task<DatabaseCommitReceipt> commit = kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 5 })
            .AsTask();
        await store.Notified.Task.WaitAsync(cancellation.Token);
        Assert.IsFalse(waiting.IsCompleted);
        Assert.AreEqual(new DatabaseVersion(0), kernel.CurrentVersion);

        store.Release.TrySetResult();
        await commit;
        Assert.IsTrue(await waiting);
        Assert.AreEqual(new DatabaseVersion(1), kernel.CurrentVersion);
        Assert.AreEqual(new DatabaseVersion(1), subscription.Current.Version);
    }

    [TestMethod]
    public async Task Subscription_fails_closed_when_notification_fails_after_becoming_visible()
    {
        string logPath = CreatePath();
        var store = new BlockingNotificationChangeStore(
            new SegmentedDatabaseCommittedChangeStore(logPath + ".changes"), failAfterNotification: true);
        await using var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            store, new ChangeRuntime());
        await kernel.RecoverAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<CommittedChangeSet> subscription = kernel.SubscribeAsync(null, new(),
            new(PageSize), cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        Task<DatabaseCommitReceipt> commit = kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 6 })
            .AsTask();
        await store.Notified.Task.WaitAsync(cancellation.Token);
        Assert.IsFalse(waiting.IsCompleted);
        store.Release.TrySetResult();

        await Assert.ThrowsExceptionAsync<EntityForge.Db.Mapping.EntityOperationCommittedException<DatabaseCommitReceipt>>(async () => await commit);
        await Assert.ThrowsExceptionAsync<EntityForgeDbRecoveryRequiredException>(async () => await waiting);
    }

    [TestMethod]
    public async Task Independent_subscribers_resume_from_their_own_cursors()
    {
        await using DatabaseKernel kernel = await OpenAsync();
        DatabaseCommitReceipt first = await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 1 });
        await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 2 });
        await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 3 });

        DatabaseChangePage slower = await kernel.ReadPageAfterAsync(null, new(), PageSize);
        DatabaseChangePage faster = await kernel.ReadPageAfterAsync(first.ChangeCursor, new(),
            DatabaseChangeFeedLimits.MaximumPageSize);

        Assert.AreEqual(1L, slower.Changes.Single().Version.Value);
        CollectionAssert.AreEqual(new long[] { 2, 3 }, faster.Changes.Select(item => item.Version.Value).ToArray());
    }

    [TestMethod]
    public async Task Cancelled_waiting_subscription_terminates_promptly()
    {
        await using DatabaseKernel kernel = await OpenAsync();
        using var cancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<CommittedChangeSet> subscription = kernel.SubscribeAsync(null, new(),
            new(PageSize), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await waiting);
    }

    [TestMethod]
    public async Task Recovery_repairs_a_missing_derived_journal_while_authoritative_wal_is_retained()
    {
        string logPath = CreatePath();
        await using (var kernel = await OpenAsync(logPath))
        {
            await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 9 });
        }

        Directory.Delete(logPath + ".changes", true);

        await using DatabaseKernel reopened = await OpenAsync(logPath);
        DatabaseChangePage repaired = await reopened.ReadPageAfterAsync(null, new(), PageSize);
        Assert.AreEqual((byte)9, repaired.Changes.Single().Payload.Content.Span[0]);
    }

    [TestMethod]
    public async Task Recovery_fails_if_checkpoint_boundary_has_lost_its_independent_journal()
    {
        string logPath = CreatePath();
        string checkpointPath = Path.Combine(_directory!, "kernel.checkpoint");
        await using (var kernel = await OpenAsync(logPath, checkpointPath))
        {
            await kernel.CommitWithReceiptAsync(Guid.NewGuid(), new byte[] { 6 });
            await kernel.CreateCheckpointAsync();
        }
        Directory.Delete(logPath + ".changes", true);

        await Assert.ThrowsExceptionAsync<EntityForgeDbFormatException>(
            async () => await OpenAsync(logPath, checkpointPath));
    }

    [TestMethod]
    public async Task Segmented_retention_expires_old_cursors_and_reports_its_floor()
    {
        string path = CreatePath() + ".segments";
        var retention = new DatabaseChangeJournalRetentionOptions(
            maximumRetainedBytes: 1, minimumRetainedChangeCount: 1,
            minimumRetainedDuration: TimeSpan.Zero);
        await using var store = new SegmentedDatabaseCommittedChangeStore(path, retention);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        DatabaseChangeCursor first = default;
        for (long version = 1; version <= 3; version++)
        {
            DatabaseChangeCursor cursor = codec.Encode(incarnation, new(version));
            if (version == 1)
            {
                first = cursor;
            }

            await store.AppendAsync(new(incarnation, new(version), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                cursor, new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1,
                    new([new("graph.subject", $"subject:{version}")]))));
            await store.PublishThroughAsync(cursor);
            await store.NotifyPublishedAsync(cursor);
        }

        DatabaseChangeJournalStatus status = await store.GetStatusAsync();
        Assert.AreEqual(1, status.SegmentCount);
        Assert.IsNotNull(status.RetentionFloor);
        Assert.AreEqual(2L, status.CompactedRecords);
        EntityForgeChangeCursorExpiredException expired =
            await Assert.ThrowsExceptionAsync<EntityForgeChangeCursorExpiredException>(async () =>
                await store.ReadPageAfterAsync(first, new(), PageSize));
        Assert.AreEqual(status.PublishedCurrent, expired.Earliest);
    }

    [TestMethod]
    public async Task Visible_publication_is_monotonic_and_retention_preserves_a_prospective_frontier()
    {
        string path = CreatePath() + ".segments";
        var retention = new DatabaseChangeJournalRetentionOptions(
            maximumRetainedBytes: 1, minimumRetainedChangeCount: 1,
            minimumRetainedDuration: TimeSpan.Zero);
        await using var store = new SegmentedDatabaseCommittedChangeStore(path, retention);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        var cursors = new List<DatabaseChangeCursor>();
        for (long version = 1; version <= 3; version++)
        {
            DatabaseChangeCursor cursor = codec.Encode(incarnation, new(version));
            cursors.Add(cursor);
            await store.AppendAsync(new(incarnation, new(version), Guid.NewGuid(),
                DatabaseProfileId.PropertyGraph, cursor,
                new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1,
                    new([new("graph.subject", $"subject:{version}")]))));
        }
        await store.PublishThroughAsync(cursors[2]);

        await store.NotifyPublishedAsync(cursors[1]);
        Assert.AreEqual(cursors[1], (await store.GetBoundsAsync()).Current);
        Assert.AreEqual(2L, (await store.ReadPageAfterAsync(cursors[0], new(), PageSize))
            .Changes.Single().Version.Value);

        await Assert.ThrowsExactlyAsync<EntityForgeChangeCursorInvalidException>(async () =>
            await store.NotifyPublishedAsync(cursors[0]));
        Assert.AreEqual(cursors[1], (await store.GetBoundsAsync()).Current);
        await store.NotifyPublishedAsync(cursors[2]);
        Assert.AreEqual(cursors[2], (await store.GetBoundsAsync()).Current);
    }

    [TestMethod]
    public async Task Segmented_manifest_publication_tolerates_a_transient_reader_lock()
    {
        string path = CreatePath() + ".segments";
        await using var store = new SegmentedDatabaseCommittedChangeStore(path);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        async ValueTask AppendAsync(long version)
        {
            DatabaseChangeCursor cursor = codec.Encode(incarnation, new(version));
            await store.AppendAsync(new(incarnation, new(version), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                cursor, new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1)));
        }

        await AppendAsync(1);
        string manifest = Path.Combine(path, SegmentedDatabaseCommittedChangeStore.CatalogHeadFileName);
        using var reader = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            reader.Dispose();
        });

        await AppendAsync(2);
        await release;
        Assert.AreEqual(2, (await store.GetStatusAsync()).SegmentCount);
    }

    [TestMethod]
    public async Task Wal_backed_projection_drains_visible_changes_on_demand_and_reopens_durably()
    {
        string path = CreatePath() + ".wal-backed";
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        DatabaseChangeCursor cursor = codec.Encode(incarnation, new(1));
        var change = new CommittedChangeSet(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
            cursor, new(EffectsCodec, CodecVersion, new byte[] { 1, 2, 3 }, 1));

        await using (var store = new WalBackedDatabaseCommittedChangeStore(path))
        {
            await store.AppendAsync(change);
            await store.PublishThroughAsync(cursor);
            await store.NotifyPublishedAsync(cursor);

            DatabaseChangePage page = await store.ReadPageAfterAsync(null, new(), PageSize);
            Assert.AreEqual(1, page.Changes.Count);
            Assert.AreEqual(cursor, page.Changes.Single().Cursor);
        }

        await using var reopened = new SegmentedDatabaseCommittedChangeStore(path);
        await reopened.PublishThroughAsync(cursor);
        await reopened.NotifyPublishedAsync(cursor);
        Assert.AreEqual(1, (await reopened.ReadPageAfterAsync(null, new(), PageSize)).Changes.Count);
    }

    [TestMethod]
    public async Task Wal_backed_projection_flush_is_a_durable_checkpoint_barrier()
    {
        string path = CreatePath() + ".wal-backed";
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        DatabaseChangeCursor cursor = codec.Encode(incarnation, new(1));
        await using var store = new WalBackedDatabaseCommittedChangeStore(path);
        await store.AppendAsync(new(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
            cursor, new(EffectsCodec, CodecVersion, new byte[] { 4 }, 1)));
        await store.PublishThroughAsync(cursor);
        await store.NotifyPublishedAsync(cursor);

        await ((IDatabaseCommittedChangeDurabilityBarrier)store).FlushAsync();

        DatabaseChangeJournalStatus status = await store.GetStatusAsync();
        Assert.AreEqual(0L, status.MaterializationDebt);
        Assert.AreEqual(cursor, status.MaterializedCurrent);
        Assert.AreEqual(cursor, status.PublishedCurrent);
    }

    [TestMethod]
    public async Task Wal_backed_flush_preserves_the_exact_visible_frontier()
    {
        string path = CreatePath() + ".wal-visible";
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        CommittedChangeSet Change(long version) => new(incarnation, new(version), Guid.NewGuid(),
            DatabaseProfileId.PropertyGraph, codec.Encode(incarnation, new(version)),
            new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1));
        await using var store = new WalBackedDatabaseCommittedChangeStore(path);
        CommittedChangeSet first = Change(1), second = Change(2);
        await store.AppendAsync(first);
        await store.PublishThroughAsync(first.Cursor);
        await store.NotifyPublishedAsync(first.Cursor);
        await store.FlushAsync();
        await store.AppendAsync(second);

        DatabaseChangePage beforePublication = await store.ReadPageAfterAsync(first.Cursor, new(), PageSize);
        Assert.AreEqual(0, beforePublication.Changes.Count);
        Assert.AreEqual(first.Cursor, beforePublication.NextCursor);

        await store.PublishThroughAsync(second.Cursor);
        await store.NotifyPublishedAsync(second.Cursor);
        DatabaseChangePage afterPublication = await store.ReadPageAfterAsync(first.Cursor, new(), PageSize);
        Assert.AreEqual(second.Cursor, afterPublication.Changes.Single().Cursor);
    }

    [TestMethod]
    public async Task Wal_backed_visibility_adopts_the_committed_inner_frontier_after_cancellation()
    {
        string path = CreatePath() + ".wal-cancel";
        using var cancellation = new CancellationTokenSource();
        bool cancelAfterCommit = false;
        var durable = new SegmentedDatabaseCommittedChangeStore(path);
        await using var store = new WalBackedDatabaseCommittedChangeStore(durable,
            afterDurableVisibilityCommitted: () =>
            {
                if (cancelAfterCommit)
                {
                    cancellation.Cancel();
                }
            });
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        CommittedChangeSet Change(long version) => new(incarnation, new(version), Guid.NewGuid(),
            DatabaseProfileId.PropertyGraph, codec.Encode(incarnation, new(version)),
            new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1));
        CommittedChangeSet first = Change(1), second = Change(2);
        await store.AppendAsync(first);
        await store.PublishThroughAsync(first.Cursor);
        await store.NotifyPublishedAsync(first.Cursor);
        await store.FlushAsync();
        await store.AppendAsync(second);
        await store.FlushAsync();
        await store.PublishThroughAsync(second.Cursor);
        cancelAfterCommit = true;

        await store.NotifyPublishedAsync(second.Cursor, cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(second.Cursor, (await store.GetBoundsAsync()).Current);
        Assert.AreEqual(second.Cursor,
            (await store.ReadPageAfterAsync(first.Cursor, new(), 2)).Changes.Single().Cursor);
        await store.NotifyPublishedAsync(first.Cursor);
        Assert.AreEqual(second.Cursor, (await store.GetBoundsAsync()).Current);
        using var subscriptionCancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<CommittedChangeSet> subscription = store.SubscribeAsync(
            second.Cursor, new(), new(2), subscriptionCancellation.Token)
            .GetAsyncEnumerator(subscriptionCancellation.Token);
        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(waiting.IsCompleted);
        subscriptionCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting);
    }

    [TestMethod]
    public async Task Delayed_durable_notification_cannot_hide_a_newer_visible_pending_change()
    {
        string path = CreatePath() + ".wal-pending-frontier";
        await using var store = new WalBackedDatabaseCommittedChangeStore(path);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        CommittedChangeSet Change(long version) => new(incarnation, new(version), Guid.NewGuid(),
            DatabaseProfileId.PropertyGraph, codec.Encode(incarnation, new(version)),
            new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1));
        CommittedChangeSet first = Change(1), second = Change(2);
        await store.AppendAsync(first);
        await store.PublishThroughAsync(first.Cursor);
        await store.NotifyPublishedAsync(first.Cursor);
        await store.FlushAsync();
        await store.AppendAsync(second);
        await store.PublishThroughAsync(second.Cursor);
        await store.NotifyPublishedAsync(second.Cursor);

        await store.NotifyPublishedAsync(first.Cursor);

        Assert.AreEqual(second.Cursor, (await store.GetBoundsAsync()).Current);
        DatabaseChangePage page = await store.ReadPageAfterAsync(first.Cursor, new(), 2);
        Assert.HasCount(1, page.Changes);
        Assert.AreEqual(second.Cursor, page.Changes.Single().Cursor);
        Assert.IsFalse(page.HasMore);
        using var subscriptionCancellation = new CancellationTokenSource();
        await using IAsyncEnumerator<CommittedChangeSet> subscription = store.SubscribeAsync(
            second.Cursor, new(), new(2), subscriptionCancellation.Token)
            .GetAsyncEnumerator(subscriptionCancellation.Token);
        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        await Task.Delay(50);
        Assert.IsFalse(waiting.IsCompleted);
        subscriptionCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await waiting);

        await store.FlushAsync();
        Assert.AreEqual(second.Cursor, (await store.GetBoundsAsync()).Current);
        Assert.AreEqual(0L, (await store.GetStatusAsync()).MaterializationDebt);
    }

    [TestMethod]
    public async Task Deferred_artifact_cleanup_does_not_duplicate_a_wal_backed_change()
    {
        string path = CreatePath() + ".wal-cleanup";
        bool failDelete = true;
        var retention = new DatabaseChangeJournalRetentionOptions(1, 1, TimeSpan.Zero);
        var durable = new SegmentedDatabaseCommittedChangeStore(path, retention, null, null,
            name =>
            {
                if (failDelete)
                {
                    failDelete = false;
                    throw new IOException("simulated delete lock");
                }
                File.Delete(Path.Combine(path, name));
            });
        await using var store = new WalBackedDatabaseCommittedChangeStore(durable);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        CommittedChangeSet Change(long version) => new(incarnation, new(version), Guid.NewGuid(),
            DatabaseProfileId.PropertyGraph, codec.Encode(incarnation, new(version)),
            new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1));
        CommittedChangeSet first = Change(1), second = Change(2);
        foreach (CommittedChangeSet change in new[] { first, second })
        {
            await store.AppendAsync(change);
            await store.PublishThroughAsync(change.Cursor);
            await store.NotifyPublishedAsync(change.Cursor);
            await store.FlushAsync();
        }

        DatabaseChangePage page = await store.ReadPageAfterAsync(null, new(), 10);
        Assert.HasCount(1, page.Changes);
        Assert.AreEqual(second.Cursor, page.Changes.Single().Cursor);
        Assert.IsFalse(page.HasMore);
        Assert.AreEqual(0L, (await store.GetStatusAsync()).MaterializationDebt);
    }

    [TestMethod]
    public async Task Admitted_wal_snapshot_capture_completes_before_disposal()
    {
        string path = CreatePath() + ".wal-snapshot-dispose";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var durable = new SegmentedDatabaseCommittedChangeStore(path);
        var store = new WalBackedDatabaseCommittedChangeStore(durable,
            beforeDurableSnapshotCapture: async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            });

        Task<DatabaseChangeJournalSnapshot> capture = store.CaptureSnapshotThroughAsync(null).AsTask();
        await entered.Task;
        Task disposal = store.DisposeAsync().AsTask();
        Assert.IsFalse(disposal.IsCompleted);
        release.TrySetResult();

        _ = await capture;
        await disposal;
    }

    [TestMethod]
    public async Task Failed_wal_shutdown_terminates_subscribers_disposes_inner_store_and_is_shared()
    {
        string path = CreatePath() + ".wal-failed-dispose";
        var durable = new SegmentedDatabaseCommittedChangeStore(path);
        var store = new WalBackedDatabaseCommittedChangeStore(durable,
            shutdownFlushFailure: () => ValueTask.FromException(new IOException("simulated flush failure")));
        using var subscriptionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using IAsyncEnumerator<CommittedChangeSet> subscription = store.SubscribeAsync(
            null, new(), new(2), subscriptionCancellation.Token)
            .GetAsyncEnumerator(subscriptionCancellation.Token);
        Task<bool> waiting = subscription.MoveNextAsync().AsTask();
        await Task.Delay(50);

        Task first = store.DisposeAsync().AsTask();
        Task second = store.DisposeAsync().AsTask();
        Assert.AreSame(first, second);
        await Assert.ThrowsExactlyAsync<AggregateException>(async () => await first);
        await Assert.ThrowsExactlyAsync<AggregateException>(async () => await second);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await waiting);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await durable.GetStatusAsync());
    }

    [TestMethod]
    public async Task Wal_backed_recovery_does_not_resurrect_retained_away_changes()
    {
        string path = CreatePath() + ".wal-retention";
        var retention = new DatabaseChangeJournalRetentionOptions(1, 1, TimeSpan.Zero);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        CommittedChangeSet[] changes = Enumerable.Range(1, 3).Select(value =>
            new CommittedChangeSet(incarnation, new(value), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                codec.Encode(incarnation, new(value)),
                new(EffectsCodec, CodecVersion, new byte[] { (byte)value }, 1))).ToArray();
        await using (var store = new WalBackedDatabaseCommittedChangeStore(path, retention))
        {
            foreach (CommittedChangeSet change in changes)
            {
                await store.AppendAsync(change);
                await store.PublishThroughAsync(change.Cursor);
                await store.NotifyPublishedAsync(change.Cursor);
                await store.FlushAsync();
            }
        }

        await using var reopened = new WalBackedDatabaseCommittedChangeStore(path, retention);
        foreach (CommittedChangeSet change in changes)
        {
            await reopened.AppendAsync(change);
        }

        await reopened.PublishThroughAsync(changes[^1].Cursor);
        await reopened.NotifyPublishedAsync(changes[^1].Cursor);
        DatabaseChangePage retained = await reopened.ReadPageAfterAsync(null, new(), PageSize);
        Assert.AreEqual(1, retained.Changes.Count);
        Assert.AreEqual(changes[^1].Cursor, retained.Changes.Single().Cursor);
        await Assert.ThrowsExceptionAsync<EntityForgeChangeCursorExpiredException>(async () =>
            await reopened.ReadPageAfterAsync(changes[0].Cursor, new(), PageSize));
        Assert.AreEqual(0L, (await reopened.GetStatusAsync()).MaterializationDebt);
    }

    [TestMethod]
    public async Task Recovery_materialization_does_not_resurrect_intentionally_retained_away_changes()
    {
        string path = CreatePath() + ".segments";
        var retention = new DatabaseChangeJournalRetentionOptions(1, 1, TimeSpan.Zero);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        CommittedChangeSet[] changes = Enumerable.Range(1, 3).Select(value =>
        {
            DatabaseChangeCursor cursor = codec.Encode(incarnation, new(value));
            return new CommittedChangeSet(incarnation, new(value), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                cursor, new(EffectsCodec, CodecVersion, new byte[] { (byte)value }, 1));
        }).ToArray();
        await using (var store = new SegmentedDatabaseCommittedChangeStore(path, retention))
        {
            foreach (CommittedChangeSet change in changes)
            {
                await store.AppendAsync(change);
                await store.PublishThroughAsync(change.Cursor);
                await store.NotifyPublishedAsync(change.Cursor);
            }
        }

        await using var reopened = new SegmentedDatabaseCommittedChangeStore(path, retention);
        await reopened.AppendAsync(changes[0]);
        await reopened.PublishThroughAsync(changes[^1].Cursor);
        await reopened.NotifyPublishedAsync(changes[^1].Cursor);
        DatabaseChangeJournalStatus status = await reopened.GetStatusAsync();

        Assert.AreEqual(1, status.SegmentCount);
        Assert.AreEqual(changes[^1].Cursor, status.PublishedCurrent);
    }

    [TestMethod]
    public async Task Selective_segment_read_uses_routing_index_without_decoding_non_matches()
    {
        string path = CreatePath() + ".segments";
        await using var store = new SegmentedDatabaseCommittedChangeStore(path);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        for (long version = 1; version <= 3; version++)
        {
            DatabaseChangeCursor cursor = codec.Encode(incarnation, new(version));
            await store.AppendAsync(new(incarnation, new(version), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                cursor, new(EffectsCodec, CodecVersion, new byte[] { (byte)version }, 1,
                    new([new("graph.subject", $"subject:{version}")]))));
            await store.PublishThroughAsync(cursor);
            await store.NotifyPublishedAsync(cursor);
        }

        DatabaseChangePage page = await store.ReadPageAfterAsync(null,
            new(DatabaseProfileId.PropertyGraph, [new("graph.subject", "subject:2")]), PageSize);
        DatabaseChangeJournalStatus status = await store.GetStatusAsync();

        Assert.AreEqual(2L, page.Changes.Single().Version.Value);
        Assert.AreEqual(1L, status.DecodedRecords);
        Assert.AreEqual(3L, status.IndexedCandidates);
    }

    [TestMethod]
    public async Task Segment_page_read_reports_duration_and_scan_amplification()
    {
        string path = CreatePath() + ".segments";
        using var telemetry = new DatabaseChangeJournalTelemetry();
        var measurements = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == DatabaseChangeJournalTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
            measurements.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
            measurements.Add(instrument.Name));
        listener.Start();
        await using var store = new SegmentedDatabaseCommittedChangeStore(path, telemetry: telemetry);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        DatabaseChangeCursor cursor = codec.Encode(incarnation, new(1));
        await store.AppendAsync(new(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
            cursor, new(EffectsCodec, CodecVersion, new byte[] { 1 }, 1)));
        await store.PublishThroughAsync(cursor);
        await store.NotifyPublishedAsync(cursor);

        await store.ReadPageAfterAsync(null, new(DatabaseProfileId.PropertyGraph), PageSize);

        CollectionAssert.Contains(measurements, "entityforge.db.change_journal.page_read.duration");
        CollectionAssert.Contains(measurements, "entityforge.db.change_journal.page_read.scanned_records");
        CollectionAssert.Contains(measurements, "entityforge.db.change_journal.page_read.returned_records");
    }

    [TestMethod]
    public async Task Selective_segment_read_conservatively_includes_an_overflowed_routing_kind()
    {
        string path = CreatePath() + ".segments";
        await using var store = new SegmentedDatabaseCommittedChangeStore(path);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        DatabaseChangeCursor cursor = codec.Encode(incarnation, new(1));
        var routing = new CommittedChangeRoutingSummary(
            [new("graph.label", "Company")], ["graph.subject"]);
        await store.AppendAsync(new(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
            cursor, new(EffectsCodec, CodecVersion, new byte[] { 1 }, 1, routing)));
        await store.PublishThroughAsync(cursor);
        await store.NotifyPublishedAsync(cursor);

        DatabaseChangePage possibleMatch = await store.ReadPageAfterAsync(null,
            new(DatabaseProfileId.PropertyGraph, [new("graph.subject", "subject:not-listed")]), PageSize);
        DatabaseChangePage definiteMiss = await store.ReadPageAfterAsync(null,
            new(DatabaseProfileId.PropertyGraph, [new("graph.object", "object:not-listed")]), PageSize);

        Assert.AreEqual(1, possibleMatch.Changes.Count);
        Assert.AreEqual(0, definiteMiss.Changes.Count);
        CollectionAssert.AreEqual(new[] { "graph.subject" },
            possibleMatch.Changes.Single().Payload.Routing.OverflowedKinds.ToArray());
    }

    [TestMethod]
    public async Task Oversized_change_materialization_fails_atomically_and_the_journal_remains_usable()
    {
        string path = CreatePath() + ".segments";
        await using var store = new SegmentedDatabaseCommittedChangeStore(path);
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        DatabaseChangeCursor cursor = codec.Encode(incarnation, new(1));
        var oversizedPayload = new CommittedChangePayload(EffectsCodec, CodecVersion,
            new byte[DatabaseChangeFeedLimits.MaximumCommittedChangePayloadBytes + 1], 1);

        await Assert.ThrowsExceptionAsync<EntityForgeDbStorageException>(async () =>
            await store.AppendAsync(new(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                cursor, oversizedPayload)));
        Assert.AreEqual(0, (await store.GetStatusAsync()).SegmentCount);

        var validPayload = new CommittedChangePayload(
            EffectsCodec, CodecVersion, new byte[] { 1 }, 1);
        await store.AppendAsync(new(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
            cursor, validPayload));
        await store.PublishThroughAsync(cursor);
        await store.NotifyPublishedAsync(cursor);

        Assert.AreEqual(1, (await store.ReadPageAfterAsync(null, new(), PageSize)).Changes.Count);
    }

    [TestMethod]
    public async Task Corrupt_published_routing_index_fails_selective_reads_closed()
    {
        string path = CreatePath() + ".segments";
        var codec = new DatabaseChangeCursorCodec();
        Guid incarnation = Guid.NewGuid();
        DatabaseChangeCursor cursor = codec.Encode(incarnation, new(1));
        await using (var store = new SegmentedDatabaseCommittedChangeStore(path))
        {
            await store.AppendAsync(new(incarnation, new(1), Guid.NewGuid(), DatabaseProfileId.PropertyGraph,
                cursor, new(EffectsCodec, CodecVersion, new byte[] { 1 }, 1,
                    new([new("graph.subject", "subject:1")]))));
            await store.PublishThroughAsync(cursor);
            await store.NotifyPublishedAsync(cursor);
        }
        string index = Directory.EnumerateFiles(path, "*.efcb").Single();
        byte[] corrupt = await File.ReadAllBytesAsync(index);
        corrupt[0] ^= byte.MaxValue;
        await File.WriteAllBytesAsync(index, corrupt);

        await using var reopened = new SegmentedDatabaseCommittedChangeStore(path);
        await reopened.PublishThroughAsync(cursor);
        await reopened.NotifyPublishedAsync(cursor);
        await Assert.ThrowsExceptionAsync<EntityForgeDbFormatException>(async () =>
            await reopened.ReadPageAfterAsync(null,
                new(DatabaseProfileId.PropertyGraph, [new("graph.subject", "subject:1")]), PageSize));
    }

    private ValueTask<DatabaseKernel> OpenAsync() => OpenAsync(CreatePath());

    private static async ValueTask<DatabaseKernel> OpenAsync(string logPath, string? checkpointPath = null)
    {
        var kernel = new DatabaseKernel(await GroupedRedoDatabaseCommitLog.OpenAsync(logPath),
            new SegmentedDatabaseCommittedChangeStore(logPath + ".changes"), new ChangeRuntime(),
            checkpointStore: checkpointPath is null ? null : new FileDatabaseCheckpointStore(checkpointPath));
        try
        {
            await kernel.RecoverAsync();
            return kernel;
        }
        catch
        {
            await kernel.DisposeAsync();
            throw;
        }
    }

    private string CreatePath()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-change-feed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, "kernel.wal");
    }

    private sealed class ChangeRuntime : ICheckpointableDatabaseProfileRuntime, IDatabaseProfileChangePlanner
    {
        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => ProfileCodec;

        public int CodecVersion => DatabaseChangeFeedTests.CodecVersion;

        public string CheckpointCodecId => "entityforge.test.changes.checkpoint";

        public int CheckpointCodecVersion => DatabaseChangeFeedTests.CodecVersion;

        public ValueTask<DatabaseProfileChangePlan> PlanCommittedChangesAsync(DatabaseVersion version,
            Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte value = payload.Span[0];
            return ValueTask.FromResult(value == 0
                ? DatabaseProfileChangePlan.UserData(null)
                : DatabaseProfileChangePlan.UserData(new(EffectsCodec, CodecVersion, new byte[] { value }, 1,
                    new([new("graph.subject", $"subject:{value}")]))));
        }

        public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Array.Empty<byte>());

        public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class BlockingPublicationRuntime : IDatabaseProfileRuntime, IDatabaseProfileChangePlanner
    {
        public TaskCompletionSource ApplyStarted
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowApply
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Published
        {
            get; private set;
        }

        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => ProfileCodec;

        public int CodecVersion => DatabaseChangeFeedTests.CodecVersion;

        public ValueTask<DatabaseProfileChangePlan> PlanCommittedChangesAsync(DatabaseVersion version,
            Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DatabaseProfileChangePlan.UserData(new(EffectsCodec, CodecVersion,
                payload, 1, new([new("graph.subject", "blocked")]))));

        public async ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId,
            ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            ApplyStarted.TrySetResult();
            await AllowApply.Task.ConfigureAwait(false);
            Published = true;
        }
    }

    private sealed class BlockingNotificationChangeStore(
        IDatabaseCommittedChangeStore inner, bool failAfterNotification = false) : IDatabaseCommittedChangeStore
    {
        public TaskCompletionSource Notified
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask AppendAsync(CommittedChangeSet changeSet,
            CancellationToken cancellationToken = default) => inner.AppendAsync(changeSet, cancellationToken);

        public ValueTask PublishThroughAsync(DatabaseChangeCursor cursor,
            CancellationToken cancellationToken = default) => inner.PublishThroughAsync(cursor, cancellationToken);

        public async ValueTask NotifyPublishedAsync(DatabaseChangeCursor cursor,
            CancellationToken cancellationToken = default)
        {
            await inner.NotifyPublishedAsync(cursor, cancellationToken);
            Notified.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (failAfterNotification)
            {
                throw new IOException("Injected notification failure.");
            }
        }

        public ValueTask<DatabaseChangeFeedBounds> GetBoundsAsync(
            CancellationToken cancellationToken = default) => inner.GetBoundsAsync(cancellationToken);

        public ValueTask<DatabaseChangePage> ReadPageAfterAsync(DatabaseChangeCursor? cursor,
            DatabaseChangeSelector selector, int maximumCount,
            CancellationToken cancellationToken = default) =>
            inner.ReadPageAfterAsync(cursor, selector, maximumCount, cancellationToken);

        public IAsyncEnumerable<CommittedChangeSet> SubscribeAsync(DatabaseChangeCursor? cursor,
            DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
            CancellationToken cancellationToken = default) =>
            inner.SubscribeAsync(cursor, selector, options, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
