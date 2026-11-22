using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("VALUES ?x { 1 1 } VALUES ?y { 2 2 }", "1,1,1,1")]
    [DataRow("VALUES ?x { 1 UNDEF } VALUES ?x { 1 2 }", "1,1,2")]
    [DataRow("VALUES () { () () } VALUES () { () () () }", "-,-,-,-,-,-")]
    [DataRow("VALUES () {} VALUES ?x { 1 }", "")]
    [DataRow("VALUES ?x { '01'^^<http://www.w3.org/2001/XMLSchema#integer> } VALUES ?x { 1 }", "")]
    [DataRow("VALUES ?x { UNDEF UNDEF } BIND('fallback' AS ?y)", "-,-")]
    [DataRow("VALUES (?x ?y) { (1 UNDEF) (UNDEF 2) } VALUES (?x ?y) { (1 2) }", "1,1")]
    public async Task Sparql_values_join_bags_survive_blank_template_update_retry_and_restart(string body, string expected)
    {
        string[] expectedRows = expected.Length == 0 ? [] : expected.Split(',');
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        string update = "INSERT { _:row <urn:marker> true; <urn:value> ?x } WHERE { " + body + " }";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var database = await OpenAsync(directory);
            var rows = RdfSparql.Execute(database, "SELECT ?x { " + body + " }");
            CollectionAssert.AreEquivalent(expectedRows,
                rows.Select(row => row.TryGetValue("x", out var value) ? ((RdfLiteral)value).LexicalForm : "-").ToArray());
            if (attempt == 0)
            {
                await database.CommitSparqlUpdateAsync(operation, update);
            }
            await database.CommitSparqlUpdateAsync(operation, update);
            var quads = database.Find(new()).ToArray();
            var markers = quads.Where(quad => quad.Predicate == new RdfIri("urn:marker")).ToArray();
            Assert.AreEqual(expectedRows.Length, markers.Length);
            Assert.AreEqual(expectedRows.Length, markers.Select(quad => quad.Subject).Distinct().Count());
            foreach (var marker in markers)
            {
                Assert.IsInstanceOfType<RdfBlankNode>(marker.Subject);
                Assert.AreEqual(new RdfLiteral("true", "http://www.w3.org/2001/XMLSchema#boolean"), marker.Object);
            }
            var expectedValues = expectedRows.Where(value => value != "-").ToArray();
            CollectionAssert.AreEquivalent(expectedValues,
                quads.Where(quad => quad.Predicate == new RdfIri("urn:value"))
                    .Select(quad => ((RdfLiteral)quad.Object).LexicalForm).ToArray());
            Assert.AreEqual(expectedRows.Length + expectedValues.Length, quads.Length);
        }
    }
}
