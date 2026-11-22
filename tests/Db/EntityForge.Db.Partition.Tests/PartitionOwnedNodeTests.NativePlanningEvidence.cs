using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_native_timestamp_saves_reuse_exact_published_planning_evidence()
    {
        await WithReductionOwners(async (nodes, authority, client) =>
        {
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            int reused = 0;
            SemanticCommittedChangePlanner.ReusedPlanningEvidence.Value = () => Interlocked.Increment(ref reused);
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    var request = store.CaptureSave(new("planning-evidence", "value-" + i), SemanticEntityWriteOperation.Create());
                    await client.DeliverAsync(request.OperationId, request.Export());
                }
                Assert.IsTrue(reused >= 5, $"Expected published evidence reuse after initial save; observed {reused}.");
            }
            finally
            {
                SemanticCommittedChangePlanner.ReusedPlanningEvidence.Value = null;
            }
        });
    }
}
