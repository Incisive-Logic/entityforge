using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Decision_stream_applies_without_gateway_and_joins_exact_delivery(bool join)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var placement = new Authority();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            placement.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.PropertyGraph, routing, false);
        var authority = new TerminalAuthority(lifetime.Authority);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, transactionAuthority: authority);
        await node.EnableDecisionNotificationsAsync();
        await authority.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Guid id = EntityOperationIds.New();
        var other = new PartitionExecutionDomain(node.Domain.Generation, new("b"), routing.Fingerprint, node.Domain.Profile, Guid.NewGuid());
        var native = GraphTransactionCodec.Decode(id, NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), id));
        var payload = GraphTransactionCodec.Encode(new(id, native.Mutations, new(0),
            [new VertexRead(((CreateVertex)native.Mutations[0]).Vertex)]));
        var plan = new PartitionTransactionPlan(id, routing.MapVersion,
            [new(node.Domain, new(0), payload), new(other, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), id))]);
        await authority.BindAsync(plan);
        await node.PrepareNativeAsync(plan);
        await authority.DecideAsync(id, plan.Descriptor, true);
        authority.Pause = true;
        await authority.Notifications.Writer.WriteAsync(new(id));
        await authority.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<DatabaseCommitReceipt?>? caller = join ? node.ApplyPartitionDecisionAsync(id).AsTask() : null;
        authority.Resume.TrySetResult();
        if (caller is not null)
        {
            Assert.AreEqual(id, (await caller.WaitAsync(TimeSpan.FromSeconds(10)))!.TransactionId);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!(await node.Kernel.PartitionTransactions.ReadAsync(deadline.Token)).Any(entry =>
            entry.Operation == id && entry.State == DurablePartitionTransactionState.Committed))
        {
            await Task.Delay(5, deadline.Token);
        }

        var receipt = await node.Kernel.PartitionTransactions.ResolveAsync(id, plan.Descriptor);
        Assert.AreEqual(id, receipt!.TransactionId);
        // Lost completion transport is repaired by native replay, not by waiting
        // for another event. Maintenance pauses autonomous admission and restarts it.
        await using (await node.DrainForMaintenanceAsync(deadline.Token))
        {
        }
        Assert.AreEqual(receipt.Version, (await node.ApplyPartitionDecisionAsync(id))!.Version);
    }
}
