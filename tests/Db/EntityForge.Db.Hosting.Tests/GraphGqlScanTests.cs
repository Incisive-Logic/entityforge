using EntityForge.Db.Gql;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    [DataRow("OFFSET 1 LIMIT 2", 2, 3)]
    [DataRow("LIMIT 0", 0, 1)]
    [DataRow("OFFSET 7", 0, 5)]
    [DataRow("OFFSET $skip LIMIT $take /* $ignored */", 2, 3)]
    public async Task Gql_scan_window_applies_after_filtering_and_releases_early(string window, int expectedRows, int expectedPages)
    {
        bool disposed = false;
        int read = 0, rows = 0, completions = 0;
        async IAsyncEnumerable<GqlScanInputPage> Read([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                for (int page = 0; page < 5; page++)
                {
                    token.ThrowIfCancellationRequested();
                    read++;
                    yield return new(new(42), Enumerable.Range(0, 2).Select(index => new GraphVertex(GraphVertexId.New(),
                        new HashSet<string>(), new Dictionary<string, DatabaseValue>
                        {
                            ["scanFlag"] = new DatabaseValue.Boolean(index == 1)
                        })).ToArray(), 2, page == 4);
                    await Task.Yield();
                }
            }
            finally
            {
                disposed = true;
            }
        }
        var request = new GraphGqlQueryRequest("MATCH (𐐀CASE) WHERE 𐐀CASE.scanFlag=true RETURN 𐐀CASE.missing AS answer " + window,
            Parameters: new Dictionary<string, JsonElement>
            {
                ["skip"] = JsonSerializer.SerializeToElement(1),
                ["take"] = JsonSerializer.SerializeToElement(2)
            });
        await foreach (var page in GraphGqlScan.ExecuteAsync(request, Read, new(PageSize: 2)))
        {
            rows += page.Rows.Count;
            completions += page.Complete ? 1 : 0;
            Assert.AreEqual(new DatabaseVersion(42), page.Version);
            Assert.IsTrue(page.Rows.All(row => row.Bindings["answer"] is UnboundValue));
        }
        Assert.AreEqual(expectedRows, rows);
        Assert.AreEqual(expectedPages, read);
        Assert.AreEqual(1, completions);
        Assert.IsTrue(disposed);
    }

    [TestMethod]
    [DataRow("MATCH (n) RETURN n.missing AS answer", 100000)]
    [DataRow("MATCH (n) RETURN n.missing AS answer OFFSET 10000 LIMIT 80000", 80000)]
    public async Task Gql_scan_returns_large_duplicate_projection_rows_at_one_snapshot_during_writes(string query, int expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var vertices = Enumerable.Range(0, 100_000).Select(_ => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue>())).ToArray();
        var version = await database.BulkLoadAsync(Guid.NewGuid(), vertices, []);
        int rows = 0, completed = 0;
        bool written = false;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(query),
            token => GraphGqlScan.ReadNativeAsync(database, null, 256, token), new()))
        {
            Assert.AreEqual(version, page.Version);
            Assert.IsTrue(page.Rows.Count <= 256);
            Assert.IsTrue(page.Rows.All(row => row.Bindings["answer"] is UnboundValue),
                "Missing properties retain the evaluator's explicit unbound representation.");
            if (!written)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new DeleteVertex(vertices[^1].Id)]));
                written = true;
            }
            rows += page.Rows.Count;
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(expected, rows);
        Assert.AreEqual(1, completed);
    }

    [TestMethod]
    [DataRow("MATCH (n) RETURN n ORDER BY n.name LIMIT 2")]
    [DataRow("MATCH (n) RETURN DISTINCT n")]
    [DataRow("MATCH (n) RETURN n ORDER BY n.name")]
    [DataRow("MATCH (n)-[e]->(m) RETURN n")]
    [DataRow("MATCH (n) RETURN COUNT(*) AS answer")]
    [DataRow("MATCH (n) WHERE EXISTS { MATCH (m) RETURN m OFFSET 1 } RETURN n LIMIT 2")]
    public async Task Gql_scan_refuses_global_operators_before_reading(string query)
    {
        IAsyncEnumerable<GqlScanInputPage> Read(CancellationToken token) =>
            throw new AssertFailedException("An unsupported plan accessed storage.");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
        {
            await foreach (var page in GraphGqlScan.ExecuteAsync(new(query), Read, new()))
            {
                Assert.Fail("An unsupported plan returned a page.");
            }
        });
    }

    [TestMethod]
    [DataRow("budget")]
    [DataRow("cancel")]
    [DataRow("break")]
    [DataRow("snapshot")]
    [DataRow("incomplete")]
    public async Task Gql_scan_failure_or_early_disposal_releases_its_source(string scenario)
    {
        bool disposed = false;
        int completed = 0;
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<GqlScanInputPage> Read([EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    token.ThrowIfCancellationRequested();
                    yield return new(new(scenario == "snapshot" ? i : 1),
                        [new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>())], 1, false);
                    await Task.Yield();
                }
            }
            finally
            {
                disposed = true;
            }
        }
        async Task Run()
        {
            await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n"), Read,
                new(MaximumRows: 1), cancellation.Token))
            {
                completed += page.Complete ? 1 : 0;
                if (scenario == "break")
                {
                    break;
                }
                if (scenario == "cancel")
                {
                    cancellation.Cancel();
                }
            }
        }
        if (scenario == "break")
        {
            await Run();
        }
        else if (scenario == "cancel")
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(Run);
        }
        else if (scenario == "snapshot")
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(Run);
        }
        else if (scenario == "incomplete")
        {
            // An empty source without a completion page cannot constitute a successful answer.
            async IAsyncEnumerable<GqlScanInputPage> Empty()
            {
                try
                {
                    await Task.Yield();
                    yield break;
                }
                finally
                {
                    disposed = true;
                }
            }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            {
                await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n"), _ => Empty(), new()))
                {
                    Assert.Fail("An empty source emitted a result.");
                }
            });
        }
        else
        {
            await Assert.ThrowsExactlyAsync<QueryScanWorkLimitException>(Run);
        }
        Assert.IsTrue(disposed);
        Assert.AreEqual(0, completed);
    }
}
