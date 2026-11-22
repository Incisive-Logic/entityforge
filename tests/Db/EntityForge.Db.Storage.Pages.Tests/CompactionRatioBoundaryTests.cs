using System.Numerics;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class CompactionRatioBoundaryTests
{
    [TestMethod]
    public void Adjacent_selection_matches_exact_ratio_arithmetic_at_int64_boundaries()
    {
        foreach (int ratio in new[] { 1, 4, int.MaxValue })
        {
            var planner = new AppendMergeCompactionPlanner(new(compactionRunThreshold: 2,
                compactionFanIn: 2, maximumCompactionSizeRatio: ratio));
            long boundary = long.MaxValue / ratio;
            foreach (long smallest in new[] { 1L, boundary, Math.Min(boundary, long.MaxValue - 1) + 1 })
            {
                foreach (long largest in new[] { smallest, long.MaxValue })
                {
                    AppendMergeRunDescriptor[] runs = [Run(2, smallest), Run(1, largest)];
                    bool expected = new BigInteger(largest) <= new BigInteger(smallest) * ratio;
                    var selected = planner.Select(runs, maximumInputPages: 2);
                    Assert.AreEqual(expected ? 2 : 0, selected.Count, $"{smallest}, {largest}, ratio {ratio}");
                    if (expected)
                    {
                        CollectionAssert.AreEqual(runs, selected.ToArray());
                    }
                    Assert.AreEqual(0, planner.Select(runs, maximumInputPages: 1).Count);
                }
            }
        }
    }

    private static AppendMergeRunDescriptor Run(long generation, long entries) => new(generation, entries,
        [new(new(generation), 1)], new byte[] { 1 }, new byte[] { 2 }, new(1, 8, new byte[] { 1 }));
}
