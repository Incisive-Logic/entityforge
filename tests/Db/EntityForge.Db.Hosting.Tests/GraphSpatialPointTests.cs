using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Graph_spatial_points_survive_native_codecs_reindex_and_checkpoint()
    {
        string path = CreateDirectory();
        var vertex = GraphVertexId.New();
        var points = new DatabaseValue.Array([
            new DatabaseValue.Point(new(7203, 1.25, -2)), new DatabaseValue.Point(new(9157, 1, 2, -3)),
            new DatabaseValue.Point(new(4326, 540, 90)), new DatabaseValue.Point(new(4979, -540, -90, 400))]);
        var transaction = new GraphTransaction(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Spatial"]), new SetVertexProperty(vertex, "points", points)]);
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, GraphTransactionCodec.Encode(transaction));
        Assert.AreEqual(points, ((SetVertexProperty)decoded.Mutations[1]).Value);
        var effects = GraphCommittedEffectsCodec.Decode(GraphCommittedEffectsCodec.Encode(new([
            new GraphVertexCreated(new(vertex, new HashSet<string> { "Spatial" }, new Dictionary<string, DatabaseValue> { ["points"] = points }))])));
        Assert.AreEqual(points, ((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties["points"]);
        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
            await database.ReindexAsync();
            await database.CreateCheckpointAsync();
        }
        await using var reopened = await OpenAsync(path);
        Assert.AreEqual(points, reopened.GetVertex(vertex)!.Properties["points"]);
        foreach (var point in points.Value.Cast<DatabaseValue.Point>())
        {
            Assert.AreEqual(point.Value, SpatialPoint.Parse(point.Value.ToString()));
        }
        Assert.ThrowsExactly<ArgumentException>(() => new SpatialPoint(4326, 0, 91));
        Assert.ThrowsExactly<ArgumentException>(() => new SpatialPoint(7203, Double.NaN, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new SpatialPoint(9157, 0, 0));
        Assert.ThrowsExactly<ArgumentException>(() => new SpatialPoint(1234, 0, 0));
    }
}
