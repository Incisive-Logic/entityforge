using EntityForge.Db.Durability;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalMaintenanceWorkTests
{
    [TestMethod]
    [Timeout(10000)]
    public async Task Timed_slices_finish_whole_batches_and_resume_until_all_work_is_done()
    {
        var clock = new MaintenanceClock();
        await using var work = new PhysicalMaintenanceWork(clock);
        var completed = new List<int>();
        work.Start(async _ =>
        {
            for (int batch = 0; batch < 3; batch++)
            {
                await work.ReserveAsync(256);
                clock.Advance(TimeSpan.FromMilliseconds(80)); // A batch can overrun; never interrupt it.
                completed.Add(batch);
            }
        });
        Assert.IsFalse(await work.AdvanceAsync(2048, maximumDuration: TimeSpan.FromMilliseconds(50)));
        CollectionAssert.AreEqual(new[] { 0 }, completed);
        Assert.IsFalse(await work.AdvanceAsync(2048, maximumDuration: TimeSpan.FromMilliseconds(50)));
        CollectionAssert.AreEqual(new[] { 0, 1 }, completed);
        Assert.IsTrue(await work.AdvanceAsync(2048, maximumDuration: TimeSpan.FromMilliseconds(50)));
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, completed);
    }

    private sealed class MaintenanceClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task Slices_stop_between_operations_and_cancel_without_unobserved_work()
    {
        await using var work = new PhysicalMaintenanceWork();
        int operations = 0;
        bool drained = false;
        work.Start(async token =>
        {
            try
            {
                while (true)
                {
                    await work.ReserveAsync(1);
                    token.ThrowIfCancellationRequested();
                    operations++;
                }
            }
            finally
            {
                drained = true;
            }
        });
        Assert.AreEqual(0, operations);
        Assert.IsFalse(await work.AdvanceAsync(256));
        Assert.AreEqual(256, operations);
        Assert.IsFalse(await work.AdvanceAsync(512));
        Assert.AreEqual(768, operations);
        await work.DisposeAsync();
        Assert.IsTrue(drained);
        Assert.AreEqual(768, operations);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task Failed_slice_is_observed_and_does_not_silently_report_completion()
    {
        var work = new PhysicalMaintenanceWork();
        work.Start(async _ =>
        {
            await work.ReserveAsync(1);
            throw new IOException("private failure");
        });
        await Assert.ThrowsAsync<IOException>(async () => await work.AdvanceAsync(256));
        Assert.IsFalse(work.IsCompleted);
        await work.DisposeAsync();
    }

    [TestMethod]
    public async Task Disposal_reports_a_failure_that_no_slice_observed()
    {
        var work = new PhysicalMaintenanceWork();
        work.Start(_ => Task.FromException(new IOException("unobserved failure")));
        await Assert.ThrowsAsync<IOException>(async () => await work.DisposeAsync());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_journal_or_flush_does_not_acknowledge_reused_pages(bool failFlush)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ef-private-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var pages = await PhysicalPageFile.OpenAsync(Path.Combine(directory, "data.pages"));
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = new PhysicalAccessPathId(Guid.NewGuid());
            var method = new PhysicalAccessMethodKind("private-failure", 1);
            var old = space.BeginTransaction(Guid.NewGuid());
            var id = old.OpenAccessPath(access, method, -1).AllocatePage();
            await pages.EnsureCapacityAsync(id.Value + 1);
            byte[] original = new SlottedDataPage(pages.PageSize, id).Encode();
            await pages.WriteAsync(id, original);
            space.Abandon(old.TransactionId);
            using var workspace = space.BeginReplacementWorkspace(Guid.NewGuid());
            Assert.AreEqual(id, workspace.AllocatePage());
            var store = new FlushFailurePages(pages, failFlush);
            await Assert.ThrowsAsync<IOException>(async () => await workspace.WriteJournaledAsync(store,
                [new(id, original)], (_, _) => failFlush ? ValueTask.CompletedTask : ValueTask.FromException(new IOException("journal failure"))));
            workspace.Complete(id);
            var publish = space.BeginTransaction(Guid.NewGuid());
            publish.OpenReplacementAccessPath(access, method, -1, workspace);
            var stage = await space.PrepareAsync(publish);
            Assert.IsTrue(stage.PhysicalMutations.Any(image => image.PageId == id.Value),
                "Unacknowledged reused bytes must still receive full protection at adoption.");
            space.Abandon(publish.TransactionId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FlushFailurePages(IResizablePhysicalPageStore inner, bool fail) : IResizablePhysicalPageStore
    {
        public int PageSize => inner.PageSize;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId id, CancellationToken token = default) => inner.ReadAsync(id, token);

        public ValueTask WriteAsync(PhysicalPageId id, ReadOnlyMemory<byte> bytes, CancellationToken token = default) => inner.WriteAsync(id, bytes, token);

        public ValueTask FlushAsync(CancellationToken token = default) => fail ? ValueTask.FromException(new IOException("flush failure")) : inner.FlushAsync(token);

        public ValueTask<long> GetPageCountAsync(CancellationToken token = default) => inner.GetPageCountAsync(token);

        public ValueTask EnsureCapacityAsync(long count, CancellationToken token = default) => inner.EnsureCapacityAsync(count, token);

        public ValueTask TruncateAsync(long count, CancellationToken token = default) => inner.TruncateAsync(count, token);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Journalled_reused_batches_recover_before_small_root_publication(bool checkpointBetweenSlices)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ef-incremental-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "data.pages"), walPath = Path.Combine(directory, "data.wal");
            await using var pages = await PhysicalPageFile.OpenAsync(path);
            await using var log = await GroupedRedoDatabaseCommitLog.OpenAsync(walPath, new(maximumGroupDelay: TimeSpan.Zero));
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var accessId = new PhysicalAccessPathId(Guid.NewGuid());
            var method = new PhysicalAccessMethodKind("incremental-test", 1);
            Guid incarnation = Guid.NewGuid();
            DurableTransactionEnvelope Envelope() => new(incarnation, new(1), Guid.NewGuid(),
                new(DatabaseProfileId.Semantic, "test", 1, ReadOnlyMemory<byte>.Empty), DurableCommittedChangeKind.CheckpointMaintenance, null);
            const int PageCount = 300;
            var unused = space.BeginTransaction(Guid.NewGuid());
            var allocation = unused.OpenAccessPath(accessId, method, -1);
            var ids = Enumerable.Range(0, PageCount).Select(_ => allocation.AllocatePage()).ToArray();
            await pages.EnsureCapacityAsync(ids.Max(p => p.Value) + 1);
            var old = ids.Select(id => DatabasePhysicalPageMutation.CreatePage(id.Value,
                new SlottedDataPage(pages.PageSize, id).Encode())).ToArray();
            await log.CommitWithPhysicalMutationsAsync(Envelope(), old);
            space.Abandon(unused.TransactionId);
            using var workspace = space.BeginReplacementWorkspace(Guid.NewGuid());
            var expected = new Dictionary<PhysicalPageId, byte[]>();
            for (int i = 0; i < PageCount; i++)
            {
                var id = workspace.AllocatePage();
                var page = new SlottedDataPage(pages.PageSize, id);
                Assert.IsTrue(page.TryInsert(BitConverter.GetBytes(i), 1, out _));
                expected.Add(id, page.Encode());
            }
            CollectionAssert.AreEquivalent(ids, expected.Keys.ToArray());
            var store = new PhysicalMaintenancePageStore(pages);
            await using var work = new PhysicalMaintenanceWork();
            int batches = 0;
            work.Start(async token =>
            {
                using var scope = store.Enter(work, async (mutations, cancellation) =>
                {
                    Assert.IsTrue(mutations.Count <= PhysicalMaintenanceWork.MaximumBatchPages);
                    await log.CommitWithPhysicalMutationsAsync(Envelope(), mutations, cancellation);
                    batches++;
                });
                using var writes = store.UseWorkspace(workspace);
                await store.WriteBatchAsync(expected.Select(p => new KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>(p.Key, p.Value)).ToArray(), token);
                workspace.Complete(expected.Keys.Last());
            });
            Assert.IsFalse(await work.AdvanceAsync(256));
            Assert.AreEqual(1, batches);
            var checkpointImages = new Dictionary<PhysicalPageId, byte[]>();
            if (checkpointBetweenSlices)
            {
                // A checkpoint captures the flushed private bytes even though no
                // access-path root owns them yet. Its covered redo can then expire.
                foreach (var id in ids)
                {
                    checkpointImages[id] = (await pages.ReadAsync(id)).ToArray();
                }

                await log.ReclaimCheckpointAsync(new(1), log.CaptureCheckpointPosition());
                Assert.ThrowsExactly<PhysicalPageSpaceException>(() => space.AuthorizeCheckpointPageReuse());
                space.AuthorizeCheckpointPageReuse(privateWorkspacesPaused: true);
                var foreground = space.BeginTransaction(Guid.NewGuid());
                var ordinaryPage = foreground.OpenAccessPath(new(Guid.NewGuid()), method, -1).AllocatePage();
                Assert.IsFalse(expected.ContainsKey(ordinaryPage), "Paused private reservations must remain unavailable to foreground allocation.");
                space.Abandon(foreground.TransactionId);
            }
            // Ordinary reads must not inherit the paused job's ambient budget.
            await store.ReadAsync(expected.Keys.First());
            Assert.IsTrue(await work.AdvanceAsync(256));
            Assert.AreEqual(2, batches);
            var publication = space.BeginTransaction(Guid.NewGuid());
            publication.OpenReplacementAccessPath(accessId, method, -1, workspace);
            var stage = await space.PrepareAsync(publication);
            Assert.IsFalse(stage.PhysicalMutations.Any(m => expected.ContainsKey(new(m.PageId))),
                "Earlier durable batches must cover reused pages without a second unbounded root-stage copy.");
            await log.CommitWithPhysicalMutationsAsync(Envelope(), stage.PhysicalMutations);
            // Crash before directory publication; an older WAL prefix overwrites
            // reused pages first, then the bounded batches restore exact new bytes.
            foreach (var mutation in old)
            {
                await pages.WriteAsync(new(mutation.PageId), checkpointBetweenSlices
                    ? checkpointImages[new(mutation.PageId)] : mutation.Bytes);
            }

            await log.RecoverPagesAsync(pages);
            var reopened = await PhysicalPageSpace.OpenAsync(pages);
            Assert.AreEqual(workspace.RootPage, reopened.CaptureDirectory().Get(accessId).RootPage);
            int index = 0;
            foreach (var item in expected)
            {
                byte[] recovered = (await pages.ReadAsync(item.Key)).ToArray();
                var decoded = SlottedDataPage.Decode(recovered, item.Key);
                Assert.AreEqual(index++ < PhysicalMaintenanceWork.MaximumBatchPages ? checkpointBetweenSlices ? 1L : 2L : 3L, decoded.PageLsn);
                var original = SlottedDataPage.Decode(item.Value, item.Key);
                original.Compact(decoded.PageLsn);
                CollectionAssert.AreEqual(original.Encode(), recovered);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
