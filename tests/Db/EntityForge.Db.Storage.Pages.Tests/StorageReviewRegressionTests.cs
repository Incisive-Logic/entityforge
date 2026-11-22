using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class StorageReviewRegressionTests
{
    [TestMethod]
    public async Task Paged_merge_matches_version_reducer_with_deletions_and_unequal_runs()
    {
        var pages = new MemoryPages();
        var codec = new AppendMergeRunCodec<int, int>(new IntCodec(), new IntCodec());
        var writer = new AppendMergeRunWriter<int, int>(codec, new());
        var allocation = new Allocator();
        var runs = new List<AppendMergeRunDescriptor>();
        var history = new List<AppendMergeRunEntry<int, int>>();
        void Add(IEnumerable<AppendMergeRunEntry<int, int>> entries, long generation)
        {
            var sorted = entries.OrderBy(e => e.Key).ThenBy(e => e.Value).ToArray();
            history.AddRange(sorted);
            var image = writer.Write(sorted, generation, allocation);
            runs.Insert(0, image.Descriptor);
            foreach (var page in image.Pages)
            {
                pages.Images[page.Key] = page.Value;
            }
        }
        Add(Enumerable.Range(1, 20).Select(key => new AppendMergeRunEntry<int, int>(AppendMergeMutationKind.Add, 1, key, 0)), 1);
        Add(Enumerable.Range(100, 20).Select(key => new AppendMergeRunEntry<int, int>(AppendMergeMutationKind.Add, 2, key, 0)), 2);
        Add(Enumerable.Range(1, 3).Select(key => new AppendMergeRunEntry<int, int>(AppendMergeMutationKind.Remove, 3, key, 0)), 3);
        using (var snapshot = new AppendMergeSnapshot<int, int>(codec, new(pages, codec), 3, runs.ToArray(),
            Comparer<int>.Default, Comparer<int>.Default, null, () =>
{

}))
        {
            CollectionAssert.AreEqual(new[] { 4, 5 }, (await snapshot.ReadPageAsync(2)).Items.Select(e => e.Key).ToArray());
        }

        var random = new Random(42);
        for (int version = 4; version <= 12; version++)
        {
            var mutations = Enumerable.Range(0, 70).Select(_ => (Key: random.Next(120), Value: random.Next(4)))
                .Distinct().Select(pair => new AppendMergeRunEntry<int, int>(random.Next(3) == 0
                    ? AppendMergeMutationKind.Remove : AppendMergeMutationKind.Add, version, pair.Key, pair.Value));
            Add(mutations, version);
        }
        foreach (long ceiling in new long[] { 3, 7, 12 })
        {
            foreach (int size in new[] { 1, 3, 17 })
            {
                var expected = history.Where(e => e.Version <= ceiling && e.Key >= 2)
                    .GroupBy(e => (e.Key, e.Value)).Select(group => group.MaxBy(e => e.Version))
                    .Where(e => e.Kind == AppendMergeMutationKind.Add).OrderBy(e => e.Key).ThenBy(e => e.Value)
                    .Select(e => new KeyValuePair<int, int>(e.Key, e.Value)).ToArray();
                using var snapshot = new AppendMergeSnapshot<int, int>(codec, new(pages, codec), 12, runs,
                    Comparer<int>.Default, Comparer<int>.Default, ceiling, () =>
{

});
                var actual = new List<KeyValuePair<int, int>>();
                AppendMergeCursor<int, int>? cursor = null;
                do
                {
                    var page = await snapshot.ReadPageAsync(size, OrderedIndexRange<int>.From(2), cursor);
                    actual.AddRange(page.Items);
                    Assert.IsTrue(actual.Count <= expected.Length);
                    cursor = page.NextCursor;
                } while (cursor is not null);
                CollectionAssert.AreEqual(expected, actual.ToArray());
                foreach (int key in new[] { 3, 20, 100 })
                {
                    CollectionAssert.AreEqual(expected.Where(entry => entry.Key == key).Take(size).Select(entry => entry.Value).ToArray(),
                        (await snapshot.FindAsync(key, maximumResults: size)).ToArray());
                }
            }
        }
    }

    [TestMethod]
    public async Task Distinct_loads_are_bounded_and_access_classes_share_the_same_load()
    {
        var pages = new DelayedPages();
        await using var pool = new PhysicalPageBufferPool(pages, 2 * pages.PageSize, 1);
        using var cancellation = new CancellationTokenSource();
        var first = pool.AcquireAsync(new(1), cancellationToken: cancellation.Token).AsTask();
        var same = pool.AcquireAsync(new(1), PageAccessClass.SequentialScan, cancellation.Token).AsTask();
        var others = Enumerable.Range(2, 100).Select(id => pool.AcquireAsync(new(id), cancellationToken: cancellation.Token).AsTask()).ToArray();
        Assert.AreEqual(2, pages.Reads);
        cancellation.Cancel();
        try
        {
            foreach (var task in others.Append(first).Append(same))
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.AreEqual(2, pages.Reads);
        }
        finally
        {
            pages.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Warm_decoding_avoids_page_copies_and_releases_latches_on_failure()
    {
        var pages = new MemoryPages();
        pages.Images[new(1)] = new SlottedDataPage(pages.PageSize, new(1), 1).EncodeOwned();
        await using var buffered = new BufferedPhysicalPageStore(pages, 4L * pages.PageSize, new(), 2);
        static int Decode(ReadOnlyMemory<byte> page) => page.Length;
        await PhysicalPageDecoding.ReadAsync(buffered, new(1), Decode, default);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            await PhysicalPageDecoding.ReadAsync(buffered, new(1), Decode, default);
        }

        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 2_000_000);
        await Assert.ThrowsAsync<IOException>(() => PhysicalPageDecoding.ReadAsync<int>(buffered, new(1), _ => throw new IOException(), default).AsTask());
        Assert.AreEqual(0, buffered.BufferSnapshot().PinnedPages);
        var owned = await buffered.ReadAsync(new(1));
        byte[] original = owned.ToArray();
        await buffered.WriteAsync(new(1), new SlottedDataPage(pages.PageSize, new(1), 2).EncodeOwned());
        CollectionAssert.AreEqual(original, owned.ToArray());
    }

    [TestMethod]
    public async Task Composite_cursor_seeks_forward_and_reverse_without_rescanning_prefix()
    {
        var pages = new MemoryPages();
        var codec = new OrderedPageTreeCodec<int, int>(new IntCodec(), new IntCodec());
        var leaves = new List<DiskResidentLeafDescriptor<int, int>>();
        for (int leaf = 0; leaf < 100; leaf++)
        {
            var id = new PhysicalPageId(leaf + 1);
            var entries = Enumerable.Range(leaf * 100, 100).Select(value => new OrderedLeafEntry<int, int>(1, value)).ToArray();
            pages.Images[id] = codec.EncodeLeaf(id, 1, null, null, entries, 1);
            leaves.Add(new(id, 1, leaf * 100, 100));
        }
        using var snapshot = new DiskResidentOrderedPageTreeSnapshot<int, int>(Guid.NewGuid(), new(1), 10_000,
            pages, codec, leaves, Comparer<int>.Default, Comparer<int>.Default, () =>
{

});
        foreach (var direction in new[] { OrderedScanDirection.Forward, OrderedScanDirection.Reverse })
        {
            pages.Reads = 0;
            var page = await snapshot.ReadPageAsync(50, direction: direction,
                cursor: new(snapshot.AccessPathId, new(1), 1, 8999, direction));
            Assert.IsTrue(pages.Reads <= 2, $"Read {pages.Reads} leaves");
            Assert.AreEqual(direction == OrderedScanDirection.Forward ? 9000 : 8998, page.Items[0].Value);
        }
    }

    [TestMethod]
    public void Catalog_reuses_unchanged_branches_and_reopens_combined_generation()
    {
        var codec = new DiskResidentOrderedCatalogCodec<int, int>(new IntCodec(), new IntCodec());
        var allocator = new Allocator();
        Guid id = Guid.NewGuid();
        var leaves = Enumerable.Range(1, 10_000).Select(i => new DiskResidentLeafDescriptor<int, int>(new(i), i, 0, 100)).ToArray();
        var initial = codec.Encode(id, 1, 1_000_000, leaves, [], allocator);
        var prior = initial.Branches.ToDictionary(p => p.Key, p => codec.DecodeBranch(p.Value.Span, p.Key));
        leaves[5000] = leaves[5000] with
        {
            EntryCount = 101
        };
        var next = codec.Encode(id, 2, 1_000_001, leaves, [], allocator, prior);
        Assert.AreEqual(1, next.Branches.Count);
        Assert.IsTrue(next.BranchIds.Intersect(initial.BranchIds).Count() >= initial.BranchIds.Count - 1);
        var decoded = next.Branches.ToDictionary(p => p.Key, p => codec.DecodeBranch(p.Value.Span, p.Key));
        var root = codec.DecodeRoot(next.Root.Span, next.RootPageId);
        CollectionAssert.AreEqual(leaves, root.Branches.SelectMany(branch => decoded.GetValueOrDefault(branch, prior.GetValueOrDefault(branch)!).Leaves).ToArray());
    }

    private sealed class IntCodec : IOrderedIndexCodec<int>
    {
        public byte[] Encode(int value) => BitConverter.GetBytes(value);

        public int Decode(ReadOnlySpan<byte> value) => BitConverter.ToInt32(value);
    }

    private sealed class Allocator : IPhysicalPageAllocator
    {
        private long _next = 20_000;

        public PhysicalPageId AllocatePage() => new(_next++);
    }

    private class MemoryPages : IResizablePhysicalPageStore
    {
        public Dictionary<PhysicalPageId, ReadOnlyMemory<byte>> Images { get; } = [];

        public int Reads;

        public int PageSize => PhysicalPageFileOptions.DefaultPageSize;

        public virtual ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId id, CancellationToken token = default)
        {
            Reads++;
            return ValueTask.FromResult(Images[id]);
        }

        public ValueTask WriteAsync(PhysicalPageId id, ReadOnlyMemory<byte> page, CancellationToken token = default)
        {
            Images[id] = page.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask<long> GetPageCountAsync(CancellationToken token = default) => ValueTask.FromResult((long)Images.Count);

        public ValueTask EnsureCapacityAsync(long count, CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask TruncateAsync(long count, CancellationToken token = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelayedPages : MemoryPages
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId id, CancellationToken token = default)
        {
            Reads++;
            await Release.Task;
            return new SlottedDataPage(PageSize, id, 1).EncodeOwned();
        }
    }
}
