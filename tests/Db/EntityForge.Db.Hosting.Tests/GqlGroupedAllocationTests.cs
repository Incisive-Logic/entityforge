using EntityForge.Db.Gql;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlGroupedAllocationTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("QueryBenchmark")]
    public async Task Grouped_execution_reports_verified_allocations_without_http_or_compilation()
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_GROUPED_ALLOCATION_REPORT") is not { Length: > 0 } output)
        {
            Assert.Inconclusive("Opt in with ENTITYFORGE_GROUPED_ALLOCATION_REPORT.");
            return;
        }
        var vertices = Enumerable.Range(0, 3000).Select(i => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            new Dictionary<string, DatabaseValue> { ["bucket"] = new DatabaseValue.Int64(i % 10), ["key"] = new DatabaseValue.Int64(i) }))
            .ToDictionary(v => v.Id);
        var view = GraphReadView.Build(vertices, new Dictionary<GraphEdgeId, GraphEdge>());
        var snapshot = new GraphQuerySnapshot(new(1), view, view.StatisticsFor(new(1)));
        var query = new GraphGqlQueryCompiler().CompileStandard(new(
            "MATCH (n) RETURN n.bucket AS bucket,COUNT(*) AS total,SUM(n.key) AS amount")).Query;
        var executor = new BoundedGraphPatternExecutor(null);
        async Task Run()
        {
            var result = await executor.ExecuteCapturedResultAsync(query, snapshot, default);
            var rows = ((RecordResult)result.Result).Rows;
            Assert.AreEqual(10, rows.Count);
            foreach (var row in rows)
            {
                long Number(string name) => Int64.Parse(((LiteralValue)row.Values[name]).LexicalValue, CultureInfo.InvariantCulture);
                long bucket = Number("bucket");
                Assert.AreEqual(300L, Number("total"));
                Assert.AreEqual(448500L + 300 * bucket, Number("amount"));
            }
        }
        for (int i = 0; i < 20; i++)
        {
            await Run();
        }
        long allocated = GC.GetTotalAllocatedBytes(true);
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 100; i++)
        {
            await Run();
        }
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            iterations = 100,
            warmup = 20,
            vertices = 3000,
            verified = true,
            millisecondsPerOperation = milliseconds / 100,
            allocatedBytesPerOperation = bytes / 100d,
            scope = "Warm in-memory executor plus result checks; process-wide allocations; excludes compilation, HTTP, storage and cold indexes. No performance threshold."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
