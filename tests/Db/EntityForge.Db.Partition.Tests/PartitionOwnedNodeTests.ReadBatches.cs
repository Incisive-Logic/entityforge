using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Read_batch_preserves_pinned_version_and_each_query_owner()
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "batch"),
            DatabaseProfileId.PropertyGraph, authority.Lease, routing, authority, Options);
        Guid operation = Guid.NewGuid();
        byte[] payload = NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), operation);
        Guid vertex = ((CreateVertex)GraphTransactionCodec.Decode(operation, payload.AsSpan()).Mutations[0]).Vertex.Value;
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(operation, payload);
            return true;
        });
        var queries = new[] { new PartitionReadQuery("vertex", vertex), new PartitionReadQuery("outgoing", vertex) };
        var response = JsonSerializer.Deserialize<JsonElement[]>(await node.ReadCutAsync(cut, Batch(queries)))!;
        Assert.AreEqual(2, response.Length);
        Assert.AreEqual(JsonValueKind.Null, response[0].ValueKind);
        Assert.AreEqual(0, response[1].GetArrayLength());
        Guid other;
        do
        {
            other = Guid.NewGuid();
        } while (routing.RouteGraphVertex(other) != new LogicalShardId("b"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut,
            Batch([queries[0], new("vertex", other)])).AsTask());
        await node.CompleteCutAsync(cut);
        await Assert.ThrowsAsync<Exception>(() => node.ReadCutAsync(cut, Batch(queries)).AsTask());
        var current = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var fresh = JsonSerializer.Deserialize<JsonElement[]>(await node.ReadCutAsync(current, Batch(queries)))!;
        Assert.AreEqual(JsonValueKind.Object, fresh[0].ValueKind);
        await node.CompleteCutAsync(current);
    }

    [TestMethod]
    public async Task Read_batch_rejects_nesting_control_queries_and_excessive_count()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "batch-bounds"),
            DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        foreach (string kind in new[] { "batch", "validate-snapshot", "mapping", "maintain-cut" })
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut, Batch([new(kind)])).AsTask());
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut,
            Batch(Enumerable.Repeat(new PartitionReadQuery("vertex", Guid.NewGuid()), PartitionReadBatch.MaximumQueries + 1).ToArray())).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut, Batch([])).AsTask());
        var oversized = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(cut,
            Batch([new("vertex", Guid.NewGuid(), Subject: new string('x', PartitionApplicationBinding.MaximumRequestBytes * 2))])).AsTask());
        StringAssert.Contains(oversized.Message, "request bound");
        await node.CompleteCutAsync(cut);
    }

    [TestMethod]
    public async Task Compact_cut_retains_association_and_rejects_changed_evidence()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "compact-cut"),
            DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        Assert.IsNotNull(cut.SnapshotEvidence);
        var compact = cut.Reference;
        Assert.IsNull(compact.SnapshotEvidence);
        Assert.IsTrue(JsonSerializer.Deserialize<bool>(await node.ReadCutAsync(compact, new("validate-snapshot", SnapshotPrepared: []))));
        foreach (var changed in new[]
        {
            compact with { Expires = compact.Expires.AddTicks(1) },
            compact with { Maintenance = true }, compact with { Position = new(compact.Position.Identity, new(compact.Position.Version.Value + 1)) },
            cut with { SnapshotEvidence = "{}" }
        })
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(changed, new("validate-snapshot", SnapshotPrepared: [])).AsTask());
        }

        await node.CompleteCutAsync(compact);
        await node.CompleteCutAsync(compact);
        await Assert.ThrowsAsync<Exception>(() => node.ReadCutAsync(compact, new("validate-snapshot", SnapshotPrepared: [])).AsTask());
    }

    private static PartitionReadQuery Batch(PartitionReadQuery[] queries) => new("batch",
        Batch: new PartitionReadBatch([], queries));
}
