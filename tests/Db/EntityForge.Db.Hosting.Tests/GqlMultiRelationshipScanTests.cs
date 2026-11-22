using EntityForge.Db.Gql;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    [DataRow("cancel", false)]
    [DataRow("work", false)]
    [DataRow("dispose", false)]
    [DataRow("cancel", true)]
    [DataRow("work", true)]
    [DataRow("dispose", true)]
    public async Task Gql_multi_relationship_scan_releases_snapshot_on_interruption(string scenario, bool aggregate)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var vertices = Enumerable.Range(0, 5).Select(index => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            new Dictionary<string, DatabaseValue> { ["ordinal"] = new DatabaseValue.Int64(index) })).ToArray();
        var edges = Enumerable.Range(0, 4).Select(index => new GraphEdge(GraphEdgeId.New(), vertices[index].Id,
            vertices[index + 1].Id, "link", new Dictionary<string, DatabaseValue>())).ToArray();
        await database.BulkLoadAsync(Guid.NewGuid(), vertices, edges);
        using var cancellation = new CancellationTokenSource();
        int completed = 0, rows = 0;
        async Task Read()
        {
            await foreach (var page in GraphGqlScan.ExecuteNativeAsync(new(
                "MATCH (a)-[:link]->(b)-[:link]->(c) RETURN " +
                (aggregate ? "c.ordinal AS key, COUNT(*) AS answer GROUP BY key ORDER BY key" : "1 AS answer")), database,
                new(MaximumExaminedElements: scenario == "work" ? 1 : 100, PageSize: 1,
                    MaximumSortTemporaryBytes: 1024 * 1024), cancellation.Token))
            {
                rows += page.Rows.Count;
                completed += page.Complete ? 1 : 0;
                if (scenario == "dispose")
                {
                    break;
                }
                cancellation.Cancel();
            }
        }
        if (scenario == "work")
        {
            await Assert.ThrowsExactlyAsync<QueryScanWorkLimitException>(Read);
        }
        else if (scenario == "cancel")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(Read);
        }
        else
        {
            await Read();
        }
        Assert.AreEqual(0, completed);
        Assert.AreEqual(scenario == "work" ? 0 : 1, rows);
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_multi_relationship_scan_streams_high_degree_root_beyond_materialized_row_limit(bool exhaustRows)
    {
        const int count = 12000;
        await using var database = await OpenAsync(CreateDirectory());
        var root = new GraphVertex(GraphVertexId.New(), new HashSet<string> { "Root" }, new Dictionary<string, DatabaseValue>());
        var middle = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var leaves = Enumerable.Range(0, count).Select(index => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            new Dictionary<string, DatabaseValue> { ["ordinal"] = new DatabaseValue.Int64(index) })).ToArray();
        var edges = leaves.Select(leaf => new GraphEdge(GraphEdgeId.New(), middle.Id, leaf.Id, "link", new Dictionary<string, DatabaseValue>()))
            .Prepend(new(GraphEdgeId.New(), root.Id, middle.Id, "link", new Dictionary<string, DatabaseValue>())).ToArray();
        await database.BulkLoadAsync(Guid.NewGuid(), new[] { root, middle }.Concat(leaves).ToArray(), edges);
        var seen = new HashSet<int>();
        int completions = 0;
        async Task Read()
        {
            await foreach (var page in GraphGqlScan.ExecuteNativeAsync(new(
                "MATCH (a:Root)-[:link]->(b)-[:link]->(c) RETURN c.ordinal AS answer"), database,
                new(MaximumRows: exhaustRows ? 1000 : count, PageSize: 37)))
            {
                Assert.IsTrue(page.Rows.Count <= 37);
                foreach (var row in page.Rows)
                {
                    int value = Int32.Parse(((LiteralValue)row.Bindings["answer"]).LexicalValue, CultureInfo.InvariantCulture);
                    Assert.IsTrue(value >= 0 && value < count);
                    Assert.IsTrue(seen.Add(value));
                }
                completions += page.Complete ? 1 : 0;
            }
        }
        if (exhaustRows)
        {
            await Assert.ThrowsExactlyAsync<QueryScanWorkLimitException>(Read);
            Assert.AreEqual(0, completions);
        }
        else
        {
            await Read();
            Assert.AreEqual(count, seen.Count);
            Assert.AreEqual(1, completions);
        }
        await database.CreateCheckpointAsync();
    }
}
