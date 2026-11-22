using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class AppendMergeOverflowManifestTests
{
    private sealed class Allocator : IPhysicalPageAllocator
    {
        private long _next;

        public PhysicalPageId AllocatePage() => new(++_next);
    }

    private static AppendMergeRunDescriptor Run(long generation = 1) => new(generation, 1,
        [new(new(10000 + (generation - 1) * 1000), 2)], new byte[] { 1 }, new byte[] { 2 }, AppendMergeKeyFilter.Build([], 1, new()))
    {
        OverflowExtents = Enumerable.Range(0, 257).Select(index => new PhysicalPageExtent(new(20000 + (generation - 1) * 1000 + index * 2), 1)).ToArray()
    };

    [TestMethod]
    public void Overflow_ownership_roundtrips_without_entering_ordered_lookup_pages()
    {
        var codec = new AppendMergeManifestCodec();
        var run = Run();
        var allocator = new Allocator();
        Guid id = Guid.NewGuid();
        var encoded = codec.Encode(id, 2, [run], [], allocator,
            new Dictionary<long, IReadOnlyList<PhysicalPageId>>(), inlineRetirement: true, inlineRuns: true);
        var root = codec.DecodeRoot(encoded.Root.Span, encoded.RootPageId);
        Assert.AreEqual(0, root.InlineRuns.Count);
        var decoded = codec.DecodeBranches(encoded.Branches.Select(pair => (pair.Key, pair.Value)));
        var actual = decoded.Runs.Single();
        CollectionAssert.AreEqual(run.Pages.ToArray(), actual.Pages.ToArray());
        CollectionAssert.AreEqual(run.OverflowExtents.ToArray(), actual.OverflowExtents.ToArray());
        Assert.AreEqual(2, actual.Pages.Count);
        Assert.AreEqual(259, actual.OwnedPages.Count());
        Assert.IsTrue(decoded.RunBranches.ContainsKey(1));
        var reused = codec.Encode(id, 3, [actual], [], allocator, decoded.RunBranches);
        Assert.AreEqual(0, reused.Branches.Count, "Stable descriptors and ownership chunks should be reused together.");
        CollectionAssert.AreEqual(root.Branches.ToArray(), codec.DecodeRoot(reused.Root.Span, reused.RootPageId).Branches.ToArray());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("unmarked")]
    [DataRow("orphan")]
    [DataRow("overlap")]
    public void Overflow_ownership_corruption_is_rejected(string damage)
    {
        var codec = new AppendMergeManifestCodec();
        var encoded = codec.Encode(Guid.NewGuid(), 2, [Run()], [], new Allocator());
        var records = encoded.Branches.SelectMany(pair => SlottedDataPage.ReadOccupiedRecords(pair.Value, pair.Key))
            .Select(record => record.ToArray()).ToList();
        switch (damage)
        {
            case "missing":
                records.RemoveAll(record => record[0] == 60);
                break;
            case "duplicate":
                records.Add(records.First(record => record[0] == 60).ToArray());
                break;
            case "unmarked":
                records.Single(record => record[0] == 59)[0] = 53;
                break;
            case "orphan":
                records.Single(record => record[0] == 59)[0] = 53;
                foreach (byte[] record in records.Where(record => record[0] == 60))
                {
                    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1), 99);
                }
                break;
            case "overlap":
                BinaryPrimitives.WriteInt64LittleEndian(records.First(record => record[0] == 60).AsSpan(21), 10000);
                break;
        }
        var pages = records.Select((record, index) =>
        {
            var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(5000 + index), 2);
            Assert.IsTrue(page.TryInsert(record, 2, out _));
            return (page.PageId, (ReadOnlyMemory<byte>)page.Encode());
        }).ToArray();
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeBranches(pages));
    }

    [TestMethod]
    public void Legacy_runs_remain_readable_and_legacy_reader_rejects_overflow_format()
    {
        var current = new AppendMergeManifestCodec();
        var legacy = new LegacyAppendMergeManifestCodec();
        var plain = Run() with
        {
            OverflowExtents = []
        };
        var old = legacy.Encode(Guid.NewGuid(), 2, [plain], [], new Allocator());
        var decoded = current.DecodeBranches(old.Branches.Select(pair => (pair.Key, pair.Value))).Runs.Single();
        CollectionAssert.AreEqual(plain.Pages.ToArray(), decoded.Pages.ToArray());
        Assert.AreEqual(0, decoded.OverflowExtents.Count);
        var extended = current.Encode(Guid.NewGuid(), 2, [Run()], [], new Allocator());
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() =>
            legacy.DecodeBranches(extended.Branches.Select(pair => (pair.Key, pair.Value))));
    }

    [TestMethod]
    public void Compaction_input_budget_includes_overflow_pages()
    {
        var runs = Enumerable.Range(1, 4).Select(index => Run(index)).ToArray();
        var planner = new AppendMergeCompactionPlanner(new());
        Assert.AreEqual(0, planner.Select(runs, maximumInputPages: 1000).Count);
        Assert.AreEqual(4, planner.Select(runs, maximumInputPages: 1036).Count);
    }

    [TestMethod]
    [DataRow("other-run")]
    [DataRow("retired")]
    [DataRow("metadata")]
    [DataRow("duplicate-metadata")]
    [DataRow("retired-overlap")]
    public void Manifest_rejects_shared_ownership_across_live_retired_and_metadata(string damage)
    {
        var run = Run();
        AppendMergeRunDescriptor[] runs = [run];
        AppendMergeRetiredPages[] retired = [];
        PhysicalPageId[] metadata = [new(1), new(2)];
        switch (damage)
        {
            case "other-run":
                runs = [run, Run(2) with { OverflowExtents = run.OverflowExtents }];
                break;
            case "retired":
                retired = [new(1, run.OverflowExtents)];
                break;
            case "metadata":
                metadata = [run.OverflowExtents[0].FirstPage];
                break;
            case "duplicate-metadata":
                metadata = [new(1), new(1)];
                break;
            case "retired-overlap":
                retired = [new(1, [new(new(100), 10)]), new(2, [new(new(105), 10)])];
                break;
        }
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() =>
            AppendMergeManifestCodec.ValidateManifestOwnership(runs, retired, metadata));
    }

    [TestMethod]
    public void Manifest_ownership_checks_adjacent_extents_without_expanding_page_counts()
    {
        var huge = Run() with
        {
            Extents = [new(new(100), long.MaxValue / 2)],
            OverflowExtents = []
        };
        AppendMergeManifestCodec.ValidateManifestOwnership([huge], [new(1, [new(new(3), 97)])], [new(1), new(2)]);
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => AppendMergeManifestCodec.ValidateManifestOwnership(
            [huge], [new(1, [new(new(1000), 1)])], [new(1), new(2)]));
    }
}
