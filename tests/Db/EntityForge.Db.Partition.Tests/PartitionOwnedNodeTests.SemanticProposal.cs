using System.Collections.Concurrent;
using System.Diagnostics;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_owner_proposal_uses_one_remote_call_and_releases_every_pin()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var phases = new ConcurrentBag<string>();
            using var listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == DistributedReadTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.OperationName == "partition.rpc" && activity.GetTagItem("entityforge.phase") is string phase)
                    {
                        phases.Add(phase);
                    }
                }
            };
            ActivitySource.AddActivityListener(listener);
            var transaction = remote.CreateTransaction();
            const int Count = 64;
            for (int i = 0; i < Count; i++)
            {
                transaction.Save(new Item("fused-" + i, "value-" + i));
            }

            await transaction.CommitAsync();
            Assert.AreEqual(1, phases.Count(phase => phase == "propose-semantic"));
            Assert.AreEqual(0, phases.Count(phase => phase == "acquire-snapshot-cut"));
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
            for (int i = 0; i < Count; i++)
            {
                Assert.AreEqual("value-" + i, (await remote.ReadSemanticAsync<Item>("fused-" + i)).Value!.Name);
            }

            await client.CloseReadEpochAsync();
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }

    [TestMethod]
    public async Task Semantic_owner_proposal_failure_and_cancellation_leave_no_pin_and_allow_later_success()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            var owner = nodes[0];
            await Assert.ThrowsAsync<InvalidDataException>(() => owner.ProposeSemanticAsync([1, 2, 3],
                PartitionReadCuts.MaximumDuration).AsTask());
            Assert.AreEqual(0, owner.UnreleasedReadCutCount);
            var badInput = PartitionSemanticRequests.Encode([new byte[] { 1, 2, 3 }]);
            await Assert.ThrowsAsync<InvalidDataException>(() => owner.ProposeSemanticAsync(badInput,
                PartitionReadCuts.MaximumDuration).AsTask());
            Assert.AreEqual(0, owner.UnreleasedReadCutCount);
            var transaction = remote.CreateTransaction().Save(new Item("fused-later", "value"));
            var captured = PartitionSemanticRequests.Encode(transaction.Capture());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => owner.ProposeSemanticAsync(captured,
                PartitionReadCuts.MaximumDuration, cancellation.Token).AsTask());
            await Assert.ThrowsAsync<InvalidDataException>(() => owner.ProposeSemanticAsync(captured, TimeSpan.Zero).AsTask());
            await Assert.ThrowsAsync<InvalidDataException>(() => owner.ProposeSemanticAsync(captured,
                PartitionReadCuts.MaximumDuration + TimeSpan.FromTicks(1)).AsTask());
            Assert.AreEqual(0, owner.UnreleasedReadCutCount);
            // Mapping setup remains part of the ordinary transaction path.
            await transaction.CommitAsync();
            Assert.AreEqual("value", (await remote.ReadSemanticAsync<Item>("fused-later")).Value!.Name);
            await client.CloseReadEpochAsync();
            Assert.IsTrue(nodes.All(node => node.UnreleasedReadCutCount == 0));
        });
    }

    [TestMethod]
    public async Task Semantic_local_proposal_matches_snapshot_protocol_bytes_with_pending_unrelated_work()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            await remote.CreateTransaction().Save(new Item("proposal-mapping", "registered")).CommitAsync();
            var pending = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                pending.Save(new Item("pending-proposal-" + i, "pending"));
            }

            await client.PrepareAsync(pending.TransactionId, pending.Capture(), default);
            var transaction = remote.CreateTransaction();
            for (int i = 0; i < 64; i++)
            {
                transaction.Save(new Item("oracle-proposal-" + i, "value"));
            }

            var captured = PartitionSemanticRequests.Encode(transaction.Capture());
            foreach (var owner in nodes)
            {
                var query = new PartitionReadQuery("semantic-expansion", Captured: captured);
                var old = await PartitionRemoteRead.ExecuteSemanticOwnerAsync(owner, owner.Routing,
                    (read, token) => read.ReadAsync(owner.Domain.Partition.Value, query, token), default, initial: query);
                var current = await owner.ProposeSemanticAsync(captured, PartitionReadCuts.MaximumDuration);
                Assert.AreEqual(old.Position.Positions.Single(), current.Position);
                CollectionAssert.AreEqual(old.Value, current.Envelope);
                Assert.AreEqual(0, owner.UnreleasedReadCutCount);
            }
            await pending.CommitAsync();
            await transaction.CommitAsync();
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual("value", (await remote.ReadSemanticAsync<Item>("oracle-proposal-" + i)).Value!.Name);
            }

            await client.CloseReadEpochAsync();
        });
    }

}
