using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    private const string BlankPatternSeed = """
        INSERT DATA {
          <urn:a> <urn:p> "same"; <urn:q> 1 .
          <urn:b> <urn:p> "same"; <urn:q> 2 .
          <urn:c> <urn:p> "different" .
        }
        """;

    [TestMethod]
    [DataRow("SELECT ?value { [ <urn:p> ?value; <urn:q> ?q ] } ORDER BY ?value")]
    [DataRow("SELECT ?value { [ <urn:p> ?value; ] <urn:q> ?q } ORDER BY ?value")]
    [DataRow("SELECT ?value { [ <urn:p> ?value, 'same' ] } ORDER BY ?value")]
    [DataRow("SELECT ?value { [ <urn:p> ?value; <urn:q> 1 ] FILTER(?value='same') }")]
    [DataRow("SELECT ?value { _:x <urn:p> ?value . _:x <urn:q> ?q } ORDER BY ?value")]
    [DataRow("SELECT ?value { _:x <urn:p> ?value FILTER(?value='same') _:x <urn:q> ?q } ORDER BY ?value")]
    [DataRow("SELECT ?value { [] <urn:p> ?value . [] <urn:q> ?q } ORDER BY ?value")]
    [DataRow("SELECT * { _:x <urn:p> ?value } ORDER BY ?value")]
    [DataRow("SELECT (COUNT(*) AS ?value) { [] <urn:p> ?x }")]
    [DataRow("SELECT (COUNT(DISTINCT *) AS ?value) { [] <urn:p> ?x }")]
    [DataRow("SELECT (COUNT(DISTINCT *) AS ?value) { [] <urn:p> 'same' }")]
    [DataRow("SELECT DISTINCT * { [] <urn:p> ?value } ORDER BY ?value")]
    [DataRow("SELECT ?value { _:x <urn:p> ?value . _:x <urn:p> ?value } ORDER BY ?value")]
    [DataRow("SELECT ?value { [] <urn:p> ?value OPTIONAL { [] <urn:missing> ?x } } ORDER BY ?value")]
    [DataRow("SELECT ?value { [] <urn:p> ?value FILTER EXISTS { [] <urn:q> 1 } } ORDER BY ?value")]
    public async Task Sparql11_blank_node_patterns_preserve_bags_and_hide_internal_variables(string query)
    {
        using var reference = new VDS.RDF.TripleStore();
        new VDS.RDF.Update.LeviathanUpdateProcessor(reference).ProcessCommandSet(
            new VDS.RDF.Parsing.SparqlUpdateParser().ParseFromString(BlankPatternSeed));
        var wanted = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(reference)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(BlankPatternSeed, "blank-pattern-test")));
        var actual = RdfSparql.Execute(database, query);
        CollectionAssert.AreEqual(wanted.Select(row => ((VDS.RDF.ILiteralNode)row["value"]).Value).ToArray(),
            actual.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray(), query);
        Assert.IsTrue(actual.All(row => row.Keys.SequenceEqual(["value"])), "Pattern blank nodes must not leak into the result columns.");
    }

    [TestMethod]
    [DataRow("SELECT ?value { [ <urn:p> ?value }")]
    [DataRow("SELECT ?value { [ <urn:p> ] }")]
    [DataRow("SELECT ?value { [ <urn:p> ?value . ] }")]
    [DataRow("SELECT ?value { <urn:s> [ <urn:p> ?value ] <urn:o> }")]
    [DataRow("SELECT ?value { _:x <urn:p> ?value OPTIONAL { _:x <urn:q> ?q } }")]
    [DataRow("SELECT ?value { { _:x <urn:p> ?value } UNION { _:x <urn:q> ?value } }")]
    [DataRow("SELECT ?value { _:x <urn:p> ?value BIND(1 AS ?n) _:x <urn:q> ?q }")]
    [DataRow("SELECT ?value { <urn:s> _:predicate ?value }")]
    public async Task Sparql11_blank_pattern_labels_reject_invalid_scope_or_position(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, query));
    }

    [TestMethod]
    public async Task Sparql11_anonymous_construct_nodes_are_fresh_per_occurrence()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = RdfSparql.ExecuteConstruct(database, "CONSTRUCT { [] <urn:p> [] } WHERE { VALUES ?x { 1 1 } }");
        Assert.AreEqual(2, quads.Count);
        Assert.AreEqual(4, quads.SelectMany(quad => new[] { quad.Subject, quad.Object }).Distinct().Count());
        Assert.IsTrue(quads.All(quad => quad.Subject is RdfBlankNode && quad.Object is RdfBlankNode));
    }

    [TestMethod]
    public async Task Sparql11_nested_property_lists_and_collections_expand_in_queries_and_templates()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string construct = "CONSTRUCT { <urn:s> <urn:list> (1 [ <urn:p> 2 ] ()) } WHERE { VALUES ?x { 1 1 } }";
        var quads = RdfSparql.ExecuteConstruct(database, construct);
        using var referenceStore = new VDS.RDF.TripleStore();
        // Spell out the expected expansion independently. The reference
        // CONSTRUCT evaluator turns nested () into a blank node, whereas
        // SPARQL section 4.2.3 defines it as rdf:nil.
        var referenceGraph = new VDS.RDF.Graph();
        new VDS.RDF.Parsing.TurtleParser().Load(referenceGraph, new StringReader("""
            @prefix rdf: <http://www.w3.org/1999/02/22-rdf-syntax-ns#> .
            <urn:s> <urn:list> _:a, _:e .
            _:a rdf:first 1; rdf:rest _:b .
            _:b rdf:first _:d; rdf:rest _:c .
            _:c rdf:first rdf:nil; rdf:rest rdf:nil .
            _:d <urn:p> 2 .
            _:e rdf:first 1; rdf:rest _:f .
            _:f rdf:first _:h; rdf:rest _:g .
            _:g rdf:first rdf:nil; rdf:rest rdf:nil .
            _:h <urn:p> 2 .
            """));
        referenceStore.Add(referenceGraph, true);
        Assert.AreEqual(16, quads.Count);
        Assert.AreEqual(referenceGraph.Triples.Count, quads.Count);
        Assert.AreEqual(8, quads.SelectMany(q => new[] { q.Subject, q.Object }).OfType<RdfBlankNode>().Distinct().Count());
        using var actualGraph = new VDS.RDF.Graph();
        actualGraph.Assert(quads.Select(quad => new VDS.RDF.Triple(
            RdfSyntax.ToNode(actualGraph, quad.Subject),
            RdfSyntax.ToNode(actualGraph, quad.Predicate),
            RdfSyntax.ToNode(actualGraph, quad.Object))));
        Assert.IsTrue(referenceGraph.Equals(actualGraph),
            "Collection links, nested property lists and empty-list terms must match up to blank-node renaming.");
        await database.CommitAsync(new(Guid.NewGuid(), quads.Select(q => new AssertRdfQuad(q)).ToArray()));
        foreach (string query in new[]
        {
            "SELECT ?value { <urn:s> <urn:list> (1 [ <urn:p> ?value ] ()) } ORDER BY ?value",
            "SELECT ?value { GRAPH ?g { <urn:s> <urn:list> (1 [ <urn:p> ?value ] ()) } }"
        })
        {
            var rows = RdfSparql.Execute(database, query);
            var reference = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(referenceStore)
                .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
            Assert.AreEqual(reference.Count, rows.Count);
            Assert.AreEqual(query.Contains("GRAPH") ? 0 : 2, rows.Count);
            Assert.IsTrue(rows.All(row => row.Count == 1 && row["value"] is RdfLiteral { LexicalForm: "2" }));
        }
        Assert.ThrowsExactly<NotSupportedException>(() => RdfSparqlUpdate.Parse(
            "DELETE { <urn:s> <urn:list> (1) } WHERE {}", "test"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparqlUpdate.ParseWhere(database,
            "DELETE { <urn:s> <urn:list> (1) } WHERE {}", "test"));
    }
}
