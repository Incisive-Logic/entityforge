using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Split_scoped_conflicts_survive_checkpoint_and_preserve_disjoint_reads()
    {
        var role = new GraphPropertyShard.Role(1, 1);
        var runtime = new GraphPropertyShard.Runtime(role);
        Guid left = Guid.NewGuid(), right = Guid.NewGuid();
        byte[] Payload(GraphTransaction tx) => GraphPropertyShard.EncodeBody(role, GraphTransactionCodec.Encode(tx));
        var seed = new GraphTransaction(Guid.NewGuid(), [new CreateVertex(new(left), []), new CreateVertex(new(right), [])]);
        await runtime.ApplyAsync(new(1), seed.TransactionId, Payload(seed));
        var a = new GraphTransaction(Guid.NewGuid(), [new SetVertexProperty(new(left), "name", new DatabaseValue.String("a"))], new(1), [new WholeVertexRead(new(left))]);
        var b = new GraphTransaction(Guid.NewGuid(), [new SetVertexProperty(new(right), "name", new DatabaseValue.String("b"))], new(1), [new WholeVertexRead(new(right))]);
        var reservations = (IDatabaseProfilePartitionReservations)runtime;
        Assert.IsTrue(reservations.DescribeTransaction(a.TransactionId, Payload(a)).IsScopedPreparation);
        Assert.IsFalse(reservations.DescribeTransaction(a.TransactionId, Payload(a)).ConflictsWith(reservations.DescribeTransaction(b.TransactionId, Payload(b))));
        await runtime.ValidateAsync(a.TransactionId, Payload(a));
        await runtime.ApplyAsync(new(2), b.TransactionId, Payload(b));
        await runtime.ValidateAsync(a.TransactionId, Payload(a));
        await runtime.ApplyAsync(new(3), a.TransactionId, Payload(a));
        await Assert.ThrowsExceptionAsync<DatabasePartitionConflictException>(() => runtime.ValidateAsync(a.TransactionId, Payload(a)).AsTask());
        var checkpoint = await runtime.CaptureCheckpointAsync();
        var restored = new GraphPropertyShard.Runtime(role);
        await restored.RestoreCheckpointAsync(new(3), checkpoint);
        await Assert.ThrowsExceptionAsync<DatabasePartitionConflictException>(() => restored.ValidateAsync(a.TransactionId, Payload(a)).AsTask());
        using (reservations.EnterPreparedCompletion(a.TransactionId))
        {
            await runtime.ValidateAsync(a.TransactionId, Payload(a));
        }

        await Assert.ThrowsExceptionAsync<DatabasePartitionConflictException>(() => runtime.ValidateAsync(a.TransactionId, Payload(a)).AsTask());
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Split_distinct_concurrent_saves_complete_and_same_bag_rejection_is_terminal(bool live)
    {
        const int writers = 32;
        var routing = DistributedRoutingBinding.GraphTopologyProperties(new(1), [new("a"), new("b"), new("c")]);
        var seed = new Authority();
        var placements = routing.Shards.Select((shard, i) => new Authority
        {
            Lease = seed.Lease with
            {
                Placement = seed.Lease.Placement with
                {
                    Claim = seed.Lease.Placement.Claim with
                    {
                        Shard = shard,
                        Owner = new("owner-" + i)
                    }
                }
            }
        }).ToArray();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"), seed.Lease.Placement.Claim.Generation.Value,
            DatabaseProfileId.PropertyGraph, routing, live);
        var nodes = new List<PartitionOwnedNode>();
        try
        {
            for (int i = 0; i < placements.Length; i++)
            {
                nodes.Add(await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node-" + i),
                DatabaseProfileId.PropertyGraph, placements[i].Lease, routing, placements[i], Options, transactionAuthority: lifetime.Authority, timestampClock: ReadClock));
            }

            var client = new PartitionApplicationClient(lifetime.Authority, routing, nodes.ToArray(), ReadClock);
            using var http = new HttpClient();
            var store = new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>();
            for (int i = 0; i < writers; i++)
            {
                var capture = store.CaptureSave(new Item("concurrent-" + i, "before"), new(EntityOperationIds.New()));
                await client.DeliverAsync(capture.OperationId, capture.Export());
            }
            var requests = Enumerable.Range(0, writers).Select(i => store.CaptureSave(new Item("concurrent-" + i, "after"), new(EntityOperationIds.New()))).ToArray();
            await Task.WhenAll(requests.Select(r => client.DeliverAsync(r.OperationId, r.Export()).AsTask()));
            var answer = await client.QueryAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n) RETURN n"), default);
            Assert.AreEqual(writers, answer.Value.GetProperty("matches").GetArrayLength());
            Assert.IsFalse(answer.Value.GetRawText().Contains("before"));
            await client.CloseReadEpochAsync();
            var a = store.CaptureSave(new Item("concurrent-0", "winner"), new(EntityOperationIds.New()));
            var b = store.CaptureSave(new Item("concurrent-0", "loser"), new(EntityOperationIds.New()));
            await client.PrepareAsync(a.OperationId, [a.Export()], default);
            await Assert.ThrowsExceptionAsync<EntityOperationAbortedException>(() => client.DeliverAsync(b.OperationId, b.Export()).AsTask());
            Assert.AreEqual(EntityOperationState.Aborted, (await client.ResolveAsync(b.OperationId, default)).State);
            await client.DeliverAsync(a.OperationId, a.Export());
            await Assert.ThrowsExceptionAsync<EntityOperationAbortedException>(() => client.DeliverAsync(b.OperationId, b.Export()).AsTask());
            await client.CloseReadEpochAsync();
            await PartitionRetention.RetainAsync(lifetime.Authority, nodes, DateTimeOffset.UtcNow);
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }
}
