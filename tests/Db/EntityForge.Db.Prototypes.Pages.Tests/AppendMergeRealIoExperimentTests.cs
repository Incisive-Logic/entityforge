using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class AppendMergeRealIoExperimentTests
{
    private const int MaximumExpectedRunCount = 8;

    [TestMethod]
    public async Task Semantic_projection_candidates_preserve_restart_and_snapshot_results()
    {
        SemanticProjectionCandidateResult result =
            await new SemanticProjectionCandidateExperiment().RunAsync();

        Assert.IsTrue(result.OrderedAdjacency.RestartDigestMatched);
        Assert.IsTrue(result.AppendMergeAdjacency.RestartDigestMatched);
        Assert.IsTrue(result.OrderedAdjacency.RetainedSnapshotMatched);
        Assert.IsTrue(result.AppendMergeAdjacency.RetainedSnapshotMatched);
        Assert.IsTrue(result.CurrentFieldEvidence.RestartDigestMatched);
        Assert.IsTrue(result.CompactFieldEvidence.RestartDigestMatched);
        Assert.IsTrue(result.CompactFieldEvidence.ForegroundPageBytes <
            result.CurrentFieldEvidence.ForegroundPageBytes);
        Assert.IsTrue(result.AppendMergeFieldMembership.ForegroundPageBytes <
            result.OrderedFieldMembership.ForegroundPageBytes);
        Assert.IsTrue(result.AppendMergeSubjectMembership.ForegroundPageBytes <
            result.OrderedSubjectMembership.ForegroundPageBytes);
        Assert.IsTrue(result.PackedFieldMembership.RestartDigestMatched);
        Assert.IsTrue(result.PackedSubjectMembership.RestartDigestMatched);
    }

    [TestMethod]
    public async Task Immutable_runs_preserve_add_remove_duplicate_and_restart_results()
    {
        AppendMergeRealIoResult result = await new AppendMergeRealIoExperiment().RunAsync();

        Assert.AreEqual(result.ExpectedDigest, result.ActualDigest);
        Assert.IsTrue(result.RunCount is > 1 and <= MaximumExpectedRunCount);
        Assert.AreEqual(40_000, result.MutationCount);
        Assert.AreEqual(500, result.QueryCount);
        Assert.IsTrue(result.ForegroundBytes > 0);
        Assert.IsTrue(result.QueryProbes < result.RunCount * result.QueryCount);
        Assert.IsTrue(result.FilteredRunCount > 0);
        Assert.IsTrue(result.QueryBytesRead < result.ForegroundBytes * result.QueryCount);
        Assert.IsTrue(result.CompactionCount > 0);
        Assert.IsTrue(result.CompactionBytes > 0);
    }

    [TestMethod]
    public async Task Manifest_is_the_visibility_boundary_across_injected_failures()
    {
        AppendMergeCrashResult result = await new AppendMergeCrashExperiment().RunAsync();

        Assert.IsTrue(result.UnpublishedRunWasInvisible);
        Assert.IsTrue(result.PublishedRunWasVisibleAfterRestart);
        Assert.AreEqual(1, result.OrphanRunCount);
        Assert.AreEqual(1, result.ReclaimedOrphanCount);
    }

    [TestMethod]
    public async Task Size_tiered_policy_reduces_compaction_amplification()
    {
        AppendMergeCompactionComparisonResult result =
            await new AppendMergeCompactionComparisonExperiment().RunAsync();

        Assert.AreEqual(result.EagerOldest.ActualDigest, result.SizeTiered.ActualDigest);
        Assert.IsTrue(result.SizeTiered.CompactionBytes < result.EagerOldest.CompactionBytes);
        Assert.IsTrue(result.SizeTiered.QueryProbes > result.EagerOldest.QueryProbes);
    }

    [TestMethod]
    public async Task Retained_snapshot_defers_compacted_run_reclamation()
    {
        AppendMergeSnapshotRetentionResult result =
            await new AppendMergeSnapshotRetentionExperiment().RunAsync();

        Assert.IsTrue(result.SnapshotPreservedValue);
        Assert.IsTrue(result.CurrentViewAppliedTombstone);
        Assert.AreEqual(1, result.DeferredBeforeRelease);
        Assert.AreEqual(0, result.DeferredAfterRelease);
    }

    [TestMethod]
    public async Task Compaction_manifest_is_atomic_across_injected_failures()
    {
        AppendMergeCompactionCrashResult result =
            await new AppendMergeCompactionCrashExperiment().RunAsync();

        Assert.IsTrue(result.PrePublicationPreservedDigest);
        Assert.IsTrue(result.PostPublicationPreservedDigest);
        Assert.IsTrue(result.PrePublicationOrphansReclaimed > 0);
        Assert.IsTrue(result.PostPublicationOrphansReclaimed > 0);
    }
}
