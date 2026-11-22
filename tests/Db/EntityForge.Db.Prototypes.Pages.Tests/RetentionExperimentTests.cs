using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class RetentionExperimentTests
{
    [TestMethod]
    public async Task Graph_reclaims_expired_versions_while_semantic_preserves_evidence_history()
    {
        RetentionExperimentResult result = await new RetentionExperiment().RunAsync(
            new RetentionExperimentOptions(100, 5, 3));
        RetentionProfileResult graph = result.Results.Single(item => item.Profile == RetentionProfileKind.Graph);
        RetentionProfileResult semantic = result.Results.Single(item => item.Profile == RetentionProfileKind.Semantic);

        Assert.AreEqual(100, graph.RetainedAfterSnapshotRelease);
        Assert.AreEqual(0, graph.EvidenceHistoryCount);
        Assert.AreEqual(500, semantic.RetainedAfterSnapshotRelease);
        Assert.AreEqual(400, semantic.EvidenceHistoryCount);
        Assert.AreNotEqual(graph.LogicalDigest, semantic.LogicalDigest);
    }

    [TestMethod]
    public void Active_snapshot_keeps_every_version_that_can_still_be_visible()
    {
        RetainedVersion[] versions =
        [
            new(1, 1, 5, "old", null, RetainedVersionKind.QueryableVersion),
            new(1, 5, null, "current", null, RetainedVersionKind.QueryableVersion)
        ];
        RetainedVersion[] retained = RetentionExperiment.Reclaim(versions, 4, RetentionProfileKind.Graph);
        Assert.AreEqual(2, retained.Length);
        Assert.IsTrue(retained.All(item => item.Kind == RetainedVersionKind.QueryableVersion));
    }
}
