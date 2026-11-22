using EntityForge.Db.Gql;
using System.Globalization;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("ordered")]
    [DataRow("distinct")]
    [DataRow("disk")]
    [DataRow("row")]
    [DataRow("cancel")]
    [DataRow("missing-end")]
    public async Task Gql_relationship_sorted_scan_spills_preserves_bags_and_cleans_up(string scenario)
    {
        const int count = 6000, size = 16;
        var a = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var b = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        bool disposed = false;
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<GqlEdgeScanInputPage> Source([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                for (int index = 0; index < count; index += size)
                {
                    token.ThrowIfCancellationRequested();
                    var edges = Enumerable.Range(index, Math.Min(size, count - index)).Select(value =>
                        new GraphEdge(GraphEdgeId.New(), a.Id, b.Id, "link", new Dictionary<string, DatabaseValue>
                        {
                            ["score"] = new DatabaseValue.Int64(value % 301)
                        })).ToArray();
                    yield return new(new(42), [a, b], edges, edges.Length + 2,
                        scenario != "missing-end" && index + size >= count);
                    if (scenario == "cancel")
                    {
                        cancellation.Cancel();
                    }
                    await Task.Yield();
                }
            }
            finally
            {
                disposed = true;
            }
        }
        var compiled = new GraphGqlQueryCompiler(DateTimeOffset.UtcNow).CompileStandard(new(
            "MATCH (a)-[e:link]->(b) RETURN " + (scenario == "distinct" ? "DISTINCT " : "") + "e.score AS v"));
        var values = new List<int>();
        int completions = 0;
        Exception? failure = null;
        try
        {
            await foreach (var page in GraphGqlScan.ExecuteEdgesAsync(compiled, 17, 123, Source,
                new(MaximumRows: scenario == "row" ? 10 : 1000, PageSize: size,
                    MaximumSortTemporaryBytes: scenario == "disk" ? 1 : 4 * 1024 * 1024), cancellation.Token,
                [new("e", "score", GraphPatternSortDirection.Descending)]))
            {
                Assert.IsTrue(page.Rows.Count <= size);
                Assert.AreEqual(new DatabaseVersion(42), page.Version);
                values.AddRange(page.Rows.Select(row => Int32.Parse(((LiteralValue)row.Bindings["v"]).LexicalValue, CultureInfo.InvariantCulture)));
                completions += page.Complete ? 1 : 0;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        Assert.IsTrue(disposed);
        if (scenario is "ordered" or "distinct")
        {
            Assert.IsNull(failure, failure?.ToString());
            IEnumerable<int> expected = Enumerable.Range(0, count).Select(value => value % 301).OrderByDescending(value => value);
            if (scenario == "distinct")
            {
                expected = expected.Distinct();
            }
            CollectionAssert.AreEqual(expected.Skip(17).Take(123).ToArray(), values);
            Assert.AreEqual(1, completions);
        }
        else
        {
            Assert.AreEqual(0, completions);
            if (scenario == "disk")
            {
                Assert.IsInstanceOfType<QuerySortBudgetException>(failure);
            }
            else if (scenario == "row")
            {
                Assert.IsInstanceOfType<QueryScanWorkLimitException>(failure);
            }
            else if (scenario == "cancel")
            {
                Assert.IsInstanceOfType<OperationCanceledException>(failure);
            }
            else
            {
                Assert.IsInstanceOfType<InvalidDataException>(failure);
            }
        }
    }
}
