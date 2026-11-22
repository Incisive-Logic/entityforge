using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Graph_UUIDs_survive_native_transaction_effects_checkpoint_and_reindex()
    {
        string path = CreateDirectory();
        var vertex = GraphVertexId.New();
        var uuid = new DatabaseValue.Uuid(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        var array = new DatabaseValue.Array([new DatabaseValue.Uuid(Guid.Empty), uuid]);
        var transaction = new GraphTransaction(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Uuid"]), new SetVertexProperty(vertex, "uuid", uuid), new SetVertexProperty(vertex, "array", array)]);
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, GraphTransactionCodec.Encode(transaction));
        Assert.AreEqual(uuid, ((SetVertexProperty)decoded.Mutations[1]).Value);
        Assert.AreEqual(array, ((SetVertexProperty)decoded.Mutations[2]).Value);
        var effects = GraphCommittedEffectsCodec.Decode(GraphCommittedEffectsCodec.Encode(new([
            new GraphVertexCreated(new(vertex, new HashSet<string> { "Uuid" }, new Dictionary<string, DatabaseValue> { ["uuid"] = uuid, ["array"] = array }))])));
        Assert.AreEqual(uuid, ((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties["uuid"]);
        Assert.AreEqual(array, ((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties["array"]);
        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
            await database.ReindexAsync();
            await database.CreateCheckpointAsync();
        }
        await using var reopened = await OpenAsync(path);
        Assert.AreEqual(uuid, reopened.GetVertex(vertex)!.Properties["uuid"]);
        Assert.AreEqual(array, reopened.GetVertex(vertex)!.Properties["array"]);
    }
}
