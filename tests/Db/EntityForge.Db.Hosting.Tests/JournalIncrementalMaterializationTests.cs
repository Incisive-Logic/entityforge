using EntityForge.Db.Kernel;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class JournalIncrementalMaterializationTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ef-journal-deltas", Guid.NewGuid().ToString("N"));
    private readonly Guid _id = Guid.NewGuid();

    private CommittedChangeSet Change(int version) => new(_id, new(version), Guid.NewGuid(),
        DatabaseProfileId.PropertyGraph, new DatabaseChangeCursorCodec().Encode(_id, new(version)),
        new("test", 1, new byte[] { (byte)version }, 1));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, true);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Checkpoint_preparation_owns_visible_prefix_without_blocking_tail_or_losing_cancelled_work(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = new WalBackedDatabaseCommittedChangeStore(new(_path), maximumPendingChanges: 1024,
            beforeBackgroundMaterialization: async () =>
            {
                entered.TrySetResult();
                await release.Task;
            });
        var first = Change(1);
        var second = Change(2);
        await store.AppendAsync(first);
        await store.NotifyPublishedAsync(first.Cursor);
        using var cancellation = new CancellationTokenSource();
        Task preparation = store.PrepareCheckpointAsync(cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(preparation.IsCompleted);
            await store.AppendAsync(second).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(first.Cursor, (await store.GetBoundsAsync()).Current);
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => preparation);
            }
            release.TrySetResult();
            if (!cancel)
            {
                await preparation.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await store.FlushAsync();
            Assert.AreEqual(1, (await store.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
            await store.PublishThroughAsync(second.Cursor);
            await store.NotifyPublishedAsync(second.Cursor);
            Assert.AreEqual(2, (await store.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
            await store.FlushAsync();
            await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
            await reopened.NotifyPublishedAsync(second.Cursor);
            Assert.AreEqual(2, (await reopened.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Pending_lookup_indexes_follow_flush_replay_and_subsequent_publication()
    {
        var changes = Enumerable.Range(1, 3).Select(Change).ToArray();
        await using var store = new WalBackedDatabaseCommittedChangeStore(_path);
        await store.AppendAsync(changes[0]);
        await store.AppendAsync(changes[0]);
        await store.PublishThroughAsync(changes[0].Cursor);
        Assert.AreEqual(0, (await store.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
        await store.NotifyPublishedAsync(changes[0].Cursor);
        await store.FlushAsync();
        await store.AppendAsync(changes[0]);
        await store.AppendAsync(changes[1]);
        await store.NotifyPublishedAsync(changes[1].Cursor);
        await store.NotifyPublishedAsync(changes[0].Cursor);
        Assert.AreEqual(changes[1].Cursor, (await store.GetBoundsAsync()).Current);
        await store.FlushAsync();
        await store.AppendAsync(changes[2]);
        await store.NotifyPublishedAsync(changes[2].Cursor);
        CollectionAssert.AreEqual(changes.Select(change => change.TransactionId).ToArray(),
            (await store.ReadPageAfterAsync(null, new(), 10)).Changes.Select(change => change.TransactionId).ToArray());
    }

    [TestMethod]
    public async Task Delta_chain_consolidates_and_snapshot_reopens_without_delta_files()
    {
        var changes = Enumerable.Range(1, 70).Select(Change).ToArray();
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            foreach (var change in changes)
            {
                await store.AppendAsync(change);
            }

            await store.PublishThroughAsync(changes[^1].Cursor);
            await store.NotifyPublishedAsync(changes[^1].Cursor);
            Assert.IsTrue(Directory.GetFiles(_path, "*.efcd").Length <= 64);
            var snapshot = await store.CaptureSnapshotThroughAsync(changes[^1].Cursor);
            string copy = Path.Combine(_path, "copy");
            Directory.CreateDirectory(copy);
            foreach (var artifact in snapshot.Artifacts)
            {
                await File.WriteAllBytesAsync(Path.Combine(copy, artifact.Name), artifact.Content.ToArray());
            }

            await using var restored = new SegmentedDatabaseCommittedChangeStore(copy);
            await restored.NotifyPublishedAsync(changes[^1].Cursor);
            Assert.AreEqual(70, (await restored.ReadPageAfterAsync(null, new(), 100)).Changes.Count);
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await reopened.AppendBatchAsync(changes);
        await reopened.NotifyPublishedAsync(changes[^1].Cursor);
        var page = await reopened.ReadPageAfterAsync(null, new(), 100);
        CollectionAssert.AreEqual(changes.Select(c => c.TransactionId).ToArray(), page.Changes.Select(c => c.TransactionId).ToArray());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Missing_or_truncated_published_delta_fails_repeated_recovery(bool truncate)
    {
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            await store.AppendAsync(Change(1));
            await store.AppendAsync(Change(2));
        }
        string delta = Directory.GetFiles(_path, "*.efcd").Order().Last();
        if (truncate)
        {
            await File.WriteAllBytesAsync(delta, new byte[] { 1, 2 });
        }
        else
        {
            File.Delete(delta);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);

            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await reopened.GetBoundsAsync());
        }
    }

    [TestMethod]
    public async Task Consolidated_catalog_supersedes_the_old_head_after_delta_reclamation()
    {
        var changes = Enumerable.Range(1, 65).Select(Change).ToArray();
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            foreach (var change in changes)
            {
                await store.AppendAsync(change);
            }

            await store.PublishThroughAsync(changes[^1].Cursor);
            Assert.AreEqual(0, Directory.GetFiles(_path, "*.efcd").Length);
            Assert.IsTrue(File.Exists(Path.Combine(_path, SegmentedDatabaseCommittedChangeStore.CatalogHeadFileName)));
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await reopened.NotifyPublishedAsync(changes[^1].Cursor);
        Assert.AreEqual(65, (await reopened.ReadPageAfterAsync(null, new(), 100)).Changes.Count);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Packed_artifacts_are_admitted_once_and_failed_recovery_does_not_cache_admission(bool consolidated)
    {
        const int recordsPerBatch = 4;
        int batches = consolidated ? SegmentedDatabaseCommittedChangeStore.MaximumCatalogDeltas + 1 : 2;
        var changes = Enumerable.Range(1, batches * recordsPerBatch).Select(Change).ToArray();
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            foreach (var batch in changes.Chunk(recordsPerBatch))
            {
                await store.AppendBatchAsync(batch);
            }

            await store.PublishThroughAsync(changes[^1].Cursor);
        }
        string[] artifacts = Directory.GetFiles(_path, "*.efcb");
        Assert.AreEqual(batches, artifacts.Length);
        long checks = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Name == "entityforge.db.change_journal.recovery.artifact_checks")
            {
                active.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => Interlocked.Add(ref checks, value));
        listener.Start();
        await using (var reopened = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            await reopened.NotifyPublishedAsync(changes[^1].Cursor);
            Assert.AreEqual(changes[^1].Cursor, (await reopened.GetBoundsAsync()).Current);
            Assert.AreEqual((long)artifacts.Length, checks);
            var page = await reopened.ReadPageAfterAsync(null, new(), changes.Length);
            CollectionAssert.AreEqual(changes.Select(c => c.TransactionId).ToArray(), page.Changes.Select(c => c.TransactionId).ToArray());
        }
        string missing = artifacts.Order().Last();
        byte[] preserved = await File.ReadAllBytesAsync(missing);
        File.Delete(missing);
        await using var failed = new SegmentedDatabaseCommittedChangeStore(_path);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsExactlyAsync<EntityForgeDbFormatException>(async () => await failed.GetBoundsAsync());
        }

        await File.WriteAllBytesAsync(missing, preserved);
        await failed.NotifyPublishedAsync(changes[^1].Cursor);
        Assert.AreEqual(changes[^1].Cursor, (await failed.GetBoundsAsync()).Current);
    }

    [TestMethod]
    public async Task Background_prefix_does_not_block_next_append_but_barrier_drains_it()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = Enumerable.Range(1, 3).Select(Change).ToArray();
        await using var store = new WalBackedDatabaseCommittedChangeStore(new(_path), maximumPendingChanges: 4,
            beforeBackgroundMaterialization: async () =>
            {
                entered.TrySetResult();
                await release.Task;
            });
        try
        {
            foreach (var change in changes.Take(2))
            {
                await store.AppendAsync(change);
                await store.NotifyPublishedAsync(change.Cursor);
            }
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await store.AppendAsync(changes[2]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            await store.NotifyPublishedAsync(changes[2].Cursor);
            Assert.AreEqual(3, (await store.ReadPageAfterAsync(null, new(), 10)).Changes.Count);
            Task flush = store.FlushAsync().AsTask();
            Assert.IsFalse(flush.IsCompleted);
            release.TrySetResult();
            await flush.WaitAsync(TimeSpan.FromSeconds(10));
            var page = await store.ReadPageAfterAsync(null, new(), 10);
            CollectionAssert.AreEqual(changes.Select(c => c.TransactionId).ToArray(), page.Changes.Select(c => c.TransactionId).ToArray());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Background_publication_never_duplicates_or_exposes_the_unpublished_tail()
    {
        await using var store = new WalBackedDatabaseCommittedChangeStore(_path, maximumPendingChanges: 8);
        for (int version = 1; version <= 100; version++)
        {
            var change = Change(version);
            await store.AppendAsync(change);
            await store.PublishThroughAsync(change.Cursor);
            var before = await store.ReadPageAfterAsync(null, new(), 200);
            CollectionAssert.AreEqual(Enumerable.Range(1, version - 1).Select(v => (long)v).ToArray(),
                before.Changes.Select(c => c.Version.Value).ToArray());
            await store.NotifyPublishedAsync(change.Cursor);
        }
        await store.FlushAsync();
        Assert.AreEqual(100, (await store.ReadPageAfterAsync(null, new(), 200)).Changes.Count);
    }

    [TestMethod]
    public async Task Failed_consolidation_keeps_the_previous_chain_recoverable()
    {
        var changes = Enumerable.Range(1, 65).Select(Change).ToArray();
        string blocked = Path.Combine(_path, "manifest.efcm.new");
        await using (var store = new SegmentedDatabaseCommittedChangeStore(_path))
        {
            foreach (var change in changes.Take(64))
            {
                await store.AppendAsync(change);
            }

            Directory.CreateDirectory(blocked);
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () => await store.AppendAsync(changes[^1]));
            Directory.Delete(blocked);
            await Assert.ThrowsExactlyAsync<EntityForgeDbStorageException>(async () => await store.GetBoundsAsync());
        }
        await using var reopened = new SegmentedDatabaseCommittedChangeStore(_path);
        await reopened.AppendBatchAsync(changes);
        await reopened.PublishThroughAsync(changes[^1].Cursor);
        await reopened.NotifyPublishedAsync(changes[^1].Cursor);
        Assert.AreEqual(65, (await reopened.ReadPageAfterAsync(null, new(), 100)).Changes.Count);
    }

    [TestMethod]
    public async Task Background_failure_is_observed_by_barrier_and_shutdown()
    {
        var store = new WalBackedDatabaseCommittedChangeStore(new(_path), maximumPendingChanges: 2,
            beforeBackgroundMaterialization: () => ValueTask.FromException(new IOException("injected")));
        var change = Change(1);
        await store.AppendAsync(change);
        await store.NotifyPublishedAsync(change.Cursor);
        await Assert.ThrowsExactlyAsync<IOException>(async () => await store.FlushAsync());
        await Assert.ThrowsExactlyAsync<IOException>(async () => await store.GetBoundsAsync());
        await Assert.ThrowsExactlyAsync<AggregateException>(async () => await store.DisposeAsync());
    }
}
