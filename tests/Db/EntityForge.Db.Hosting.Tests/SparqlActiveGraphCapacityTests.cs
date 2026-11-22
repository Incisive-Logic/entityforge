using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql_graph_name_index_is_bounded_and_snapshot_consistent()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var first = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("first"), new RdfIri("urn:first"));
        var second = first with
        {
            Graph = new RdfIri("urn:second")
        };
        var third = first with
        {
            Graph = new RdfIri("urn:third")
        };
        await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(first)]));
        using var snapshot = database.AcquireSnapshot();
        await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(first), new AssertRdfQuad(second), new AssertRdfQuad(third)]));
        CollectionAssert.AreEqual(new RdfTerm[] { first.Graph! }, database.ReadSparqlGraphNames(snapshot, 2, default).ToArray());
        using var current = database.AcquireSnapshot();
        CollectionAssert.AreEquivalent(new RdfTerm[] { second.Graph!, third.Graph! }, database.ReadSparqlGraphNames(current, 3, default).ToArray());
        Assert.AreEqual(1, database.ReadSparqlGraphNames(current, 1, default).Count);
        Assert.AreEqual(0, database.ReadSparqlGraphNames(current, 0, default).Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => database.ReadSparqlGraphNames(current, 1, cancellation.Token));
        current.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => database.ReadSparqlGraphNames(current, 1, default));
    }

    [TestMethod]
    public async Task Sparql_active_graph_candidate_bound_is_not_the_result_row_bound()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var graph = new RdfIri("urn:g");
        var quads = Enumerable.Range(0, 10_100).Select(key => new RdfQuad(new RdfIri("urn:s:" + key),
            new("urn:p"), new RdfIri("urn:other"), graph)).ToList();
        var match = new RdfIri("urn:match");
        quads.Add(new(match, new("urn:p"), match, graph));
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        var rows = RdfSparql.Execute(database, "SELECT ?x WHERE { GRAPH <urn:g> { ?x <urn:p> ?x } }");
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(match, rows[0]["x"]);
    }

    [TestMethod]
    public async Task Sparql_constant_active_graph_retains_indexed_reads_and_nested_dataset_scope()
    {
        // This fixture deliberately exceeds the ordinary 100,000-quad admission
        // budget to verify indexed reads against a larger active graph.
        await using var database = await OpenAsync(CreateDirectory(), bulkLoad: new()
        {
            MaximumInputQuads = 100_103
        });
        var graph = new RdfIri("urn:large");
        var target = new RdfIri("urn:target");
        var predicate = new RdfIri("urn:p");
        var quads = Enumerable.Range(0, 100_100).Select(key => new RdfQuad(new RdfIri("urn:filler:" + key),
            predicate, new RdfLiteral("filler"), graph)).ToList();
        quads.Add(new(target, predicate, new RdfLiteral("selected"), graph));
        quads.Add(new(target, predicate, new RdfLiteral("nested"), new RdfIri("urn:other")));
        quads.Add(new(target, predicate, new RdfLiteral("hidden")));
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        foreach (var (body, expected) in new[]
        {
            ("<urn:target> <urn:p> ?value", "selected"),
            ("{ SELECT ?value WHERE { <urn:target> <urn:p> ?value } }", "selected"),
            ("<urn:target> <urn:p> ?value FILTER EXISTS { <urn:target> <urn:p> 'selected' }", "selected"),
            ("GRAPH <urn:other> { <urn:target> <urn:p> ?value }", "nested")
        })
        {
            var rows = RdfSparql.Execute(database, "SELECT ?value WHERE { GRAPH <urn:large> { " + body + " } }");
            Assert.AreEqual(1, rows.Count, body);
            Assert.AreEqual(new RdfLiteral(expected), rows[0]["value"], body);
        }
        Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT * WHERE { GRAPH <urn:missing> {} }").Count);
        var named = RdfSparql.Execute(database, "SELECT ?value WHERE { GRAPH ?g { <urn:target> <urn:p> ?value } }");
        CollectionAssert.AreEquivalent(new[] { "selected", "nested" },
            named.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
        var repeated = RdfSparql.Execute(database,
            "SELECT ?value WHERE { VALUES ?g { <urn:large> <urn:large> } GRAPH ?g { <urn:target> <urn:p> ?value } }");
        CollectionAssert.AreEqual(new[] { "selected", "selected" },
            repeated.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
        foreach (string graphTerm in new[] { "<urn:large>", "?g" })
        {
            var service = (RdfSparqlSelectResult)await RdfSparql.ExecuteAtSnapshotAsync(database,
                "SELECT ?value WHERE { GRAPH " + graphTerm +
                " { <urn:target> <urn:p> ?value SERVICE SILENT <https://example.invalid/sparql> { ?remote ?p ?o } } }",
                database.AcquireSnapshot(), null, default);
            CollectionAssert.AreEquivalent(graphTerm == "?g" ? new[] { "selected", "nested" } : new[] { "selected" },
                service.Bindings.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
        }
        Assert.AreEqual(new RdfLiteral("hidden"),
            RdfSparql.Execute(database, "SELECT ?value WHERE { <urn:target> <urn:p> ?value }")[0]["value"]);
    }
}
