using System.Globalization;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (?o*2 AS ?v) { ?s <urn:p> ?o FILTER(?o>=2) } ORDER BY DESC(?v) OFFSET 1 LIMIT 2", "v", "6,4")]
    [DataRow("SELECT ?o { GRAPH ?g { ?s <urn:p> ?o FILTER(?o>=2) } FILTER(?g=<urn:g>) } ORDER BY (?o+0)", "o", "2,3,4")]
    [DataRow("SELECT (?o/0 AS ?bad) ?o { ?s <urn:p> ?o FILTER(?o>=2) } ORDER BY ?o", "o", "2,3,4")]
    [DataRow("SELECT ?v { ?s <urn:p> ?o FILTER(?v>=4) BIND(?o*2 AS ?v) } ORDER BY DESC(?v) OFFSET 1 LIMIT 2", "v", "6,4")]
    [DataRow("SELECT ?o { ?s <urn:p> ?o BIND(?o/0 AS ?bad) FILTER(!BOUND(?bad) && ?o>=2) } ORDER BY ?o", "o", "2,3,4")]
    public async Task Sparql_scalar_scan_filters_projects_and_orders_before_window(string query, string variable, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = new List<RdfQuad>();
        foreach (RdfIri? graph in new RdfIri?[] { null, new("urn:g"), new("urn:other") })
        {
            foreach (int value in Enumerable.Range(1, 4))
            {
                quads.Add(new(new RdfIri("urn:s:" + value), new("urn:p"),
                    new RdfLiteral(value.ToString(CultureInfo.InvariantCulture), "http://www.w3.org/2001/XMLSchema#integer"), graph));
            }
            quads.Add(new(new RdfIri("urn:bad"), new("urn:p"), new RdfLiteral("not numeric"), graph));
        }
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        var values = new List<string>();
        int complete = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync, database.ReleaseQueryContinuationAsync,
            new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                values.Add(((RdfLiteral)row[variable]).LexicalForm);
                Assert.IsFalse(row.ContainsKey("bad"), "Projection errors leave the alias unbound.");
            }
            complete += page.Complete ? 1 : 0;
        }
        Assert.AreEqual(expected, String.Join(',', values));
        Assert.AreEqual(1, complete);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }

    [TestMethod]
    public async Task Sparql_scalar_scan_captures_random_uuid_and_statement_clock_before_spilling()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.BulkLoadAsync(Guid.NewGuid(), Enumerable.Range(0, 5000)
            .Select(index => new RdfQuad(new RdfIri("urn:s:" + index), new("urn:p"), new RdfLiteral("value"))).ToArray());
        double previous = -1;
        var identifiers = new HashSet<RdfTerm>();
        var times = new HashSet<string>();
        int count = 0;
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await foreach (var page in RdfSparql.ScanAsync(
            "SELECT (RAND() AS ?r) (UUID() AS ?u) (NOW() AS ?t) { ?s <urn:p> ?o } ORDER BY ?r OFFSET 123 LIMIT 4500",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync,
            new(MaximumSortTemporaryBytes: 32 * 1024 * 1024)))
        {
            foreach (var row in page.Rows)
            {
                double current = Double.Parse(((RdfLiteral)row["r"]).LexicalForm, CultureInfo.InvariantCulture);
                Assert.IsTrue(current >= previous && current is >= 0 and < 1,
                    "Random aliases must retain the values used for global ordering.");
                previous = current;
                Assert.IsTrue(identifiers.Add(row["u"]));
                times.Add(((RdfLiteral)row["t"]).LexicalForm);
                count++;
            }
        }
        Assert.AreEqual(4500, count);
        Assert.AreEqual(1, times.Count);
        DateTimeOffset clock = DateTimeOffset.Parse(times.Single(), CultureInfo.InvariantCulture);
        Assert.IsTrue(clock >= before.AddMilliseconds(-1) && clock <= DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public async Task Sparql_scalar_scan_preserves_blank_node_scope_across_projection_aliases()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedRetainedQueryAsync(database);
        var identities = new HashSet<RdfTerm>();
        int count = 0;
        await foreach (var page in RdfSparql.ScanAsync(
            "SELECT (BNODE('x') AS ?a) (BNODE('x') AS ?b) { ?s ?p ?o } ORDER BY ?a",
            database.QueryPageAsync, database.ReleaseQueryContinuationAsync,
            new(PageSize: 1, MaximumSortTemporaryBytes: 65536)))
        {
            foreach (var row in page.Rows)
            {
                Assert.AreEqual(row["a"], row["b"]);
                Assert.IsTrue(identities.Add(row["a"]));
                count++;
            }
        }
        Assert.AreEqual(4, count);
    }

    [TestMethod]
    [DataRow("SELECT ?s { ?s ?p ?o FILTER EXISTS { ?s <urn:q> ?v } }")]
    [DataRow("SELECT (IF(EXISTS { ?s <urn:q> ?v },1,0) AS ?x) { ?s ?p ?o }")]
    [DataRow("SELECT ?s { ?s ?p ?o } ORDER BY ASC(EXISTS { ?s <urn:q> ?v })")]
    [DataRow("SELECT * { ?s ?p ?o BIND(EXISTS { ?s <urn:q> ?v } AS ?x) }")]
    public async Task Sparql_scalar_scan_rejects_nested_graph_reads_before_storage(string query)
    {
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken token) =>
            throw new AssertFailedException("Unsupported expression accessed storage.");
        await Assert.ThrowsExactlyAsync<NotSupportedException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync(query, Read, (_, _) => ValueTask.FromResult(false),
                new(MaximumSortTemporaryBytes: 65536)))
            {
                Assert.Fail("Unsupported expression returned a page.");
            }
        });
    }
}
