using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalPageSpaceDirectoryCodecTests
{
    [TestMethod]
    public void Inline_directory_roundtrips_and_transitions_between_legacy_branches_and_overflow()
    {
        var codec = new PhysicalPageSpaceDirectoryCodec();
        var small = new PhysicalPageSpaceDirectory(1, [new(new(Guid.NewGuid()), new("small", 1),
            new(1), 1, [new(new(1), 1)])]);
        var legacy = codec.Encode(small, new PhysicalPageSpaceAllocator(small, [], 100)
            .BeginTransaction(Guid.NewGuid()), [], []);
        var reserved = legacy.BranchPageIds.Concat(legacy.StandbyBranchPageIds).ToArray();
        var inline = codec.Encode(small, new PhysicalPageSpaceAllocator(small, reserved, 200)
            .BeginTransaction(Guid.NewGuid()), legacy.BranchPageIds, legacy.StandbyBranchPageIds,
            small, inlineEntries: true);
        Assert.AreEqual(0, inline.Branches.Count);
        Assert.AreEqual(0, inline.BranchPageIds.Count);
        CollectionAssert.AreEquivalent(reserved, inline.StandbyBranchPageIds.ToArray());
        var root = codec.DecodeRoot(inline.Root.Span);
        Assert.AreEqual(small.Entries.Single().AccessPathId, root.InlineEntries.Single().AccessPathId);
        var large = new PhysicalPageSpaceDirectory(2, Enumerable.Range(1, 3).Select(i =>
            new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()),
                new(new string('x', PhysicalPageFileOptions.DefaultPageSize / 2), 1),
                new(i), 2, [new(new(i), 1)])).ToArray());
        var overflow = codec.Encode(large, new PhysicalPageSpaceAllocator(small, reserved, 200)
            .BeginTransaction(Guid.NewGuid()), [], inline.StandbyBranchPageIds, small, inlineEntries: true);
        Assert.AreEqual(3, overflow.Branches.Count);
        Assert.AreEqual(0, codec.DecodeRoot(overflow.Root.Span).InlineEntries.Count);
        Assert.AreEqual(3, codec.DecodeBranches(overflow.Branches.Select(x => (x.Key, x.Value))).Count);
        Assert.AreEqual(0, overflow.BranchPageIds.Intersect(overflow.StandbyBranchPageIds).Count());
        var all = overflow.BranchPageIds.Concat(overflow.StandbyBranchPageIds).ToArray();
        var contracted = codec.Encode(small, new PhysicalPageSpaceAllocator(large, all, 300)
            .BeginTransaction(Guid.NewGuid()), overflow.BranchPageIds, overflow.StandbyBranchPageIds,
            large, inlineEntries: true);
        Assert.AreEqual(0, contracted.Branches.Count);
        CollectionAssert.AreEquivalent(all, contracted.StandbyBranchPageIds.ToArray());
        Assert.AreEqual(1, codec.DecodeRoot(contracted.Root.Span).InlineEntries.Count);
        byte[] corrupt = contracted.Root.ToArray();
        corrupt[^1] ^= 1;
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeRoot(corrupt));
        Assert.AreEqual(1, codec.DecodeBranches(legacy.Branches.Select(x => (x.Key, x.Value))).Count);
    }

    [TestMethod]
    public void Unchanged_directory_branches_are_shared_while_changed_records_use_standby_pages()
    {
        var entries = Enumerable.Range(1, 3).Select(i => new PhysicalAccessPathDirectoryEntry(
            new(new Guid(i, 0, 0, new byte[8])), new(new string((char)('a' + i), PhysicalPageFileOptions.DefaultPageSize / 2), 1),
            new(i), 1, [new(new(i), 1)])).ToArray();
        var before = new PhysicalPageSpaceDirectory(1, entries);
        var codec = new PhysicalPageSpaceDirectoryCodec();
        var first = codec.Encode(before, new PhysicalPageSpaceAllocator(before, [], 4)
            .BeginTransaction(Guid.NewGuid()), [], []);
        Assert.AreEqual(3, first.Branches.Count);
        var changed = entries.Select((entry, i) => i == 1 ? new PhysicalAccessPathDirectoryEntry(
            entry.AccessPathId, entry.Method, entry.RootPage, 2, entry.Extents) : entry).ToArray();
        var after = new PhysicalPageSpaceDirectory(2, changed);
        var reserved = first.BranchPageIds.Concat(first.StandbyBranchPageIds).ToArray();
        var second = codec.Encode(after, new PhysicalPageSpaceAllocator(before, reserved, 10)
            .BeginTransaction(Guid.NewGuid()), first.BranchPageIds, first.StandbyBranchPageIds, before);
        Assert.AreEqual(1, second.Branches.Count, "Only the changed branch needs a page image and redo.");
        Assert.AreEqual(2, second.BranchPageIds.Intersect(first.BranchPageIds).Count());
        Assert.AreEqual(0, second.BranchPageIds.Intersect(second.StandbyBranchPageIds).Count());
        var images = first.Branches.Concat(second.Branches).ToDictionary(x => x.Key, x => x.Value);
        var root = codec.DecodeRoot(second.Root.Span);
        var decoded = codec.DecodeBranches(root.BranchPages.Select(id => (id, images[id])));
        CollectionAssert.AreEqual(changed.OrderBy(x => x.AccessPathId.Value).Select(x => x.Generation).ToArray(),
            decoded.OrderBy(x => x.AccessPathId.Value).Select(x => x.Generation).ToArray());
        var old = codec.DecodeBranches(first.BranchPageIds.Select(id => (id, images[id])));
        Assert.IsTrue(old.All(x => x.Generation == 1), "The prior published image remains intact.");
    }

    [TestMethod]
    public void Directory_encoding_preserves_unicode_names_and_fragmented_extent_chunks()
    {
        var extents = Enumerable.Range(0, 600).Select(i => new PhysicalPageExtent(new(1 + i * 2), 1)).ToArray();
        var entry = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), new("ordered-\u03bb-\u6570\u636e", 3),
            new(1), 7, extents);
        var directory = new PhysicalPageSpaceDirectory(7, [entry]);
        var codec = new PhysicalPageSpaceDirectoryCodec();
        var transaction = new PhysicalPageSpaceAllocator(directory, [], 1201).BeginTransaction(Guid.NewGuid());
        var encoded = codec.Encode(directory, transaction, [], []);
        var root = codec.DecodeRoot(encoded.Root.Span);
        var decoded = codec.DecodeBranches(encoded.Branches.Select(p => (p.Key, p.Value))).Single();
        Assert.AreEqual(entry.Method, decoded.Method);
        Assert.AreEqual(entry.Generation, decoded.Generation);
        Assert.AreEqual(entry.RootPage, decoded.RootPage);
        CollectionAssert.AreEqual(extents, decoded.Extents.ToArray());
        Assert.IsTrue(root.BranchPages.Count > 0);
    }

    [TestMethod]
    public void Readonly_branch_decode_owns_results_without_copying_whole_pages()
    {
        var entry = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), new("ordered", 1),
            new(1), 1, [new(new(1), 3)]);
        var directory = new PhysicalPageSpaceDirectory(1, [entry]);
        var codec = new PhysicalPageSpaceDirectoryCodec();
        var transaction = new PhysicalPageSpaceAllocator(directory, [], 4).BeginTransaction(Guid.NewGuid());
        var encoded = codec.Encode(directory, transaction, [], []);
        var branch = encoded.Branches.Single();
        byte[] bytes = branch.Value.ToArray();
        (PhysicalPageId, ReadOnlyMemory<byte>)[] input = [(branch.Key, bytes)];
        for (int i = 0; i < 10; i++)
        {
            codec.DecodeBranches(input);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            codec.DecodeBranches(input);
        }

        long average = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        Assert.IsTrue(average < bytes.Length, $"Decode allocated {average} bytes for one {bytes.Length}-byte page.");
        var decoded = codec.DecodeBranches(input).Single();
        Array.Clear(bytes);
        Assert.AreEqual(entry.Method, decoded.Method);
        CollectionAssert.AreEqual(entry.Extents.ToArray(), decoded.Extents.ToArray());
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeBranches(input));
    }

    [TestMethod]
    public void Directory_packing_accepts_an_exact_page_and_rejects_one_extra_byte()
    {
        const int entryFramingBytes = sizeof(byte) + 16 + sizeof(int) * 5 + sizeof(long) * 2;
        const int extentBytes = sizeof(long) * 2;
        int nameLength = PhysicalPageFileOptions.DefaultPageSize - SlottedDataPage.HeaderSize -
            SlottedDataPage.SlotSize - entryFramingBytes - extentBytes;
        var codec = new PhysicalPageSpaceDirectoryCodec();
        foreach (int extra in new[] { 0, 1 })
        {
            var entry = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()),
                new(new string('x', nameLength + extra), 1), new(1), 1, [new(new(1), 1)]);
            var directory = new PhysicalPageSpaceDirectory(1, [entry]);
            var transaction = new PhysicalPageSpaceAllocator(directory, [], 2)
                .BeginTransaction(Guid.NewGuid());
            if (extra != 0)
            {
                Assert.ThrowsExactly<PhysicalPageCapacityException>(() => codec.Encode(directory, transaction, [], []));
                continue;
            }
            var image = codec.Encode(directory, transaction, [], []);
            Assert.AreEqual(1, image.Branches.Count);
            var decoded = codec.DecodeBranches(image.Branches.Select(branch => (branch.Key, branch.Value)));
            Assert.AreEqual(entry.Method, decoded.Single().Method);
        }
    }

    [TestMethod]
    public void Directory_round_trips_through_checksummed_root_and_branch_pages()
    {
        var first = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()),
            new("ordered", 1), new(1), 3, [new(new(1), 5)]);
        var second = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()),
            new("append-merge", 1), new(6), 7, [new(new(6), 9)]);
        var expected = new PhysicalPageSpaceDirectory(11, [first, second]);
        var codec = new PhysicalPageSpaceDirectoryCodec();
        PhysicalPageSpaceTransaction transaction = new PhysicalPageSpaceAllocator(expected, [], 0)
            .BeginTransaction(Guid.NewGuid());

        PhysicalPageSpaceDirectoryImage image = codec.Encode(expected, transaction, [], []);
        PhysicalPageSpaceDirectoryRoot root = codec.DecodeRoot(image.Root.Span);
        IReadOnlyList<PhysicalAccessPathDirectoryEntry> entries = codec.DecodeBranches(
            image.Branches.Select(branch => (branch.Key, branch.Value)));
        var actual = new PhysicalPageSpaceDirectory(root.Generation, entries);

        Assert.AreEqual(expected.Generation, actual.Generation);
        foreach (PhysicalAccessPathDirectoryEntry expectedEntry in expected.Entries)
        {
            PhysicalAccessPathDirectoryEntry actualEntry = actual.Get(expectedEntry.AccessPathId);
            Assert.AreEqual(expectedEntry.Method, actualEntry.Method);
            Assert.AreEqual(expectedEntry.RootPage, actualEntry.RootPage);
            Assert.AreEqual(expectedEntry.Generation, actualEntry.Generation);
            CollectionAssert.AreEqual(expectedEntry.Extents.ToArray(), actualEntry.Extents.ToArray());
        }
        Assert.AreEqual(image.BranchPageIds.Single(), root.BranchPages.Single());
        Assert.AreEqual(image.StandbyBranchPageIds.Single(), root.StandbyBranchPages.Single());
        Assert.AreNotEqual(root.BranchPages.Single(), root.StandbyBranchPages.Single());
    }

    [TestMethod]
    public void Directory_generations_alternate_within_the_reserved_double_buffer()
    {
        var entry = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()),
            new("ordered", 1), new(1), 3, [new(new(1), 1)]);
        var directory = new PhysicalPageSpaceDirectory(4, [entry]);
        var codec = new PhysicalPageSpaceDirectoryCodec();
        PhysicalPageSpaceTransaction firstTransaction = new PhysicalPageSpaceAllocator(
            directory, [], 2).BeginTransaction(Guid.NewGuid());
        PhysicalPageSpaceDirectoryImage first = codec.Encode(
            directory, firstTransaction, [], []);
        PhysicalPageId[] reserved = first.BranchPageIds.Concat(first.StandbyBranchPageIds).ToArray();
        long physicalPageCount = reserved.Max(page => page.Value) + 1;
        PhysicalPageSpaceTransaction secondTransaction = new PhysicalPageSpaceAllocator(
            directory, reserved, physicalPageCount).BeginTransaction(Guid.NewGuid());

        PhysicalPageSpaceDirectoryImage second = codec.Encode(directory, secondTransaction,
            first.BranchPageIds, first.StandbyBranchPageIds);

        CollectionAssert.AreEqual(first.StandbyBranchPageIds.ToArray(), second.BranchPageIds.ToArray());
        CollectionAssert.AreEqual(first.BranchPageIds.ToArray(), second.StandbyBranchPageIds.ToArray());
        Assert.AreEqual(physicalPageCount,
            second.BranchPageIds.Concat(second.StandbyBranchPageIds).Max(page => page.Value) + 1);
    }

    [TestMethod]
    public void Corrupt_directory_root_fails_closed()
    {
        var codec = new PhysicalPageSpaceDirectoryCodec();
        var empty = new PhysicalPageSpaceDirectory(0, []);
        PhysicalPageSpaceTransaction transaction = new PhysicalPageSpaceAllocator(empty, [], 0)
            .BeginTransaction(Guid.NewGuid());
        PhysicalPageSpaceDirectoryImage image = codec.Encode(empty, transaction, [], []);
        byte[] corrupt = image.Root.ToArray();
        corrupt[^1] ^= byte.MaxValue;

        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeRoot(corrupt));
    }

    [TestMethod]
    public void Fragmented_directory_entry_round_trips_across_multiple_records_and_pages()
    {
        const int extentCount = 1_200;
        PhysicalPageExtent[] extents = Enumerable.Range(0, extentCount)
            .Select(index => new PhysicalPageExtent(new(index * 2L + 1), 1)).ToArray();
        var entry = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()),
            new("append-merge", 1), extents[0].FirstPage, 9, extents);
        var expected = new PhysicalPageSpaceDirectory(12, [entry]);
        var codec = new PhysicalPageSpaceDirectoryCodec();
        PhysicalPageSpaceTransaction transaction = new PhysicalPageSpaceAllocator(
            expected, [], extentCount * 2L + 1).BeginTransaction(Guid.NewGuid());

        PhysicalPageSpaceDirectoryImage image = codec.Encode(expected, transaction, [], []);
        IReadOnlyList<PhysicalAccessPathDirectoryEntry> entries = codec.DecodeBranches(
            image.Branches.Select(branch => (branch.Key, branch.Value)));

        CollectionAssert.AreEqual(extents, entries.Single().Extents.ToArray());
        Assert.IsTrue(image.Branches.Count > 1);
    }

    [TestMethod]
    public void Fragmented_append_merge_run_round_trips_across_manifest_records()
    {
        const int extentCount = 1_200;
        PhysicalPageExtent[] extents = Enumerable.Range(0, extentCount)
            .Select(index => new PhysicalPageExtent(new(index * 2L + 1), 1)).ToArray();
        byte[] filter = Enumerable.Range(0, 200_000).Select(index => (byte)(index % 251)).ToArray();
        var run = new AppendMergeRunDescriptor(7, 25_000, extents, new byte[] { 1 },
            new byte[] { 9 }, new(5, filter.Length * 8, filter));
        var directory = new PhysicalPageSpaceDirectory(1, []);
        PhysicalPageSpaceTransaction transaction = new PhysicalPageSpaceAllocator(
            directory, [], extentCount * 2L + 1).BeginTransaction(Guid.NewGuid());
        PhysicalAccessPathAllocationScope allocation = transaction.OpenAccessPath(
            new(Guid.NewGuid()), new("append-merge", 1), -1);
        var codec = new AppendMergeManifestCodec();

        AppendMergeManifestImage image = codec.Encode(Guid.NewGuid(), 8, [run], [], allocation);
        AppendMergeManifestBranch decoded = codec.DecodeBranches(
            image.Branches.Select(branch => (branch.Key, branch.Value)));

        CollectionAssert.AreEqual(extents, decoded.Runs.Single().Extents.ToArray());
        CollectionAssert.AreEqual(filter, decoded.Runs.Single().Filter.Bits.ToArray());
        Assert.IsTrue(image.Branches.Count > 1);
    }
}
