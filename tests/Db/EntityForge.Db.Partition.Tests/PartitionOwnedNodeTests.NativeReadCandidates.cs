using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using System.Text.Json;
using EntityForge.Db.Graph;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Native_operator_cache_is_exact_view_bounded_and_owns_frontier_keys()
    {
        var first = new object();
        var next = new object();
        var cache = new PartitionSnapshotOperatorCache<string, string>(2, 8);
        cache.Add(first, "a", "old", 4);
        Assert.IsTrue(cache.TryGet(first, "a", out var old));
        Assert.AreEqual("old", old);
        Assert.IsFalse(cache.TryGet(next, "a", out _));
        Assert.AreEqual(0, cache.Count);
        cache.Add(next, "a", "new", 4);
        Assert.IsFalse(cache.TryGet(first, "a", out _), "An old view cannot adopt a new view's answer.");
        cache.Add(first, "a", "one", 4);
        cache.Add(first, "b", "two", 4);
        cache.Add(first, "c", "three", 4);
        Assert.AreEqual(2, cache.Count);
        Assert.AreEqual(8L, cache.RetainedBytes);
        Assert.IsFalse(cache.TryGet(first, "a", out _));
        Assert.IsTrue(cache.TryGet(first, "b", out var retained));
        Assert.AreEqual("two", retained);
        cache.Add(first, "too-big", "ignored", 9);
        Assert.AreEqual(2, cache.Count);
        var ids = new[] { Guid.NewGuid() };
        var key = new PartitionQueryWorkKey(new(1, ids, []));
        var original = new PartitionQueryWorkKey(new(1, ids.ToArray(), []));
        ids[0] = Guid.NewGuid();
        Assert.AreEqual(original, key);
        cache.Clear();
        Assert.AreEqual(0, cache.Count);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Native_operator_cache_reuses_only_detached_relations_after_fresh_job_admission(DatabaseProfileId profile)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "cached-node"), profile,
            authority.Lease, Routing, authority, Options, timestampClock: ReadClock);
        var selection = profile == DatabaseProfileId.PropertyGraph
            ? new PartitionQueryExchangeRequest("gql", "MATCH (n) RETURN n")
            : profile == DatabaseProfileId.Rdf
                ? new PartitionQueryExchangeRequest("sparql", "SELECT ?s ?p ?o WHERE { ?s ?p ?o }")
                : new PartitionQueryExchangeRequest("entity");
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
        try
        {
            async Task<PartitionQueryJobResult> Read() => JsonSerializer.Deserialize<PartitionQueryJobResult>(
                await node.ReadCutAsync(cut, new("query-job", QueryJob: new(Guid.NewGuid(), selection, Finish: true),
                    ReadTime: TestQueryTime(cut))), PartitionParticipantWire.Json)!;
            var first = await Read();
            var second = await Read();
            Assert.IsTrue(first.Completed && second.Completed);
            Assert.AreEqual(first.Value!.Element.GetRawText(), second.Value!.Element.GetRawText());
            Assert.AreEqual(1L, node.CachedOperatorHits);
            Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
            Assert.AreEqual(0, node.ActiveQueryJobCount);
            // Cached values own no old query lease: release its parent entirely,
            // then reacquire a fresh protected cut of the unchanged native image.
            await node.AbandonCutAsync(cut.Id);
            cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20));
            var fresh = await Read();
            Assert.AreEqual(first.Value.Element.GetRawText(), fresh.Value!.Element.GetRawText());
            Assert.AreEqual(2L, node.CachedOperatorHits);
            Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
        }
        finally
        {
            await node.AbandonCutAsync(cut.Id);
        }
    }

    [DataTestMethod]
    [DataRow("{\"name\":{\"$value\":\"string\",\"kind\":1,\"value\":\"one\",\"value\":\"two\"}}")]
    [DataRow("{\"name\":{\"$value\":\"string\",\"kind\":1,\"value\":\"one\"},\"name\":{\"$value\":\"string\",\"kind\":1,\"value\":\"two\"}}")]
    public void Native_read_graph_decoder_rejects_duplicates_at_the_containing_boundary(string properties)
    {
        var id = Guid.NewGuid();
        using var json = JsonDocument.Parse($"{{\"id\":{{\"value\":\"{id}\"}},\"labels\":[\"item\"],\"properties\":{properties}}}");
        Assert.ThrowsExactly<InvalidDataException>(() => GraphWireJson.Vertex(json.RootElement, new(id)));
        using var direct = JsonDocument.Parse(properties);
        Assert.ThrowsExactly<InvalidDataException>(() => GraphWireJson.Properties(direct.RootElement));
    }
}
