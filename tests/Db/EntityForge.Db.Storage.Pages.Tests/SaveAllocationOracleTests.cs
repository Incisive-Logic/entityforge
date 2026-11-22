namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class SaveAllocationOracleTests
{
    private sealed class Allocator : IPhysicalPageAllocator
    {
        private long _next = 100_000;

        public PhysicalPageId AllocatePage() => new(_next++);
    }

    private static AppendMergeRunDescriptor Run(long generation, int extents, int filterBytes, long entries = 100) =>
        new(generation, entries, Enumerable.Range(0, extents).Select(i => new PhysicalPageExtent(new(1 + i * 2), 1)).ToArray(),
            new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }, new(3, filterBytes * 8,
                Enumerable.Range(0, filterBytes).Select(i => (byte)i).ToArray()));

    [TestMethod]
    public void Manifest_bytes_match_frozen_codec_for_dense_reused_excluded_and_fragmented_records()
    {
        var id = Guid.NewGuid();
        var codec = new AppendMergeManifestCodec();
        var old = new LegacyAppendMergeManifestCodec();
        AppendMergeRunDescriptor[] runs = [Run(3, 520, 24000), Run(2, 1, 1), Run(1, 2, 8200)];
        AppendMergeRetiredPages[] retired = [new(2, Enumerable.Range(0, 2500).Select(i => new PhysicalPageExtent(new(5000 + 2 * i), 1)).ToArray())];
        var reuse = new Dictionary<long, IReadOnlyList<PhysicalPageId>> { [1] = new PhysicalPageId[] { new(90000), new(90001) }, [2] = new PhysicalPageId[] { new(90002) } };
        foreach (bool separate in new[] { false, true })
        {
            var before = old.Encode(id, 5, runs, retired, new Allocator(), separate ? reuse : null);
            var after = codec.Encode(id, 5, runs, retired, new Allocator(), separate ? reuse : null);
            Equal(before, after);
            var excluded = new HashSet<long> { 2 };
            var filtered = reuse.Where(p => !excluded.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            Equal(old.Encode(id, 5, runs, retired, new Allocator(), filtered),
                codec.Encode(id, 5, runs, retired, new Allocator(), reuse, excluded));
        }
        var dense = codec.Encode(id, 5, runs, retired, new Allocator());
        var decoded = codec.DecodeBranches(dense.Branches.Select(p => (p.Key, p.Value)));
        Assert.AreEqual(runs.Length, decoded.Runs.Count);
        // Encoding another manifest must not mutate a previously published image.
        byte[] saved = dense.Root.ToArray();
        codec.Encode(id, 6, [Run(6, 1, 1)], [], new Allocator());
        CollectionAssert.AreEqual(saved, dense.Root.ToArray());
        var invalid = Run(7, 1, 1) with
        {
            MinimumKey = new byte[PhysicalPageFileOptions.DefaultPageSize]
        };
        Assert.ThrowsExactly<PhysicalPageCapacityException>(() => old.Encode(id, 7, [invalid], [], new Allocator()));
        Assert.ThrowsExactly<PhysicalPageCapacityException>(() => codec.Encode(id, 7, [invalid], [], new Allocator()));
    }

    private static void Equal(AppendMergeManifestImage expected, AppendMergeManifestImage actual)
    {
        Assert.AreEqual(expected.RootPageId, actual.RootPageId);
        CollectionAssert.AreEqual(expected.Root.ToArray(), actual.Root.ToArray());
        CollectionAssert.AreEqual(expected.AllBranches.ToArray(), actual.AllBranches.ToArray());
        Assert.AreEqual(expected.Branches.Count, actual.Branches.Count);
        for (int i = 0; i < expected.Branches.Count; i++)
        {
            Assert.AreEqual(expected.Branches[i].Key, actual.Branches[i].Key);
            CollectionAssert.AreEqual(expected.Branches[i].Value.ToArray(), actual.Branches[i].Value.ToArray());
        }
        Assert.AreEqual(expected.RunBranches.Count, actual.RunBranches.Count);
        foreach (var entry in expected.RunBranches)
        {
            CollectionAssert.AreEqual(entry.Value.ToArray(), actual.RunBranches[entry.Key].ToArray());
        }
    }

    [TestMethod]
    public void Manifest_intermediates_have_measured_allocation_reduction()
    {
        var id = Guid.NewGuid();
        AppendMergeRunDescriptor[] runs = [Run(1, 520, 24000)];
        var old = new LegacyAppendMergeManifestCodec();
        var codec = new AppendMergeManifestCodec();
        long before = Allocated(() => old.Encode(id, 2, runs, [], new Allocator()));
        long after = Allocated(() => codec.Encode(id, 2, runs, [], new Allocator()));
        Console.WriteLine($"manifest bytes/20 encodes: {before} -> {after}");
        Assert.IsTrue(after < before * 0.75, $"{before} -> {after}");
    }

    [TestMethod]
    public async Task Compaction_matches_old_policy_for_ties_budgets_randomized_and_concurrent_calls()
    {
        var options = new AppendMergeOptions();
        var old = new LegacyAppendMergeCompactionPlanner(options);
        var planner = new AppendMergeCompactionPlanner(options);
        var random = new Random(157);
        for (int trial = 0; trial < 200; trial++)
        {
            var runs = Enumerable.Range(0, random.Next(1, 80)).Select(i => Run(i + 1, random.Next(1, 5), 1,
                new[] { 1, 10, 100, 1000 }[random.Next(4)])).ToArray();
            foreach (long budget in new long[] { 0, 3, 8, 32, long.MaxValue })
            {
                CollectionAssert.AreEqual(old.Select(runs, budget).ToArray(), planner.Select(runs, budget).ToArray());
            }
        }
        var mixed = Enumerable.Range(0, 128).Select(i => Run(i + 1, 1, 1, i % 2 == 0 ? 10 : 10000)).ToArray();
        var expected = old.Select(mixed).ToArray();
        var outputs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => planner.Select(mixed))));
        foreach (var output in outputs)
        {
            CollectionAssert.AreEqual(expected, output.ToArray());
        }

        Assert.AreNotSame(outputs[0], outputs[1]);
        long before = Allocated(() => old.Select(mixed));
        long after = Allocated(() => planner.Select(mixed));
        Console.WriteLine($"compaction bytes/20 selections: {before} -> {after}");
        Assert.IsTrue(after < before / 2);
        var overflow = Enumerable.Range(0, 4).Select(i => Run(i + 1, 1, 1, long.MaxValue / 2)).ToArray();
        Assert.ThrowsExactly<OverflowException>(() => old.Select(overflow));
        // Exact ratio comparison no longer inherits the old policy's arithmetic overflow.
        CollectionAssert.AreEqual(overflow, planner.Select(overflow).ToArray());
        CollectionAssert.AreEqual(expected, planner.Select(mixed).ToArray());
    }

    [TestMethod]
    public void Directory_preview_is_owned_reused_only_for_completed_unchanged_scopes()
    {
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 1);
        var transaction = allocator.BeginTransaction(Guid.NewGuid());
        var method = new PhysicalAccessMethodKind("test", 1);
        var first = transaction.OpenAccessPath(new(Guid.NewGuid()), method, -1);
        var page = first.AllocatePage();
        first.Complete(page);
        var preview = transaction.PreviewDirectory();
        transaction.AllocateDirectoryPage();
        Assert.AreSame(preview, transaction.PreviewDirectory());
        var second = transaction.OpenAccessPath(new(Guid.NewGuid()), method, -1);
        Assert.ThrowsExactly<PhysicalPageSpaceException>(() => transaction.PreviewDirectory());
        transaction.CancelAccessPath(second);
        var replacement = transaction.OpenAccessPath(new(Guid.NewGuid()), method, -1);
        var other = replacement.AllocatePage();
        replacement.Complete(other);
        var finalPreview = transaction.PreviewDirectory();
        Assert.AreEqual(1, preview.Entries.Count);
        Assert.AreEqual(2, finalPreview.Entries.Count);
        Assert.AreSame(finalPreview, transaction.Prepare().Directory);
        Assert.ThrowsExactly<PhysicalPageSpaceException>(() => first.ReleasePage(page));
        var extents = new[] { new PhysicalPageExtent(new(20), 1) };
        var entry = PhysicalAccessPathDirectoryEntry.Combine(new(Guid.NewGuid()), method, new(20), 1, extents, []);
        extents[0] = new(new(30), 1);
        Assert.AreEqual(new PhysicalPageId(20), entry.Extents[0].FirstPage);
    }

    [TestMethod]
    public void Directory_combination_avoids_the_intermediate_array_and_preview_rebuild()
    {
        var id = new PhysicalAccessPathId(Guid.NewGuid());
        var method = new PhysicalAccessMethodKind("test", 1);
        var remaining = Enumerable.Range(0, 10000).Select(i => new PhysicalPageExtent(new(1 + 2 * i), 1)).ToArray();
        var added = Enumerable.Range(0, 1000).Select(i => new PhysicalPageExtent(new(30000 + 2 * i), 1)).ToArray();
        long before = Allocated(() => new PhysicalAccessPathDirectoryEntry(id, method, new(1), 1, remaining.Concat(added).ToArray()));
        long after = Allocated(() => PhysicalAccessPathDirectoryEntry.Combine(id, method, new(1), 1, remaining, added));
        Console.WriteLine($"directory bytes/20 combinations: {before} -> {after}");
        Assert.IsTrue(after < before * 0.6);
        var allocator = new PhysicalPageSpaceAllocator(new(0, []), [], 1);
        var transaction = allocator.BeginTransaction(Guid.NewGuid());
        var scope = transaction.OpenAccessPath(id, method, -1);
        var page = scope.AllocatePage();
        scope.Complete(page);
        var preview = transaction.PreviewDirectory();
        Assert.AreEqual(0L, Allocated(() => transaction.PreviewDirectory()));
        Assert.AreSame(preview, transaction.Prepare().Directory);
    }

    private static long Allocated<T>(Func<T> action)
    {
        for (int i = 0; i < 5; i++)
        {
            GC.KeepAlive(action());
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            GC.KeepAlive(action());
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
