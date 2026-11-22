using EntityForge.Db.Gql;
using System.Globalization;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Gql_distinct_aggregate_scan_preserves_first_numeric_representatives_across_runs()
    {
        DatabaseValue[] values = [new DatabaseValue.Int64(1), new DatabaseValue.Double(1),
            new DatabaseValue.Int64(2), new DatabaseValue.Decimal(2.0m)];
        async IAsyncEnumerable<GqlScanInputPage> Read([EnumeratorCancellation] CancellationToken cancellation)
        {
            for (int start = 0; start < 5000; start += 256)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = Math.Min(256, 5000 - start);
                yield return new(new(1), Enumerable.Range(start, count).Select(i => new GraphVertex(GraphVertexId.New(),
                    new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["score"] = values[i % 4] })).ToArray(),
                    count, start + count == 5000);
                await Task.Yield();
            }
        }
        int rows = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(
            "MATCH (n) RETURN SUM(DISTINCT n.score) AS s, COUNT(DISTINCT n.score) AS c, COUNT(*) AS allCount"), Read,
            new(MaximumSortTemporaryBytes: 32 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                var sum = (LiteralValue)row.Bindings["s"];
                Assert.AreEqual("3", sum.LexicalValue);
                Assert.AreEqual(QueryScalarKind.Integer, sum.ScalarKind);
                Assert.AreEqual("2", ((LiteralValue)row.Bindings["c"]).LexicalValue);
                Assert.AreEqual("5000", ((LiteralValue)row.Bindings["allCount"]).LexicalValue);
                rows++;
            }
        }
        Assert.AreEqual(1, rows);
    }

    [TestMethod]
    public async Task Gql_distinct_aggregate_scan_preserves_mixed_bags_at_scale()
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (var batch in Enumerable.Range(0, 100_000).Chunk(1000))
        {
            await database.CommitAsync(new(Guid.NewGuid(), batch.SelectMany(i =>
            {
                var id = GraphVertexId.New();
                return new GraphMutation[] { new CreateVertex(id, []), new SetVertexProperty(id, "score", new DatabaseValue.Int64(i % 20000)) };
            }).ToArray()));
        }
        int rows = 0, completed = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(
            "MATCH (n) RETURN COUNT(*) AS allCount, COUNT(DISTINCT n.score) AS uniqueCount, SUM(n.score) AS total, SUM(DISTINCT n.score) AS uniqueTotal"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 256, token), new(MaximumSortTemporaryBytes: 512 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual("100000", ((LiteralValue)row.Bindings["allCount"]).LexicalValue);
                Assert.AreEqual("20000", ((LiteralValue)row.Bindings["uniqueCount"]).LexicalValue);
                Assert.AreEqual("999950000", ((LiteralValue)row.Bindings["total"]).LexicalValue);
                Assert.AreEqual("199990000", ((LiteralValue)row.Bindings["uniqueTotal"]).LexicalValue);
                rows++;
            }
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(1, rows);
        Assert.AreEqual(1, completed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_distinct_aggregate_scan_uses_numeric_equivalence_and_keeps_null_warning(bool grouped)
    {
        await using var database = await OpenAsync(CreateDirectory());
        DatabaseValue?[] values = [new DatabaseValue.Int64(1), new DatabaseValue.Decimal(1.0m), new DatabaseValue.Double(1), null, null];
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 10).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), values[i % 5] is { } value ? new Dictionary<string, DatabaseValue>
            {
                ["score"] = value,
                ["groupKey"] = new DatabaseValue.Int64(i / 5)
            }
            : new Dictionary<string, DatabaseValue>
            {
                ["groupKey"] = new DatabaseValue.Int64(i / 5)
            })).ToArray(), []);
        string query = grouped
            ? "MATCH (n) RETURN n.groupKey AS g, COUNT(DISTINCT n.score) AS c, COUNT(n.score) AS allCount, COUNT(DISTINCT n) AS nodes GROUP BY g ORDER BY g"
            : "MATCH (n) RETURN COUNT(DISTINCT n.score) AS c, COUNT(n.score) AS allCount, COUNT(DISTINCT n) AS nodes";
        int rows = 0, warnings = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(query),
            token => GraphGqlScan.ReadNativeAsync(database, null, 1, token), new(PageSize: 1, MaximumSortTemporaryBytes: 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual("1", ((LiteralValue)row.Bindings["c"]).LexicalValue);
                Assert.AreEqual(grouped ? "3" : "6", ((LiteralValue)row.Bindings["allCount"]).LexicalValue);
                Assert.AreEqual(grouped ? "5" : "10", ((LiteralValue)row.Bindings["nodes"]).LexicalValue);
                if (grouped)
                {
                    Assert.AreEqual(rows.ToString(CultureInfo.InvariantCulture), ((LiteralValue)row.Bindings["g"]).LexicalValue);
                }
                rows++;
            }
            warnings += page.Diagnostics?.Count(item => item.Code == "01G11") ?? 0;
        }
        Assert.AreEqual(grouped ? 2 : 1, rows);
        Assert.AreEqual(1, warnings);
    }
}
