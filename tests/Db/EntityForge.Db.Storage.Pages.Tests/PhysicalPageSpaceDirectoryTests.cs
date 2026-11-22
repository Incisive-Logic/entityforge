using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalPageSpaceDirectoryTests
{
    private static readonly PhysicalAccessMethodKind OrderedMethod = new("ordered", 1);
    private static readonly PhysicalAccessMethodKind AppendMergeMethod = new("append-merge", 1);

    [TestMethod]
    public void Adjacent_owned_ranges_coalesce_without_claiming_holes_or_mutating_input()
    {
        PhysicalPageExtent[] input = [new(new(7), 2), new(new(2), 3), new(new(1), 1), new(new(5), 1)];
        var original = input.ToArray();
        var entry = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), OrderedMethod, new(3), 7, input);
        CollectionAssert.AreEqual(original, input);
        CollectionAssert.AreEqual(new PhysicalPageExtent[] { new(new(1), 5), new(new(7), 2) }, entry.Extents.ToArray());
        for (long page = 1; page <= 10; page++)
        {
            Assert.AreEqual(original.Any(e => e.Contains(new(page))), entry.Extents.Any(e => e.Contains(new(page))));
        }

        Assert.ThrowsExactly<PhysicalPageSpaceException>(() =>
            new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), OrderedMethod, new(1), 7,
                [new(new(1), 2), new(new(2), 2)]));
    }

    [TestMethod]
    public void Global_ownership_validation_merges_large_interleaved_entries_without_extent_sized_allocations()
    {
        const int extentCount = 10000;
        PhysicalPageExtent[] odd = Enumerable.Range(0, extentCount).Select(i => new PhysicalPageExtent(new(2L * i + 1), 1)).ToArray();
        PhysicalPageExtent[] even = Enumerable.Range(0, extentCount).Select(i => new PhysicalPageExtent(new(2L * i + 2), 1)).ToArray();
        PhysicalAccessPathDirectoryEntry[] entries = [
            new(new(Guid.NewGuid()), OrderedMethod, new(1), 1, odd),
            new(new(Guid.NewGuid()), AppendMergeMethod, new(2), 1, even)];
        for (int i = 0; i < 10; i++)
        {
            GC.KeepAlive(new PhysicalPageSpaceDirectory(i, entries));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        var directory = new PhysicalPageSpaceDirectory(10, entries);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < extentCount, $"Global validation allocated {allocated} bytes for immutable extents.");
        Assert.AreEqual(2, directory.Entries.Count);
        Assert.AreEqual(0, new PhysicalPageSpaceDirectory(0, []).Entries.Count);
    }

    [TestMethod]
    public void Entry_sorts_and_owns_extents_without_exposing_mutable_storage()
    {
        PhysicalPageExtent[] input = [new(new(10), 2), new(new(1), 2)];
        var entry = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), OrderedMethod, new(1), 1, input);
        input[0] = new(new(1), 100);
        Assert.AreEqual(1L, entry.Extents[0].FirstPage.Value);
        Assert.AreEqual(10L, entry.Extents[1].FirstPage.Value);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<PhysicalPageExtent>)entry.Extents)[0] = new(new(1), 100));
        Assert.ThrowsExactly<PhysicalPageSpaceException>(() =>
            new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), OrderedMethod, new(1), 1,
                [new(new(10), 2), new(new(1), 10)]));
    }

    [TestMethod]
    public void Directory_rejects_overlap_between_interleaved_sorted_entries()
    {
        var first = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), OrderedMethod, new(1), 1,
            [new(new(20), 2), new(new(1), 2)]);
        var second = new PhysicalAccessPathDirectoryEntry(new(Guid.NewGuid()), OrderedMethod, new(10), 1,
            [new(new(10), 11)]);
        Assert.ThrowsExactly<PhysicalPageSpaceException>(() => new PhysicalPageSpaceDirectory(1, [second, first]));
    }

    [TestMethod]
    public void Directory_accepts_disjoint_access_method_extents()
    {
        PhysicalAccessPathDirectoryEntry ordered = Entry(OrderedMethod, firstPage: 1, pageCount: 10);
        PhysicalAccessPathDirectoryEntry appendMerge = Entry(AppendMergeMethod, firstPage: 11, pageCount: 20);

        var directory = new PhysicalPageSpaceDirectory(1, [ordered, appendMerge]);

        Assert.AreEqual(2, directory.Entries.Count);
        Assert.AreEqual(ordered, directory.Get(ordered.AccessPathId));
        Assert.AreEqual(appendMerge, directory.Get(appendMerge.AccessPathId));
    }

    [TestMethod]
    public void Directory_rejects_overlapping_access_method_extents()
    {
        PhysicalAccessPathDirectoryEntry ordered = Entry(OrderedMethod, firstPage: 1, pageCount: 10);
        PhysicalAccessPathDirectoryEntry appendMerge = Entry(AppendMergeMethod, firstPage: 10, pageCount: 20);

        Assert.ThrowsExactly<PhysicalPageSpaceException>(() =>
            new PhysicalPageSpaceDirectory(1, [ordered, appendMerge]));
    }

    [TestMethod]
    public void Entry_requires_its_root_to_be_owned()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PhysicalAccessPathDirectoryEntry(
            new(Guid.NewGuid()), OrderedMethod, new(20), 1, [new(new(1), 10)]));
    }

    [TestMethod]
    public void Access_methods_cannot_claim_the_directory_root()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PhysicalPageExtent(new(0), 1));
    }

    private static PhysicalAccessPathDirectoryEntry Entry(PhysicalAccessMethodKind method,
        long firstPage, long pageCount) => new(new(Guid.NewGuid()), method,
        new(firstPage), generation: 1, [new(new(firstPage), pageCount)]);
}
