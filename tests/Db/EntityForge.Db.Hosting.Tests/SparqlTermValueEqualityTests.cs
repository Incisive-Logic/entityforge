using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("'2024-01-01T00:00:00Z'^^xsd:dateTime", "'2023-12-31T19:00:00-05:00'^^xsd:dateTime", true, false)]
    [DataRow("'2024-01-01T00:00:00Z'^^xsd:dateTime", "'2024-01-01T00:00:00.000Z'^^xsd:dateTime", true, false)]
    [DataRow("'2024-01-01T00:00:00Z'^^xsd:dateTime", "'2024-01-01T00:00:01Z'^^xsd:dateTime", false, false)]
    [DataRow("'01'^^xsd:integer", "'1'^^xsd:integer", true, false)]
    [DataRow("'hello'@EN", "'hello'@en", true, true)]
    [DataRow("'hello'@en", "'hello'@fr", false, false)]
    [DataRow("'hello'", "'hello'^^xsd:string", true, true)]
    public async Task Sparql_term_equality_preserves_lexical_identity_separately_from_value_equality(
        string first, string second, bool equal, bool same)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "PREFIX xsd:<http://www.w3.org/2001/XMLSchema#> " +
            "SELECT (?a=?b AS ?equal) (sameTerm(?a,?b) AS ?same) WHERE { VALUES (?a ?b) { (" +
            first + " " + second + ") } }";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(1, result.Bindings.Count);
        Assert.AreEqual(equal ? "true" : "false", ((RdfLiteral)result.Bindings[0]["equal"]).LexicalForm);
        Assert.AreEqual(same ? "true" : "false", ((RdfLiteral)result.Bindings[0]["same"]).LexicalForm);
    }
}
