using System.Globalization;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ORDER BY ?missing DESC(?o) ?s")]
    [DataRow("ORDER BY ?missing ?s LIMIT 0")]
    public async Task Sparql_sorted_scan_preserves_term_identity_and_unbound_order_keys(string ordering)
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfTerm[] values =
        [
            new RdfBlankNode("local"), new RdfIri("urn:value"),
            new RdfLiteral("hello", language: "en"), new RdfLiteral("héllo 🌍"),
            new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1.0", "http://www.w3.org/2001/XMLSchema#decimal"),
            new RdfLiteral("2026-11-15T00:00:00Z", "http://www.w3.org/2001/XMLSchema#dateTime")
        ];
        var quads = values.Select((value, index) => new RdfQuad(new RdfIri("urn:term:" + index), new("urn:p"), value)).ToArray();
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        string query = "SELECT ?s ?o { ?s <urn:p> ?o } " + ordering;
        var expected = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(quads, query, default);
        var actual = new List<IReadOnlyDictionary<string, RdfTerm>>();
        int completed = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
            database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            actual.AddRange(page.Rows);
            completed += page.Complete ? 1 : 0;
        }
        CollectionAssert.AreEqual(expected.Bindings.Select(row => row["s"]).ToArray(), actual.Select(row => row["s"]).ToArray());
        CollectionAssert.AreEqual(expected.Bindings.Select(row => row["o"]).ToArray(), actual.Select(row => row["o"]).ToArray());
        Assert.AreEqual(1, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    [DataRow(0, 100000)]
    [DataRow(10000, 80000)]
    public async Task Sparql_sorted_scan_preserves_global_order_duplicates_and_snapshot_during_writes(int offset, int limit)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 100_000).Select(i => new RdfQuad(new RdfIri("urn:sorted:" + i), new("urn:p"),
            new RdfLiteral((i % 1000).ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"))).ToArray();
        var version = await database.BulkLoadAsync(Guid.NewGuid(), quads);
        int reads = 0;
        async ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken cancellation)
        {
            var page = await database.QueryPageAsync(pattern, size, cursor, cancellation);
            if (++reads == 1)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(quads[^1])]));
            }
            return page;
        }
        int rows = 0, completed = 0;
        await foreach (var page in RdfSparql.ScanAsync(
            $"SELECT ?o WHERE {{ ?s <urn:p> ?o }} ORDER BY DESC(?o) LIMIT {limit} OFFSET {offset}",
            Read, database.ReleaseQueryContinuationAsync, new(MaximumSortTemporaryBytes: 256 * 1024 * 1024)))
        {
            Assert.AreEqual(version.Value, page.SnapshotVersion);
            Assert.IsTrue(page.Rows.Count <= 256);
            CollectionAssert.AreEqual(new[] { "o" }, page.Variables.ToArray());
            foreach (var row in page.Rows)
            {
                Assert.AreEqual(1, row.Count);
                int expected = 999 - ((offset + rows) / 100);
                Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), ((RdfLiteral)row["o"]).LexicalForm);
                rows++;
            }
            completed += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(limit, rows);
        Assert.AreEqual(1, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task Sparql_sorted_scan_uses_nonprojected_keys_and_applies_window_after_sort()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), new[]
        {
            new RdfQuad(new RdfIri("urn:c"), new("urn:p"), new RdfLiteral("third")),
            new RdfQuad(new RdfIri("urn:a"), new("urn:p"), new RdfLiteral("first")),
            new RdfQuad(new RdfIri("urn:b"), new("urn:p"), new RdfLiteral("second"))
        });
        var values = new List<string>();
        await foreach (var page in RdfSparql.ScanAsync("SELECT ?o { ?s <urn:p> ?o } ORDER BY ?s OFFSET 1 LIMIT 1",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            values.AddRange(page.Rows.Select(row => ((RdfLiteral)row["o"]).LexicalForm));
        }
        CollectionAssert.AreEqual(new[] { "second" }, values);
    }

    [TestMethod]
    public async Task Sparql_sorted_scan_disk_budget_failure_never_reports_completion()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedRetainedQueryAsync(database);
        int completed = 0;
        await Assert.ThrowsExactlyAsync<QuerySortBudgetException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT * { ?s ?p ?o } ORDER BY ?s",
                database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 1)))
            {
                completed += page.Complete ? 1 : 0;
            }
        });
        Assert.AreEqual(0, completed);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }
}
