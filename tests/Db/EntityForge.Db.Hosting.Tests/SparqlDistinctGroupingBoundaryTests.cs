using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?g (COUNT(DISTINCT *) AS ?n)", "GROUP BY ?g ORDER BY ?g", "a:1,b:2")]
    [DataRow("SELECT ?g (COUNT(DISTINCT *) AS ?n)", "GROUP BY ?g HAVING(COUNT(DISTINCT *)=1) ORDER BY ?g", "a:1")]
    [DataRow("SELECT ?g (COUNT(DISTINCT *) AS ?n)", "GROUP BY ?g ORDER BY DESC(COUNT(DISTINCT *))", "b:2,a:1")]
    [DataRow("SELECT ?g (COUNT(*)-COUNT(DISTINCT *) AS ?n)", "GROUP BY ?g ORDER BY ?g", "a:1,b:2")]
    [DataRow("SELECT ?g (COUNT(DISTINCT *) AS ?original) (?original+COUNT(*) AS ?n)", "GROUP BY ?g ORDER BY ?g", "a:3,b:6")]
    public async Task Sparql_distinct_mapping_identity_survives_grouping_having_and_ordering(
        string select, string modifiers, string expected)
    {
        // UNION introduces the same mappings in different binding orders.
        // Missing y and a bound y remain distinct, despite matching x and g.
        const string pattern = "{ VALUES (?g ?x ?y) {('a' 1 2) ('b' 1 2) ('b' 1 UNDEF)} } " +
            "UNION { VALUES (?y ?x ?g) {(2 1 'a') (2 1 'b') (UNDEF 1 'b')} }";
        string query = select + " WHERE { " + pattern + " } " + modifiers;
        await using var database = await OpenAsync(CreateDirectory());
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(expected, string.Join(',', result.Bindings.Select(row =>
            ((RdfLiteral)row["g"]).LexicalForm + ":" + ((RdfLiteral)row["n"]).LexicalForm)));
        Assert.IsTrue(result.Bindings.All(row => row.Keys.All(key => !key.StartsWith('$'))));
    }
}
