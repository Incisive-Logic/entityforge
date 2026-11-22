using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("{ ?s <urn:p> ?o } ?s <urn:q> ?v", 1)]
    [DataRow("VALUES ?x { 1 } { ?s <urn:p> ?o FILTER(?x = 1) }", 0)]
    [DataRow("VALUES ?x { 1 } { ?s <urn:p> ?o } FILTER(?x = 1)", 2)]
    [DataRow("?s <urn:p> ?o OPTIONAL { { ?s <urn:q> ?v } }", 2)]
    [DataRow("?s <urn:p> ?o MINUS { { ?s <urn:q> ?v } }", 1)]
    [DataRow("{ { ?s <urn:p> ?o } } UNION { { ?s <urn:q> ?o } }", 3)]
    [DataRow("?s <urn:p> ?o FILTER EXISTS { { ?s <urn:q> ?v } }", 1)]
    [DataRow("?s <urn:p> ?o FILTER NOT EXISTS { { ?s <urn:q> ?v } }", 1)]
    [DataRow("VALUES ?x { 1 } FILTER EXISTS { { ?s <urn:p> ?o FILTER(?x = 1) } }", 1)]
    [DataRow("VALUES ?x { 2 } FILTER EXISTS { { ?s <urn:p> ?o FILTER(?x = 1) } }", 0)]
    [DataRow("{ ?s <urn:p> ?o } { SELECT ?s WHERE { { ?s <urn:q> ?v } } }", 1)]
    [DataRow("GRAPH <urn:g> { { ?s <urn:p> ?o } }", 1)]
    [DataRow("{ GRAPH ?g { { ?s <urn:p> ?o } } }", 1)]
    public async Task Sparql_nested_groups_preserve_scope_and_fragment_results(string body, int count)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var version = await database.CommitAsync(new(Guid.NewGuid(), [
            new AssertRdfQuad(new(new RdfIri("urn:a"), new RdfIri("urn:p"), new RdfIri("urn:b"))),
            new AssertRdfQuad(new(new RdfIri("urn:a"), new RdfIri("urn:q"), new RdfIri("urn:c"))),
            new AssertRdfQuad(new(new RdfIri("urn:d"), new RdfIri("urn:p"), new RdfIri("urn:e"))),
            new AssertRdfQuad(new(new RdfIri("urn:named"), new RdfIri("urn:p"), new RdfIri("urn:b"), new RdfIri("urn:g")))
        ]));
        string query = "SELECT * WHERE { " + body + " }";
        var direct = RdfSparql.Execute(database, query);
        Assert.AreEqual(count, direct.Count, query);
        if (body.Contains("GRAPH", StringComparison.Ordinal))
        {
            Assert.IsFalse(RdfSparql.SupportsBindingFragments(query));
            Assert.IsNull(RdfSparql.ExchangePatterns(query));
            var relation = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(database.Find(new()), query, default);
            Assert.AreEqual(direct.Count, relation.Bindings.Count);
            return;
        }
        using var snapshot = await database.AcquireSnapshotAsync(version);
        Assert.IsTrue(RdfSparql.SupportsBindingFragments(query));
        var fragments = RdfSparql.CaptureBindingFragments(database, snapshot, query, 100, default);
        var distributed = (RdfSparqlSelectResult)RdfSparql.ExecuteBindingFragments(query, fragments, default);
        string[] Canonical(IEnumerable<IReadOnlyDictionary<string, RdfTerm>> rows) =>
            rows.Select(row => JsonSerializer.Serialize(row.OrderBy(pair => pair.Key).ToArray())).Order().ToArray();
        CollectionAssert.AreEqual(Canonical(direct), Canonical(distributed.Bindings));
        Assert.IsTrue(RdfSparql.ExchangePatterns(query)!.Count > 0);
    }

    [TestMethod]
    public async Task Sparql_nested_groups_preserve_bags_and_reject_incompatible_bindings()
    {
        await using var database = await OpenAsync(CreateDirectory());
        // Correlated constants must not manufacture shared variable domains for MINUS.
        Assert.AreEqual(1, RdfSparql.Execute(database,
            "SELECT * WHERE { VALUES ?outer { 1 } FILTER EXISTS { VALUES ?x { 2 } MINUS { { VALUES ?y { 3 } } } } }").Count);
        Assert.AreEqual(0, RdfSparql.Execute(database,
            "SELECT * WHERE { VALUES ?outer { 1 } FILTER EXISTS { VALUES ?x { 2 } MINUS { { VALUES ?x { 2 } } } } }").Count);
        Assert.AreEqual(4, RdfSparql.Execute(database,
            "SELECT * WHERE { VALUES ?x { 1 1 } { VALUES ?y { 2 2 } } }").Count);
        Assert.AreEqual(0, RdfSparql.Execute(database,
            "SELECT * WHERE { VALUES ?x { 1 } { VALUES ?x { 2 } } }").Count);
    }

    [TestMethod]
    public async Task Sparql_nested_groups_obey_pattern_depth_and_join_bounds()
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => RdfSparql.Execute(database,
            "SELECT * WHERE { " + String.Concat(Enumerable.Repeat("{ ?s <urn:p> ?o } ", 17)) + " }"));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database,
            "SELECT * WHERE { " + new string('{', 18) + "?s <urn:p> ?o" + new string('}', 18) + " }"));
        string values = String.Join(" ", Enumerable.Range(0, 101));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database,
            "SELECT * WHERE { VALUES ?x { " + values + " } { VALUES ?y { " + values + " } } }"));
    }
}
