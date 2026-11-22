using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class GroupedDurabilityCoordinatorTests
{
    [TestMethod]
    public async Task Concurrent_independent_writers_are_grouped_and_all_recover()
    {
        ConcurrentDurabilityExperimentResult result = await new ConcurrentDurabilityExperiment().RunAsync();

        Assert.AreEqual(2, result.Results.Count);
        Assert.IsTrue(result.Results.All(item => item.MaximumBatchSize > 1));
        Assert.IsTrue(result.Results.All(item => item.BatchCount < item.WriterCount));
        Assert.IsTrue(result.Results.All(item => item.LogicalDigest.Length == 64));
        ConcurrentDurabilityCaseResult redo = result.Results.Single(item =>
            item.Strategy == DurabilityStrategyKind.RedoWal);
        ConcurrentDurabilityCaseResult noOverwrite = result.Results.Single(item =>
            item.Strategy == DurabilityStrategyKind.NoOverwrite);
        Assert.AreEqual(redo.BatchCount, redo.FlushCount);
        Assert.AreEqual(noOverwrite.BatchCount * 2, noOverwrite.FlushCount);
    }
}
