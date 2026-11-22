using System.Globalization;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_literal_subject_scan_pins_snapshot_and_releases_probe_cursor(bool oversized)
    {
        int reads = 0;
        int releases = 0;
        int completed = 0;
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken token)
        {
            reads++;
            Assert.AreEqual(1, size);
            Assert.IsNull(cursor);
            var quad = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("v"));
            return ValueTask.FromResult(new RdfQueryPage(7, oversized ? [quad, quad] : [quad], "probe", null));
        }
        ValueTask<bool> Release(string cursor, CancellationToken token)
        {
            Assert.AreEqual("probe", cursor);
            releases++;
            return ValueTask.FromResult(true);
        }
        async Task Run()
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT ?o { 1 <urn:p> ?o }", Read, Release, new()))
            {
                Assert.AreEqual(7L, page.SnapshotVersion);
                Assert.AreEqual(0, page.Rows.Count);
                completed += page.Complete ? 1 : 0;
            }
        }
        if (oversized)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(Run);
        }
        else
        {
            await Run();
        }
        Assert.AreEqual(oversized ? 0 : 1, completed);
        Assert.AreEqual(1, reads);
        Assert.AreEqual(1, releases);
    }

    [TestMethod]
    [DataRow("rows")]
    [DataRow("work")]
    [DataRow("cancel")]
    public async Task Sparql_join_scan_budget_failures_never_complete_and_release_all_patterns(string failure)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedJoinScanAsync(database);
        using var cancellation = new CancellationTokenSource();
        int completed = 0;
        await using (var source = new RdfQueryScanReader(database, failure == "work" ? 1 : 1000, multiplePatterns: true))
        {
            async Task Read()
            {
                await foreach (var page in RdfSparql.ScanAsync("SELECT ?v { ?s <urn:route> <urn:hub> . ?s <urn:p> ?v }",
                    source.ReadAsync, source.ReleaseAsync, new(MaximumRows: failure == "rows" ? 1 : 100, PageSize: 1), cancellation.Token))
                {
                    completed += page.Complete ? 1 : 0;
                    if (failure == "cancel")
                    {
                        cancellation.Cancel();
                    }
                }
            }
            if (failure == "cancel")
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(Read);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(Read);
            }
        }
        Assert.AreEqual(0, completed);
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    public async Task Sparql_join_scan_rejects_cross_pattern_snapshot_change_and_releases_both_cursors()
    {
        var released = new HashSet<string>();
        ValueTask<RdfQueryPage> Read(RdfQuadPattern pattern, int size, string? cursor, CancellationToken token)
        {
            bool first = pattern.Predicate == new RdfIri("urn:p");
            return ValueTask.FromResult(new RdfQueryPage(first ? 1 : 2,
                [new(new RdfIri("urn:s"), pattern.Predicate!, new RdfLiteral("v"))], first ? "first" : "second", null));
        }
        ValueTask<bool> Release(string cursor, CancellationToken token) => ValueTask.FromResult(released.Add(cursor));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT * { ?s <urn:p> ?v . ?s <urn:q> ?x }",
                Read, Release, new(PageSize: 1)))
            {
                Assert.Fail("A mixed-snapshot binding escaped the join.");
            }
        });
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, released.ToArray());
    }

    [TestMethod]
    [DataRow("SELECT ?v { 1 <urn:p> ?v }", "")]
    [DataRow("SELECT ?v { 1 <urn:p> ?v } ORDER BY ?v", "")]
    [DataRow("SELECT (COUNT(*) AS ?v) { 1 <urn:p> ?x }", "0")]
    [DataRow("SELECT ?v { 1 <urn:p> ?v . ?s <urn:q> ?tag }", "")]
    [DataRow("SELECT ?v { ?s <urn:route> <urn:hub> . ?s <urn:p> ?v } ORDER BY ?v", "1,2,3")]
    [DataRow("SELECT ?v { ?s <urn:route> <urn:hub> . ?s <urn:p> ?v . ?s <urn:q> ?tag } ORDER BY ?v", "1,1,2,2")]
    [DataRow("SELECT DISTINCT ?v { ?s <urn:p> ?v . ?s <urn:q> ?tag } ORDER BY ?v", "1,2")]
    [DataRow("SELECT ?v { ?s <urn:p> ?v . ?s <urn:q> ?tag } ORDER BY DESC(?v) OFFSET 1 LIMIT 2", "2,1")]
    [DataRow("SELECT (SUM(?x) AS ?v) { ?s <urn:p> ?x . ?s <urn:q> ?tag }", "6")]
    [DataRow("SELECT (COUNT(*) AS ?v) { ?s <urn:p> ?x . ?s <urn:q> ?tag }", "4")]
    [DataRow("SELECT ?v { ?s <urn:p> ?v BIND(?v*2 AS ?x) ?other <urn:p> ?x }", "1")]
    [DataRow("SELECT ?v { GRAPH ?g { ?s <urn:route> <urn:hub> . ?s <urn:p> ?v } } ORDER BY ?v", "1,2,3")]
    [DataRow("SELECT ?v { ?s <urn:route> <urn:hub> . ?s <urn:p> ?v FILTER(?v>1) } ORDER BY ?v", "2,3")]
    [DataRow("SELECT ?v { ?s <urn:p> ?v . ?s <urn:missing> ?x }", "")]
    [DataRow("SELECT ?v { ?s <urn:p> ?v . ?v <urn:q> ?tag }", "")]
    [DataRow("SELECT (COUNT(*) AS ?v) { ?s <urn:p> ?x . ?x <urn:q> ?tag }", "0")]
    [DataRow("SELECT ?v { ?s <urn:p> ?v BIND(1 AS ?literal) ?literal <urn:q> ?tag }", "")]
    [DataRow("SELECT (COUNT(*) AS ?v) { ?s <urn:p> ?x . ?s <urn:missing> ?y }", "0")]
    [DataRow("SELECT ?v { ?s <urn:p> ?v . ?other <urn:q> ?tag } ORDER BY ?v LIMIT 5", "1,1,1,1,2")]
    public async Task Sparql_join_scan_preserves_binding_bags_and_global_operators(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedJoinScanAsync(database);
        var values = new List<string>();
        int completed = 0;
        await using (var source = new RdfQueryScanReader(database, multiplePatterns: true))
        {
            await foreach (var page in RdfSparql.ScanAsync(query, source.ReadAsync, source.ReleaseAsync,
                new(PageSize: 1, MaximumSortTemporaryBytes: 1024 * 1024)))
            {
                Assert.IsTrue(page.Rows.Count <= 1);
                values.AddRange(page.Rows.Select(row => ((RdfLiteral)row["v"]).LexicalForm));
                completed += page.Complete ? 1 : 0;
            }
        }
        Assert.AreEqual(expected, String.Join(',', values));
        Assert.AreEqual(1, completed);
        await database.CreateCheckpointAsync();
    }

    [TestMethod]
    public async Task Sparql_join_scan_keeps_snapshot_when_later_patterns_are_opened_after_writes()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await SeedJoinScanAsync(database);
        var values = new List<string>();
        long? version = null;
        await using (var source = new RdfQueryScanReader(database, multiplePatterns: true))
        {
            await foreach (var page in RdfSparql.ScanAsync("SELECT ?v { ?s <urn:route> <urn:hub> . ?s <urn:p> ?v }",
                source.ReadAsync, source.ReleaseAsync, new(PageSize: 1)))
            {
                version ??= page.SnapshotVersion;
                Assert.AreEqual(version.Value, page.SnapshotVersion);
                values.AddRange(page.Rows.Select(row => ((RdfLiteral)row["v"]).LexicalForm));
                if (values.Count == 1)
                {
                    var existing = database.Find(new(Predicate: new("urn:p")));
                    await database.CommitAsync(new(Guid.NewGuid(), existing.Select(quad => (RdfMutation)new RetractRdfQuad(quad)).ToArray()));
                }
            }
        }
        CollectionAssert.AreEquivalent(new[] { "1", "2", "3" }, values.ToArray());
        await database.CreateCheckpointAsync();
    }

    private static async Task SeedJoinScanAsync(RdfDatabaseEngine database)
    {
        var quads = new List<RdfQuad>();
        foreach (RdfIri? graph in new RdfIri?[] { null, new("urn:g") })
        {
            for (int value = 1; value <= 3; value++)
            {
                var subject = new RdfIri("urn:join:" + value);
                quads.Add(new(subject, new("urn:route"), new RdfIri("urn:hub"), graph));
                quads.Add(new(subject, new("urn:p"), new RdfLiteral(value.ToString(CultureInfo.InvariantCulture),
                    "http://www.w3.org/2001/XMLSchema#integer"), graph));
                if (value <= 2)
                {
                    quads.Add(new(subject, new("urn:q"), new RdfLiteral("a"), graph));
                    quads.Add(new(subject, new("urn:q"), new RdfLiteral("b"), graph));
                }
            }
        }
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
    }
}
