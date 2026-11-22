using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class PrototypePageBufferPoolTests
{
    private const int PageSize = 8 * 1024;
    private const int CacheFrameCount = 4;
    private const int ScanFrameCount = 1;

    [TestMethod]
    public async Task Concurrent_misses_coalesce_into_one_source_read()
    {
        var source = new GatedPageSource();
        var pool = Pool(source);
        ValueTask<PrototypePageLease> first = pool.AcquireAsync(7);
        ValueTask<PrototypePageLease> second = pool.AcquireAsync(7);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Release.SetResult();

        using PrototypePageLease firstLease = await first;
        using PrototypePageLease secondLease = await second;
        Assert.AreEqual(1, source.ReadCount);
        Assert.AreEqual(1, pool.Snapshot().Loads);
    }

    [TestMethod]
    public async Task Cancelling_one_waiter_does_not_cancel_the_shared_page_load()
    {
        var source = new GatedPageSource();
        var pool = Pool(source);
        ValueTask<PrototypePageLease> surviving = pool.AcquireAsync(9);
        using var cancellation = new CancellationTokenSource();
        ValueTask<PrototypePageLease> cancelled = pool.AcquireAsync(9,
            cancellationToken: cancellation.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await cancelled.AsTask());
        source.Release.SetResult();

        using PrototypePageLease lease = await surviving;
        Assert.AreEqual(1, source.ReadCount);
        Assert.AreEqual(1, pool.Snapshot().Loads);
    }

    [TestMethod]
    public async Task Pinned_page_is_not_selected_as_eviction_victim()
    {
        var source = new DeterministicPrototypePageSource();
        var pool = Pool(source);
        using PrototypePageLease pinned = await pool.AcquireAsync(1);
        for (long pageId = 2; pageId <= CacheFrameCount + 2; pageId++)
        {
            using PrototypePageLease lease = await pool.AcquireAsync(pageId);
        }
        using PrototypePageLease pinnedAgain = await pool.AcquireAsync(1);

        Assert.IsTrue(pinnedAgain.WasCacheHit);
        Assert.IsTrue(pool.Snapshot().RejectedPinnedVictims > 0);
    }

    [TestMethod]
    public async Task Sequential_scan_does_not_displace_normal_working_set()
    {
        var source = new DeterministicPrototypePageSource();
        var pool = Pool(source);
        for (long pageId = 0; pageId < CacheFrameCount - ScanFrameCount; pageId++)
        {
            using PrototypePageLease lease = await pool.AcquireAsync(pageId);
        }
        for (long pageId = 100; pageId < 200; pageId++)
        {
            using PrototypePageLease lease = await pool.AcquireAsync(pageId,
                PrototypePageAccessKind.SequentialScan);
        }
        for (long pageId = 0; pageId < CacheFrameCount - ScanFrameCount; pageId++)
        {
            using PrototypePageLease lease = await pool.AcquireAsync(pageId);
            Assert.IsTrue(lease.WasCacheHit);
        }

        Assert.IsTrue(pool.Snapshot().ResidentBytes <= (long)PageSize * CacheFrameCount);
    }

    private static PrototypePageBufferPool Pool(IPrototypePageSource source) =>
        new(source, PageSize, (long)PageSize * CacheFrameCount, ScanFrameCount);

    private sealed class GatedPageSource : IPrototypePageSource
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(long pageId, int pageSize,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new byte[pageSize];
        }
    }
}
