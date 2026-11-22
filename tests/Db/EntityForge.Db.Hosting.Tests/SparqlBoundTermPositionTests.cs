using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?o { 1 <urn:p> ?o }")]
    [DataRow("SELECT ?o { 1 <urn:p> ?o } ORDER BY ?o")]
    [DataRow("SELECT ?o (COUNT(*) AS ?v) { 1 <urn:p> ?o } GROUP BY ?o")]
    [DataRow("SELECT ?o { 1 <urn:p> ?o . ?s <urn:p> ?o } ORDER BY ?o")]
    public async Task Sparql11_literal_subject_patterns_preserve_empty_results_across_plans(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var version = await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 }");
        Assert.AreEqual(0, ((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings.Count);
        Assert.AreEqual(0, ((RdfSparqlSelectResult)RdfSparql.ExecuteRelation(database.Find(new()), query, default)).Bindings.Count);
        using var snapshot = await database.AcquireSnapshotAsync(version.Version);
        var fragments = RdfSparql.CaptureBindingFragments(database, snapshot, query, 100, default);
        Assert.AreEqual(0, ((RdfSparqlSelectResult)RdfSparql.ExecuteBindingFragments(query, fragments, default)).Bindings.Count);
        Assert.IsNotNull(RdfSparql.ExchangePatterns(query));
    }

    [TestMethod]
    [DataRow("SELECT (COUNT(*) AS ?v) { 1 <urn:p> ?o }", "0")]
    [DataRow("SELECT (EXISTS { 'literal' <urn:p> ?o } AS ?v) {}", "false")]
    [DataRow("SELECT (NOT EXISTS { true <urn:p> ?o } AS ?v) {}", "true")]
    [DataRow("SELECT (COUNT(*) AS ?v) { VALUES ?s { 1 } ?s <urn:p> ?o }", "0")]
    [DataRow("SELECT (COUNT(*) AS ?v) { VALUES ?s { 1 <urn:a> } ?s <urn:p> ?o }", "1")]
    [DataRow("SELECT (COUNT(*) AS ?v) { VALUES ?s { 1 } OPTIONAL { ?s <urn:p> ?o } }", "1")]
    [DataRow("SELECT (BOUND(?o) AS ?v) { VALUES ?s { 1 } OPTIONAL { ?s <urn:p> ?o } }", "false")]
    [DataRow("SELECT (EXISTS { ?s <urn:p> ?o } AS ?v) { VALUES ?s { 1 } }", "false")]
    [DataRow("SELECT (NOT EXISTS { ?s <urn:p> ?o } AS ?v) { VALUES ?s { 1 } }", "true")]
    [DataRow("SELECT (COUNT(*) AS ?v) { VALUES ?s { 1 } MINUS { ?s <urn:p> ?o } }", "1")]
    [DataRow("SELECT (COUNT(*) AS ?v) { VALUES ?s { 1 } { ?s <urn:p> ?o } UNION { BIND(7 AS ?o) } }", "1")]
    [DataRow("SELECT (EXISTS { <urn:a> ?p ?o } AS ?v) { VALUES ?p { 1 } }", "false")]
    public async Task Sparql11_bound_invalid_term_positions_produce_no_match(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 }");
        var stored = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        var relation = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(database.Find(new()), query, default);
        foreach (var result in new[] { stored, relation })
        {
            Assert.AreEqual(1, result.Bindings.Count);
            Assert.AreEqual(expected, ((RdfLiteral)result.Bindings[0]["v"]).LexicalForm);
        }
        Assert.AreEqual(1, database.Find(new()).Count, "Query evaluation must leave the dataset unchanged.");
    }
}
