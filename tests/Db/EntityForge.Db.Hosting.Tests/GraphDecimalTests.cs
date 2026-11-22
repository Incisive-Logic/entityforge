using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Graph_decimals_survive_native_transaction_effects_checkpoint_and_reindex()
    {
        string path = CreateDirectory();
        var vertex = GraphVertexId.New();
        var exact = new DatabaseValue.Decimal(0.1234567890123456789012345678m);
        var array = new DatabaseValue.Array([new DatabaseValue.Decimal(-123.4500m), exact]);
        var transaction = new GraphTransaction(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Exact"]), new SetVertexProperty(vertex, "exact", exact), new SetVertexProperty(vertex, "array", array)]);
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, GraphTransactionCodec.Encode(transaction));
        Assert.AreEqual(exact, ((SetVertexProperty)decoded.Mutations[1]).Value);
        Assert.AreEqual(array, ((SetVertexProperty)decoded.Mutations[2]).Value);
        var effects = GraphCommittedEffectsCodec.Decode(GraphCommittedEffectsCodec.Encode(new([
            new GraphVertexCreated(new(vertex, new HashSet<string> { "Exact" }, new Dictionary<string, DatabaseValue> { ["exact"] = exact, ["array"] = array }))])));
        Assert.AreEqual(exact, ((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties["exact"]);
        Assert.AreEqual(array, ((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties["array"]);
        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
            await database.ReindexAsync();
            await database.CreateCheckpointAsync();
        }
        await using var reopened = await OpenAsync(path);
        Assert.AreEqual(exact, reopened.GetVertex(vertex)!.Properties["exact"]);
        Assert.AreEqual(array, reopened.GetVertex(vertex)!.Properties["array"]);
    }
}
