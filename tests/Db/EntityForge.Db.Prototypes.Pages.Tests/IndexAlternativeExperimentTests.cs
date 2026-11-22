using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class IndexAlternativeExperimentTests
{
    [TestMethod]
    public void Scalar_and_adjacency_alternatives_return_identical_logical_results()
    {
        IndexAlternativeExperimentResult result = new IndexAlternativeExperiment().Run();

        Assert.AreEqual(1, result.ScalarIndexes.Select(item => item.ResultDigest).Distinct().Count());
        Assert.AreEqual(1, result.AdjacencyIndexes.Select(item => item.ResultDigest).Distinct().Count());
        ScalarIndexResult ordered = result.ScalarIndexes.Single(item =>
            item.Index == nameof(OrderedPagePrototypeIndex));
        ScalarIndexResult lsm = result.ScalarIndexes.Single(item =>
            item.Index == nameof(LogStructuredPrototypeIndex));
        Assert.IsTrue(lsm.ModeledBytesWritten < ordered.ModeledBytesWritten);
        Assert.IsTrue(lsm.ModeledPageProbes > ordered.ModeledPageProbes);
        AdjacencyIndexResult ordinary = result.AdjacencyIndexes.Single(item =>
            item.Index == "DirectionalAdjacencyList");
        AdjacencyIndexResult dense = result.AdjacencyIndexes.Single(item =>
            item.Index == "DenseBlockedAdjacency");
        Assert.IsTrue(dense.DensePaginationBytes < ordinary.DensePaginationBytes);
        Assert.IsTrue(dense.FullTraversalBytes > ordinary.FullTraversalBytes);
        Assert.AreEqual(40_000, ordered.MutationCount);
        Assert.AreEqual(5_500, ordered.QueryCount);
        Assert.AreEqual(70_000, ordinary.EdgeCount);
    }
}
