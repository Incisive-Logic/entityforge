using EntityForge.Db.Gql;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("row")]
    [DataRow("work")]
    [DataRow("missing-end")]
    [DataRow("snapshot")]
    [DataRow("requested-snapshot")]
    [DataRow("oversized")]
    [DataRow("missing-endpoint")]
    [DataRow("cancel")]
    [DataRow("deadline")]
    [DataRow("limit")]
    [DataRow("offset-limit")]
    [DataRow("dispose")]
    public async Task Gql_relationship_scan_limits_fail_without_completion_and_release_source(string scenario)
    {
        var a = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var b = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var edge = new GraphEdge(GraphEdgeId.New(), a.Id, b.Id, "link", new Dictionary<string, DatabaseValue>());
        int read = 0, rows = 0, completions = 0;
        bool disposed = false;
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<GqlEdgeScanInputPage> Source([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                read++;
                yield return new(new(42), scenario == "missing-endpoint" ? [a] : [a, b],
                    scenario == "oversized" ? [edge, edge] : [edge],
                    scenario == "missing-endpoint" ? 2 : scenario == "oversized" ? 4 : 3, false);
                if (scenario is "cancel" or "deadline")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                if (scenario == "missing-end")
                {
                    yield break;
                }
                read++;
                yield return new(new(scenario == "snapshot" ? 43 : 42), [a, b], [edge], 3, true);
            }
            finally
            {
                disposed = true;
            }
        }
        var compiled = new GraphGqlQueryCompiler(DateTimeOffset.UtcNow)
            .CompileStandard(new("MATCH (a)-[e:link]->(b) RETURN 1 AS answer")
            {
                SnapshotVersion = scenario == "requested-snapshot" ? 43 : null
            });
        var budget = new GqlScanBudget(MaximumRows: scenario == "row" ? 1 : 10,
            MaximumExaminedElements: scenario == "work" ? 2 : 10, PageSize: 1,
            Timeout: scenario == "deadline" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5));
        Exception? failure = null;
        try
        {
            await foreach (var page in GraphGqlScan.ExecuteEdgesAsync(compiled, scenario == "offset-limit" ? 1 : 0,
                scenario is "limit" or "offset-limit" ? 1 : long.MaxValue, Source, budget, cancellation.Token))
            {
                rows += page.Rows.Count;
                completions += page.Complete ? 1 : 0;
                if (scenario == "cancel")
                {
                    cancellation.Cancel();
                }
                if (scenario == "dispose")
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        Assert.IsTrue(disposed, "The source was not released.");
        if (scenario is "limit" or "offset-limit" or "dispose")
        {
            Assert.IsNull(failure);
            Assert.AreEqual(1, rows);
            Assert.AreEqual(scenario == "dispose" ? 0 : 1, completions);
            Assert.AreEqual(scenario == "offset-limit" ? 2 : 1, read);
        }
        else
        {
            Assert.AreEqual(0, completions, "A failed scan reported successful completion.");
            if (scenario is "row" or "work")
            {
                Assert.IsInstanceOfType<QueryScanWorkLimitException>(failure);
            }
            else if (scenario is "cancel" or "deadline")
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
