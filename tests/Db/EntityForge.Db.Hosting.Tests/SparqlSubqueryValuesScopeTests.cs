using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (2 AS ?x) { { SELECT * {} VALUES ?x { 1 } } }")]
    [DataRow("SELECT * { { SELECT * {} VALUES ?x { 1 } } BIND(2 AS ?x) }")]
    [DataRow("SELECT ?x { { SELECT * {} VALUES ?x { 1 } } } GROUP BY (2 AS ?x)")]
    public void Sparql_subquery_trailing_values_prevent_alias_rebinding(string query) =>
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ResultVariables(query));

    [TestMethod]
    [DataRow("SELECT * { { SELECT * {} VALUES ?x { 1 } } }", "x", "1")]
    [DataRow("SELECT * { { SELECT * { { SELECT * {} VALUES ?x { 1 } } } } }", "x", "1")]
    [DataRow("SELECT * { OPTIONAL { SELECT * {} VALUES ?x { 1 } } }", "x", "1")]
    [DataRow("SELECT * { { SELECT ?y { BIND(2 AS ?y) } VALUES ?x { 1 } } }", "y", "2")]
    public async Task Sparql_subquery_trailing_values_metadata_matches_visible_bindings(string query, string variable, string expected)
    {
        CollectionAssert.AreEqual(new[] { variable }, RdfSparql.ResultVariables(query).ToArray());
        await using var database = await OpenAsync(CreateDirectory());
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(1, result.Bindings.Count);
        CollectionAssert.AreEqual(new[] { variable }, result.Bindings[0].Keys.ToArray());
        Assert.AreEqual(expected, ((RdfLiteral)result.Bindings[0][variable]).LexicalForm);
    }
}
