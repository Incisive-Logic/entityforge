using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Cached_negative_root_membership_needs_native_progress_not_only_placement_validation()
    {
        await WithQueryHomePair(async (first, second, client, routing, decisions) =>
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (routing.RouteGraphVertex(id) != second.Domain.Partition);
            const string statement = "MATCH (n:Root) WHERE n.key = \"selected\" RETURN n";
            var cut = await second.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            var selection = new PartitionQueryExchangeRequest("gql", statement, GraphFrontier: new(0), GraphFragments: true);
            try
            {
                var absent = await second.ReadQueryJobValueAsync(cut, new("query-job", QueryJob:
                    new(Guid.NewGuid(), selection, Finish: true, Home: client.QueryHome!.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
                Assert.IsTrue(absent.Valid);
                Assert.IsNotNull(absent.Pending);
                Assert.AreEqual(0, absent.Value!.Element.GetProperty("relation").GetProperty("vertices").GetArrayLength());
                var operation = EntityOperationIds.New();
                await second.Authority.ExecuteAsync(async () =>
                {
                    await second.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                        [new CreateVertex(new(id), ["Root"]), new SetVertexProperty(new(id), "key", new DatabaseValue.String("selected"))],
                        second.Kernel.CurrentVersion, [])));
                    return true;
                });
                // The placement is still valid. It cannot certify that a retained
                // negative answer describes the current native generation.
                await client.QueryHome.ValidateAsync([absent.Pending!], default);
                var stale = await second.ReadQueryJobValueAsync(cut, new("query-job", QueryJob:
                    new(Guid.NewGuid(), selection, Finish: true, Home: client.QueryHome.Binding, Sequence: 1), ReadTime: TestQueryTime(cut)));
                Assert.IsTrue(stale.Valid);
                Assert.IsNotNull(stale.Pending);
                Assert.AreEqual(0, stale.Value!.Element.GetProperty("relation").GetProperty("vertices").GetArrayLength(), "A reused timestamp remains the original negative result, not current membership.");
                var current = await client.QueryAsync(new(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", statement), default);
                Assert.AreEqual(1, current.Value.GetProperty("matches").GetArrayLength());
            }
            finally
            {
                await second.AbandonCutAsync(cut.Id);
            }
        });
    }
}
