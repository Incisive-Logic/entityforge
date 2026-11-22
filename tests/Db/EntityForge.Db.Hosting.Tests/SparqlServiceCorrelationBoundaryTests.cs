using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("VALUES ?x {1} MINUS { VALUES ?x {1} }", "1", 2)]
    [DataRow("VALUES (?x ?y) {(1 7)} MINUS { VALUES (?x ?y) {(1 7)} }", "", 2)]
    [DataRow("VALUES (?x ?y) {(1 7)} MINUS { VALUES (?x ?z) {(1 7)} }", "1", 2)]
    [DataRow("{ SELECT ?y { VALUES (?x ?y) {(1 7) (2 8)} } } FILTER(?y=7)", "1", 2)]
    [DataRow("{ SELECT ?y { VALUES (?x ?y) {(1 7) (2 8)} } ORDER BY DESC(?y) LIMIT 1 } FILTER(?y=7)", "1", 2)]
    [DataRow("{ SELECT (COUNT(*) AS ?n) { VALUES ?x {} } } FILTER(?n=0)", "1,2", 1)]
    [DataRow("{ SELECT (COUNT(*) AS ?n) { VALUES ?x {UNDEF UNDEF} } } FILTER(?n=2)", "1,2", 1)]
    [DataRow("VALUES ?x {1} OPTIONAL { VALUES ?y {} } FILTER(!BOUND(?y))", "1", 2)]
    [DataRow("{ SELECT DISTINCT ?x { VALUES ?x {UNDEF UNDEF} } }", "1,2", 2)]
    [DataRow("{ SELECT (COUNT(DISTINCT *) AS ?n) { VALUES ?x {UNDEF UNDEF} } } FILTER(?n=1)", "1,2", 1)]
    [DataRow("{ SELECT (COUNT(*) AS ?n) { VALUES ?x {UNDEF UNDEF} BIND(7 AS ?__entityforge_unbound) FILTER(?__entityforge_unbound=7) } } FILTER(?n=2)", "1,2", 1)]
    [DataRow("{ SELECT ?x {} VALUES ?x {} }", "", 2)]
    public Task Sparql_correlated_SERVICE_preserves_minus_subquery_and_empty_table_boundaries(string body, string expected, int calls) =>
        Sparql11_correlated_SERVICE_expressions_use_substituted_solutions(
            "SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { " + body + " } } }", expected, calls);

    [TestMethod]
    [DataRow("SELECT (COUNT(*) AS ?n) {} VALUES ?x {UNDEF UNDEF}", "1,1")]
    [DataRow("SELECT (COUNT(*) AS ?n) {} HAVING(COUNT(*)=1) VALUES ?x {UNDEF UNDEF}", "1,1")]
    [DataRow("SELECT (COUNT(*) AS ?n) {} VALUES ?x {}", "")]
    [DataRow("SELECT ?n { { SELECT (COUNT(*) AS ?n) {} VALUES ?x {UNDEF UNDEF} } }", "1,1")]
    [DataRow("SELECT DISTINCT (COUNT(*) AS ?n) {} VALUES ?x {UNDEF UNDEF}", "1")]
    [DataRow("SELECT (COUNT(*) AS ?n) {} ORDER BY ?n LIMIT 1 OFFSET 1 VALUES ?x {UNDEF UNDEF}", "1")]
    [DataRow("SELECT (COUNT(*) AS ?n) {} ORDER BY ?n OFFSET 2 VALUES ?x {UNDEF UNDEF}", "")]
    [DataRow("SELECT (COUNT(*) AS ?n) {} HAVING(COUNT(*)=2) VALUES ?x {UNDEF UNDEF}", "")]
    [DataRow("SELECT (COUNT(*) AS ?n) { { SELECT (COUNT(*) AS ?inner) {} VALUES ?x {UNDEF UNDEF} } }", "2")]
    public async Task Sparql_trailing_VALUES_preserves_post_aggregate_join_order(string query, string expected)
    {
        // SPARQL 1.1 section 18.2.4 applies final VALUES after aggregation and
        // HAVING. The current external SERVICE fixture disagrees on duplicate
        // all-UNDEF final tables, so it is not an oracle for this requirement.
        var relation = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation([], query, default);
        Assert.AreEqual(expected, String.Join(',', relation.Bindings.Select(row => ((RdfLiteral)row["n"]).LexicalForm)));
        await using var database = await OpenAsync(CreateDirectory());
        var persisted = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(expected, String.Join(',', persisted.Bindings.Select(row => ((RdfLiteral)row["n"]).LexicalForm)));
    }
}
