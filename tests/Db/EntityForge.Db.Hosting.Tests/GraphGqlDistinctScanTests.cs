using EntityForge.Db.Gql;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Gql_distinct_scan_deduplicates_before_global_window()
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (var batch in Enumerable.Range(0, 100_000).Chunk(1000))
        {
            var mutations = batch.SelectMany(i =>
            {
                var id = GraphVertexId.New();
                return new GraphMutation[]
                {
                    new CreateVertex(id, []),
                    new SetVertexProperty(id, "score", new DatabaseValue.Int64(i % 20000))
                };
            }).ToArray();
            await database.CommitAsync(new(Guid.NewGuid(), mutations));
        }
        int count = 0, complete = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN DISTINCT n.score AS v ORDER BY v DESC OFFSET 1000 LIMIT 18000"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 256, token), new(MaximumSortTemporaryBytes: 256 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual((18999 - count++).ToString(CultureInfo.InvariantCulture), ((LiteralValue)row.Bindings["v"]).LexicalValue);
            }
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(18000, count);
        Assert.AreEqual(1, complete);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ORDER BY v ASC NULLS LAST")]
    public async Task Gql_distinct_scan_uses_numeric_value_equivalence_and_null_grouping(string order)
    {
        await using var database = await OpenAsync(CreateDirectory());
        DatabaseValue?[] values = [new DatabaseValue.Int64(1), new DatabaseValue.Double(1), new DatabaseValue.Decimal(1.0m),
            new DatabaseValue.Int64(2), null, null];
        await database.BulkLoadAsync(Guid.NewGuid(), values.Select(value => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            value is null ? new Dictionary<string, DatabaseValue>() : new Dictionary<string, DatabaseValue> { ["score"] = value })).ToArray(), []);
        var result = new List<string>();
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN DISTINCT n.score AS v" + order),
            token => GraphGqlScan.ReadNativeAsync(database, null, 1, token), new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            result.AddRange(page.Rows.Select(row => row.Bindings["v"] is LiteralValue literal
                ? Decimal.Parse(literal.LexicalValue, CultureInfo.InvariantCulture).ToString("0", CultureInfo.InvariantCulture) : "null"));
        }
        CollectionAssert.AreEquivalent(new[] { "1", "2", "null" }, result);
        if (order.Length != 0)
        {
            CollectionAssert.AreEqual(new[] { "1", "2", "null" }, result);
        }
    }
}
