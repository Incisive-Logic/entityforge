using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Graph_calendar_durations_survive_native_effects_and_checkpoint()
    {
        string path = CreateDirectory();
        var vertex = GraphVertexId.New();
        var value = new DatabaseValue.Duration(new(14, -2, -1, 500_000_000));
        var array = new DatabaseValue.Array([value, new DatabaseValue.Duration(new(0, 0, 0, 0))]);
        var transaction = new GraphTransaction(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Duration"]), new SetVertexProperty(vertex, "value", value), new SetVertexProperty(vertex, "array", array)]);
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, GraphTransactionCodec.Encode(transaction));
        Assert.AreEqual(value, ((SetVertexProperty)decoded.Mutations[1]).Value);
        Assert.AreEqual(array, ((SetVertexProperty)decoded.Mutations[2]).Value);
        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
            await database.ReindexAsync();
            await database.CreateCheckpointAsync();
        }
        await using var reopened = await OpenAsync(path);
        Assert.AreEqual(value, reopened.GetVertex(vertex)!.Properties["value"]);
        Assert.AreEqual(array, reopened.GetVertex(vertex)!.Properties["array"]);
    }

    [TestMethod]
    public void Calendar_durations_format_and_parse_normalized_signed_components()
    {
        foreach (long months in new[] { Int64.MinValue, -13L, 0L, 13L, Int64.MaxValue })
        {
            foreach (long seconds in new[] { Int64.MinValue, -3601L, -1L, 0L, 3601L, Int64.MaxValue })
            {
                foreach (int nanos in new[] { 0, 1, 500_000_000, 999_999_999 })
                {
                    var duration = new CalendarDuration(months, -3, seconds, nanos);
                    Assert.AreEqual(duration, CalendarDuration.Parse(duration.ToString()), duration.ToString());
                }
            }
        }
        Assert.AreEqual(new CalendarDuration(0, 0, 0, 0), CalendarDuration.FromComponents(0, 0, -0.0000000001m));
    }

    [TestMethod]
    public void Calendar_dates_preserve_epoch_arithmetic_across_the_full_year_range()
    {
        foreach (int year in new[] { CalendarDate.MinimumYear, -400, -1, 0, 1, 400, 1970, 2000, 9999, 10000, CalendarDate.MaximumYear })
        {
            for (int month = 1; month <= 12; month++)
            {
                foreach (int day in new[] { 1, CalendarDate.DaysInMonth(year, month) })
                {
                    var date = new CalendarDate(year, month, day);
                    Assert.AreEqual(date, CalendarDate.FromEpochDay(date.EpochDay));
                    Assert.AreEqual(date, CalendarDate.Parse(date.ToString()));
                    if (year is >= 1 and <= 9999)
                    {
                        Assert.AreEqual((long)new DateOnly(year, month, day).DayNumber - new DateOnly(1970, 1, 1).DayNumber, date.EpochDay);
                        Assert.AreEqual(((int)new DateOnly(year, month, day).DayOfWeek + 6) % 7 + 1, date.DayOfWeek);
                    }
                }
            }
        }
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CalendarDate.FromEpochDay(Int64.MinValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CalendarDate.FromEpochDay(Int64.MaxValue));
        Assert.ThrowsExactly<FormatException>(() => CalendarDate.Parse("1900-02-29"));
        Assert.AreEqual(new CalendarDate(1970, 1, 1), CalendarDate.FromEpochDay(0));
    }

    [TestMethod]
    public async Task Graph_calendar_dates_and_arrays_survive_checkpoint_and_reindex()
    {
        string path = CreateDirectory();
        var vertex = GraphVertexId.New();
        var date = new DatabaseValue.Date(new(-50000, 2, 29));
        var dates = new DatabaseValue.Array([date, new DatabaseValue.Date(new(2020, 2, 29))]);
        var transaction = new GraphTransaction(Guid.NewGuid(),
            [new CreateVertex(vertex, ["Calendar"]), new SetVertexProperty(vertex, "date", date), new SetVertexProperty(vertex, "dates", dates)]);
        var decoded = GraphTransactionCodec.Decode(transaction.TransactionId, GraphTransactionCodec.Encode(transaction));
        Assert.AreEqual(date, ((SetVertexProperty)decoded.Mutations[1]).Value);
        Assert.AreEqual(dates, ((SetVertexProperty)decoded.Mutations[2]).Value);
        var effects = GraphCommittedEffectsCodec.Decode(GraphCommittedEffectsCodec.Encode(new([
            new GraphVertexCreated(new(vertex, new HashSet<string> { "Calendar" },
                new Dictionary<string, DatabaseValue> { ["date"] = date, ["dates"] = dates }))])));
        var properties = ((GraphVertexCreated)effects.Effects.Single()).Vertex.Properties;
        Assert.AreEqual(date, properties["date"]);
        Assert.AreEqual(dates, properties["dates"]);
        await using (var database = await OpenAsync(path))
        {
            await database.CommitAsync(transaction);
            await database.ReindexAsync();
            await database.CreateCheckpointAsync();
        }
        await using var reopened = await OpenAsync(path);
        Assert.AreEqual(date, reopened.GetVertex(vertex)!.Properties["date"]);
        Assert.AreEqual(dates, reopened.GetVertex(vertex)!.Properties["dates"]);
    }
}
