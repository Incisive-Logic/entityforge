using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("MINUS {}", "a,a,b,c")]
    [DataRow("MINUS { VALUES ?other { 1 } }", "a,a,b,c")]
    [DataRow("MINUS { VALUES ?x { UNDEF } }", "a,a,b,c")]
    [DataRow("MINUS { VALUES ?x { 1 1 } }", "b,c")]
    [DataRow("MINUS { VALUES ?x { 2 } }", "a,a,c")]
    [DataRow("MINUS { VALUES ?x { 1 } FILTER(false) }", "a,a,b,c")]
    [DataRow("MINUS { VALUES ?x { 1 } FILTER(?tag = 'a') }", "a,a,b,c")]
    [DataRow("MINUS { VALUES (?x ?tag) { (1 'different') } }", "a,a,b,c")]
    [DataRow("MINUS { VALUES (?x ?tag) { (UNDEF 'c') } }", "a,a,b")]
    [DataRow("MINUS { VALUES ?x { '01'^^<http://www.w3.org/2001/XMLSchema#integer> } }", "a,a,b,c")]
    [DataRow("FILTER NOT EXISTS {}", "")]
    [DataRow("FILTER NOT EXISTS { VALUES ?other { 1 } }", "")]
    [DataRow("FILTER NOT EXISTS { BIND(?x AS ?copy) FILTER(?copy = 1) }", "b,c")]
    [DataRow("MINUS { BIND(?x AS ?copy) FILTER(?copy = 1) }", "a,a,b,c")]
    public void Sparql11_negation_preserves_bags_and_distinguishes_binding_domains(string negation, string expected)
    {
        string query = "SELECT ?tag WHERE { VALUES (?x ?tag) { (1 'a') (1 'a') (2 'b') (UNDEF 'c') } "
            + negation + " } ORDER BY ?tag";
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation([], query, default);
        Assert.AreEqual(expected, String.Join(',', result.Bindings.Select(row => ((RdfLiteral)row["tag"]).LexicalForm)));
        Assert.IsTrue(result.Bindings.All(row => row.Count == 1), "Negation must not introduce right-side bindings.");
    }
}
