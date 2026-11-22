using System.Globalization;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql_distinct_scan_collapses_unbound_expression_errors_to_one_row()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(1, 3).Select(i =>
            new RdfQuad(new RdfIri("urn:s:" + i), new("urn:p"), new RdfLiteral(i.ToString(),
                "http://www.w3.org/2001/XMLSchema#integer"))).ToArray());
        int rows = 0, complete = 0;
        await foreach (var page in RdfSparql.ScanAsync("SELECT DISTINCT (?o / 0 AS ?bad) { ?s <urn:p> ?o }",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            CollectionAssert.AreEqual(new[] { "bad" }, page.Variables.ToArray());
            foreach (var row in page.Rows)
            {
                Assert.AreEqual(0, row.Count);
                rows++;
            }
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(1, rows);
        Assert.AreEqual(1, complete);
    }

    [TestMethod]
    public async Task Sparql_distinct_scan_deduplicates_before_global_window()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 100_000).Select(i => new RdfQuad(new RdfIri("urn:distinct:" + i), new("urn:p"),
            new RdfLiteral((i % 20000).ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"))).ToArray();
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        int count = 0, complete = 0;
        await foreach (var page in RdfSparql.ScanAsync("SELECT DISTINCT ?o { ?s <urn:p> ?o } ORDER BY DESC(?o) OFFSET 1000 LIMIT 18000",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(MaximumSortTemporaryBytes: 256 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual((18999 - count++).ToString(CultureInfo.InvariantCulture), ((RdfLiteral)row["o"]).LexicalForm);
            }
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(18000, count);
        Assert.AreEqual(1, complete);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task Sparql_distinct_scan_preserves_exact_rdf_term_identity()
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfTerm[] terms = [new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1.0", "http://www.w3.org/2001/XMLSchema#decimal"), new RdfLiteral("1"),
            new RdfLiteral("1", language: "en"), new RdfLiteral("1", language: "fr")];
        await database.BulkLoadAsync(Guid.NewGuid(), terms.Concat(terms).Select((term, i) =>
            new RdfQuad(new RdfIri("urn:s:" + i), new("urn:p"), term)).ToArray());
        var result = new List<RdfTerm>();
        await foreach (var page in RdfSparql.ScanAsync("SELECT DISTINCT ?o { ?s <urn:p> ?o }",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            result.AddRange(page.Rows.Select(row => row["o"]));
        }
        CollectionAssert.AreEquivalent(terms, result);
    }

    [TestMethod]
    public async Task Sparql_distinct_scan_selects_nonprojected_order_representative()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), new[] { ("a", "alpha"), ("z", "alpha"), ("b", "beta"), ("y", "beta") }
            .Select(pair => new RdfQuad(new RdfIri("urn:" + pair.Item1), new("urn:p"), new RdfLiteral(pair.Item2))).ToArray());
        var result = new List<string>();
        await foreach (var page in RdfSparql.ScanAsync("SELECT DISTINCT ?o { ?s <urn:p> ?o } ORDER BY ?s OFFSET 1 LIMIT 1",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync, new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            result.AddRange(page.Rows.Select(row => ((RdfLiteral)row["o"]).LexicalForm));
        }
        CollectionAssert.AreEqual(new[] { "beta" }, result);
    }
}
