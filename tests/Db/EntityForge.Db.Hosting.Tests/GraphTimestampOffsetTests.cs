using System.Globalization;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    [DataRow("2026-11-13T01:02:03.1234567+05:30", false)]
    [DataRow("2026-11-13T01:02:03.1234567-03:30", true)]
    [DataRow("0001-01-01T14:00:00.0000000+14:00", true)]
    [DataRow("9999-12-31T09:59:59.9999999-14:00", true)]
    public async Task Graph_timestamp_offsets_survive_native_journal_and_checkpoint(string lexical, bool checkpoint)
    {
        string path = CreateDirectory();
        var vertex = GraphVertexId.New();
        var value = DateTimeOffset.ParseExact(lexical, "O", CultureInfo.InvariantCulture);
        var transaction = new GraphTransaction(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Time"]), new SetVertexProperty(vertex, "time", new DatabaseValue.DateTimeOffset(value))]);
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, GraphTransactionCodec.Encode(transaction));
        var decodedValue = ((DatabaseValue.DateTimeOffset)((SetVertexProperty)decoded.Mutations[1]).Value).Value;
        Assert.AreEqual(value.Offset, decodedValue.Offset);
        Assert.AreEqual(value.Ticks, decodedValue.Ticks);
        var effects = GraphCommittedEffectsCodec.Decode(GraphCommittedEffectsCodec.Encode(new([
            new GraphVertexCreated(new(vertex, new HashSet<string> { "Time" },
                new Dictionary<string, DatabaseValue> { ["time"] = new DatabaseValue.DateTimeOffset(value) }))])));
        var effectValue = ((DatabaseValue.DateTimeOffset)((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties["time"]).Value;
        Assert.AreEqual(value.Offset, effectValue.Offset);
        Assert.AreEqual(value.Ticks, effectValue.Ticks);
        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
            if (checkpoint)
            {
                await database.CreateCheckpointAsync();
            }
        }
        await using var reopened = await OpenAsync(path);
        var actual = ((DatabaseValue.DateTimeOffset)reopened.GetVertex(vertex)!.Properties["time"]).Value;
        Assert.AreEqual(value.Offset, actual.Offset);
        Assert.AreEqual(value.Ticks, actual.Ticks);
        Assert.AreEqual(value.UtcTicks, actual.UtcTicks);
    }
}
