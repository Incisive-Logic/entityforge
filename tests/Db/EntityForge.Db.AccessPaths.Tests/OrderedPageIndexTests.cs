using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.AccessPaths.Tests;

[TestClass]
public sealed class OrderedPageIndexTests
{
    private const int SmallNodeCapacity = 4;
    private const int EntryCount = 200;
    private const int CursorPageSize = 7;

    [TestMethod]
    public void Insert_splits_leaf_and_branch_pages_without_losing_order()
    {
        OrderedPageIndex<int, int> index = Create();
        foreach (int value in Enumerable.Range(0, EntryCount).Reverse())
        {
            Assert.IsTrue(index.Add(value, value));
        }

        Assert.IsTrue(index.PageCount > 2);
        CollectionAssert.AreEqual(Enumerable.Range(0, EntryCount).ToArray(),
            index.Snapshot().Select(item => item.Key).ToArray());
    }

    [TestMethod]
    public void Composite_key_preserves_distinct_duplicate_values_and_rejects_exact_duplicate()
    {
        OrderedPageIndex<int, int> index = Create();
        Assert.IsTrue(index.Add(1, 30));
        Assert.IsTrue(index.Add(1, 10));
        Assert.IsTrue(index.Add(1, 20));
        Assert.IsFalse(index.Add(1, 20));

        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, index.Find(1).ToArray());
    }

    [TestMethod]
    public void Remove_borrows_and_merges_underfilled_pages()
    {
        OrderedPageIndex<int, int> index = Create();
        foreach (int value in Enumerable.Range(0, EntryCount))
        {
            index.Add(value, value);
        }

        int splitPageCount = index.PageCount;

        foreach (int value in Enumerable.Range(0, EntryCount - 3))
        {
            Assert.IsTrue(index.Remove(value, value));
        }

        Assert.IsTrue(index.PageCount < splitPageCount);
        CollectionAssert.AreEqual(new[] { 197, 198, 199 },
            index.Snapshot().Select(item => item.Key).ToArray());
    }

    [TestMethod]
    public void Cursor_pagination_is_stable_across_duplicate_keys()
    {
        OrderedPageIndex<int, int> index = Create();
        foreach (int key in Enumerable.Range(0, 13))
        {
            foreach (int value in Enumerable.Range(0, 3))
            {
                index.Add(key, value);
            }
        }

        var actual = new List<KeyValuePair<int, int>>();
        OrderedIndexCursor<int, int>? cursor = null;
        do
        {
            OrderedIndexPage<int, int> page = index.ReadPage(CursorPageSize, cursor: cursor);
            actual.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        CollectionAssert.AreEqual(index.Snapshot().ToArray(), actual.ToArray());
    }

    [TestMethod]
    public void Reverse_range_traversal_honours_inclusive_bounds()
    {
        OrderedPageIndex<int, int> index = Create();
        foreach (int value in Enumerable.Range(0, 20))
        {
            index.Add(value, value);
        }

        OrderedIndexPage<int, int> page = index.ReadPage(20,
            OrderedIndexRange<int>.Between(5, 9), OrderedScanDirection.Reverse);

        CollectionAssert.AreEqual(new[] { 9, 8, 7, 6, 5 },
            page.Items.Select(item => item.Key).ToArray());
    }

    [TestMethod]
    public void Snapshot_rebuild_recovers_the_same_ordered_access_path()
    {
        OrderedPageIndex<int, int> original = Create();
        foreach (int value in Enumerable.Range(0, EntryCount).Reverse())
        {
            original.Add(value % 17, value);
        }

        OrderedPageIndex<int, int> recovered = OrderedPageIndex<int, int>.Rebuild(
            original.Snapshot(), options: Options());

        CollectionAssert.AreEqual(original.Snapshot().ToArray(), recovered.Snapshot().ToArray());
        Assert.AreEqual(original.Count, recovered.Count);
    }

    [TestMethod]
    public void Cursor_cannot_be_reused_in_the_opposite_direction()
    {
        OrderedPageIndex<int, int> index = Create();
        foreach (int value in Enumerable.Range(0, 10))
        {
            index.Add(value, value);
        }

        OrderedIndexCursor<int, int> cursor = index.ReadPage(2).NextCursor!;

        Assert.ThrowsException<ArgumentException>(() => index.ReadPage(2,
            direction: OrderedScanDirection.Reverse, cursor: cursor));
    }

    [TestMethod]
    public void Cursor_cannot_cross_an_access_path_generation()
    {
        OrderedPageIndex<int, int> index = Create();
        foreach (int value in Enumerable.Range(0, 10))
        {
            index.Add(value, value);
        }

        OrderedIndexCursor<int, int> cursor = index.ReadPage(2).NextCursor!;

        index.Add(11, 11);

        Assert.ThrowsException<ArgumentException>(() => index.ReadPage(2, cursor: cursor));
    }

    [TestMethod]
    public void Mixed_insert_and_delete_matches_the_composite_key_reference_order()
    {
        const int operationCount = 2_000;
        const int keyCardinality = 47;
        const int valueCardinality = 113;
        const int randomSeed = 7241;
        OrderedPageIndex<int, int> index = Create();
        var expected = new SortedSet<(int Key, int Value)>();
        var random = new Random(randomSeed);
        for (int operation = 0; operation < operationCount; operation++)
        {
            int key = random.Next(keyCardinality);
            int value = random.Next(valueCardinality);
            bool add = random.Next(2) == 0;
            Assert.AreEqual(add ? expected.Add((key, value)) : expected.Remove((key, value)),
                add ? index.Add(key, value) : index.Remove(key, value));
        }

        CollectionAssert.AreEqual(expected.ToArray(),
            index.Snapshot().Select(item => (item.Key, item.Value)).ToArray());
    }

    private static OrderedPageIndex<int, int> Create() => new(options: Options());

    private static OrderedPageIndexOptions Options() => new(SmallNodeCapacity, SmallNodeCapacity);
}
