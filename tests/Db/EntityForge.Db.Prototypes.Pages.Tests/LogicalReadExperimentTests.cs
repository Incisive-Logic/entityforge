using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class LogicalReadExperimentTests
{
    [TestMethod]
    public async Task Same_logical_workload_is_deterministic_for_every_page_size()
    {
        var experiment = new LogicalReadExperiment();
        LogicalReadExperimentResult first = await experiment.RunAsync();
        LogicalReadExperimentResult second = await experiment.RunAsync();

        Assert.AreEqual(first.ToJson(), second.ToJson());
        CollectionAssert.AreEqual(LogicalReadExperimentOptions.Default.PageSizes.ToArray(),
            first.Results.Select(result => result.PageSize).ToArray());
        Assert.IsTrue(first.Results.All(result => result.SourcePageReads > 0));
        Assert.IsTrue(first.Results.All(result => result.PhysicalBytesPerLogicalQuery > 0));
        Assert.IsTrue(first.Results.All(result => result.CacheHitRate is >= 0 and <= 1));
    }
}
