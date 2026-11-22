using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?value WHERE {} VALUES ?value { 1 2 2 }", "1,2,2")]
    [DataRow("SELECT ?value WHERE {} ORDER BY DESC(?value) LIMIT 1 VALUES ?value { 1 2 }", "2")]
    [DataRow("SELECT ?value WHERE { VALUES ?value { 1 2 } } VALUES ?value { 2 UNDEF }", "1,2,2")]
    [DataRow("SELECT ?value WHERE { { SELECT ?value WHERE {} VALUES ?value { 3 4 } } }", "3,4")]
    [DataRow("SELECT ?value WHERE { GRAPH ?g { VALUES ?value { 1 } } }", "1,1")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { GRAPH ?g {} }", "2")]
    [DataRow("SELECT ?value WHERE { GRAPH <urn:g> { SELECT (COUNT(*) AS ?value) WHERE { ?s ?p ?o } } }", "2")]
    [DataRow("SELECT ?value WHERE { GRAPH <urn:g> { ?s <urn:p> ?value FILTER(EXISTS { ?s <urn:p> 'a' }) } }", "a")]
    [DataRow("SELECT ?value WHERE { GRAPH <urn:g> { GRAPH <urn:h> { ?s <urn:p> ?value } } } ORDER BY ?value", "b,shared")]
    [DataRow("SELECT ?value WHERE { GRAPH <urn:absent> { VALUES ?value { 1 } } }", "")]
    [DataRow("SELECT ?value WHERE { GRAPH <urn:g> { VALUES (?g ?value) { (<urn:h> 1) } } }", "1")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { VALUES ?g { <urn:g> <urn:g> <urn:h> <urn:missing> 42 UNDEF } GRAPH ?g {} }", "5")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { GRAPH ?g { GRAPH ?g { ?s ?p ?o } } }", "4")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { GRAPH <urn:g> { GRAPH ?g {} } }", "2")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { GRAPH ?g {} ?s <urn:p> 'default' }", "2")]
    public async Task Sparql11_values_and_active_graph_scope_match_reference(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(DatasetSeed, "graph-scope")));
        using var reference = new VDS.RDF.TripleStore();
        new VDS.RDF.Update.LeviathanUpdateProcessor(reference).ProcessCommandSet(
            new VDS.RDF.Parsing.SparqlUpdateParser().ParseFromString(DatasetSeed));
        var wanted = (VDS.RDF.Query.SparqlResultSet)new VDS.RDF.Query.LeviathanQueryProcessor(reference)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(query));
        var actual = RdfSparql.Execute(database, query);
        Assert.AreEqual(expected, String.Join(',', actual.Select(row => ((RdfLiteral)row["value"]).LexicalForm)));
        CollectionAssert.AreEquivalent(wanted.Select(row => ((VDS.RDF.ILiteralNode)row["value"]).Value).ToArray(),
            actual.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
    }

    [TestMethod]
    public async Task Sparql11_trailing_values_applies_to_graph_query_forms()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(DatasetSeed, "graph-scope")));
        // The final VALUES join preserves duplicate empty mappings after aggregation.
        // dotNetRDF collapses this table, so assert the standard bag semantics directly.
        var grouped = RdfSparql.Execute(database, "SELECT (COUNT(*) AS ?value) WHERE { VALUES ?x { 1 2 } } VALUES () { () () }");
        Assert.AreEqual(2, grouped.Count);
        Assert.IsTrue(grouped.All(row => row["value"] is RdfLiteral { LexicalForm: "2" }));
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, "ASK {} VALUES ?x { 1 }"));
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, "ASK {} VALUES ?x {}"));
        Assert.AreEqual(1, RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT { <urn:result> <urn:p> ?value } WHERE {} VALUES ?value { 1 }").Count);
        Assert.AreEqual(1, RdfSparql.ExecuteConstruct(database,
            "CONSTRUCT WHERE { ?s <urn:p> ?value } VALUES ?value { 'default' }").Count);
    }

    [TestMethod]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { VALUES ?g { <urn:missing> } GRAPH ?g {} }", "0")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { GRAPH ?g { FILTER(BOUND(?g)) } }", "0")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { VALUES ?g { <urn:g> } GRAPH ?g { FILTER(BOUND(?g)) } }", "0")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { GRAPH ?g { VALUES ?g { <urn:g> <urn:g> } FILTER(BOUND(?g)) } }", "2")]
    [DataRow("SELECT (COUNT(*) AS ?value) WHERE { GRAPH ?g {} FILTER(BOUND(?g)) }", "2")]
    public async Task Sparql_graph_name_bindings_join_after_inner_graph_evaluation(string query, string expected)
    {
        // SPARQL 1.1 section 18.6 evaluates P in each named graph, then joins
        // that result with the graph-name binding. It does not seed P with it.
        // The reference engine used above differs on the first three cases;
        // assert the algebra directly, including positive binding controls.
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), DatasetSeed);
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(expected, ((RdfLiteral)result.Bindings.Single()["value"]).LexicalForm);
    }
}
