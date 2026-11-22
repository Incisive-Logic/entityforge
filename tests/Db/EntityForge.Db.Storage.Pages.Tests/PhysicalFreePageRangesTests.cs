using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalFreePageRangesTests
{
    [TestMethod]
    public void Indexed_ranges_match_page_set_through_fragmentation_and_reuse()
    {
        var ranges = new PhysicalFreePageRanges();
        var pages = new SortedSet<long>();
        var random = new Random(5417);
        for (int i = 0; i < 20000; i++)
        {
            if (random.Next(3) != 0)
            {
                long page = random.Next(1, 1500);
                ranges.Add(page);
                pages.Add(page);
            }
            else
            {
                long? preferred = random.Next(2) == 0 ? random.Next(1, 1500) : null;
                bool lowest = random.Next(2) == 0;
                var runs = new List<(long First, long Count)>();
                foreach (long page in pages)
                {
                    if (runs.Count > 0 && runs[^1].First + runs[^1].Count == page)
                    {
                        runs[^1] = (runs[^1].First, runs[^1].Count + 1);
                    }
                    else
                    {
                        runs.Add((page, 1));
                    }
                }
                bool found = ranges.TryTake(preferred, lowest, out long actual);
                Assert.AreEqual(pages.Count != 0, found);
                if (found)
                {
                    long expected = preferred is { } p && runs.Any(run => run.First == p) ? p :
                        lowest ? pages.Min : runs.OrderByDescending(run => run.Count).ThenBy(run => run.First).First().First;
                    Assert.AreEqual(expected, actual);
                    pages.Remove(actual);
                }
            }
            Assert.AreEqual((long)pages.Count, ranges.PageCount);
            Assert.AreEqual(pages.Count == 0 ? (long?)null : pages.Min, ranges.FirstPage);
        }
    }

    [TestMethod]
    public void Including_overlapping_ranges_merges_bridges_without_double_counting()
    {
        var left = new PhysicalFreePageRanges();
        var right = new PhysicalFreePageRanges();
        foreach (long page in new long[] { 1, 2, 5, 6, 9, 10 })
        {
            left.Add(page);
        }

        foreach (long page in new long[] { 2, 3, 4, 5, 6, 7, 8, 9 })
        {
            right.Add(page);
        }

        left.Include(right);
        left.Include(left);
        Assert.AreEqual(10L, left.PageCount);
        for (long expected = 1; expected <= 10; expected++)
        {
            Assert.IsTrue(left.TryTake(null, false, out long actual));
            Assert.AreEqual(expected, actual);
        }
        Assert.IsFalse(left.TryTake(null, false, out _));
        Assert.AreEqual(0L, left.PageCount);
        Assert.AreEqual(8L, right.PageCount);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => left.Add(0));
        Assert.ThrowsException<OverflowException>(() => left.Add(long.MaxValue));
        Assert.AreEqual(0L, left.PageCount);
        left.Add(1);
        left.Add(long.MaxValue - 1);
        Assert.AreEqual(2L, left.PageCount);
        Assert.IsTrue(left.TryTake(long.MaxValue - 1, false, out long high));
        Assert.AreEqual(long.MaxValue - 1, high);
    }
}
