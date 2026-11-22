using EntityForge.Db.Gql;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Gql_aggregate_scan_retains_warning_when_postfilter_removes_the_group()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), new[]
        {
            new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>()),
            new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(2) })
        }, []);
        int rows = 0, warnings = 0, complete = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN SUM(n.score) AS s NEXT FILTER s < 0 RETURN s AS answer"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 1, token), new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            rows += page.Rows.Count;
            warnings += page.Diagnostics?.Count(item => item.Code == "01G11") ?? 0;
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(0, rows);
        Assert.AreEqual(1, warnings);
        Assert.AreEqual(1, complete);
    }

    [TestMethod]
    public async Task Gql_aggregate_scan_rejects_intermediate_distinct_before_storage()
    {
        IAsyncEnumerable<GqlScanInputPage> Read(CancellationToken token) => throw new AssertFailedException("Unsupported plan read storage.");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
        {
            await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n.score AS v, COUNT(*) AS c GROUP BY v NEXT RETURN DISTINCT v NEXT RETURN 1 AS answer"),
                Read, new(MaximumSortTemporaryBytes: 65536)))
            {
                Assert.Fail("Intermediate DISTINCT was moved past projection.");
            }
        });
    }

    [TestMethod]
    public async Task Gql_aggregate_scan_drops_hidden_ordering_inputs_after_computed_projection()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 6).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(i % 3) })).ToArray(), []);
        var values = new List<string>();
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n.score AS v, COUNT(*) AS c GROUP BY v NEXT RETURN v+10 AS answer ORDER BY c DESC, v DESC"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 2, token), new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual(1, row.Bindings.Count);
                values.Add(((LiteralValue)row.Bindings["answer"]).LexicalValue);
            }
        }
        CollectionAssert.AreEqual(new[] { "12", "11", "10" }, values);
    }

    [TestMethod]
    [DataRow("MATCH (n) RETURN SUM(n.score)+1 AS answer", "11")]
    [DataRow("MATCH (n) RETURN SUM(DISTINCT n.score)+1 AS answer", "11")]
    [DataRow("MATCH (n) RETURN COUNT(*)+SUM(DISTINCT n.score) AS answer", "15")]
    [DataRow("MATCH (n) RETURN COUNT(*)+SUM(n.score) AS answer", "15")]
    [DataRow("MATCH (n) RETURN SUM(n.score) AS total NEXT FILTER total > 5 RETURN total+1 AS answer", "11")]
    [DataRow("MATCH (n) RETURN SUM(n.score) AS total NEXT FILTER total > 15 RETURN total+1 AS answer", "")]
    public async Task Gql_aggregate_scan_applies_scalar_pipeline_after_group_completion(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 5).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(i) })).ToArray(), []);
        var values = new List<string>();
        int completed = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(query),
            token => GraphGqlScan.ReadNativeAsync(database, null, 2, token), new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            values.AddRange(page.Rows.Select(row => ((LiteralValue)row.Bindings["answer"]).LexicalValue));
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(expected, String.Join(',', values));
        Assert.AreEqual(1, completed);
    }

    [TestMethod]
    public async Task Gql_aggregate_scan_computed_groups_preserve_bags_until_final_distinct()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 6).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(i % 3) })).ToArray(), []);
        foreach (bool distinct in new[] { false, true })
        {
            var values = new List<string>();
            string query = "MATCH (n) RETURN n.score AS v, COUNT(*) AS c GROUP BY v NEXT RETURN " + (distinct ? "DISTINCT " : "") + "c+1 AS answer ORDER BY answer";
            await foreach (var page in GraphGqlScan.ExecuteAsync(new(query),
                token => GraphGqlScan.ReadNativeAsync(database, null, 2, token), new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
            {
                values.AddRange(page.Rows.Select(row => ((LiteralValue)row.Bindings["answer"]).LexicalValue));
            }
            CollectionAssert.AreEqual(distinct ? new[] { "3" } : new[] { "3", "3", "3" }, values);
        }
    }

    [TestMethod]
    public async Task Gql_aggregate_scan_large_computed_projection_filters_before_order_and_window()
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (var batch in Enumerable.Range(0, 100000).Chunk(1000))
        {
            await database.CommitAsync(new(Guid.NewGuid(), batch.SelectMany(index =>
            {
                var id = GraphVertexId.New();
                return new GraphMutation[]
                {
                    new CreateVertex(id, []), new SetVertexProperty(id, "score", new DatabaseValue.Int64(index % 20000))
                };
            }).ToArray()));
        }
        int count = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n.score AS v, SUM(n.score)+1 AS total GROUP BY v NEXT FILTER total < 95000 RETURN v AS keyValue, total AS answer ORDER BY keyValue DESC OFFSET 1000 LIMIT 17000"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 256, token), new(MaximumSortTemporaryBytes: 256 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                int expected = 17999 - count++;
                Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), ((LiteralValue)row.Bindings["keyValue"]).LexicalValue);
                Assert.AreEqual((5 * expected + 1).ToString(CultureInfo.InvariantCulture), ((LiteralValue)row.Bindings["answer"]).LexicalValue);
                Assert.AreEqual(2, row.Bindings.Count);
            }
        }
        Assert.AreEqual(17000, count);
    }
}
