using System.Collections.Concurrent;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalPageWritebackTests
{
    private const int PageSize = PhysicalPageFileOptions.DefaultPageSize;

    [TestMethod]
    public async Task Sequential_writeback_is_bounded_and_preserves_exact_ranges()
    {
        var ranges = new ConcurrentQueue<(long Offset, long Length)>();
        var writeback = new PhysicalPageWriteback((offset, length) =>
        {
            ranges.Enqueue((offset, length));
            return true;
        });
        const long start = 17L * PageSize;
        int pages = PhysicalPageWriteback.BatchBytes / PageSize;
        for (int i = 0; i < pages - 1; i++)
        {
            await writeback.WrittenAsync(start + i * PageSize, PageSize);
        }

        Assert.AreEqual(0, ranges.Count);
        await writeback.WrittenAsync(start + (pages - 1) * PageSize, PageSize);
        CollectionAssert.AreEqual(new[] { (start, (long)PhysicalPageWriteback.BatchBytes) }, ranges.ToArray());
        await writeback.WrittenAsync(start, PageSize);
        Assert.AreEqual(1, ranges.Count, "A new partial batch must not replay the preceding batch.");
    }

    [TestMethod]
    public async Task Sparse_writeback_does_not_expand_across_unwritten_gaps()
    {
        var ranges = new ConcurrentQueue<(long Offset, long Length)>();
        var writeback = new PhysicalPageWriteback((offset, length) =>
        {
            ranges.Enqueue((offset, length));
            return true;
        });
        var expected = new List<(long, long)>();
        for (int i = 0; i < PhysicalPageWriteback.MaximumRanges; i++)
        {
            long offset = i * (long)PhysicalPageWriteback.BatchBytes;
            expected.Add((offset, PageSize));
            await writeback.WrittenAsync(offset, PageSize);
        }
        CollectionAssert.AreEqual(expected.ToArray(), ranges.ToArray());
    }

    [TestMethod]
    public async Task Advisory_submission_is_awaited_and_io_failure_remains_visible()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new IOException("injected writeback failure");
        var writeback = new PhysicalPageWriteback((_, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            throw error;
        });
        Task submitted = writeback.WrittenAsync(0, PhysicalPageWriteback.BatchBytes).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(submitted.IsCompleted, "No unowned writeback task may escape the write.");
        }
        finally
        {
            release.Set();
        }
        Assert.AreSame(error, await Assert.ThrowsExactlyAsync<IOException>(() => submitted));
        Assert.AreSame(error, Assert.ThrowsExactly<IOException>(writeback.ThrowIfFailed));
        await Assert.ThrowsExactlyAsync<IOException>(async () => await writeback.WrittenAsync(0, PageSize));
    }

    [TestMethod]
    public async Task Unsupported_advice_is_disabled_without_claiming_durability()
    {
        int calls = 0;
        var writeback = new PhysicalPageWriteback((_, _) =>
        {
            calls++;
            return false;
        });
        await writeback.WrittenAsync(0, PhysicalPageWriteback.BatchBytes);
        await writeback.WrittenAsync(0, PhysicalPageWriteback.BatchBytes);
        writeback.ThrowIfFailed();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Buffered_writeback_spreads_pages_without_adding_or_removing_durable_barriers()
    {
        int batchPages = PhysicalPageWriteback.BatchBytes / PageSize;
        var source = new RecordingStore(batchPages * 2 + 1);
        await using var pages = new BufferedPhysicalPageStore(source, PhysicalPageWriteback.BatchBytes * 3L,
            new PhysicalPageTelemetry("test"), source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            await pages.WriteAsync(new(i), Image(i, 1));
            Assert.AreEqual((i + 1) / batchPages * batchPages, source.Writes);
            Assert.AreEqual(0, source.Flushes);
        }
        await pages.WriteCheckpointPagesAsync();
        Assert.AreEqual(source.Count, source.Writes);
        Assert.AreEqual(0, source.Flushes);
        // Re-dirty an earlier page after capture: the subsequent checkpoint must
        // still write its current image and execute the durable barrier.
        await pages.WriteAsync(new(0), Image(0, 2));
        await pages.WriteCheckpointPagesAsync();
        await pages.FlushCheckpointPagesAsync();
        Assert.AreEqual(1, source.Flushes);
        for (int i = 0; i < source.Count; i++)
        {
            CollectionAssert.AreEqual(Image(i, i == 0 ? 2 : 1), source.Images[i]);
        }
    }

    [TestMethod]
    public async Task Failed_and_cancelled_early_writeback_retains_dirty_pages_for_later_flush()
    {
        int count = PhysicalPageWriteback.BatchBytes / PageSize;
        var source = new RecordingStore(count) { FailAtWrite = count / 2 };
        await using var pages = new BufferedPhysicalPageStore(source, PhysicalPageWriteback.BatchBytes * 2L,
            new PhysicalPageTelemetry("test"), count);
        for (int i = 0; i < count - 1; i++)
        {
            await pages.WriteAsync(new(i), Image(i, 1));
        }

        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () => await pages.WriteAsync(new(count - 1), Image(count - 1, 1)));
        }
        finally
        {
            source.FailAtWrite = null;
        }
        Assert.IsTrue(pages.BufferSnapshot().DirtyPages > 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pages.WriteCheckpointPagesAsync(cancelled.Token));
        await pages.FlushAsync();
        Assert.AreEqual(0, pages.BufferSnapshot().DirtyPages);
        Assert.AreEqual(1, source.Flushes);
        for (int i = 0; i < count; i++)
        {
            CollectionAssert.AreEqual(Image(i, 1), source.Images[i]);
        }
    }

    [TestMethod]
    public async Task Physical_file_reopens_complete_images_after_multiple_advisory_batches()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-writeback-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "pages.dat");
        int count = PhysicalPageWriteback.BatchBytes / PageSize * 2 + 1;
        try
        {
            await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
            {
                await pages.EnsureCapacityAsync(count);
                for (int i = 0; i < count; i++)
                {
                    await pages.WriteAsync(new(i), Image(i, 1));
                }

                await pages.WriteAsync(new(0), Image(0, 2));
                await pages.FlushAsync();
            }
            await using PhysicalPageFile reopened = await PhysicalPageFile.OpenAsync(path);
            for (int i = 0; i < count; i++)
            {
                CollectionAssert.AreEqual(Image(i, i == 0 ? 2 : 1), (await reopened.ReadAsync(new(i))).ToArray());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public async Task Dirty_batch_orders_selected_pages_for_contiguous_advisory_writeback()
    {
        int count = PhysicalPageWriteback.BatchBytes / PageSize;
        var source = new RecordingStore(count);
        await using var pool = new PhysicalPageBufferPool(source, (count + 1L) * PageSize, scanFrames: 1);
        // Populate the frame cache in fragmented order, as free-page reuse can do.
        int[] order = Enumerable.Range(0, count).OrderBy(i => i % 2).ToArray();
        foreach (int id in order)
        {
            source.Images[id] = Image(id, 1);
            await using var lease = await pool.AcquireAsync(new(id));
            lease.MarkDirty(10);
        }
        await Assert.ThrowsExactlyAsync<PhysicalPageWalDependencyException>(async () => await pool.FlushDirtyAsync(9));
        Assert.AreEqual(0, source.Writes);
        Assert.AreEqual(0, source.Flushes);
        await pool.FlushDirtyAsync(10);
        CollectionAssert.AreEqual(Enumerable.Range(0, count).Select(i => (long)i).ToArray(), source.WriteOrder.ToArray());
        Assert.AreEqual(1, source.Flushes);
        Assert.AreEqual(0, pool.Snapshot().DirtyPages);
        var ranges = new List<(long Offset, long Length)>();
        var advisory = new PhysicalPageWriteback((offset, length) =>
        {
            ranges.Add((offset, length));
            return true;
        });
        foreach (long id in source.WriteOrder)
        {
            await advisory.WrittenAsync(id * PageSize, PageSize);
        }

        Assert.AreEqual(1, ranges.Count);
        Assert.AreEqual((0L, (long)PhysicalPageWriteback.BatchBytes), ranges[0]);
        foreach (int id in order)
        {
            CollectionAssert.AreEqual(Image(id, 1), source.Images[id]);
        }
    }

    private static byte[] Image(int id, int marker)
    {
        var page = new SlottedDataPage(PageSize, new(id));
        Assert.IsTrue(page.TryInsert(new[] { checked((byte)marker) }, marker, out _));
        return page.Encode();
    }

    private sealed class RecordingStore(int count) : IResizablePhysicalPageStore
    {
        public int Count => count;

        public int PageSize => PhysicalPageWritebackTests.PageSize;

        public Dictionary<long, byte[]> Images { get; } = [];

        public int Writes
        {
            get; private set;
        }

        public List<long> WriteOrder { get; } = [];

        public int Flushes
        {
            get; private set;
        }

        public int? FailAtWrite
        {
            get; set;
        }

        public ValueTask WriteAsync(PhysicalPageId id, ReadOnlyMemory<byte> page, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Writes == FailAtWrite)
            {
                throw new IOException("injected page write failure");
            }

            Images[id.Value] = page.ToArray();
            WriteOrder.Add(id.Value);
            Writes++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId id, CancellationToken token = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Images[id.Value]);

        public ValueTask FlushAsync(CancellationToken token = default)
        {
            Flushes++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<long> GetPageCountAsync(CancellationToken token = default) => ValueTask.FromResult((long)count);

        public ValueTask EnsureCapacityAsync(long pageCount, CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask TruncateAsync(long pageCount, CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
