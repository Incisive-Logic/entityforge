using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class PropertyTemperatureExperimentTests
{
    [TestMethod]
    public void Both_profiles_preserve_results_across_distinct_physical_representations()
    {
        PropertyTemperatureExperimentResult result = new PropertyTemperatureExperiment().Run();

        Assert.AreEqual(2, result.Representations.Select(item => item.Profile).Distinct().Count());
        foreach (IGrouping<string, PropertyTemperatureMetrics> profile in
                 result.Representations.GroupBy(item => item.Profile))
        {
            Assert.AreEqual(3, profile.Count());
            Assert.AreEqual(1, profile.Select(item => item.ResultDigest).Distinct().Count());
            PropertyTemperatureMetrics cold = profile.Single(item => item.Representation == "ColdJson");
            PropertyTemperatureMetrics indexed = profile.Single(item => item.Representation == "IndexedJson");
            PropertyTemperatureMetrics hot = profile.Single(item => item.Representation == "HotScalar");
            Assert.IsTrue(indexed.QueryPageProbes < cold.QueryPageProbes);
            Assert.IsTrue(hot.CanonicalFetches < indexed.CanonicalFetches);
            Assert.AreEqual(1_000, cold.BucketRewrites);
            Assert.AreEqual(1_000, indexed.BucketRewrites);
            Assert.AreEqual(1_000, hot.IndependentScalarWrites);
            Assert.AreEqual(0, hot.BucketRewrites);
        }
    }

    [TestMethod]
    public void Promotion_and_demotion_capture_deltas_and_preserve_semantic_evidence()
    {
        PropertyTemperatureExperimentResult result = new PropertyTemperatureExperiment().Run();

        Assert.AreEqual(6, result.PlacementChanges.Count);
        foreach (PropertyPlacementChangeEvidence change in result.PlacementChanges)
        {
            Assert.IsFalse(change.LostUpdates);
            Assert.IsTrue(change.IdentityPreserved);
            Assert.IsTrue(change.ProvenancePreserved);
            Assert.AreEqual(change.ExpectedDigest, change.MaterializedDigest);
            Assert.AreEqual(5_000, change.BackfilledEntities);
            Assert.AreEqual(1_000, change.CapturedDeltas);
        }
        Assert.IsTrue(result.PlacementChanges.Any(item =>
            item.From == "HotScalar" && item.To == "ColdJson"));
    }
}
