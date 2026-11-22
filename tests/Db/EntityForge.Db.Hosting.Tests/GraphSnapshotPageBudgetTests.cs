using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphSnapshotPageBudgetTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-graph-snapshot-page");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1001)]
    [DataRow(int.MaxValue)]
    public async Task Snapshot_page_rejects_invalid_sizes_without_invalidating_lease(int size)
    {
        await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
        using var lease = database.AcquireSnapshot();
        var failure = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await database.ReadSnapshotPageAsync(lease, maximumCount: size));
        Assert.AreEqual("maximumCount", failure.ParamName);
        var page = await database.ReadSnapshotPageAsync(lease, maximumCount: 1);
        Assert.AreEqual(0, page.Effects.Effects.Count);
        Assert.IsNull(page.NextCursor);
    }

    [TestMethod]
    public async Task Maximum_snapshot_pages_preserve_all_entities_and_the_original_snapshot()
    {
        await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
        GraphVertexId[] vertices = Enumerable.Range(1, 1001)
            .Select(i => new GraphVertexId(new Guid(i, 0, 0, new byte[8]))).ToArray();
        var edge = GraphEdgeId.New();
        var changes = vertices.Select(id => (GraphMutation)new CreateVertex(id, ["PageBudget"])).ToList();
        changes.Add(new CreateEdge(edge, vertices[0], vertices[^1], "link"));
        await database.CommitAsync(new(Guid.NewGuid(), changes));
        using var lease = database.AcquireSnapshot();
        var first = await database.ReadSnapshotPageAsync(lease, maximumCount: 1000);
        Assert.AreEqual(1000, first.Effects.Effects.Count);
        Assert.IsNotNull(first.NextCursor);
        await database.CommitAsync(new(Guid.NewGuid(), [new CreateVertex(GraphVertexId.New(), ["Later"])]));
        var last = await database.ReadSnapshotPageAsync(lease, first.NextCursor, maximumCount: 1000);
        Assert.AreEqual(2, last.Effects.Effects.Count);
        Assert.IsNull(last.NextCursor);
        var effects = first.Effects.Effects.Concat(last.Effects.Effects).ToArray();
        CollectionAssert.AreEquivalent(vertices, effects.OfType<GraphVertexCreated>().Select(item => item.Vertex.Id).ToArray());
        Assert.AreEqual(edge, effects.OfType<GraphEdgeCreated>().Single().Edge.Id);
    }
}
