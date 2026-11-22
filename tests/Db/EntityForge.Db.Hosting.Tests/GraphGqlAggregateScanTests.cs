using EntityForge.Db.Gql;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_count_scan_groups_nested_lists_with_equivalent_numbers_and_nulls(bool nested)
    {
        await using var database = await OpenAsync(CreateDirectory());
        DatabaseValue?[] values = [new DatabaseValue.Int64(1), new DatabaseValue.Double(1), new DatabaseValue.Decimal(1.0m), null];
        await database.BulkLoadAsync(Guid.NewGuid(), values.Select(value => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            value is null ? new Dictionary<string, DatabaseValue>() : new Dictionary<string, DatabaseValue> { ["score"] = value })).ToArray(), []);
        string expression = nested ? "[[n.score], NULL]" : "[n.score, NULL]";
        int groups = 0, completions = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new($"MATCH (n) RETURN {expression} AS v, COUNT(*) AS c GROUP BY v"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 1, token), new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                var key = (ListValue)row.Bindings["v"];
                Assert.AreEqual(2, key.Items.Count);
                Assert.IsTrue(key.Items[1] is NullValue or UnboundValue);
                QueryValue first = nested ? ((ListValue)key.Items[0]).Items.Single() : key.Items[0];
                Assert.AreEqual(first is NullValue or UnboundValue ? "1" : "3", ((LiteralValue)row.Bindings["c"]).LexicalValue);
                groups++;
            }
            completions += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(2, groups);
        Assert.AreEqual(1, completions);
    }

    [TestMethod]
    public async Task Gql_aggregate_scan_keeps_standard_computed_projection_alias_requirement()
    {
        IAsyncEnumerable<GqlScanInputPage> Read(CancellationToken token) => throw new AssertFailedException("Invalid query accessed storage.");
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
        {
            await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN COUNT(*)"), Read,
                new(MaximumSortTemporaryBytes: 65536)))
            {
                Assert.Fail("Invalid computed projection returned a page.");
            }
        });
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    public async Task Gql_aggregate_scan_distinguishes_empty_implicit_and_explicit_groups(bool grouped, bool distinct, bool countOnly)
    {
        await using var database = await OpenAsync(CreateDirectory());
        int rows = 0, complete = 0;
        string query = grouped ? "MATCH (n) RETURN n.score AS v, COUNT(*) AS c GROUP BY v" : "MATCH (n) RETURN COUNT(*) AS c, SUM(n.score) AS s";
        if (countOnly)
        {
            query = "MATCH (n) RETURN COUNT(*) AS c";
        }
        if (distinct)
        {
            query = query.Replace("COUNT(*)", "COUNT(DISTINCT n)", StringComparison.Ordinal)
                .Replace("SUM(n.score)", "SUM(DISTINCT n.score)", StringComparison.Ordinal);
        }
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(query),
            token => GraphGqlScan.ReadNativeAsync(database, null, 1, token), new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual("0", ((LiteralValue)row.Bindings["c"]).LexicalValue);
                if (!countOnly)
                {
                    Assert.IsInstanceOfType<UnboundValue>(row.Bindings["s"]);
                }
                rows++;
            }
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(grouped ? 0 : 1, rows);
        Assert.AreEqual(1, complete);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Gql_aggregate_scan_groups_equivalent_numeric_types_and_nulls(bool countStar)
    {
        await using var database = await OpenAsync(CreateDirectory());
        DatabaseValue?[] values = [new DatabaseValue.Int64(1), new DatabaseValue.Double(1), new DatabaseValue.Decimal(1.0m), null];
        await database.BulkLoadAsync(Guid.NewGuid(), values.Select(value => new GraphVertex(GraphVertexId.New(), new HashSet<string>(),
            value is null ? new Dictionary<string, DatabaseValue>() : new Dictionary<string, DatabaseValue> { ["score"] = value })).ToArray(), []);
        var counts = new List<string>();
        int warnings = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n.score AS v, COUNT(" +
            (countStar ? "*" : "n.score") + ") AS c GROUP BY v ORDER BY v NULLS LAST"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 1, token), new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            counts.AddRange(page.Rows.Select(row => ((LiteralValue)row.Bindings["c"]).LexicalValue));
            warnings += page.Diagnostics?.Count(item => item.Code == "01G11") ?? 0;
        }
        CollectionAssert.AreEqual(new[] { "3", countStar ? "1" : "0" }, counts);
        Assert.AreEqual(countStar ? 0 : 1, warnings);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Gql_aggregate_scan_handles_100000_inputs_and_snapshot_write_before_window(bool distinct, bool countOnly)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var ids = Enumerable.Range(0, 100000).Select(_ => GraphVertexId.New()).ToArray();
        DatabaseVersion version = default;
        foreach (var batch in Enumerable.Range(0, ids.Length).Chunk(1000))
        {
            version = await database.CommitAsync(new(Guid.NewGuid(), batch.SelectMany(index => new GraphMutation[]
            {
                new CreateVertex(ids[index], []),
                new SetVertexProperty(ids[index], "score", new DatabaseValue.Int64(index % 20000))
            }).ToArray()));
        }
        async IAsyncEnumerable<GqlScanInputPage> Read([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
        {
            bool written = false;
            await foreach (var page in GraphGqlScan.ReadNativeAsync(database, null, 256, cancellation))
            {
                if (!written)
                {
                    await database.CommitAsync(new(Guid.NewGuid(), [new SetVertexProperty(ids[18999], "score", new DatabaseValue.Int64(-1))]));
                    written = true;
                }
                yield return page;
            }
        }
        int rows = 0, completed = 0;
        string query = "MATCH (n) RETURN n.score AS v, COUNT(*) AS c" +
            (countOnly ? "" : ", SUM(" + (distinct ? "DISTINCT " : "") + "n.score) AS s") +
            " GROUP BY v ORDER BY v DESC OFFSET 1000 LIMIT 18000";
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(query),
            Read, new(MaximumSortTemporaryBytes: 256 * 1024 * 1024)))
        {
            Assert.AreEqual(version, page.Version);
            foreach (var row in page.Rows)
            {
                int expected = 18999 - rows++;
                Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), ((LiteralValue)row.Bindings["v"]).LexicalValue);
                Assert.AreEqual("5", ((LiteralValue)row.Bindings["c"]).LexicalValue);
                if (!countOnly)
                {
                    Assert.AreEqual(((distinct ? 1 : 5) * expected).ToString(CultureInfo.InvariantCulture), ((LiteralValue)row.Bindings["s"]).LexicalValue);
                }
            }
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(18000, rows);
        Assert.AreEqual(1, completed);
    }

    [TestMethod]
    [DataRow("MATCH (n) RETURN COUNT(*) AS c", "c", "5")]
    [DataRow("MATCH (n) RETURN COUNT(n) AS c", "c", "5")]
    [DataRow("MATCH (n) RETURN COUNT(DISTINCT n) AS c", "c", "5")]
    [DataRow("MATCH (n) RETURN COUNT(DISTINCT n) AS c ORDER BY c NULLS LAST", "c", "5")]
    [DataRow("MATCH (n) RETURN SUM(DISTINCT n.score) AS s", "s", "10")]
    [DataRow("MATCH (n) RETURN COUNT(n) AS c ORDER BY c NULLS LAST", "c", "5")]
    [DataRow("MATCH (n) RETURN SUM(n.score) AS s", "s", "10")]
    [DataRow("MATCH (n) RETURN AVG(n.score) AS a", "a", "2.5")]
    [DataRow("MATCH (n) RETURN MIN(n.score) AS m", "m", "1")]
    [DataRow("MATCH (n) RETURN MAX(n.score) AS m", "m", "4")]
    public async Task Gql_aggregate_scan_preserves_values_and_null_diagnostics(string query, string alias, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 5).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), i == 0 ? new Dictionary<string, DatabaseValue>() : new Dictionary<string, DatabaseValue>
            {
                ["score"] = new DatabaseValue.Int64(i)
            })).ToArray(), []);
        int rows = 0, complete = 0, warnings = 0;
        await foreach (var page in GraphGqlScan.ExecuteAsync(new(query),
            token => GraphGqlScan.ReadNativeAsync(database, null, 2, token), new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual(expected, ((LiteralValue)row.Bindings[alias]).LexicalValue);
                rows++;
            }
            warnings += page.Diagnostics?.Count(item => item.Code == "01G11") ?? 0;
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(1, rows);
        Assert.AreEqual(1, complete);
        Assert.AreEqual(query.Contains("COUNT(*)", StringComparison.Ordinal) || query.Contains("COUNT(n)", StringComparison.Ordinal) ||
            query.Contains("COUNT(DISTINCT n)", StringComparison.Ordinal) ? 0 : 1, warnings);
    }

    [TestMethod]
    public async Task Gql_aggregate_scan_groups_before_ordering_and_window()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 20).Select(i => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(), new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(i % 4) })).ToArray(), []);
        var scores = new List<string>();
        await foreach (var page in GraphGqlScan.ExecuteAsync(new("MATCH (n) RETURN n.score AS v, COUNT(*) AS c GROUP BY v ORDER BY v DESC OFFSET 1 LIMIT 2"),
            token => GraphGqlScan.ReadNativeAsync(database, null, 2, token), new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual("5", ((LiteralValue)row.Bindings["c"]).LexicalValue);
                scores.Add(((LiteralValue)row.Bindings["v"]).LexicalValue);
            }
        }
        CollectionAssert.AreEqual(new[] { "2", "1" }, scores);
    }
}
