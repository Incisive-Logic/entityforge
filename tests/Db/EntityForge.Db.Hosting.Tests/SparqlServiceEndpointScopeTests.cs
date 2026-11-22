using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (1 AS ?endpoint) { SERVICE SILENT ?endpoint {} }")]
    [DataRow("SELECT * { SERVICE SILENT ?endpoint {} BIND(1 AS ?endpoint) }")]
    [DataRow("SELECT ?endpoint { SERVICE SILENT ?endpoint {} } GROUP BY (1 AS ?endpoint)")]
    [DataRow("SELECT (1 AS ?endpoint) { OPTIONAL { SERVICE SILENT ?endpoint {} } }")]
    public void Sparql_service_endpoint_variables_prevent_alias_rebinding(string query)
    {
        // Parse without issuing remote requests, even for an invalid query.
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ResultVariables(query));
    }

    [TestMethod]
    [DataRow("SELECT * { SERVICE SILENT ?endpoint { ?s <urn:p> ?o } }", "endpoint,s,o")]
    [DataRow("SELECT * { OPTIONAL { SERVICE SILENT ?endpoint {} } }", "endpoint")]
    [DataRow("SELECT * { { SELECT * { SERVICE SILENT ?endpoint {} } } }", "endpoint")]
    [DataRow("SELECT * { { SELECT ?x { SERVICE SILENT ?endpoint {} BIND(1 AS ?x) } } }", "x")]
    [DataRow("SELECT * { MINUS { SERVICE SILENT ?endpoint {} } BIND(1 AS ?x) }", "x")]
    public void Sparql_service_endpoint_variables_follow_projection_scope(string query, string expected)
    {
        CollectionAssert.AreEquivalent(expected.Split(','), RdfSparql.ResultVariables(query).ToArray());
    }
}
