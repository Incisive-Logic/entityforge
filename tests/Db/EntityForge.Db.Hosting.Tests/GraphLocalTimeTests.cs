using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Graph_local_times_survive_native_codecs_checkpoint_and_reindex()
    {
        string path = CreateDirectory();
        var vertex = GraphVertexId.New();
        var times = new DatabaseValue.Array([new DatabaseValue.LocalTime(new(0)), new DatabaseValue.LocalTime(new(LocalTime.NanosecondsPerDay - 1))]);
        var datetimes = new DatabaseValue.Array([
            new DatabaseValue.LocalDateTime(new(new(-999_999_999, 1, 1), new(0))),
            new DatabaseValue.LocalDateTime(new(new(999_999_999, 12, 31), new(LocalTime.NanosecondsPerDay - 1)))]);
        var zoned = new DatabaseValue.Array([new DatabaseValue.ZonedTime(new(new(0), 64800)), new DatabaseValue.ZonedTime(new(new(LocalTime.NanosecondsPerDay - 1), -64800))]);
        var instants = new DatabaseValue.Array([new DatabaseValue.ZonedDateTime(new(new(new(-999_999_999, 1, 1), new(0)), 64800)),
            new DatabaseValue.ZonedDateTime(new(new(new(999_999_999, 12, 31), new(LocalTime.NanosecondsPerDay - 1)), -64800))]);
        var transaction = new GraphTransaction(Guid.NewGuid(), [new CreateVertex(vertex, ["Clock"]), new SetVertexProperty(vertex, "times", times), new SetVertexProperty(vertex, "datetimes", datetimes), new SetVertexProperty(vertex, "zoned", zoned), new SetVertexProperty(vertex, "instants", instants)]);
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, GraphTransactionCodec.Encode(transaction));
        Assert.AreEqual(times, ((SetVertexProperty)decoded.Mutations[1]).Value);
        Assert.AreEqual(datetimes, ((SetVertexProperty)decoded.Mutations[2]).Value);
        Assert.AreEqual(zoned, ((SetVertexProperty)decoded.Mutations[3]).Value);
        Assert.AreEqual(instants, ((SetVertexProperty)decoded.Mutations[4]).Value);
        var effects = GraphCommittedEffectsCodec.Decode(GraphCommittedEffectsCodec.Encode(new([
            new GraphVertexCreated(new(vertex, new HashSet<string> { "Clock" }, new Dictionary<string, DatabaseValue> { ["times"] = times }))])));
        Assert.AreEqual(times, ((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties["times"]);
        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
            await database.ReindexAsync();
            await database.CreateCheckpointAsync();
        }
        await using var reopened = await OpenAsync(path);
        Assert.AreEqual(zoned, reopened.GetVertex(vertex)!.Properties["zoned"]);
        Assert.AreEqual(instants, reopened.GetVertex(vertex)!.Properties["instants"]);
        Assert.AreEqual(times, reopened.GetVertex(vertex)!.Properties["times"]);
        Assert.AreEqual(datetimes, reopened.GetVertex(vertex)!.Properties["datetimes"]);
        foreach (var datetime in datetimes.Value.Cast<DatabaseValue.LocalDateTime>())
        {
            Assert.AreEqual(datetime.Value, LocalDateTime.Parse(datetime.Value.ToString()));
        }
        foreach (var time in times.Value.Cast<DatabaseValue.LocalTime>())
        {
            Assert.AreEqual(time.Value, LocalTime.Parse(time.Value.ToString()));
        }
        foreach (var instant in instants.Value.Cast<DatabaseValue.ZonedDateTime>())
        {
            Assert.AreEqual(instant.Value, ZonedDateTime.Parse(instant.Value.ToString()));
        }
    }

    [TestMethod]
    [DataRow("24:00")]
    [DataRow("23:60")]
    [DataRow("23:59:60")]
    [DataRow("12:3045")]
    [DataRow("1230:45")]
    [DataRow("12:30:45.1234567890")]
    [DataRow("12:30Z")]
    [DataRow("12:30+01:00")]
    [DataRow("12:30 ")]
    public void Graph_local_time_parser_rejects_invalid_components_and_offsets(string text) =>
        Assert.ThrowsExactly<FormatException>(() => LocalTime.Parse(text));
}
