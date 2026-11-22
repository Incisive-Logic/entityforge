using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SUM(COUNT(*))")]
    [DataRow("SUM(1 + COUNT(*))")]
    [DataRow("SUM(ABS(SUM(?v)))")]
    [DataRow("SUM(COALESCE(SUM(?v), 0))")]
    [DataRow("SUM(IF(COUNT(*) > 0, 1, 0))")]
    [DataRow("SUM(IF(true, 1, COUNT(*)))")]
    [DataRow("SUM(IF(COUNT(*) IN (1, 2), 1, 0))")]
    [DataRow("SUM(IF(1 IN (COUNT(*), 2), 1, 0))")]
    [DataRow("GROUP_CONCAT(STR(COUNT(*)); SEPARATOR=',')")]
    public async Task Sparql_nested_aggregates_reject_across_modifiers_and_atomic_update(string expression)
    {
        string directory = CreateDirectory();
        await using (var database = await OpenAsync(directory))
        {
            string[] queries =
            [
                "SELECT (" + expression + " AS ?result) { VALUES ?v { 1 2 } }",
                "SELECT (COUNT(*) AS ?result) { VALUES ?v { 1 2 } } HAVING(" + expression + " > 0)",
                "SELECT (COUNT(*) AS ?result) { VALUES ?v { 1 2 } } ORDER BY (" + expression + ")"
            ];
            foreach (string query in queries)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask());
            }
            var before = database.CurrentVersion;
            string update = "INSERT DATA { <urn:probe> <urn:p> 1 }; " +
                "INSERT { <urn:result> <urn:p> ?result } WHERE { { " + queries[0] + " } }";
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
            Assert.AreEqual(before, database.CurrentVersion);
            Assert.AreEqual(0, database.Find(new()).Count());
        }
        await using var reopened = await OpenAsync(directory);
        Assert.AreEqual(0, reopened.Find(new()).Count());
    }

    [TestMethod]
    public async Task Sparql_aggregates_in_separate_subquery_scopes_remain_valid()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string query = "SELECT (SUM(?subtotal) AS ?total) { " +
            "{ SELECT ?group (SUM(?value) AS ?subtotal) { " +
            "VALUES (?group ?value) { ('a' 1) ('a' 2) ('b' 5) } } GROUP BY ?group } }";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        var expected = new RdfLiteral("8", "http://www.w3.org/2001/XMLSchema#integer");
        Assert.AreEqual(1, result.Bindings.Count);
        Assert.AreEqual(expected, result.Bindings[0]["total"]);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT { <urn:result> <urn:p> ?total } WHERE { { " + query + " } }");
        CollectionAssert.AreEqual(new[] { new RdfQuad(new RdfIri("urn:result"), new("urn:p"), expected) },
            database.Find(new()).ToArray());
    }
}
