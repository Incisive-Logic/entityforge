using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class OwnershipCollectionAllocationTests
{
    [TestMethod]
    public void Combined_ownership_matches_constructor_oracle_and_preserves_input_isolation()
    {
        var random = new Random(871);
        var id = new PhysicalAccessPathId(Guid.NewGuid());
        var method = new PhysicalAccessMethodKind("test", 1);
        for (int sample = 0; sample < 100; sample++)
        {
            var pages = Enumerable.Range(1, 300).Where(_ => random.Next(3) != 0).ToArray();
            var left = pages.Where((_, i) => i % 2 == 0).Select(p => new PhysicalPageExtent(new(p), 1)).ToArray();
            var right = pages.Where((_, i) => i % 2 != 0).Select(p => new PhysicalPageExtent(new(p), 1)).ToArray();
            var expected = new PhysicalAccessPathDirectoryEntry(id, method, new(pages[0]), 2, left.Concat(right).ToArray());
            var actual = PhysicalAccessPathDirectoryEntry.Combine(id, method, new(pages[0]), 2, left, right);
            CollectionAssert.AreEqual(expected.Extents.ToArray(), actual.Extents.ToArray());
            left[0] = new(new(1000), 1);
            CollectionAssert.AreEqual(expected.Extents.ToArray(), actual.Extents.ToArray());
        }
        Assert.ThrowsException<PhysicalPageSpaceException>(() => PhysicalAccessPathDirectoryEntry.Combine(id, method, new(1), 1, [new(new(1), 3)], [new(new(2), 1)]));
        Assert.ThrowsException<ArgumentException>(() => PhysicalAccessPathDirectoryEntry.Combine(id, method, new(9), 1, [new(new(1), 3)], []));
        var unsorted = PhysicalAccessPathDirectoryEntry.Combine(id, method, new(1), 1, [new(new(3), 1), new(new(1), 1)], [new(new(2), 1)]);
        Assert.AreEqual(new PhysicalPageExtent(new(1), 3), unsorted.Extents.Single());
    }

    [TestMethod]
    public void Coalescing_preserves_exact_ownership_for_shuffled_duplicates_and_gaps()
    {
        var random = new Random(712);
        for (int sample = 0; sample < 100; sample++)
        {
            var pages = Enumerable.Range(0, sample * 7).Select(_ => new PhysicalPageId(random.Next(1, 400))).ToArray();
            var original = pages.ToArray();
            var extents = PhysicalAccessPathAllocationScope.Coalesce(pages);
            var expanded = extents.SelectMany(e => Enumerable.Range(0, (int)e.PageCount).Select(i => e.FirstPage.Value + i)).ToArray();
            CollectionAssert.AreEqual(pages.Select(p => p.Value).Distinct().Order().ToArray(), expanded);
            CollectionAssert.AreEqual(original, pages);
            for (int i = 1; i < extents.Count; i++)
            {
                Assert.IsTrue(extents[i - 1].EndExclusive < extents[i].FirstPage.Value);
            }
        }
        var edge = PhysicalAccessPathAllocationScope.Coalesce([new(long.MaxValue - 1), new(long.MaxValue - 2), new(long.MaxValue - 1)]);
        Assert.AreEqual(long.MaxValue, edge.Single().EndExclusive);
    }

    [TestMethod]
    public void Coalescing_allocates_only_private_page_copy_and_exact_extent_output()
    {
        var pages = Enumerable.Range(1, 4096).Reverse().Select(i => new PhysicalPageId(i)).ToArray();
        _ = PhysicalAccessPathAllocationScope.Coalesce(pages);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Assert.AreEqual(1, PhysicalAccessPathAllocationScope.Coalesce(pages).Count);
        }

        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 100L * pages.Length * 12);
    }

    [TestMethod]
    public void Directory_ordered_view_is_immutable_owned_and_reused_without_allocation()
    {
        var entries = Enumerable.Range(1, 16).Select(i => new PhysicalAccessPathDirectoryEntry(
            new(Guid.NewGuid()), new("test", 1), new(i), 1, [new(new(i), 1)])).ToArray();
        var directory = new PhysicalPageSpaceDirectory(1, entries);
        var view = directory.Entries;
        CollectionAssert.AreEqual(entries.OrderBy(e => e.AccessPathId.Value).ToArray(), view.ToArray());
        var original = entries[0];
        entries[0] = entries[1];
        Assert.AreSame(original, directory.Get(original.AccessPathId));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PhysicalAccessPathDirectoryEntry>)view)[0] = entries[1]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10000; i++)
        {
            Assert.AreSame(view, directory.Entries);
        }

        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 1024);
        var next = new PhysicalPageSpaceDirectory(2, view);
        Assert.AreNotSame(view, next.Entries);
        CollectionAssert.AreEqual(view.ToArray(), next.Entries.ToArray());
    }
}
