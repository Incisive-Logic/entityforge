using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_complete_single_owner_domain_uses_native_application_lifecycle()
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a")])).Binding;
        var lease = new Authority();
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "local-authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "local-semantic"),
            DatabaseProfileId.Semantic, lease.Lease, routing, lease, Options,
            transactionAuthority: authority, timestampClock: ReadClock);
        var client = new PartitionApplicationClient(authority, routing, [node], ReadClock);
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
        var operation = SemanticEntityWriteOperation.Create();
        var capture = store.CaptureSave(new("local", "value"), operation);
        var receipt = await client.DeliverAsync(operation.OperationId, capture.Export());
        Assert.AreEqual(1, receipt.Members.Count);
        Assert.IsTrue((await authority.ReadApplicationAsync(operation.OperationId))!.Local);
        Assert.IsNull(await authority.ReadPlanAsync(operation.OperationId), "No distributed plan is needed for complete local closure.");
        Assert.AreEqual(EntityOperationState.Committed, (await client.ResolveAsync(operation.OperationId, default)).State);
        var replay = await client.DeliverAsync(operation.OperationId, capture.Export());
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members.Single().Receipt.Outcome);
        var changed = store.CaptureSave(new("local", "changed"), operation);
        await Assert.ThrowsAsync<EntityForgeDbIdempotencyException>(() => client.DeliverAsync(operation.OperationId, changed.Export()).AsTask());
        var next = store.CaptureSave(new("prepared", "value"), SemanticEntityWriteOperation.Create());
        await client.PrepareAsync(next.OperationId, [next.Export()], default);
        Assert.IsTrue((await authority.ReadApplicationAsync(next.OperationId))!.Local);
        await client.DeliverAsync(next.OperationId, next.Export());
        var aborted = store.CaptureSave(new("aborted", "value"), SemanticEntityWriteOperation.Create());
        await client.PrepareAsync(aborted.OperationId, [aborted.Export()], default);
        Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(aborted.OperationId, default)).State);
    }
}
