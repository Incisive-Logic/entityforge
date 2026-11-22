using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("?s ?p ?o", "s,p,o")]
    [DataRow("?s <urn:p>/<urn:q> ?o", "s,o")]
    [DataRow("GRAPH ?g {}", "g")]
    [DataRow("{ VALUES ?a {UNDEF} } UNION { VALUES ?b {} }", "a,b")]
    [DataRow("OPTIONAL { ?s <urn:p> ?o }", "s,o")]
    [DataRow("SERVICE SILENT ?endpoint { ?s <urn:p> ?o }", "endpoint,s,o")]
    [DataRow("BIND(?unbound+1 AS ?x)", "x")]
    [DataRow("VALUES (?a ?b) { (UNDEF UNDEF) }", "a,b")]
    [DataRow("{ SELECT ?s { ?s ?hidden ?o } }", "s")]
    [DataRow("{ SELECT (COUNT(*) AS ?count) { ?s ?p ?o } }", "count")]
    [DataRow("{ SELECT ?key { ?s <urn:p> ?o } GROUP BY (?o AS ?key) }", "key")]
    [DataRow("{ SELECT * {} VALUES ?x {UNDEF} }", "x")]
    [DataRow("FILTER(?hidden=1) BIND(2 AS ?visible)", "visible")]
    [DataRow("MINUS { ?hidden ?p ?o } BIND(2 AS ?visible)", "visible")]
    [DataRow("FILTER EXISTS { ?hidden ?p ?o } BIND(2 AS ?visible)", "visible")]
    public void Sparql_variable_scope_matrix_preserves_declared_and_hidden_columns(string pattern, string expected)
    {
        // Scope is a property of the query, including empty/unbound bindings;
        // it must not be inferred from rows returned by a particular dataset.
        CollectionAssert.AreEquivalent(expected.Split(','),
            RdfSparql.ResultVariables("SELECT * { " + pattern + " }").ToArray());
    }
}
