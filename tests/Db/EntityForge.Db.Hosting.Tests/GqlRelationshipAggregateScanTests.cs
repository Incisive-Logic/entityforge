using EntityForge.Db.Gql;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Gql_relationship_aggregate_scan_spills_distinct_contributions_and_releases_snapshot(bool exhaustDisk, bool multipleRelationships)
    {
        const int count = 6000;
        await using var database = await OpenAsync(CreateDirectory());
        var a = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var b = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var edges = Enumerable.Range(0, count).Select(index => new GraphEdge(GraphEdgeId.New(), a.Id, b.Id, "link",
            new Dictionary<string, DatabaseValue>
            {
                ["bucket"] = new DatabaseValue.Int64(index % 7),
                ["score"] = new DatabaseValue.Int64(index % 19)
            })).ToArray();
        var root = new GraphVertex(GraphVertexId.New(), new HashSet<string> { "Root" }, new Dictionary<string, DatabaseValue>());
        await database.BulkLoadAsync(Guid.NewGuid(), [root, a, b], multipleRelationships
            ? edges.Prepend(new(GraphEdgeId.New(), root.Id, a.Id, "route", new Dictionary<string, DatabaseValue>())).ToArray() : edges);
        int groups = 0, completions = 0;
        async Task Read()
        {
            await foreach (var page in GraphGqlScan.ExecuteNativeAsync(new(
                (multipleRelationships ? "MATCH (:Root)-[:route]->(a)-[e:link]->(b) " : "MATCH (a)-[e:link]->(b) ") +
                "RETURN e.bucket AS bucket, COUNT(*) AS total, " +
                "COUNT(DISTINCT e) AS uniqueEdges, SUM(e.score) AS scoreSum GROUP BY bucket ORDER BY bucket"), database,
                new(PageSize: 128, MaximumSortTemporaryBytes: exhaustDisk ? 1024 : 32 * 1024 * 1024)))
            {
                foreach (var row in page.Rows)
                {
                    long Value(string alias) => Int64.Parse(((LiteralValue)row.Bindings[alias]).LexicalValue, CultureInfo.InvariantCulture);
                    Assert.AreEqual((long)groups, Value("bucket"));
                    var members = Enumerable.Range(0, count).Where(index => index % 7 == groups).ToArray();
                    Assert.AreEqual((long)members.Length, Value("total"));
                    Assert.AreEqual((long)members.Length, Value("uniqueEdges"));
                    Assert.AreEqual(members.Sum(index => (long)(index % 19)), Value("scoreSum"));
                    groups++;
                }
                completions += page.Complete ? 1 : 0;
            }
        }
        if (exhaustDisk)
        {
            await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(Read);
            Assert.AreEqual(0, completions);
        }
        else
        {
            await Read();
            Assert.AreEqual(7, groups);
            Assert.AreEqual(1, completions);
        }
        await database.CreateCheckpointAsync();
    }
}
