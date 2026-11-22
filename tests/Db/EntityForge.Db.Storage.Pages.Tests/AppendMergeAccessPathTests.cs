using System.Buffers.Binary;
using System.Text;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class AppendMergeAccessPathTests
{
    private static readonly PhysicalAccessPathId AccessPathId =
        new(Guid.Parse("5185360d-cd16-4e0b-b3a1-a41c8ed5b92e"));

    private static readonly PhysicalAccessMethodKind Method = new("append-merge.test", 1);
    private string? _directory;

    [TestMethod]
    public void Inline_run_creation_allocates_only_one_payload_copy_and_owns_plan_bytes()
    {
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), new StringCodec());
        var writer = new AppendMergeRunWriter<int, string>(codec, new());
        var small = writer.Plan([new(AppendMergeMutationKind.Add, 1, 1, "x")]);
        var large = writer.Plan([new(AppendMergeMutationKind.Add, 1, 1, new string('z', 3500))]);
        long Measure(AppendMergeRunPlan<int, string> plan)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                writer.WriteInlineOrDisk(plan, 1, new SequentialAllocator());
            }

            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Measure(small);
        Measure(large);
        long difference = Measure(large) - Measure(small);
        Assert.IsTrue(difference < 100 * 4000, $"An extra record copy allocated {difference} bytes.");
        var image = writer.WriteInlineOrDisk(large, 1, new SequentialAllocator());
        var payload = image.Descriptor.InlinePayload!;
        byte[] canonical = codec.Encode(large.Entries[0].Entry);
        CollectionAssert.AreEqual(canonical, payload.Records.Single().ToArray());
        Array.Clear(large.Entries[0].Key);
        Array.Clear(large.Entries[0].Value);
        CollectionAssert.AreEqual(canonical, payload.Records.Single().ToArray());
    }

    [TestMethod]
    public void Inline_manifest_encoding_does_not_allocate_a_payload_sized_intermediate()
    {
        var codec = new AppendMergeManifestCodec();
        var records = new AppendMergeRunCodec<int, string>(new IntCodec(), new StringCodec());
        var filter = AppendMergeKeyFilter.Build([], 1, new());
        AppendMergeRunDescriptor Run(int size) => new(1, 1, [], records.EncodeKey(1), records.EncodeKey(1), filter)
        {
            InlinePayload = AppendMergeInlinePayload.TryCreate(
                [records.Encode(new(AppendMergeMutationKind.Add, 1, 1, new string('x', size)))], 4096)
        };
        var small = Run(10);
        var large = Run(3500);
        long Measure(AppendMergeRunDescriptor run, bool inline)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 100; index++)
            {
                codec.Encode(AccessPathId.Value, 2, [run], [], new SequentialAllocator(),
                    new Dictionary<long, IReadOnlyList<PhysicalPageId>>(), inlineRetirement: true, inlineRuns: inline);
            }

            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
        foreach (bool inline in new[] { true, false })
        {
            Measure(small, inline);
            Measure(large, inline);
            long smallBytes = Measure(small, inline), largeBytes = Measure(large, inline);
            Assert.IsTrue(largeBytes - smallBytes < 100 * 512,
                $"Payload bytes should be written directly to the fixed page: {largeBytes - smallBytes} extra bytes.");
            var image = codec.Encode(AccessPathId.Value, 2, [large], [], new SequentialAllocator(),
                new Dictionary<long, IReadOnlyList<PhysicalPageId>>(), inlineRetirement: true, inlineRuns: inline);
            var root = codec.DecodeRoot(image.Root.Span, image.RootPageId);
            var decoded = inline ? root.InlineRuns.Single() :
                codec.DecodeBranches(image.Branches.Select(pair => (pair.Key, pair.Value))).Runs.Single();
            CollectionAssert.AreEqual(large.InlinePayload!.Bytes.ToArray(), decoded.InlinePayload!.Bytes.ToArray());
        }
    }

    [TestMethod]
    public async Task Inline_payloads_survive_branch_overflow_mixed_disk_runs_abandon_and_snapshot_vacuum()
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space, validateValues: true);
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 1, "original")]);
        var codec = new AppendMergeManifestCodec();
        var rootId = space.CaptureDirectory().Get(AccessPathId).RootPage;
        var first = codec.DecodeRoot((await pages.ReadAsync(rootId)).Span, rootId);
        Assert.IsNotNull(first.InlineRuns.Single().InlinePayload);
        Assert.AreEqual(0, first.InlineRuns.Single().Pages.Count);
        Assert.AreEqual(0, first.Branches.Count);
        using var old = access.CaptureSnapshot(pinExactPages: true);
        for (int index = 2; index <= 14; index++)
        {
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, index, new string('x', 2000))]);
        }

        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 20, new string('z', 8000))]);
        rootId = space.CaptureDirectory().Get(AccessPathId).RootPage;
        var overflow = codec.DecodeRoot((await pages.ReadAsync(rootId)).Span, rootId);
        var branches = codec.DecodeBranches(await Task.WhenAll(overflow.Branches.Select(async id => (id, await pages.ReadAsync(id)))));
        Assert.IsTrue(branches.Runs.Any(run => run.InlinePayload is not null), "Complete inline payload must spill to an owned branch.");
        Assert.IsTrue(branches.Runs.Concat(overflow.InlineRuns).Any(run => run.Pages.Count != 0));
        access = await OpenAsync(pages, space, validateValues: true);
        Guid abandoned = Guid.NewGuid();
        var transaction = space.BeginTransaction(abandoned);
        await access.PrepareAsync(abandoned, [new(AppendMergeMutationKind.Add, 1, "abandoned")], 200,
            transaction.OpenAccessPath(AccessPathId, Method, access.Generation));
        access.Abandon(abandoned);
        space.Abandon(abandoned);
        using (var current = access.CaptureSnapshot())
        {
            CollectionAssert.AreEqual(new[] { "original" }, (await current.FindAsync(1)).ToArray());
            var batch = await current.FindManyAsync([1, 2, 20]);
            Assert.AreEqual(3, batch.Count);
            var all = new List<KeyValuePair<int, string>>();
            await foreach (var entry in current.ReadVisibleAsync(OrderedIndexRange<int>.All))
            {
                all.Add(entry);
            }

            Assert.AreEqual(15, all.Count);
        }
        Guid vacuumId = Guid.NewGuid();
        var vacuum = space.BeginTransaction(vacuumId);
        var compacted = (await access.PrepareVacuumAsync(vacuumId, 1,
            vacuum.OpenAccessPath(AccessPathId, Method, access.Generation)))!;
        await space.PrepareAsync(vacuum);
        await space.PersistAsync(vacuumId, compacted.Stage.PhysicalMutations);
        access.Publish(vacuumId);
        space.Publish(vacuumId);
        CollectionAssert.AreEqual(new[] { "original" }, (await old.FindAsync(1)).ToArray());
        Assert.AreEqual(0, (await old.FindAsync(20)).Count);
        access = await OpenAsync(pages, space, validateValues: true);
        using var reopened = access.CaptureSnapshot();
        Assert.AreEqual(8000, (await reopened.FindAsync(20)).Single().Length);
    }

    [TestMethod]
    public void Inline_retirement_eliminates_a_metadata_page_and_preserves_legacy_and_overflow_decoding()
    {
        var codec = new AppendMergeManifestCodec();
        var filter = AppendMergeKeyFilter.Build([], 1, new());
        var run = new AppendMergeRunDescriptor(2, 1, [new(new PhysicalPageId(100000), 1)],
            new byte[] { 1 }, new byte[] { 1 }, filter);
        foreach (int extentCount in new[] { 1, 2000 })
        {
            var retired = new AppendMergeRetiredPages(1, Enumerable.Range(0, extentCount)
                .Select(i => new PhysicalPageExtent(new(200000 + i * 2), 1)).ToArray());
            var legacy = codec.Encode(AccessPathId.Value, 3, [run], [retired], new SequentialAllocator(),
                new Dictionary<long, IReadOnlyList<PhysicalPageId>>());
            var compact = codec.Encode(AccessPathId.Value, 3, [run], [retired], new SequentialAllocator(),
                new Dictionary<long, IReadOnlyList<PhysicalPageId>>(), inlineRetirement: true, inlineRuns: true);
            var root = codec.DecodeRoot(compact.Root.Span, compact.RootPageId);
            var branches = codec.DecodeBranches(compact.Branches.Select(x => (x.Key, x.Value)));
            var decoded = root.InlineRetired.Concat(branches.Retired).SelectMany(x => x.Extents).ToArray();
            CollectionAssert.AreEqual(retired.Extents.ToArray(), decoded);
            Assert.AreEqual(1, branches.Runs.Count + root.InlineRuns.Count);
            Assert.AreEqual(extentCount == 1 ? legacy.Branches.Count - 2 : legacy.Branches.Count - 1,
                compact.Branches.Count);
            Assert.AreEqual(extentCount == 1 ? 1 : 0, root.InlineRetired.Count);
            Assert.AreEqual(0, codec.DecodeRoot(legacy.Root.Span, legacy.RootPageId).InlineRetired.Count);
            Assert.IsFalse(compact.RunBranches.ContainsKey(2), "Root-inline descriptors cannot be reused as independent branches.");
            byte[] corrupt = compact.Root.ToArray();
            corrupt[^1] ^= 1;
            Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeRoot(corrupt, compact.RootPageId));
        }
    }

    [TestMethod]
    public void Inline_manifest_descriptors_overflow_without_losing_runs_or_branch_ownership()
    {
        var codec = new AppendMergeManifestCodec();
        var filter = AppendMergeKeyFilter.Build([], 1, new());
        var runs = Enumerable.Range(1, 300).Select(i => new AppendMergeRunDescriptor(i, 1,
            [new(new PhysicalPageId(100000 + i), 1)], new byte[] { 1 }, new byte[] { 2 }, filter)).ToArray();
        var image = codec.Encode(AccessPathId.Value, 301, runs, [], new SequentialAllocator(),
            new Dictionary<long, IReadOnlyList<PhysicalPageId>>(), inlineRetirement: true, inlineRuns: true);
        var root = codec.DecodeRoot(image.Root.Span, image.RootPageId);
        var decoded = codec.DecodeBranches(image.Branches.Select(x => (x.Key, x.Value)));
        Assert.IsTrue(root.InlineRuns.Count > 0 && decoded.Runs.Count > 0);
        CollectionAssert.AreEqual(runs.Select(x => x.Generation).Order().ToArray(),
            root.InlineRuns.Concat(decoded.Runs).Select(x => x.Generation).Order().ToArray());
        Assert.IsTrue(root.InlineRuns.All(x => !image.RunBranches.ContainsKey(x.Generation)));
        Assert.AreEqual(decoded.Runs.Count, image.RunBranches.Count);
    }

    [TestMethod]
    public void Multi_page_run_filter_is_written_once_while_single_page_descriptors_are_inline()
    {
        var codec = new AppendMergeManifestCodec();
        var allocator = new SequentialAllocator();
        byte[] bits = new byte[4096];
        new Random(193).NextBytes(bits);
        var stable = new AppendMergeRunDescriptor(1, 20000,
            [new(new PhysicalPageId(100000), 100)], new byte[] { 1 }, new byte[] { 2 },
            new AppendMergeKeyFilter(3, bits.Length * 8, bits));
        var small = new AppendMergeRunDescriptor(2, 1,
            [new(new PhysicalPageId(200000), 1)], new byte[] { 3 }, new byte[] { 3 },
            AppendMergeKeyFilter.Build([], 1, new()));
        var first = codec.Encode(AccessPathId.Value, 2, [stable, small], [], allocator,
            new Dictionary<long, IReadOnlyList<PhysicalPageId>>(), inlineRetirement: true, inlineRuns: true);
        var root = codec.DecodeRoot(first.Root.Span, first.RootPageId);
        Assert.AreEqual(2L, root.InlineRuns.Single().Generation);
        Assert.IsTrue(first.RunBranches.ContainsKey(1));
        var originalBranch = first.Branches.Single();
        var next = codec.Encode(AccessPathId.Value, 3, [stable, small, small with { Generation = 3 }], [],
            allocator, first.RunBranches, inlineRetirement: true, inlineRuns: true);
        Assert.AreEqual(0, next.Branches.Count, "Stable filter must not generate another page image.");
        CollectionAssert.AreEqual(first.AllBranches.ToArray(), next.AllBranches.ToArray());
        Assert.AreEqual(2, codec.DecodeRoot(next.Root.Span, next.RootPageId).InlineRuns.Count);
        var decoded = codec.DecodeBranches([(originalBranch.Key, originalBranch.Value)]);
        CollectionAssert.AreEqual(bits, decoded.Runs.Single().Filter.Bits.ToArray());
    }

    private sealed class MixedValueIdentity : IAppendMergeKeyedValueComparer<int, string>
    {
        public int Compare(string? left, string? right) => StringComparer.Ordinal.Compare(left, right);

        public int Compare(int key, string left, string right) => key % 2 == 0 ? 0 : Compare(left, right);
    }

    [TestMethod]
    public async Task Keyed_replacements_preserve_composite_neighbors_all_readers_ceilings_compaction_and_reopen()
    {
        const int population = 128;
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(pages);
        async ValueTask<AppendMergeAccessPath<int, string>> Open() => await AppendMergeAccessPath<int, string>.OpenAsync(
            pages, AccessPathId, Method, space.CaptureDirectory().Entries.SingleOrDefault(e => e.AccessPathId == AccessPathId),
            new IntCodec(), new ValidatingStringCodec(), Comparer<int>.Default, new MixedValueIdentity());
        var path = await Open();
        async Task Write(long version, AppendMergeMutation<int, string>[] mutations)
        {
            var id = Guid.NewGuid();
            var transaction = space.BeginTransaction(id);
            var generation = space.CaptureDirectory().Entries.SingleOrDefault(e => e.AccessPathId == AccessPathId)?.Generation ?? -1;
            var stage = await path.PrepareAsync(id, mutations, version, transaction.OpenAccessPath(AccessPathId, Method, generation));
            await space.PrepareAsync(transaction);
            await space.PersistAsync(id, stage.PhysicalMutations);
            path.Publish(id);
            space.Publish(id);
        }
        await Write(1, Enumerable.Range(0, population).Select(k => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, k, "old")).ToArray());
        using var pinned = path.CaptureSnapshot();
        await Write(2, Enumerable.Range(0, population).Select(k => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, k, "new")).ToArray());
        await Write(3, Enumerable.Range(0, population).Where(k => k % 4 == 0).Select(k => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Remove, k, "")).ToArray());
        // Last mutation wins within a version, independent of payload ordering.
        await Write(4, [new(AppendMergeMutationKind.Add, 2, "z"), new(AppendMergeMutationKind.Remove, 2, ""), new(AppendMergeMutationKind.Add, 2, "a")]);
        async Task Verify()
        {
            foreach (int ceiling in new[] { 1, 2, 3, 4 })
            {
                using var snapshot = path.CaptureSnapshot(maximumVersion: ceiling);
                var expected = Enumerable.Range(0, population).SelectMany(k =>
                    k % 2 != 0 ? (ceiling == 1 ? new[] { "old" } : new[] { "new", "old" }).Select(v => new KeyValuePair<int, string>(k, v)) :
                    ceiling >= 3 && k % 4 == 0 ? [] : new[] { new KeyValuePair<int, string>(k, ceiling == 1 ? "old" : ceiling == 4 && k == 2 ? "a" : "new") }).ToArray();
                var streamed = new List<KeyValuePair<int, string>>();
                await foreach (var row in snapshot.ReadVisibleAsync())
                {
                    streamed.Add(row);
                }

                CollectionAssert.AreEqual(expected, streamed.ToArray());
                var paged = new List<KeyValuePair<int, string>>();
                AppendMergeCursor<int, string>? cursor = null;
                do
                {
                    var page = await snapshot.ReadPageAsync(7, cursor: cursor);
                    paged.AddRange(page.Items);
                    cursor = page.NextCursor;
                } while (cursor is not null);
                CollectionAssert.AreEqual(expected, paged.ToArray());
                var many = await snapshot.FindManyAsync(Enumerable.Range(0, population).ToArray());
                foreach (int key in Enumerable.Range(0, population))
                {
                    var values = expected.Where(row => row.Key == key).Select(row => row.Value).ToArray();
                    CollectionAssert.AreEqual(values, (await snapshot.FindAsync(key)).ToArray());
                    CollectionAssert.AreEqual(values, many.GetValueOrDefault(key, []).ToArray());
                }
            }
        }
        // Conservative reference tracing must include superseded values even when
        // current visibility resolves them away, and respect the captured ceiling.
        using (var history = path.CaptureSnapshot(maximumVersion: 2))
        {
            var stored = new List<KeyValuePair<int, string>>();
            await foreach (var row in history.ReadStoredAdditionsAsync(OrderedIndexRange<int>.Between(0, 2)))
            {
                stored.Add(row);
            }

            CollectionAssert.AreEquivalent(new[]
            {
                new KeyValuePair<int, string>(0, "old"), new(0, "new"),
                new(1, "old"), new(1, "new"), new(2, "old"), new(2, "new")
            }, stored.ToArray());
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var row in history.ReadStoredAdditionsAsync(OrderedIndexRange<int>.All, stop.Token))
                {
                }
            });
            CollectionAssert.AreEqual(new[] { "new" }, (await history.FindAsync(0)).ToArray());
        }
        await Verify();
        var compactId = Guid.NewGuid();
        var compact = space.BeginTransaction(compactId);
        var compacted = (await path.PrepareVacuumAsync(compactId, 1, compact.OpenAccessPath(AccessPathId, Method, path.Generation)))!;
        await space.PrepareAsync(compact);
        await space.PersistAsync(compactId, compacted.Stage.PhysicalMutations);
        path.Publish(compactId);
        space.Publish(compactId);
        path = await Open();
        await Verify();
        CollectionAssert.AreEqual(new[] { "old" }, (await pinned.FindAsync(2)).ToArray());
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var current = path.CaptureSnapshot();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
{
    await foreach (var _ in current.ReadVisibleAsync(cancellationToken: cancel.Token))
    {

    }

});
        CollectionAssert.AreEqual(new[] { "a" }, (await current.FindAsync(2)).ToArray());
    }

    [TestMethod]
    public void Reclamation_uses_strict_oldest_snapshot_boundary_including_maximum_generation()
    {
        var retired = new long[] { 0, 1, 5, 10, long.MaxValue }
            .Select(generation => new AppendMergeRetiredPages(generation,
                [new PhysicalPageExtent(new PhysicalPageId(100), 1)])).ToArray();
        long[][] protections = [[], [0], [5], [10, 5, 10], [long.MaxValue]];
        foreach (long[] snapshots in protections)
        {
            var expected = retired.Where(item => !snapshots.Any(value => value <= item.LastVisibleGeneration)).ToArray();
            CollectionAssert.AreEqual(expected, new AppendMergeReclaimer().Select(retired, snapshots).ToArray());
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(128)]
    [DataRow(1024)]
    public void Manifest_topology_scaling_preserves_shared_branches_and_roundtrips(int runCount)
    {
        const int samples = 32;
        var codec = new AppendMergeManifestCodec();
        var allocator = new SequentialAllocator();
        var filter = AppendMergeKeyFilter.Build([], 1, new());
        var runs = Enumerable.Range(1, runCount).Select(index => new AppendMergeRunDescriptor(
            index, 1, [new(new PhysicalPageId(100_000 + index), 1)],
            new byte[] { 1 }, new byte[] { 2 }, filter)).Reverse().ToArray();
        var original = codec.Encode(AccessPathId.Value, runCount, runs, [], allocator,
            new Dictionary<long, IReadOnlyList<PhysicalPageId>>());
        var nextRuns = new[] { runs[0] with { Generation = runCount + 1 } }.Concat(runs).ToArray();
        Assert.IsTrue(AppendMergeManifestCodec.CanSeparateRuns(nextRuns, 0));
        _ = codec.Encode(AccessPathId.Value, runCount + 1, nextRuns, [], allocator, original.RunBranches);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        AppendMergeManifestImage next = null!;
        for (int index = 0; index < samples; index++)
        {
            next = codec.Encode(AccessPathId.Value, runCount + 1, nextRuns, [], allocator, original.RunBranches);
        }

        double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / samples;
        long allocation = (GC.GetAllocatedBytesForCurrentThread() - before) / samples;
        Assert.AreEqual(1, next.Branches.Count, "Only the new run descriptor should be written.");
        Assert.AreEqual(runCount + 1, next.AllBranches.Count);
        var pages = original.Branches.Concat(next.Branches).ToDictionary(p => p.Key, p => p.Value);
        var root = codec.DecodeRoot(next.Root.Span, next.RootPageId);
        var decoded = codec.DecodeBranches(root.Branches.Select(page => (page, pages[page])));
        CollectionAssert.AreEqual(nextRuns.Select(run => run.Generation).ToArray(),
            decoded.Runs.Select(run => run.Generation).ToArray());
        foreach (var run in runs)
        {
            CollectionAssert.AreEqual(original.RunBranches[run.Generation].ToArray(),
                next.RunBranches[run.Generation].ToArray());
        }

        Console.WriteLine($"Manifest runs={runCount} samples={samples} meanMs={milliseconds:F6} allocationBytes={allocation} newBranches={next.Branches.Count}");
    }

    [TestMethod]
    public async Task Manifest_growth_past_single_root_capacity_does_not_rewrite_existing_run_filters()
    {
        const int count = 2300;
        var codec = new AppendMergeManifestCodec();
        var allocation = new SequentialAllocator();
        byte[] bits = new byte[6250];
        var runs = Enumerable.Range(1, count).Select(index => new AppendMergeRunDescriptor(index, 5000,
            [new(new PhysicalPageId(100_000 + index), 1)], new byte[] { 1 }, new byte[] { 2 },
            new AppendMergeKeyFilter(5, bits.Length * 8, bits))).Reverse().ToArray();
        bool separate = AppendMergeManifestCodec.CanSeparateRuns(runs, 0);
        var first = codec.Encode(AccessPathId.Value, count, runs, [], allocation,
            separate ? new Dictionary<long, IReadOnlyList<PhysicalPageId>>() : null);
        var nextRuns = new[] { runs[0] with { Generation = count + 1 } }.Concat(runs).ToArray();
        var next = codec.Encode(AccessPathId.Value, count + 1, nextRuns, [], allocation,
            separate ? first.RunBranches : null);
        Assert.IsTrue(next.Branches.Count <= 4,
            $"Appending one run rewrote {next.Branches.Count} metadata pages; existing filters must remain shared.");
        var images = first.Branches.Concat(next.Branches).ToDictionary(item => item.Key, item => item.Value);
        var root = codec.DecodeRoot(next.Root.Span, next.RootPageId);
        Assert.IsTrue(root.IndirectBranches);
        root = await codec.ResolveBranchIndexAsync(root, next.RootPageId,
            page => ValueTask.FromResult(images[page]), next.AllBranches.Contains);
        Assert.AreEqual(2, root.IndexPages.Count);
        Assert.AreEqual(count + 1, root.Branches.Count);
        var decoded = codec.DecodeBranches(root.Branches.Select(page => (page, images[page])));
        CollectionAssert.AreEqual(nextRuns.Select(run => run.Generation).ToArray(), decoded.Runs.Select(run => run.Generation).ToArray());
        foreach (var run in runs)
        {
            CollectionAssert.AreEqual(first.RunBranches[run.Generation].ToArray(), decoded.RunBranches[run.Generation].ToArray());
        }
        var reopened = codec.Encode(AccessPathId.Value, count + 2, nextRuns, [], allocation, decoded.RunBranches);
        Assert.AreEqual(2, reopened.Branches.Count, "Reopening must retain sharing; only index pages change.");
    }

    [TestMethod]
    public async Task Indirect_manifest_reopens_and_keeps_index_pages_owned_across_later_writes()
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        string value = new('x', 2000);
        for (int index = 0; index < 2100; index++)
        {
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, index, value)]);
        }
        var entry = space.CaptureDirectory().Entries.Single();
        var codec = new AppendMergeManifestCodec();
        Assert.IsTrue(codec.DecodeRoot((await pages.ReadAsync(entry.RootPage)).Span, entry.RootPage).IndirectBranches);
        space = await PhysicalPageSpace.OpenAsync(pages);
        access = await OpenAsync(pages, space);
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 2100, "after reopen")]);
        access = await OpenAsync(pages, space);
        using var snapshot = access.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { value }, (await snapshot.FindAsync(0)).ToArray());
        CollectionAssert.AreEqual(new[] { value }, (await snapshot.FindAsync(2099)).ToArray());
        CollectionAssert.AreEqual(new[] { "after reopen" }, (await snapshot.FindAsync(2100)).ToArray());
    }

    [TestMethod]
    public void Manifest_fragments_large_retired_ownership_without_losing_pages_or_generation()
    {
        var codec = new AppendMergeManifestCodec();
        // Fragmented allocations can exceed one page even though every extent is valid.
        var extents = Enumerable.Range(0, PhysicalPageFileOptions.DefaultPageSize / 16 + 17)
            .Select(index => new PhysicalPageExtent(new PhysicalPageId(100_000 + index * 3), 2)).ToArray();
        var retired = new AppendMergeRetiredPages(7, extents);
        foreach (bool separate in new[] { false, true })
        {
            var image = codec.Encode(AccessPathId.Value, 8, [], [retired], new SequentialAllocator(),
                separate ? new Dictionary<long, IReadOnlyList<PhysicalPageId>>() : null);
            var root = codec.DecodeRoot(image.Root.Span, image.RootPageId);
            CollectionAssert.AreEqual(image.AllBranches.ToArray(), root.Branches.ToArray());
            var restored = codec.DecodeBranches(image.Branches.Select(item => (item.Key, item.Value)));
            Assert.IsTrue(restored.Retired.Count > 1);
            Assert.IsTrue(restored.Retired.All(item => item.LastVisibleGeneration == 7));
            CollectionAssert.AreEqual(retired.Pages.ToArray(), restored.Retired.SelectMany(item => item.Pages).ToArray());
        }
    }

    [TestMethod]
    public void Manifest_reuses_all_fragments_of_a_large_run_and_preserves_dense_capacity()
    {
        var codec = new AppendMergeManifestCodec();
        var allocation = new SequentialAllocator();
        byte[] bits = new byte[AppendMergeOptions.MaximumSupportedFilterBytes];
        Random.Shared.NextBytes(bits);
        var large = new AppendMergeRunDescriptor(1, 100_000,
            [new(new PhysicalPageId(100_000), 1)], new byte[] { 1 }, new byte[] { 2 },
            new AppendMergeKeyFilter(5, bits.Length * 8, bits));
        var first = codec.Encode(AccessPathId.Value, 1, [large], [], allocation,
            new Dictionary<long, IReadOnlyList<PhysicalPageId>>());
        Assert.IsTrue(first.AllBranches.Count > 1);
        var decoded = codec.DecodeBranches(first.Branches.Select(item => (item.Key, item.Value)));
        var small = large with
        {
            Generation = 2,
            Filter = AppendMergeKeyFilter.Build([], 1, new())
        };
        var next = codec.Encode(AccessPathId.Value, 2, [small, large], [], allocation, decoded.RunBranches);
        Assert.IsTrue(first.AllBranches.All(next.AllBranches.Contains));
        Assert.IsFalse(next.Branches.Any(item => first.AllBranches.Contains(item.Key)));
        var roundTrip = codec.DecodeBranches(first.Branches.Concat(next.Branches)
            .Select(item => (item.Key, item.Value)));
        CollectionAssert.AreEqual(bits, roundTrip.Runs.Single(run => run.Generation == 1).Filter.Bits.ToArray());

        const int manyTinyRuns = 4_096;
        var runs = Enumerable.Range(1, manyTinyRuns).Select(index => small with { Generation = index }).ToArray();
        Assert.IsTrue(AppendMergeManifestCodec.CanSeparateRuns(runs, 0));
        var dense = codec.Encode(AccessPathId.Value, manyTinyRuns, runs, [], allocation);
        Assert.AreEqual(manyTinyRuns, codec.DecodeBranches(dense.Branches.Select(item => (item.Key, item.Value))).Runs.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reclamation_preserves_unchanged_manifest_branches_and_next_write_sharing(bool largeDescriptor)
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        await CommitAsync(pages, space, access, new[] { new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, 1, "first") }.Concat(Enumerable.Range(100, largeDescriptor ? 20000 : 0).Select(key => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, key, "padding"))).ToArray());
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 2, "second")]);
        var codec = new AppendMergeManifestCodec();
        var root = space.CaptureDirectory().Get(AccessPathId).RootPage;
        var branches = codec.DecodeRoot((await pages.ReadAsync(root)).Span, root).Branches;
        var manifest = codec.DecodeBranches(await Task.WhenAll(branches.Select(async page =>
            (PageId: page, Image: await pages.ReadAsync(page)))));
        var runPages = manifest.RunBranches.Values.SelectMany(values => values).ToArray();
        Assert.AreEqual(largeDescriptor, runPages.Length > 0);
        using var retained = access.CaptureSnapshot();
        async Task Maintain(bool abandon)
        {
            var id = Guid.NewGuid();
            var transaction = space.BeginTransaction(id);
            var scope = transaction.OpenAccessPath(AccessPathId, Method, access.Generation);
            var stage = (await access.PrepareMaintenanceAsync(id, long.MaxValue, scope))!;
            Assert.AreEqual(AppendMergeMaintenanceKind.Reclamation, stage.Kind);
            Assert.IsFalse(stage.Stage.PhysicalMutations.Any(m => runPages.Any(p => p.Value == m.PageId)));
            if (abandon)
            {
                access.Abandon(id);
                space.Abandon(id);
                return;
            }
            await space.PrepareAsync(transaction);
            await space.PersistAsync(id, stage.Stage.PhysicalMutations);
            access.Publish(id);
            space.Publish(id);
        }
        await Maintain(true);
        await Maintain(false);
        access = await OpenAsync(pages, space);
        root = space.CaptureDirectory().Get(AccessPathId).RootPage;
        Assert.IsTrue(runPages.All(codec.DecodeRoot((await pages.ReadAsync(root)).Span, root).Branches.Contains));
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 3, "third")]);
        root = space.CaptureDirectory().Get(AccessPathId).RootPage;
        Assert.IsTrue(runPages.All(codec.DecodeRoot((await pages.ReadAsync(root)).Span, root).Branches.Contains));
        CollectionAssert.AreEqual(new[] { "first" }, (await retained.FindAsync(1)).ToArray());
        using var current = access.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "third" }, (await current.FindAsync(3)).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Unchanged_run_manifest_pages_survive_reopen_and_abandoned_preparation(bool largeDescriptor)
    {
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(NewPath());
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access, new[] { new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, 1, "original") }.Concat(Enumerable.Range(100, largeDescriptor ? 20000 : 0).Select(key => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, key, "padding"))).ToArray());
        var codec = new AppendMergeManifestCodec();
        PhysicalPageId root = pageSpace.CaptureDirectory().Get(AccessPathId).RootPage;
        AppendMergeManifestRoot original = codec.DecodeRoot((await pages.ReadAsync(root)).Span, root);
        Assert.AreEqual(largeDescriptor, original.Branches.Count > 0);
        PhysicalPageId branch = largeDescriptor ? original.Branches.First() : root;
        byte[] originalImage = (await pages.ReadAsync(branch)).ToArray();
        using AppendMergeSnapshot<int, string> retained = access.CaptureSnapshot();
        await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 2, "second")]);
        access = await OpenAsync(pages, pageSpace);
        Guid id = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(id);
        var allocation = transaction.OpenAccessPath(AccessPathId, Method, access.Generation);
        AppendMergeStage stage = await access.PrepareAsync(id,
            [new(AppendMergeMutationKind.Add, 3, "abandoned")], 102, allocation);
        Assert.IsFalse(stage.PhysicalMutations.Any(mutation => mutation.PageId == branch.Value));
        access.Abandon(id);
        pageSpace.Abandon(id);
        await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 4, "fourth")]);
        root = pageSpace.CaptureDirectory().Get(AccessPathId).RootPage;
        if (largeDescriptor)
        {
            Assert.IsTrue(codec.DecodeRoot((await pages.ReadAsync(root)).Span, root).Branches.Contains(branch));
        }

        CollectionAssert.AreEqual(originalImage, (await pages.ReadAsync(branch)).ToArray());
        using (var current = access.CaptureSnapshot())
        {
            CollectionAssert.AreEqual(new[] { "original" }, (await current.FindAsync(1)).ToArray());
            Assert.AreEqual(0, (await current.FindAsync(3)).Count);
        }
        CollectionAssert.AreEqual(new[] { "original" }, (await retained.FindAsync(1)).ToArray());
        if (!largeDescriptor)
        {
            branch = root; // The current inline root is retired by vacuum.
        }

        using var beforeVacuum = access.CaptureSnapshot();
        Guid vacuumId = Guid.NewGuid();
        var vacuum = pageSpace.BeginTransaction(vacuumId);
        var vacuumScope = vacuum.OpenAccessPath(AccessPathId, Method, access.Generation);
        var compacted = (await access.PrepareVacuumAsync(vacuumId, long.MaxValue, vacuumScope))!;
        await pageSpace.PrepareAsync(vacuum);
        await pageSpace.PersistAsync(vacuumId, compacted.Stage.PhysicalMutations);
        access.Publish(vacuumId);
        pageSpace.Publish(vacuumId);
        CollectionAssert.AreEqual(new[] { "original" }, (await beforeVacuum.FindAsync(1)).ToArray());
        Assert.IsTrue(OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId)).Contains(branch.Value));
        beforeVacuum.Dispose();
        await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 5, "after-vacuum")]);
        Assert.IsFalse(OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId)).Contains(branch.Value));

    }

    [TestMethod]
    public void Batch_lookup_strategy_uses_key_density_not_a_fixed_batch_limit()
    {
        Assert.IsTrue(AppendMergeRunReader<int, string>.UsesSparseBatch(500, 8_699));
        Assert.IsFalse(AppendMergeRunReader<int, string>.UsesSparseBatch(3_000, 8_699));
    }

    [TestMethod]
    public async Task Streaming_run_writer_bounds_each_emitted_page_batch()
    {
        const int entryCount = 600;
        var writer = new AppendMergeRunWriter<int, string>(
            new(new IntCodec(), new StringCodec()), new());
        var allocator = new SequentialAllocator();
        var batchSizes = new List<int>();
        int writtenPages = 0;

        AppendMergeRunDescriptor? descriptor = await writer.WriteStreamingAsync(
            Entries(), entryCount, 1, allocator,
            (pages, _) =>
            {
                batchSizes.Add(pages.Count);
                writtenPages += pages.Count;
                return ValueTask.CompletedTask;
            }, CancellationToken.None);

        Assert.IsNotNull(descriptor);
        Assert.IsTrue(writtenPages > 256);
        Assert.IsTrue(batchSizes.Count > 1);
        Assert.IsTrue(batchSizes.All(count => count is > 0 and <= 256));

        static async IAsyncEnumerable<AppendMergeRunEntry<int, string>> Entries()
        {
            for (int index = 0; index < entryCount; index++)
            {
                yield return new(AppendMergeMutationKind.Add, 1, index,
                    new string((char)('a' + index % 26), 8_000));
                await Task.Yield();
            }
        }
    }

    [TestMethod]
    public async Task Planned_mutations_materialize_and_publish_like_direct_preparation()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        AppendMergeMutationPlan<int, string> plan = access.PlanMutations(
            [new(AppendMergeMutationKind.Add, 2, "second"),
                new(AppendMergeMutationKind.Add, 1, "first"),
                new(AppendMergeMutationKind.Remove, 2, "second"),
                new(AppendMergeMutationKind.Add, 2, "replacement")],
            databaseVersion: 100);

        Assert.AreEqual(3, plan.Count);
        Assert.IsTrue(plan.PlanningDuration >= TimeSpan.Zero);
        Assert.IsTrue(plan.AllocatedBytes >= 0);

        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope allocation = transaction.OpenAccessPath(
            AccessPathId, Method, expectedGeneration: -1);
        AppendMergeStage stage = await access.PrepareAsync(transactionId, plan, allocation);
        await pageSpace.PrepareAsync(transaction);
        await pageSpace.PersistAsync(transactionId, stage.PhysicalMutations);
        access.Publish(transactionId);
        pageSpace.Publish(transactionId);

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        AppendMergePage<int, string> page = await snapshot.ReadPageAsync(10);
        CollectionAssert.AreEqual(new[] { "1:first", "2:replacement" },
            page.Items.Select(item => $"{item.Key}:{item.Value}").ToArray());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [TestMethod]
    public async Task Ordered_pages_merge_runs_tombstones_ranges_and_cursors()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "a"),
                new(AppendMergeMutationKind.Add, 2, "b"),
                new(AppendMergeMutationKind.Add, 3, "c"),
                new(AppendMergeMutationKind.Add, 4, "d")]);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Remove, 2, "b"),
                new(AppendMergeMutationKind.Add, 2, "bb"),
                new(AppendMergeMutationKind.Add, 3, "cc")]);

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        AppendMergePage<int, string> first = await snapshot.ReadPageAsync(2,
            OrderedIndexRange<int>.Between(2, 4));
        AppendMergePage<int, string> second = await snapshot.ReadPageAsync(2,
            OrderedIndexRange<int>.Between(2, 4), first.NextCursor);

        CollectionAssert.AreEqual(new[] { "2:bb", "3:c" },
            first.Items.Select(item => $"{item.Key}:{item.Value}").ToArray());
        CollectionAssert.AreEqual(new[] { "3:cc", "4:d" },
            second.Items.Select(item => $"{item.Key}:{item.Value}").ToArray());
        Assert.IsNull(second.NextCursor);
    }

    [TestMethod]
    public async Task Paged_reads_advance_through_tombstone_heavy_runs_without_losing_visible_values()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access, Enumerable.Range(0, 120)
            .Select(index => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add,
                1, $"value-{index:D3}")).ToArray());
        await CommitAsync(pages, pageSpace, access, Enumerable.Range(0, 100)
            .Select(index => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Remove,
                1, $"value-{index:D3}")).ToArray());

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        AppendMergePage<int, string> first = await snapshot.ReadPageAsync(5);

        CollectionAssert.AreEqual(Enumerable.Range(100, 5).Select(index => $"value-{index:D3}").ToArray(),
            first.Items.Select(item => item.Value).ToArray());
        Assert.IsNotNull(first.NextCursor);
    }

    [TestMethod]
    public async Task Small_pages_merge_many_interleaved_runs_and_preserve_pinned_visibility()
    {
        await using var file = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(file);
        var access = await OpenAsync(file, space);
        string Value(int index) => index.ToString("D5") + new string('x', 256);
        for (int run = 0; run < 16; run++)
        {
            await CommitAsync(file, space, access, Enumerable.Range(0, 64).Select(index =>
                new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, 1, Value(index * 16 + run))).ToArray());
        }
        using var pinned = access.CaptureSnapshot();
        await CommitAsync(file, space, access, Enumerable.Range(0, 1024).Where(index => index % 7 == 0).Select(index =>
            new AppendMergeMutation<int, string>(AppendMergeMutationKind.Remove, 1, Value(index))).ToArray());
        using var current = access.CaptureSnapshot();
        foreach (var (snapshot, historical) in new[] { (pinned, true), (current, false) })
        {
            var actual = new List<string>();
            AppendMergeCursor<int, string>? cursor = null;
            do
            {
                var page = await snapshot.ReadPageAsync(17, OrderedIndexRange<int>.Between(1, 1), cursor);
                Assert.IsTrue(page.Items.Count <= 17);
                actual.AddRange(page.Items.Select(item => item.Value));
                cursor = page.NextCursor;
            } while (cursor is not null);
            CollectionAssert.AreEqual(Enumerable.Range(0, 1024).Where(index => historical || index % 7 != 0)
                .Select(Value).ToArray(), actual.ToArray());
        }
    }

    [TestMethod]
    public async Task Resolution_honours_configured_value_comparison_semantics()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await AppendMergeAccessPath<int, string>.OpenAsync(
            pages, AccessPathId, Method, null, new IntCodec(), new StringCodec(),
            valueComparer: StringComparer.OrdinalIgnoreCase);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "Alpha")]);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Remove, 1, "alpha")]);

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        Assert.AreEqual(0, (await snapshot.FindAsync(1)).Count);
        Assert.AreEqual(0, (await snapshot.FindManyAsync([1]))[1].Count);
        Assert.AreEqual(0, (await snapshot.ReadPageAsync(10)).Items.Count);
    }

    [TestMethod]
    public async Task Immutable_runs_resolve_add_remove_duplicates_and_restart()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
            AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
            await CommitAsync(pages, pageSpace, access,
                [new(AppendMergeMutationKind.Add, 7, "alpha"),
                    new(AppendMergeMutationKind.Add, 7, "beta"),
                    new(AppendMergeMutationKind.Add, 8, "other")]);
            AppendMergeSnapshot<int, string> retained = access.CaptureSnapshot();

            await CommitAsync(pages, pageSpace, access,
                [new(AppendMergeMutationKind.Remove, 7, "alpha"),
                    new(AppendMergeMutationKind.Add, 7, "gamma")]);

            CollectionAssert.AreEqual(new[] { "alpha", "beta" }, (await retained.FindAsync(7)).ToArray());
            CollectionAssert.AreEqual(new[] { "beta", "gamma" },
                (await access.CaptureSnapshot().FindAsync(7)).ToArray());
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace reopenedSpace = await PhysicalPageSpace.OpenAsync(reopenedPages);
        AppendMergeAccessPath<int, string> reopened = await OpenAsync(reopenedPages, reopenedSpace);

        Assert.AreEqual(2L, reopened.Generation);
        CollectionAssert.AreEqual(new[] { "beta", "gamma" },
            (await reopened.CaptureSnapshot().FindAsync(7)).ToArray());
    }

    [TestMethod]
    public async Task Version_bounded_snapshot_resolves_mutations_as_of_database_version()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 7, "alpha"),
                new(AppendMergeMutationKind.Add, 8, "beta")]);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Remove, 7, "alpha"),
                new(AppendMergeMutationKind.Add, 7, "gamma")]);

        await using AppendMergeSnapshot<int, string> historical = access.CaptureSnapshot(maximumVersion: 100);

        CollectionAssert.AreEqual(new[] { "alpha" }, (await historical.FindAsync(7)).ToArray());
        IReadOnlyDictionary<int, IReadOnlyList<string>> many = await historical.FindManyAsync([7, 8]);
        CollectionAssert.AreEqual(new[] { "alpha" }, many[7].ToArray());
        CollectionAssert.AreEqual(new[] { "beta" }, many[8].ToArray());
        AppendMergePage<int, string> page = await historical.ReadPageAsync(10);
        CollectionAssert.AreEqual(new[] { "7:alpha", "8:beta" },
            page.Items.Select(item => $"{item.Key}:{item.Value}").ToArray());
    }

    [TestMethod]
    public async Task One_transaction_run_is_normalized_by_composite_entry()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);

        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "value"),
                new(AppendMergeMutationKind.Remove, 1, "value"),
                new(AppendMergeMutationKind.Add, 1, "value")]);

        CollectionAssert.AreEqual(new[] { "value" },
            (await access.CaptureSnapshot().FindAsync(1)).ToArray());
    }

    [TestMethod]
    public async Task Bounded_duplicate_lookup_resolves_removals_across_runs()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access, Enumerable.Range(0, 100)
            .Select(index => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add,
                1, $"value-{index:D3}-{new string('x', 80)}")).ToArray());
        await CommitAsync(pages, pageSpace, access, Enumerable.Range(0, 8)
            .Select(index => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Remove,
                1, $"value-{index:D3}-{new string('x', 80)}")).ToArray());

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        IReadOnlyList<string> bounded = await snapshot.FindAsync(1, maximumResults: 5);

        CollectionAssert.AreEqual(Enumerable.Range(8, 5)
            .Select(index => $"value-{index:D3}-{new string('x', 80)}").ToArray(), bounded.ToArray());
    }

    [TestMethod]
    public async Task Exact_key_lookup_resolves_a_large_duplicate_set_without_quadratic_scanning()
    {
        const int DuplicateCount = 20_000;
        const int DuplicateKey = 7;
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access, Enumerable.Range(0, DuplicateCount)
            .Select(index => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add,
                DuplicateKey, $"value-{index:D5}")).ToArray());

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        IReadOnlyList<string> values = await snapshot.FindAsync(DuplicateKey);

        Assert.AreEqual(DuplicateCount, values.Count);
        Assert.AreEqual("value-00000", values[0]);
        Assert.AreEqual("value-19999", values[^1]);
    }

    [TestMethod]
    public async Task Normalization_orders_composites_and_keeps_the_last_mutation_in_a_large_batch()
    {
        const int distinctKeys = 1_000;
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        var mutations = new List<AppendMergeMutation<int, string>>(distinctKeys * 3);
        for (int key = distinctKeys - 1; key >= 0; key--)
        {
            string value = $"value-{key:D4}";
            mutations.Add(new(AppendMergeMutationKind.Add, key, value));
            mutations.Add(new(AppendMergeMutationKind.Remove, key, value));
            mutations.Add(new(AppendMergeMutationKind.Add, key, value));
        }

        await CommitAsync(pages, pageSpace, access, mutations);

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "value-0000" }, (await snapshot.FindAsync(0)).ToArray());
        CollectionAssert.AreEqual(new[] { "value-0500" }, (await snapshot.FindAsync(500)).ToArray());
        CollectionAssert.AreEqual(new[] { "value-0999" }, (await snapshot.FindAsync(999)).ToArray());
    }

    [TestMethod]
    public async Task Batch_lookup_matches_point_reads_across_runs_and_retained_snapshots()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "alpha"),
                new(AppendMergeMutationKind.Add, 2, "beta")]);
        await using AppendMergeSnapshot<int, string> retained = access.CaptureSnapshot();
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Remove, 1, "alpha"),
                new(AppendMergeMutationKind.Add, 1, "gamma"),
                new(AppendMergeMutationKind.Add, 3, "delta")]);

        IReadOnlyDictionary<int, IReadOnlyList<string>> retainedBatch =
            await retained.FindManyAsync([3, 1, 2, 1]);
        await using AppendMergeSnapshot<int, string> current = access.CaptureSnapshot();
        IReadOnlyDictionary<int, IReadOnlyList<string>> currentBatch =
            await current.FindManyAsync([3, 1, 2, 1]);

        CollectionAssert.AreEqual(new[] { "alpha" }, retainedBatch[1].ToArray());
        CollectionAssert.AreEqual(new[] { "beta" }, retainedBatch[2].ToArray());
        Assert.AreEqual(0, retainedBatch[3].Count);
        CollectionAssert.AreEqual((await current.FindAsync(1)).ToArray(), currentBatch[1].ToArray());
        CollectionAssert.AreEqual((await current.FindAsync(2)).ToArray(), currentBatch[2].ToArray());
        CollectionAssert.AreEqual((await current.FindAsync(3)).ToArray(), currentBatch[3].ToArray());
    }

    [TestMethod]
    public async Task Batch_lookup_matches_point_reads_across_many_pages_and_page_spanning_duplicates()
    {
        const int keyCount = 600;
        const int duplicateCount = 160;
        const int duplicateKey = keyCount / 2;
        const int missingKey = keyCount + 1;
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        var mutations = new List<AppendMergeMutation<int, string>>(keyCount + duplicateCount);
        for (int key = 0; key < keyCount; key++)
        {
            mutations.Add(new(AppendMergeMutationKind.Add, key, $"value-{key:D4}-{new string('x', 80)}"));
        }

        for (int value = 0; value < duplicateCount; value++)
        {
            mutations.Add(new(AppendMergeMutationKind.Add, duplicateKey,
                $"duplicate-{value:D4}-{new string('y', 80)}"));
        }

        await CommitAsync(pages, pageSpace, access, mutations);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Remove, 1, $"value-{1:D4}-{new string('x', 80)}"),
                new(AppendMergeMutationKind.Add, 1, "replacement")]);

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        int[] requested = Enumerable.Range(0, keyCount).Reverse().Append(duplicateKey)
            .Append(missingKey).ToArray();
        IReadOnlyDictionary<int, IReadOnlyList<string>> batch = await snapshot.FindManyAsync(requested);

        foreach (int key in requested.Distinct())
        {
            CollectionAssert.AreEqual((await snapshot.FindAsync(key)).ToArray(), batch[key].ToArray());
        }

        Assert.AreEqual(duplicateCount + 1, batch[duplicateKey].Count);
        Assert.AreEqual(0, batch[missingKey].Count);
    }

    [TestMethod]
    public async Task Persisted_filter_avoids_a_negative_run_probe_inside_key_bounds()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "low"),
                new(AppendMergeMutationKind.Add, 100, "high")]);
        AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();

        Assert.AreEqual(0, (await snapshot.FindAsync(50)).Count);
        Assert.AreEqual(0L, snapshot.RunProbeCount);
    }

    [TestMethod]
    public async Task Size_tiered_compaction_preserves_retained_snapshot_and_restart_semantics()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
            AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 1, "alpha")]);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 1, "beta")]);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Remove, 1, "alpha")]);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 1, "gamma")]);
            AppendMergeSnapshot<int, string> retained = access.CaptureSnapshot();
            Assert.IsTrue(access.NeedsCompaction);

            await CompactAsync(pageSpace, access, retentionFloorVersion: long.MaxValue);

            Assert.AreEqual(1, access.RunCount);
            CollectionAssert.AreEqual(new[] { "beta", "gamma" }, (await retained.FindAsync(1)).ToArray());
            CollectionAssert.AreEqual(new[] { "beta", "gamma" },
                (await access.CaptureSnapshot().FindAsync(1)).ToArray());
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace reopenedSpace = await PhysicalPageSpace.OpenAsync(reopenedPages);
        AppendMergeAccessPath<int, string> reopened = await OpenAsync(reopenedPages, reopenedSpace);
        Assert.AreEqual(1, reopened.RunCount);
        CollectionAssert.AreEqual(new[] { "beta", "gamma" },
            (await reopened.CaptureSnapshot().FindAsync(1)).ToArray());
    }

    [TestMethod]
    public async Task Compaction_preserves_every_version_from_the_retention_floor_across_restart()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
            AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 1, "alpha")]);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Remove, 1, "alpha")]);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 1, "alpha")]);
            await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Remove, 1, "alpha")]);

            await CompactAsync(pageSpace, access, retentionFloorVersion: 101);

            Assert.AreEqual(0, (await access.CaptureSnapshot(maximumVersion: 101).FindAsync(1)).Count);
            CollectionAssert.AreEqual(new[] { "alpha" },
                (await access.CaptureSnapshot(maximumVersion: 102).FindAsync(1)).ToArray());
            Assert.AreEqual(0, (await access.CaptureSnapshot(maximumVersion: 103).FindAsync(1)).Count);
        }

        await using PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace reopenedSpace = await PhysicalPageSpace.OpenAsync(reopenedPages);
        AppendMergeAccessPath<int, string> reopened = await OpenAsync(reopenedPages, reopenedSpace);
        Assert.AreEqual(0, (await reopened.CaptureSnapshot(maximumVersion: 101).FindAsync(1)).Count);
        CollectionAssert.AreEqual(new[] { "alpha" },
            (await reopened.CaptureSnapshot(maximumVersion: 102).FindAsync(1)).ToArray());
        Assert.AreEqual(0, (await reopened.CaptureSnapshot(maximumVersion: 103).FindAsync(1)).Count);
    }

    [TestMethod]
    public async Task Cancelled_compaction_does_not_publish_a_replacement_manifest()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        for (int index = 0; index < AppendMergeOptions.DefaultCompactionRunThreshold; index++)
        {
            await CommitAsync(pages, pageSpace, access,
                [new(AppendMergeMutationKind.Add, index, $"value-{index}")]);
        }

        long generation = access.Generation;
        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope allocation = transaction.OpenAccessPath(
            AccessPathId, Method, generation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await access.PrepareMaintenanceAsync(transactionId, long.MaxValue, allocation, cancellation.Token));
        pageSpace.Abandon(transactionId);

        Assert.AreEqual(generation, access.Generation);
        Assert.AreEqual(AppendMergeOptions.DefaultCompactionRunThreshold, access.RunCount);
    }

    [TestMethod]
    public async Task Compaction_is_invisible_before_durability_and_recoverable_after_page_publication()
    {
        string beforePath = NewPath("before.pages");
        string durablePath = Path.Combine(_directory!, "durable.pages");
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(beforePath))
        {
            PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
            AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
            await AddCompactionRunsAsync(pages, pageSpace, access);
            (Guid id, _, _) = await PrepareMaintenanceAsync(pageSpace, access);
            access.Abandon(id);
            pageSpace.Abandon(id);
        }
        await using (PhysicalPageFile reopenedPages = await PhysicalPageFile.OpenAsync(beforePath))
        {
            PhysicalPageSpace reopenedSpace = await PhysicalPageSpace.OpenAsync(reopenedPages);
            AppendMergeAccessPath<int, string> reopened = await OpenAsync(reopenedPages, reopenedSpace);
            Assert.AreEqual(AppendMergeOptions.DefaultCompactionRunThreshold, reopened.RunCount);
        }

        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(durablePath))
        {
            PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
            AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
            await AddCompactionRunsAsync(pages, pageSpace, access);
            (Guid id, PhysicalPageSpaceTransaction transaction, AppendMergeStage stage) =
                await PrepareMaintenanceAsync(pageSpace, access);
            await pageSpace.PrepareAsync(transaction);
            await pageSpace.PersistAsync(id, stage.PhysicalMutations);
        }
        await using PhysicalPageFile durablePages = await PhysicalPageFile.OpenAsync(durablePath);
        PhysicalPageSpace durableSpace = await PhysicalPageSpace.OpenAsync(durablePages);
        AppendMergeAccessPath<int, string> recovered = await OpenAsync(durablePages, durableSpace);
        Assert.AreEqual(1, recovered.RunCount);
    }

    [TestMethod]
    public async Task Released_snapshot_allows_manifest_guided_page_ownership_reclamation()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await AddCompactionRunsAsync(pages, pageSpace, access);
        AppendMergeSnapshot<int, string> retained = access.CaptureSnapshot();
        HashSet<long> originalPages = OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId));

        await CompactAsync(pageSpace, access, long.MaxValue);
        HashSet<long> protectedPages = OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId));
        HashSet<long> snapshotProtectedPages = originalPages.Where(protectedPages.Contains).ToHashSet();
        Assert.IsTrue(snapshotProtectedPages.Count > 0);
        AppendMergeMaintenanceStatus protectedStatus = access.GetMaintenanceStatus();
        Assert.AreEqual(protectedStatus, access.GetMaintenanceStatus());
        retained.Dispose();
        AppendMergeMaintenanceStatus releasedStatus = access.GetMaintenanceStatus();
        Assert.IsTrue(releasedStatus.ReclaimablePageCount > protectedStatus.ReclaimablePageCount);
        Assert.AreEqual(releasedStatus.RetiredPageCount, releasedStatus.ReclaimablePageCount);
        await CommitAsync(pages, pageSpace, access, [new(AppendMergeMutationKind.Add, 20, "new")]);
        Assert.AreEqual(access.RunCount, access.GetMaintenanceStatus().RunCount);

        HashSet<long> reclaimedDirectory = OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId));
        Assert.AreEqual(0, snapshotProtectedPages.Count(reclaimedDirectory.Contains));
    }

    [TestMethod]
    public async Task Overflow_pages_remain_snapshot_owned_then_are_reclaimed_and_reused()
    {
        string path = NewPath();
        string value = new('x', 128 * 1024);
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = await OpenAsync(pages, space);
            for (int key = 0; key < 4; key++)
            {
                await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, key, value)]);
            }
            using var retained = access.CaptureSnapshot();
            var children = new HashSet<long>();
            foreach (long id in OwnedPages(space.CaptureDirectory().Get(AccessPathId)))
            {
                var records = SlottedDataPage.ReadOccupiedRecords(await pages.ReadAsync(new(id)), new(id));
                if (records.Any(record => !record.IsEmpty && record.Span[0] == PhysicalOverflowRecord.ChunkKind))
                {
                    children.Add(id);
                }
            }
            Assert.IsTrue(children.Count > 4);
            await CompactAsync(space, access, long.MaxValue);
            Assert.IsTrue(children.IsSubsetOf(OwnedPages(space.CaptureDirectory().Get(AccessPathId))));
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Remove, 0, value)]);
            CollectionAssert.AreEqual(new[] { value }, (await retained.FindAsync(0)).ToArray());
            using (var current = access.CaptureSnapshot())
            {
                Assert.AreEqual(0, (await current.FindAsync(0)).Count);
            }
            Assert.IsTrue(children.IsSubsetOf(OwnedPages(space.CaptureDirectory().Get(AccessPathId))));
            retained.Dispose();
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 10, "release")]);
            Assert.AreEqual(0, children.Count(OwnedPages(space.CaptureDirectory().Get(AccessPathId)).Contains));
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 11, value)]);
            Assert.IsFalse(children.Overlaps(OwnedPages(space.CaptureDirectory().Get(AccessPathId))),
                "Reclaimed pages remain quarantined until a durable boundary authorizes reuse.");
            // This direct page-store fixture has no replay log. Flush its published directory
            // before invoking the same reuse boundary that the database checkpoint controls.
            await pages.FlushAsync();
            space.AuthorizeCheckpointPageReuse();
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 12, value)]);
            Assert.IsTrue(children.Overlaps(OwnedPages(space.CaptureDirectory().Get(AccessPathId))),
                "Released overflow pages should be available to subsequent allocations.");
        }
        await using var reopenedPages = await PhysicalPageFile.OpenAsync(path);
        var reopenedSpace = await PhysicalPageSpace.OpenAsync(reopenedPages);
        var reopened = await OpenAsync(reopenedPages, reopenedSpace);
        using var snapshot = reopened.CaptureSnapshot();
        Assert.AreEqual(0, (await snapshot.FindAsync(0)).Count);
        foreach (int key in new[] { 1, 2, 3, 11, 12 })
        {
            CollectionAssert.AreEqual(new[] { value }, (await snapshot.FindAsync(key)).ToArray());
        }
    }

    [TestMethod]
    public async Task Reopen_rejects_overflow_claiming_owned_manifest_pages_even_with_valid_checksums()
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 1, new string('x', 32768))]);
        var directory = space.CaptureDirectory().Get(AccessPathId);
        var codec = new AppendMergeManifestCodec();
        var root = codec.DecodeRoot((await pages.ReadAsync(directory.RootPage)).Span, directory.RootPage);
        bool damaged = false;
        foreach (var id in root.Branches)
        {
            var records = SlottedDataPage.ReadOccupiedRecords(await pages.ReadAsync(id), id);
            var replacement = new SlottedDataPage(pages.PageSize, id, root.Generation);
            foreach (var record in records)
            {
                byte[] bytes = record.ToArray();
                if (bytes[0] == 60)
                {
                    // Keep the claimed page inside the directory's owned extent, but
                    // alias the live root rather than an exclusively owned overflow page.
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(21), directory.RootPage.Value);
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(29), 1);
                    damaged = true;
                }
                Assert.IsTrue(replacement.TryInsert(bytes, root.Generation, out _));
            }
            await pages.WriteAsync(id, replacement.Encode());
        }
        Assert.IsTrue(damaged);
        await pages.FlushAsync();
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () => await OpenAsync(pages, space));
    }

    [TestMethod]
    public async Task Membership_workload_matches_the_ordered_access_path_test_oracle()
    {
        string appendPath = NewPath("append.pages");
        string oraclePath = Path.Combine(_directory!, "oracle.pages");
        await using PhysicalPageFile appendPages = await PhysicalPageFile.OpenAsync(appendPath);
        await using PhysicalPageFile oraclePages = await PhysicalPageFile.OpenAsync(oraclePath);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(appendPages);
        AppendMergeAccessPath<int, string> append = await OpenAsync(appendPages, pageSpace);
        OrderedPageTree<int, string> oracle = await OrderedPageTree<int, string>.OpenAsync(
            oraclePages, new IntCodec(), new StringCodec(), valueComparer: StringComparer.Ordinal);
        AppendMergeMutation<int, string>[][] batches =
        [
            [new(AppendMergeMutationKind.Add, 1, "a"), new(AppendMergeMutationKind.Add, 1, "b")],
            [new(AppendMergeMutationKind.Add, 2, "c")],
            [new(AppendMergeMutationKind.Remove, 1, "a"), new(AppendMergeMutationKind.Add, 1, "d")],
            [new(AppendMergeMutationKind.Remove, 2, "c")]
        ];
        foreach (AppendMergeMutation<int, string>[] batch in batches)
        {
            await CommitAsync(appendPages, pageSpace, append, batch);
            Guid transactionId = Guid.NewGuid();
            await oracle.PrepareAsync(transactionId, batch.Select(item => new OrderedPageTreeMutation<int, string>(
                item.Kind is AppendMergeMutationKind.Add
                    ? OrderedPageTreeMutationKind.Add : OrderedPageTreeMutationKind.Remove,
                item.Key, item.Value)).ToArray());
            await oracle.PublishAsync(transactionId);
            await using AppendMergeSnapshot<int, string> snapshot = append.CaptureSnapshot();
            for (int key = 1; key <= 2; key++)
            {
                CollectionAssert.AreEqual(oracle.Find(key).ToArray(),
                    (await snapshot.FindAsync(key)).ToArray());
            }
        }
    }

    [TestMethod]
    public async Task Replacement_catch_up_preserves_commits_published_during_build()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "baseline")]);

        Guid replacementId = Guid.NewGuid();
        PhysicalPageWorkspace workspace = pageSpace.BeginReplacementWorkspace(replacementId);
        await access.BuildReplacementAsync(replacementId, long.MaxValue, workspace, false);
        var obsoleteManifestRoots = new Dictionary<PhysicalPageId, byte[]>
        {
            [workspace.RootPage] = (await pages.ReadAsync(workspace.RootPage)).ToArray()
        };
        await CommitAsync(pages, pageSpace, access,
        [
            new(AppendMergeMutationKind.Remove, 1, "baseline"),
            new(AppendMergeMutationKind.Add, 1, "current"),
            new(AppendMergeMutationKind.Add, 2, "foreground")
        ]);

        await access.CatchUpReplacementAsync(replacementId, long.MaxValue, false);
        Assert.IsFalse(obsoleteManifestRoots.ContainsKey(workspace.RootPage));
        obsoleteManifestRoots.Add(workspace.RootPage, (await pages.ReadAsync(workspace.RootPage)).ToArray());
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 3, "second-foreground")]);
        await access.CatchUpReplacementAsync(replacementId, long.MaxValue, false);
        // A released private workspace page may legitimately be allocated again.
        // It must contain the new generation, never the obsolete root image.
        if (obsoleteManifestRoots.TryGetValue(workspace.RootPage, out byte[]? obsolete))
        {
            Assert.IsFalse((await pages.ReadAsync(workspace.RootPage)).Span.SequenceEqual(obsolete));
        }

        Guid cutover = Guid.NewGuid();
        long expectedGeneration = access.GetReplacementExpectedGeneration(replacementId);
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(cutover);
        transaction.OpenReplacementAccessPath(AccessPathId, Method, expectedGeneration, workspace);
        AppendMergeStage stage = access.PrepareReplacement(cutover, replacementId);
        await pageSpace.PrepareAsync(transaction);
        await pageSpace.PersistAsync(cutover, stage.PhysicalMutations);
        access.Publish(cutover);
        pageSpace.Publish(cutover);
        HashSet<long> publishedPages = OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId));
        foreach ((PhysicalPageId obsoleteRoot, byte[] image) in obsoleteManifestRoots)
        {
            if (publishedPages.Contains(obsoleteRoot.Value))
            {
                Assert.IsFalse((await pages.ReadAsync(obsoleteRoot)).Span.SequenceEqual(image));
            }
        }

        await using AppendMergeSnapshot<int, string> snapshot = access.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "current" }, (await snapshot.FindAsync(1)).ToArray());
        CollectionAssert.AreEqual(new[] { "foreground" }, (await snapshot.FindAsync(2)).ToArray());
        CollectionAssert.AreEqual(new[] { "second-foreground" }, (await snapshot.FindAsync(3)).ToArray());
    }

    [TestMethod]
    public async Task Reclamation_only_pass_preserves_eligible_live_runs_until_explicit_compaction()
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        await AddCompactionRunsAsync(pages, space, access);
        int before = access.GetMaintenanceStatus().RunCount;
        Guid id = Guid.NewGuid();
        var transaction = space.BeginTransaction(id);
        var allocation = transaction.OpenAccessPath(AccessPathId, Method, access.Generation);
        var reclamation = await access.PrepareMaintenanceAsync(id, long.MaxValue, allocation, reclaimOnly: true);
        if (reclamation is null)
        {
            space.Abandon(id);
        }
        else
        {
            await space.PrepareAsync(transaction);
            await space.PersistAsync(id, reclamation.Stage.PhysicalMutations);
            access.Publish(id);
            space.Publish(id);
        }
        Assert.AreEqual(before, access.GetMaintenanceStatus().RunCount);
        await CompactAsync(space, access, long.MaxValue);
        Assert.IsTrue(access.GetMaintenanceStatus().RunCount < before,
            "The fixture must contain a merge that reclamation deliberately deferred.");
        using var snapshot = access.CaptureSnapshot();
        for (int key = 0; key < 4; key++)
        {
            CollectionAssert.AreEqual(new[] { $"value-{key}" }, (await snapshot.FindAsync(key)).ToArray());
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Exact_maintenance_pins_allow_newer_retired_pages_to_be_reclaimed(bool exact)
    {
        await using var pages = await PhysicalPageFile.OpenAsync(NewPath());
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 1, "original")]);
        using var source = access.PinReplacementSource();
        using var reader = access.CaptureSnapshot(pinExactPages: exact);
        // A second exact reference at the same generation must have its own lifetime.
        using (var duplicate = access.CaptureSnapshot(pinExactPages: true))
        {
            CollectionAssert.AreEqual(new[] { "original" }, (await duplicate.FindAsync(1)).ToArray());
        }

        long reclaimed = 0;
        for (int cycle = 0; cycle < 8; cycle++)
        {
            for (int run = 0; run < AppendMergeOptions.DefaultCompactionRunThreshold; run++)
            {
                await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 2 + run, $"cycle-{cycle}")]);
            }

            await CompactAsync(space, access, long.MaxValue);
            Guid id = Guid.NewGuid();
            var transaction = space.BeginTransaction(id);
            var stage = await access.PrepareMaintenanceAsync(id, long.MaxValue,
                transaction.OpenAccessPath(AccessPathId, Method, access.Generation), reclaimOnly: true);
            if (stage is null)
            {
                space.Abandon(id);
                continue;
            }
            reclaimed += stage.ReclaimedPageCount;
            await space.PrepareAsync(transaction);
            await space.PersistAsync(id, stage.Stage.PhysicalMutations);
            access.Publish(id);
            space.Publish(id);
            await pages.FlushAsync();
            space.AuthorizeCheckpointPageReuse(privateWorkspacesPaused: true);
            CollectionAssert.AreEqual(new[] { "original" }, (await reader.FindAsync(1)).ToArray());
            Assert.AreEqual(0, (await reader.FindAsync(2)).Count);
        }
        Console.WriteLine($"Exact={exact}; reclaimed pages with source pinned={reclaimed}; file pages={await pages.GetPageCountAsync()}");
        if (exact)
        {
            Assert.IsTrue(reclaimed > 0, "A maintenance pin must not retain unrelated newer garbage.");
        }
        else
        {
            Assert.AreEqual(0L, reclaimed, "The original generation-wide protection is the comparison oracle.");
        }
        // A forced replacement can retire the source itself: the exact reader still
        // protects it even though its generation is absent from ordinary pins.
        Guid vacuumId = Guid.NewGuid();
        var vacuum = space.BeginTransaction(vacuumId);
        var replacement = (await access.PrepareVacuumAsync(vacuumId, long.MaxValue,
            vacuum.OpenAccessPath(AccessPathId, Method, access.Generation)))!;
        await space.PrepareAsync(vacuum);
        await space.PersistAsync(vacuumId, replacement.Stage.PhysicalMutations);
        access.Publish(vacuumId);
        space.Publish(vacuumId);
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 99, "later")]);
        CollectionAssert.AreEqual(new[] { "original" }, (await reader.FindAsync(1)).ToArray());
    }

    [TestMethod]
    public async Task Pinned_replacement_catches_up_when_foreground_writes_arrive_during_a_slice()
    {
        await using var file = await PhysicalPageFile.OpenAsync(NewPath());
        var pages = new PhysicalMaintenancePageStore(file);
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 1, "baseline")]);
        using var pin = access.PinReplacementSource();
        Guid replacement = Guid.NewGuid();
        using var workspace = space.BeginReplacementWorkspace(replacement);
        await access.BuildReplacementAsync(replacement, long.MaxValue, workspace, false);
        const int Count = 2048;
        string value = new('x', 8000);
        await CommitAsync(pages, space, access, Enumerable.Range(2, Count)
            .Select(key => new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, key, value)).ToArray());
        for (int i = 0; i < AppendMergeOptions.DefaultCompactionRunThreshold; i++)
        {
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, Count + 2 + i, $"small-{i}")]);
        }

        int runsBefore = access.GetMaintenanceStatus().RunCount;
        await CompactAsync(space, access, long.MaxValue);
        Assert.IsTrue(access.GetMaintenanceStatus().RunCount < runsBefore,
            "New runs must remain mergeable while the older source is pinned.");
        await using var work = new PhysicalMaintenanceWork();
        work.Start(async token =>
        {
            // Recovery of journalled pages is covered independently. This test
            // isolates source lineage under writes during an unfinished catch-up.
            using var scope = pages.Enter(work, static (_, _) => ValueTask.CompletedTask);
            using var writes = pages.UseWorkspace(workspace);
            do
            {
                await access.CatchUpReplacementAsync(replacement, long.MaxValue, false, token);
            } while (access.GetReplacementExpectedGeneration(replacement) != access.Generation);
        });
        Assert.IsFalse(await work.AdvanceAsync(256));
        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Remove, 1, "baseline"),
            new(AppendMergeMutationKind.Add, 1, "during-catch-up")]);
        int slices = 1;
        while (!await work.AdvanceAsync(256))
        {
            Assert.IsTrue(++slices < 128);
        }

        Guid id = Guid.NewGuid();
        var transaction = space.BeginTransaction(id);
        transaction.OpenReplacementAccessPath(AccessPathId, Method, access.Generation, workspace);
        var stage = access.PrepareReplacement(id, replacement);
        await space.PrepareAsync(transaction);
        await space.PersistAsync(id, stage.PhysicalMutations);
        access.Publish(id);
        space.Publish(id);
        using var snapshot = access.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "during-catch-up" }, (await snapshot.FindAsync(1)).ToArray());
        for (int key = 2; key < Count + 2; key++)
        {
            CollectionAssert.AreEqual(new[] { value }, (await snapshot.FindAsync(key)).ToArray());
        }
    }

    [TestMethod]
    public async Task Replacement_rejects_catch_up_after_compaction_breaks_its_source_lineage()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await AddCompactionRunsAsync(pages, pageSpace, access);
        Guid replacementId = Guid.NewGuid();
        PhysicalPageWorkspace workspace = pageSpace.BeginReplacementWorkspace(replacementId);
        await access.BuildReplacementAsync(replacementId, long.MaxValue, workspace, false);

        await CompactAsync(pageSpace, access, long.MaxValue);

        AppendMergeReplacementStaleException error = await Assert.ThrowsAsync<AppendMergeReplacementStaleException>(async () =>
            await access.CatchUpReplacementAsync(replacementId, long.MaxValue, false));
        Assert.IsTrue(error.ActualGeneration > error.ExpectedGeneration);
        access.AbandonReplacement(replacementId);
    }

    [TestMethod]
    public async Task Replacement_retains_old_pages_until_a_captured_snapshot_is_released()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "baseline")]);
        AppendMergeSnapshot<int, string> retained = access.CaptureSnapshot();
        HashSet<long> originalPages = OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId));
        Guid replacementId = Guid.NewGuid();
        PhysicalPageWorkspace workspace = pageSpace.BeginReplacementWorkspace(replacementId);
        await access.BuildReplacementAsync(replacementId, long.MaxValue, workspace, false);
        Guid cutover = Guid.NewGuid();
        long expected = access.GetReplacementExpectedGeneration(replacementId);
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(cutover);
        transaction.OpenReplacementAccessPath(AccessPathId, Method, expected, workspace);
        AppendMergeStage stage = access.PrepareReplacement(cutover, replacementId);
        await pageSpace.PrepareAsync(transaction);
        await pageSpace.PersistAsync(cutover, stage.PhysicalMutations);
        access.Publish(cutover);
        pageSpace.Publish(cutover);

        CollectionAssert.AreEqual(new[] { "baseline" }, (await retained.FindAsync(1)).ToArray());
        HashSet<long> protectedPages = OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId));
        Assert.IsTrue(originalPages.All(protectedPages.Contains));

        await retained.DisposeAsync();
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 2, "after")]);
        HashSet<long> reclaimed = OwnedPages(pageSpace.CaptureDirectory().Get(AccessPathId));
        Assert.IsTrue(originalPages.All(page => !reclaimed.Contains(page)));
    }

    [TestMethod]
    public async Task Failed_catch_up_rolls_back_every_new_workspace_allocation_and_can_retry()
    {
        string path = NewPath();
        await using var pages = new ThrowOncePageStore(await PhysicalPageFile.OpenAsync(path));
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages);
        AppendMergeAccessPath<int, string> access = await OpenAsync(pages, pageSpace);
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 1, "baseline")]);
        Guid replacementId = Guid.NewGuid();
        PhysicalPageWorkspace workspace = pageSpace.BeginReplacementWorkspace(replacementId);
        await access.BuildReplacementAsync(replacementId, long.MaxValue, workspace, false);
        int allocationMark = workspace.CaptureAllocationMark();
        PhysicalPageId originalRoot = workspace.RootPage;
        await CommitAsync(pages, pageSpace, access,
            [new(AppendMergeMutationKind.Add, 2, "foreground")]);
        pages.FailNextWrite();

        await Assert.ThrowsAsync<IOException>(async () =>
            await access.CatchUpReplacementAsync(replacementId, long.MaxValue, false));

        Assert.AreEqual(allocationMark, workspace.CaptureAllocationMark());
        Assert.AreEqual(originalRoot, workspace.RootPage);
        Assert.IsTrue(workspace.IsCompleted);
        await access.CatchUpReplacementAsync(replacementId, long.MaxValue, false);
        access.AbandonReplacement(replacementId);
    }

    [TestMethod]
    public void Bounded_compaction_skips_large_old_tiers_without_starving_small_new_runs()
    {
        var filter = AppendMergeKeyFilter.Build([], 1, new());
        var runs = Enumerable.Range(1, 8).Reverse().Select(generation =>
            new AppendMergeRunDescriptor(generation, generation <= 4 ? 1_000 : 1,
                [new(new PhysicalPageId(generation * 100), generation <= 4 ? 40 : 1)],
                new byte[] { 1 }, new byte[] { 1 }, filter)).ToArray();
        var planner = new AppendMergeCompactionPlanner(new());
        CollectionAssert.AreEqual(new long[] { 4, 3, 2, 1 },
            planner.Select(runs).Select(run => run.Generation).ToArray());
        CollectionAssert.AreEqual(new long[] { 8, 7, 6, 5 },
            planner.Select(runs, maximumInputPages: 128).Select(run => run.Generation).ToArray());
        Assert.AreEqual(0, planner.Select(runs, maximumInputPages: 3).Count);
    }

    [TestMethod]
    public void Bounded_compaction_combines_interleaved_tiers_without_changing_order_or_budget()
    {
        var filter = AppendMergeKeyFilter.Build([], 1, new());
        var runs = Enumerable.Range(1, 8).Reverse().Select(generation =>
            new AppendMergeRunDescriptor(generation, generation % 2 == 0 ? 16 : 1,
                [new(new PhysicalPageId(generation * 100), 1)],
                new byte[] { 1 }, new byte[] { 1 }, filter)).ToArray();
        var planner = new AppendMergeCompactionPlanner(new());
        CollectionAssert.AreEqual(new long[] { 7, 5, 3, 1 },
            planner.Select(runs, maximumInputPages: 4).Select(run => run.Generation).ToArray());
        Assert.AreEqual(0, planner.Select(runs, maximumInputPages: 3).Count);
        Assert.AreEqual(0, planner.Select(runs.Take(3).ToArray()).Count);

        // Ineligible physical sizes must not hide another feasible tier.
        var oversized = runs.Select(run => run.EntryCount == 1
            ? run with
            {
                Extents = [new(new PhysicalPageId(run.Generation * 100), 40)]
            } : run).ToArray();
        CollectionAssert.AreEqual(new long[] { 8, 6, 4, 2 },
            planner.Select(oversized, maximumInputPages: 4).Select(run => run.Generation).ToArray());
    }

    [TestMethod]
    [TestCategory("PreservedArtifactAudit")]
    public void Bounded_compaction_replays_preserved_structural_run_layouts()
    {
        string? root = Environment.GetEnvironmentVariable("ENTITYFORGE_COMPACTION_LAYOUT_AUDIT_ROOT");
        if (string.IsNullOrEmpty(root))
        {
            Assert.Inconclusive("Requires explicitly selected retained WAL inspection artifacts.");
        }

        const int InputPageBudget = 32;
        int maximumSeparateBranches = (PhysicalPageFileOptions.DefaultPageSize - SlottedDataPage.HeaderSize -
            SlottedDataPage.SlotSize - (sizeof(byte) + Guid.Empty.ToByteArray().Length + sizeof(long) + sizeof(int))) / sizeof(long);
        var planner = new AppendMergeCompactionPlanner(new());
        var filter = AppendMergeKeyFilter.Build([], 1, new());
        foreach (string owner in new[] { "a", "b" })
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root!, $"write-native-audit-{owner}.json")));
            var audit = document.RootElement;
            byte[] wal = File.ReadAllBytes(audit.GetProperty("Path").GetString()!);
            Assert.AreEqual(audit.GetProperty("Sha256").GetString(),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wal)));
            string id = audit.GetProperty("LastDirectory").EnumerateArray().Single(entry =>
                entry.GetProperty("method").GetString() == "append-merge.semantic-shared").GetProperty("id").GetString()!;
            var manifest = audit.GetProperty("Manifests").GetProperty(id);
            Assert.AreEqual(0, manifest.GetProperty("MissingBranches").GetInt32());
            var runs = manifest.GetProperty("Runs").EnumerateArray().Select(run =>
                new AppendMergeRunDescriptor(run.GetProperty("generation").GetInt64(),
                    run.GetProperty("entries").GetInt64(),
                    [new(new PhysicalPageId(100), run.GetProperty("pages").GetInt64())],
                    new byte[] { 1 }, new byte[] { 15 }, filter))
                .OrderByDescending(run => run.Generation).ToList();
            Assert.AreEqual(manifest.GetProperty("VisibleRunsObserved").GetInt32(), runs.Count);
            int original = runs.Count, merges = 0;
            long generation = runs[0].Generation;
            while (true)
            {
                var selected = planner.Select(runs, InputPageBudget);
                if (selected.Count == 0)
                {
                    break;
                }

                long pages = selected.Sum(run => run.Extents.Sum(extent => extent.PageCount));
                Assert.AreEqual(AppendMergeOptions.DefaultCompactionFanIn, selected.Count);
                Assert.IsTrue(pages <= InputPageBudget);
                var removed = selected.Select(run => run.Generation).ToHashSet();
                runs.RemoveAll(run => removed.Contains(run.Generation));
                // Conservative layout oracle: no version pruning or packing savings.
                runs.Insert(0, new(++generation, selected.Sum(run => run.EntryCount),
                    [new(new PhysicalPageId(100), pages)], new byte[] { 1 }, new byte[] { 15 }, filter));
                merges++;
            }
            Console.WriteLine($"Retained {owner}: {original} runs -> {runs.Count}, {merges} bounded merges; no new data IO.");
            Assert.IsTrue(runs.Count < maximumSeparateBranches,
                "The preserved mixed tiers must be drainable below the dense-manifest fallback boundary.");
        }
    }

    [TestMethod]
    public async Task Nonadjacent_write_compaction_preserves_unmerged_versions_streams_and_reopen()
    {
        const int InitialRuns = 8;
        const int FillerCount = 15;
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = await OpenAsync(pages, space, validateValues: true);
            for (int index = 1; index <= InitialRuns; index++)
            {
                var mutations = new List<AppendMergeMutation<int, string>>
                {
                    new(index % 2 == 1 ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove, 1, "value")
                };
                if (index % 2 == 0)
                {
                    mutations.AddRange(Enumerable.Range(0, FillerCount).Select(key =>
                        new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, index * 100 + key, "filler")));
                }

                await CommitAsync(pages, space, access, mutations);
            }
            Assert.AreEqual(InitialRuns, access.RunCount);
            access = await OpenAsync(pages, space, compactOnWrite: true, validateValues: true);
            await using var retained = access.CaptureSnapshot();

            Guid abandoned = Guid.NewGuid();
            var transaction = space.BeginTransaction(abandoned);
            await access.PrepareAsync(abandoned, [new(AppendMergeMutationKind.Add, 1, "value")], 108,
                transaction.OpenAccessPath(AccessPathId, Method, access.Generation));
            access.Abandon(abandoned);
            space.Abandon(abandoned);
            Assert.AreEqual(InitialRuns, access.RunCount);
            Assert.AreEqual(0, (await retained.FindAsync(1)).Count);

            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 1, "value")]);
            Assert.AreEqual(InitialRuns - AppendMergeOptions.DefaultCompactionFanIn + 1, access.RunCount);
            Assert.AreEqual(0, (await retained.FindAsync(1)).Count);
            await VerifyVersionsAsync(access);
            await pages.FlushAsync();
        }
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var space = await PhysicalPageSpace.OpenAsync(pages);
            await VerifyVersionsAsync(await OpenAsync(pages, space, compactOnWrite: true, validateValues: true));
        }

        static async Task VerifyVersionsAsync(AppendMergeAccessPath<int, string> access)
        {
            for (int index = 1; index <= InitialRuns + 1; index++)
            {
                await using var snapshot = access.CaptureSnapshot(maximumVersion: index + 99);
                var expected = new List<KeyValuePair<int, string>>();
                if (index % 2 == 1)
                {
                    expected.Add(new(1, "value"));
                }

                for (int run = 2; run <= Math.Min(index, InitialRuns); run += 2)
                {
                    expected.AddRange(Enumerable.Range(0, FillerCount).Select(key =>
                        new KeyValuePair<int, string>(run * 100 + key, "filler")));
                }

                var actual = new List<KeyValuePair<int, string>>();
                await foreach (var entry in snapshot.ReadVisibleAsync())
                {
                    actual.Add(entry);
                }

                CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray(), $"Version {index + 99}");
                Assert.AreEqual(index % 2, (await snapshot.FindAsync(1)).Count);
                foreach (var entry in expected)
                {
                    CollectionAssert.AreEqual(new[] { entry.Value }, (await snapshot.FindAsync(entry.Key)).ToArray());
                }
            }
        }
    }

    [TestMethod]
    public async Task Grouped_run_preserves_each_member_version_when_combined_with_compaction()
    {
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = await OpenAsync(pages, space, compactOnWrite: true);
            await AddCompactionRunsAsync(pages, space, access);
            var first = access.PlanMutations([new(AppendMergeMutationKind.Add, 99, "group")], 200);
            var second = access.PlanMutations([new(AppendMergeMutationKind.Remove, 99, "group")], 201);
            var third = access.PlanMutations([new(AppendMergeMutationKind.Add, 99, "group")], 202);
            Assert.Throws<AppendMergeException>(() => access.PlanMutationGroup([second, first]));
            Guid id = Guid.NewGuid();
            var physical = space.BeginTransaction(id);
            var stage = await access.PrepareAsync(id, access.PlanMutationGroup([first, second, third]),
                physical.OpenAccessPath(AccessPathId, Method, access.Generation));
            await space.PrepareAsync(physical);
            await space.PersistAsync(id, stage.PhysicalMutations);
            access.Publish(id);
            space.Publish(id);
            await pages.FlushAsync();
        }
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = await OpenAsync(pages, space);
            for (int version = 199; version <= 202; version++)
            {
                await using var snapshot = access.CaptureSnapshot(maximumVersion: version);
                Assert.AreEqual(version % 2 == 0 ? 1 : 0, (await snapshot.FindAsync(99)).Count);
            }
        }
    }

    [TestMethod]
    public async Task Write_compaction_preserves_tombstones_historical_versions_and_retained_pages_after_reopen()
    {
        const int Writes = 80;
        string path = NewPath();
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = await OpenAsync(pages, space, compactOnWrite: true);
            await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 1, "value")]);
            await using var retained = access.CaptureSnapshot();
            for (int index = 2; index <= Writes; index++)
            {
                await CommitAsync(pages, space, access, [new(index % 2 == 0
                    ? AppendMergeMutationKind.Remove : AppendMergeMutationKind.Add, 1, "value")]);
            }

            Assert.IsTrue(access.RunCount < Writes / 2, "Foreground writes must pay down run debt.");
            CollectionAssert.AreEqual(new[] { "value" }, (await retained.FindAsync(1)).ToArray());
            await pages.FlushAsync();
        }
        await using (PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path))
        {
            var space = await PhysicalPageSpace.OpenAsync(pages);
            var access = await OpenAsync(pages, space, compactOnWrite: true);
            for (int index = 1; index <= Writes; index++)
            {
                await using var snapshot = access.CaptureSnapshot(maximumVersion: index + 99);
                Assert.AreEqual(index % 2, (await snapshot.FindAsync(1)).Count, $"Version {index + 99}");
            }
        }
    }

    [TestMethod]
    public async Task Abandoning_a_compacting_write_keeps_the_published_manifest_and_can_retry()
    {
        string path = NewPath();
        await using PhysicalPageFile pages = await PhysicalPageFile.OpenAsync(path);
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space, compactOnWrite: true);
        await AddCompactionRunsAsync(pages, space, access);
        Assert.IsTrue(access.NeedsCompaction);
        int runs = access.RunCount;
        Guid id = Guid.NewGuid();
        var transaction = space.BeginTransaction(id);
        var allocation = transaction.OpenAccessPath(AccessPathId, Method, access.Generation);
        await access.PrepareAsync(id, [new(AppendMergeMutationKind.Add, 99, "pending")], 999, allocation);
        access.Abandon(id);
        space.Abandon(id);
        Assert.AreEqual(runs, access.RunCount);
        await using (var snapshot = access.CaptureSnapshot())
        {
            Assert.AreEqual(0, (await snapshot.FindAsync(99)).Count);
        }

        await CommitAsync(pages, space, access, [new(AppendMergeMutationKind.Add, 99, "committed")]);
        Assert.IsTrue(access.RunCount < runs);
        await using var final = access.CaptureSnapshot();
        CollectionAssert.AreEqual(new[] { "committed" }, (await final.FindAsync(99)).ToArray());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(128)]
    [DataRow(4096)]
    public async Task Paged_scan_seeks_past_previously_returned_entries_including_duplicate_keys(int valuesPerKey)
    {
        const int EntryCount = 4096;
        const int ResultPageSize = 64;
        const int MaximumPageReads = EntryCount / 2;
        await using var file = await PhysicalPageFile.OpenAsync(NewPath());
        var pages = new CountingPageStore(file);
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        var expected = Enumerable.Range(0, EntryCount).Select(index =>
            new KeyValuePair<int, string>(index / valuesPerKey, index.ToString("D5") + new string('x', 512))).ToArray();
        await CommitAsync(pages, space, access, expected.Select(item =>
            new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, item.Key, item.Value)).ToArray());
        using var snapshot = access.CaptureSnapshot();
        pages.Reads = 0;
        var actual = new List<KeyValuePair<int, string>>();
        AppendMergeCursor<int, string>? cursor = null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var page = await snapshot.ReadPageAsync(ResultPageSize, cursor: cursor);
            actual.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Console.WriteLine($"Paged scan: {actual.Count} entries, {pages.Reads} physical page reads, {timer.Elapsed.TotalMilliseconds:F2} ms");
        CollectionAssert.AreEqual(expected, actual.ToArray());
        Assert.IsTrue(pages.Reads < MaximumPageReads,
            $"Scan re-read its growing prefix: {pages.Reads} page reads for {EntryCount} entries.");
    }

    [TestMethod]
    public async Task Dense_batch_matches_ordered_requests_with_bounded_comparison_work()
    {
        const int EntryCount = 20_000;
        const int RequestedCount = 512;
        const int SpanningKey = EntryCount / 2;
        const int AdditionalSpanningValues = 50;
        const int PayloadLength = 128;
        const int EntryComparisonAllowance = 5;
        const int RequestComparisonAllowance = 40;
        await using var file = await PhysicalPageFile.OpenAsync(NewPath());
        var pages = new CountingPageStore(file);
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var comparer = new CountingIntComparer();
        var access = await AppendMergeAccessPath<int, string>.OpenAsync(pages, AccessPathId, Method, null,
            new IntCodec(), new StringCodec(), keyComparer: comparer, valueComparer: StringComparer.Ordinal);
        var mutations = Enumerable.Range(0, EntryCount).Select(key =>
            new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, key,
                key.ToString("D5") + new string('x', PayloadLength))).ToList();
        mutations.AddRange(Enumerable.Range(0, AdditionalSpanningValues).Select(value =>
            new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, SpanningKey,
                value.ToString("D5") + new string('y', PayloadLength))));
        await CommitAsync(pages, space, access, mutations);
        using var snapshot = access.CaptureSnapshot();
        int[] requested = Enumerable.Range(0, RequestedCount).Select(index => index * EntryCount / RequestedCount)
            .Append(SpanningKey).Append(-1).Append(EntryCount).Reverse().Append(SpanningKey).ToArray();
        var cold = await snapshot.FindManyAsync(requested);
        Assert.AreEqual(AdditionalSpanningValues + 1, cold[SpanningKey].Count);
        Assert.AreEqual(0, cold[-1].Count);
        Assert.AreEqual(0, cold[EntryCount].Count);
        var expected = mutations.GroupBy(item => item.Key).ToDictionary(group => group.Key,
            group => group.Select(item => item.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        foreach (int key in requested.Distinct())
        {
            CollectionAssert.AreEqual(expected.GetValueOrDefault(key, []), cold[key].ToArray());
        }

        comparer.Comparisons = 0;
        pages.Reads = 0;
        var warm = await snapshot.FindManyAsync(requested);
        long comparisons = comparer.Comparisons;
        foreach (int key in requested.Distinct())
        {
            CollectionAssert.AreEqual(cold[key].ToArray(), warm[key].ToArray());
        }

        Console.WriteLine($"Dense ordered lookup: {comparisons} comparisons, {pages.Reads} reads, " +
            $"{EntryCount} entries, {requested.Distinct().Count()} requested keys.");
        Assert.IsTrue(comparisons < EntryCount * EntryComparisonAllowance +
            requested.Distinct().Count() * RequestComparisonAllowance,
            $"Dense matching repeated logarithmic searches: {comparisons} comparisons.");
    }

    private sealed class CountingIntComparer : IComparer<int>
    {
        public long Comparisons;

        public int Compare(int left, int right)
        {
            Comparisons++;
            return left.CompareTo(right);
        }
    }

    [TestMethod]
    public async Task Dense_batch_skips_unrequested_payload_pages_without_losing_spanning_keys()
    {
        const int EntryCount = 4096;
        const int RequestedEdgeCount = 128;
        const int SpanningKey = EntryCount / 2;
        const int SpanningValues = 160;
        const int PayloadLength = 2048;
        await using var file = await PhysicalPageFile.OpenAsync(NewPath());
        var pages = new CountingPageStore(file);
        var space = await PhysicalPageSpace.OpenAsync(pages);
        var access = await OpenAsync(pages, space);
        var mutations = Enumerable.Range(0, EntryCount).Select(key =>
            new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, key,
                key.ToString("D5") + new string('x', PayloadLength))).ToList();
        mutations.AddRange(Enumerable.Range(0, SpanningValues).Select(value =>
            new AppendMergeMutation<int, string>(AppendMergeMutationKind.Add, SpanningKey,
                value.ToString("D5") + new string('y', PayloadLength))));
        await CommitAsync(pages, space, access, mutations);
        using var snapshot = access.CaptureSnapshot();
        int[] requested = Enumerable.Range(0, RequestedEdgeCount)
            .Concat(Enumerable.Range(EntryCount - RequestedEdgeCount, RequestedEdgeCount))
            .Append(SpanningKey).Reverse().Append(SpanningKey).ToArray();
        var cold = await snapshot.FindManyAsync(requested);
        Assert.AreEqual(SpanningValues + 1, cold[SpanningKey].Count);
        foreach (int key in requested.Distinct())
        {
            CollectionAssert.AreEqual((await snapshot.FindAsync(key)).ToArray(), cold[key].ToArray());
        }

        pages.Reads = 0;
        var warm = await snapshot.FindManyAsync(requested);
        foreach (int key in requested.Distinct())
        {
            CollectionAssert.AreEqual(cold[key].ToArray(), warm[key].ToArray());
        }

        Console.WriteLine($"Dense gap lookup: {pages.Reads} warm page reads for {requested.Distinct().Count()} requested keys.");
        Assert.IsTrue(pages.Reads < RequestedEdgeCount,
            $"Dense lookup read unrequested payload pages: {pages.Reads} reads.");
    }

    private sealed class CountingPageStore(IResizablePhysicalPageStore inner) : IResizablePhysicalPageStore
    {
        public int PageSize => inner.PageSize;

        public int Reads;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId id, CancellationToken token = default)
        {
            Reads++;
            return inner.ReadAsync(id, token);
        }

        public ValueTask WriteAsync(PhysicalPageId id, ReadOnlyMemory<byte> page, CancellationToken token = default) => inner.WriteAsync(id, page, token);

        public ValueTask FlushAsync(CancellationToken token = default) => inner.FlushAsync(token);

        public ValueTask<long> GetPageCountAsync(CancellationToken token = default) => inner.GetPageCountAsync(token);

        public ValueTask EnsureCapacityAsync(long count, CancellationToken token = default) => inner.EnsureCapacityAsync(count, token);

        public ValueTask TruncateAsync(long count, CancellationToken token = default) => inner.TruncateAsync(count, token);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async ValueTask<AppendMergeAccessPath<int, string>> OpenAsync(
        IResizablePhysicalPageStore pages, PhysicalPageSpace pageSpace, bool compactOnWrite = false,
        bool validateValues = false)
    {
        PhysicalAccessPathDirectoryEntry? published = pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
        return await AppendMergeAccessPath<int, string>.OpenAsync(pages, AccessPathId, Method, published,
            new IntCodec(), validateValues ? new ValidatingStringCodec() : new StringCodec(), valueComparer: StringComparer.Ordinal,
            options: new(compactOnWrite: compactOnWrite));
    }

    private static async ValueTask CommitAsync(IResizablePhysicalPageStore pages,
        PhysicalPageSpace pageSpace, AppendMergeAccessPath<int, string> access,
        IReadOnlyList<AppendMergeMutation<int, string>> mutations)
    {
        Guid transactionId = Guid.NewGuid();
        PhysicalAccessPathDirectoryEntry? current = pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope allocation = transaction.OpenAccessPath(
            AccessPathId, Method, current?.Generation ?? -1);
        AppendMergeStage accessStage = await access.PrepareAsync(
            transactionId, mutations, checked(access.Generation + 100), allocation);
        await pageSpace.PrepareAsync(transaction);
        await pageSpace.PersistAsync(transactionId, accessStage.PhysicalMutations);
        access.Publish(transactionId);
        pageSpace.Publish(transactionId);
    }

    private static async ValueTask CompactAsync(PhysicalPageSpace pageSpace,
        AppendMergeAccessPath<int, string> access, long retentionFloorVersion)
    {
        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope allocation = transaction.OpenAccessPath(
            AccessPathId, Method, access.Generation);
        AppendMergeStage stage = (await access.PrepareMaintenanceAsync(
            transactionId, retentionFloorVersion, allocation))!.Stage;
        await pageSpace.PrepareAsync(transaction);
        await pageSpace.PersistAsync(transactionId, stage.PhysicalMutations);
        access.Publish(transactionId);
        pageSpace.Publish(transactionId);
    }

    private static async ValueTask<(Guid TransactionId, PhysicalPageSpaceTransaction Transaction,
        AppendMergeStage Stage)> PrepareMaintenanceAsync(PhysicalPageSpace pageSpace,
        AppendMergeAccessPath<int, string> access)
    {
        Guid transactionId = Guid.NewGuid();
        PhysicalPageSpaceTransaction transaction = pageSpace.BeginTransaction(transactionId);
        PhysicalAccessPathAllocationScope allocation = transaction.OpenAccessPath(
            AccessPathId, Method, access.Generation);
        AppendMergeStage stage = (await access.PrepareMaintenanceAsync(
            transactionId, long.MaxValue, allocation))!.Stage;
        return (transactionId, transaction, stage);
    }

    private static async ValueTask AddCompactionRunsAsync(IResizablePhysicalPageStore pages,
        PhysicalPageSpace pageSpace, AppendMergeAccessPath<int, string> access)
    {
        for (int index = 0; index < AppendMergeOptions.DefaultCompactionRunThreshold; index++)
        {
            await CommitAsync(pages, pageSpace, access,
                [new(AppendMergeMutationKind.Add, index, $"value-{index}")]);
        }
    }

    private static HashSet<long> OwnedPages(PhysicalAccessPathDirectoryEntry entry) => entry.Extents
        .SelectMany(extent => Enumerable.Range(0, checked((int)extent.PageCount))
            .Select(offset => extent.FirstPage.Value + offset)).ToHashSet();

    private string NewPath(string fileName = "database.pages")
    {
        _directory = Path.Combine(Path.GetTempPath(), $"entityforge-append-merge-pages-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, fileName);
    }

    private sealed class IntCodec : IOrderedIndexCodec<int>
    {
        public byte[] Encode(int value)
        {
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            return bytes;
        }

        public int Decode(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt32BigEndian(value);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Inline_lookup_validates_unmatched_values_without_materializing_them(bool batch)
    {
        await using var file = await PhysicalPageFile.OpenAsync(NewPath());
        var values = new CountingValidatingStringCodec();
        var codec = new AppendMergeRunCodec<int, string>(new IntCodec(), values);
        var writer = new AppendMergeRunWriter<int, string>(codec, new AppendMergeOptions());
        var plan = writer.Plan(Enumerable.Range(0, 3).Select(i =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 1, i, new string((char)('a' + i), 1000))).ToArray());
        var run = new AppendMergeRunDescriptor(1, 3, [], plan.FirstKey, plan.LastKey, plan.Filter)
        {
            InlinePayload = AppendMergeInlinePayload.TryCreate(plan, codec)!
        };
        var reader = new AppendMergeRunReader<int, string>(file, codec);
        async Task Read(AppendMergeRunDescriptor descriptor)
        {
            if (batch)
            {
                var result = await reader.FindManyAsync(descriptor, [1, 1, 9], Comparer<int>.Default, default);
                Assert.AreEqual(new string('b', 1000), result[1].Single().Value);
                Assert.AreEqual(0, result[9].Count);
            }
            else
            {
                Assert.AreEqual(new string('b', 1000), (await reader.FindAsync(descriptor, 1,
                    Comparer<int>.Default, StringComparer.Ordinal, default)).Single().Value);
            }
        }
        await Read(run);
        Assert.AreEqual(1, values.Decodes);
        Assert.AreEqual(2, values.Validations);
        var smallPlan = writer.Plan(Enumerable.Range(0, 3).Select(i =>
            new AppendMergeRunEntry<int, string>(AppendMergeMutationKind.Add, 1, i,
                new string((char)('a' + i), i == 1 ? 1000 : 1))).ToArray());
        var smallRun = run with
        {
            InlinePayload = AppendMergeInlinePayload.TryCreate(smallPlan, codec)!
        };
        long Measure(AppendMergeRunDescriptor descriptor)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                if (batch)
                {
                    reader.FindManyAsync(descriptor, [1], Comparer<int>.Default, default).GetAwaiter().GetResult();
                }
                else
                {
                    reader.FindAsync(descriptor, 1, Comparer<int>.Default, StringComparer.Ordinal, default).GetAwaiter().GetResult();
                }
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        _ = Measure(smallRun);
        _ = Measure(run);
        Assert.IsTrue(Measure(run) - Measure(smallRun) < 100 * 512,
            "Unrequested value lengths must not produce payload-sized decoded allocations.");
        // Malformed unrequested tail must still fail before any result is returned.
        var records = plan.Entries.Select(entry => codec.Encode(entry.Entry)).ToArray();
        records[^1][^1] = 0xff;
        var corrupt = run with
        {
            InlinePayload = AppendMergeInlinePayload.TryCreate(records, 4096)!
        };
        await Assert.ThrowsExactlyAsync<DecoderFallbackException>(() => Read(corrupt));
        var badBoundary = run with
        {
            MaximumKey = new IntCodec().Encode(99)
        };
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(() => Read(badBoundary));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await reader.FindAsync(run, 1, Comparer<int>.Default, StringComparer.Ordinal, cancelled.Token));
    }

    private sealed class CountingValidatingStringCodec : IValidatingOrderedIndexCodec<string>
    {
        private static readonly UTF8Encoding Encoding = new(false, true);
        public int Decodes, Validations;

        public byte[] Encode(string value) => Encoding.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> bytes)
        {
            Decodes++;
            return Encoding.GetString(bytes);
        }

        public void Validate(ReadOnlySpan<byte> bytes)
        {
            Validations++;
            _ = Encoding.GetCharCount(bytes);
        }
    }

    private sealed class ValidatingStringCodec : IValidatingOrderedIndexCodec<string>
    {
        private static readonly UTF8Encoding Encoding = new(false, true);

        public byte[] Encode(string value) => Encoding.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> bytes) => Encoding.GetString(bytes);

        public void Validate(ReadOnlySpan<byte> bytes) => _ = Encoding.GetCharCount(bytes);
    }

    private sealed class StringCodec : IOrderedIndexCodec<string>
    {
        public byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);
    }

    private sealed class ThrowOncePageStore(IResizablePhysicalPageStore inner) : IResizablePhysicalPageStore
    {
        private int _failNextWrite;

        public int PageSize => inner.PageSize;

        public void FailNextWrite() => Volatile.Write(ref _failNextWrite, 1);

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId,
            CancellationToken cancellationToken = default) => inner.ReadAsync(pageId, cancellationToken);

        public ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page,
            CancellationToken cancellationToken = default) =>
            Interlocked.Exchange(ref _failNextWrite, 0) != 0
                ? ValueTask.FromException(new IOException("Injected workspace write failure."))
                : inner.WriteAsync(pageId, page, cancellationToken);

        public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default) =>
            inner.GetPageCountAsync(cancellationToken);

        public ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.EnsureCapacityAsync(pageCount, cancellationToken);

        public ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default) =>
            inner.TruncateAsync(pageCount, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SequentialAllocator : IPhysicalPageAllocator
    {
        private long _next = PhysicalPageSpaceDirectory.FirstAccessMethodPageId;

        public PhysicalPageId AllocatePage() => new(_next++);
    }
}
