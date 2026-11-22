using EntityForge.Db.Gql;
using System.Runtime.CompilerServices;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    [DataRow("MATCH (n) RETURN n.score AS v ORDER BY RAND()")]
    [DataRow("MATCH (n) WHERE RAND() > 0.5 RETURN n.score AS v ORDER BY v")]
    public async Task Gql_sorted_scan_rejects_nonstandard_function_spellings_before_reading(string query)
    {
        IAsyncEnumerable<GqlScanInputPage> Read(CancellationToken token) => throw new AssertFailedException("Nonstandard scan accessed storage.");
        try
        {
            await foreach (var page in GraphGqlScan.ExecuteAsync(new(query), Read, new(MaximumSortTemporaryBytes: 65536)))
            {
                Assert.Fail("Nonstandard scan produced a page.");
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
        {
            // Grammar/standard validation may reject a spelling before scan planning.
            return;
        }
        Assert.Fail("Nonstandard ordered query was accepted.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_scan_statement_clock_is_shared_by_all_pages_and_sorting(bool ordered)
    {
        async IAsyncEnumerable<GqlScanInputPage> Read([EnumeratorCancellation] CancellationToken cancellation)
        {
            for (int index = 0; index < 3; index++)
            {
                await Task.Delay(20, cancellation);
                yield return new(new(1), [new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
                    new Dictionary<string, DatabaseValue>())], 1, index == 2);
            }
        }
        var values = new List<string>();
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN CURRENT_TIMESTAMP AS stamp" +
            (ordered ? " ORDER BY stamp" : "")), Read, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            values.AddRange(page.Rows.Select(row => ((LiteralValue)row.Bindings["stamp"]).LexicalValue));
        }
        Assert.AreEqual(3, values.Count);
        Assert.AreEqual(1, values.Distinct().Count());
        DateTimeOffset captured = DateTimeOffset.Parse(values[0], CultureInfo.InvariantCulture);
        Assert.IsTrue(captured >= before.AddMilliseconds(-1) && captured <= DateTimeOffset.UtcNow);
    }

    [TestMethod]
    [DataRow("v ASC NULLS FIRST", "null,1.0,2,3")]
    [DataRow("v DESC NULLS LAST", "3,2,1.0,null")]
    public async Task Gql_sorted_scan_preserves_alias_numeric_order_and_null_placement(string ordering, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        DatabaseValue?[] scores = [new DatabaseValue.Double(2), null, new DatabaseValue.Decimal(1.0m), new DatabaseValue.Int64(3)];
        var vertices = scores.Select(value => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            value is null ? new Dictionary<string, DatabaseValue>() : new Dictionary<string, DatabaseValue> { ["score"] = value })).ToArray();
        await database.BulkLoadAsync(Guid.NewGuid(), vertices, []);
        var values = new List<string>();
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n.score AS v ORDER BY " + ordering),
            token => GraphGqlScan.ReadNativeAsync(database, null, 2, token), new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            values.AddRange(page.Rows.Select(row => (row.Bindings["v"] as LiteralValue)?.LexicalValue ?? "null"));
        }
        Assert.AreEqual(expected, String.Join(',', values));
    }

    [TestMethod]
    public async Task Gql_sorted_scan_work_exhaustion_releases_source_without_completion()
    {
        bool disposed = false;
        async IAsyncEnumerable<GqlScanInputPage> Read([EnumeratorCancellation] CancellationToken cancellation)
        {
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    yield return new(new(1), [new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
                        new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(i) })], 1, i == 2);
                    await Task.Yield();
                }
            }
            finally
            {
                disposed = true;
            }
        }
        int completed = 0;
        await Assert.ThrowsExactlyAsync<QueryScanWorkLimitException>(async () =>
        {
            await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n.score AS score ORDER BY score"),
                Read, new(MaximumExaminedElements: 1, PageSize: 1, MaximumSortTemporaryBytes: 65536)))
            {
                completed += page.Complete ? 1 : 0;
            }
        });
        Assert.IsTrue(disposed);
        Assert.AreEqual(0, completed);
    }

    [TestMethod]
    [DataRow(0, 100000)]
    [DataRow(10000, 80000)]
    public async Task Gql_sorted_scan_preserves_global_order_duplicates_and_snapshot(int offset, int limit)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var vertices = Enumerable.Range(0, 100_000).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(i % 1000) })).ToArray();
        DatabaseVersion version = default;
        foreach (var batch in vertices.Chunk(1000))
        {
            version = await database.CommitAsync(new(Guid.NewGuid(), batch.SelectMany(vertex => new GraphMutation[]
            {
                new CreateVertex(vertex.Id, vertex.Labels.ToArray()),
                new SetVertexProperty(vertex.Id, "score", vertex.Properties["score"])
            }).ToArray()));
        }
        bool written = false;
        async IAsyncEnumerable<GqlScanInputPage> Read([EnumeratorCancellation] CancellationToken cancellation)
        {
            await foreach (var page in GraphGqlScan.ReadNativeAsync(database, null, 256, cancellation))
            {
                if (!written)
                {
                    await database.CommitAsync(new(Guid.NewGuid(), [new DeleteVertex(vertices[^1].Id)]));
                    written = true;
                }
                yield return page;
            }
        }
        int rows = 0, complete = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(
            new($"MATCH (n) RETURN n.score AS score ORDER BY n.score DESC OFFSET {offset} LIMIT {limit}"),
            Read, new(MaximumSortTemporaryBytes: 256 * 1024 * 1024)))
        {
            Assert.AreEqual(version, page.Version);
            Assert.IsTrue(page.Rows.Count <= 256);
            foreach (var row in page.Rows)
            {
                Assert.AreEqual((999 - ((offset + rows) / 100)).ToString(CultureInfo.InvariantCulture),
                    ((LiteralValue)row.Bindings["score"]).LexicalValue);
                rows++;
            }
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(limit, rows);
        Assert.AreEqual(1, complete);
        Assert.IsTrue(written);
    }

    [TestMethod]
    [DataRow("ORDER BY n.score OFFSET 1 LIMIT 1", "second")]
    [DataRow("ORDER BY -n.score LIMIT 1", "third")]
    public async Task Gql_sorted_scan_filters_before_sort_and_preserves_nonprojected_keys(string order, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var vertices = new[] { (3, "third"), (1, "first"), (2, "second") }.Select(item => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue>
            {
                ["score"] = new DatabaseValue.Int64(item.Item1),
                ["text"] = new DatabaseValue.String(item.Item2),
                ["includeRow"] = new DatabaseValue.Boolean(true)
            })).ToArray();
        await database.BulkLoadAsync(Guid.NewGuid(), vertices.Append(new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue>
            {
                ["score"] = new DatabaseValue.String("not numeric"),
                ["includeRow"] = new DatabaseValue.Boolean(false)
            })).ToArray(), []);
        var values = new List<string>();
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) WHERE n.includeRow=true RETURN n.text AS answer " + order),
            token => GraphGqlScan.ReadNativeAsync(database, null, 1, token), new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            values.AddRange(page.Rows.Select(row => ((LiteralValue)row.Bindings["answer"]).LexicalValue));
        }
        CollectionAssert.AreEqual(new[] { expected }, values);
    }
}
