using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("VALUES (?k ?n) { (1 0) (1 1) (2 0) (2 UNDEF) (4 1) } FILTER(?n > 0)", "1:1,1:1,2:-,3:-")]
    [DataRow("VALUES (?k ?n) { (1 0) (1 1) (2 0) (2 UNDEF) (4 1) } FILTER(1 / ?n > 0)", "1:1,1:1,2:-,3:-")]
    [DataRow("VALUES ?n { 0 2 UNDEF } FILTER(?n > ?k)", "1:2,1:2,2:-,3:-")]
    [DataRow("VALUES ?n { 0 2 UNDEF } FILTER(1 / (?n - ?k) > 0)", "1:2,1:2,2:-,3:-")]
    [DataRow("VALUES ?n {} FILTER(?n > ?k)", "1:-,1:-,2:-,3:-")]
    [DataRow("VALUES ?n { UNDEF } FILTER(?n > ?k)", "1:-,1:-,2:-,3:-")]
    public async Task Sparql_optional_diff_preserves_bags_without_spurious_unextended_solutions(string optional, string expected)
    {
        string directory = CreateDirectory();
        string body = "VALUES ?k { 1 1 2 3 } OPTIONAL { " + optional + " }";
        string query = "SELECT ?k ?n WHERE { " + body + " }";
        string update = "INSERT { [] <urn:left> ?k ; <urn:right> ?n } WHERE { " + body + " }";
        string[] wanted = expected.Split(',');
        Guid request = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        RdfQuad[]? committedQuads = null;
        for (int round = 0; round < 2; round++)
        {
            await using var database = await OpenAsync(directory);
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
            string[] rows = result.Bindings.Select(row => ((RdfLiteral)row["k"]).LexicalForm + ":" +
                (row.TryGetValue("n", out var value) ? ((RdfLiteral)value).LexicalForm : "-")).ToArray();
            CollectionAssert.AreEquivalent(wanted, rows);
            var committed = await database.CommitSparqlUpdateAsync(request, update);
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            RdfQuad[] quads = database.Find(new()).ToArray();
            committedQuads ??= quads;
            CollectionAssert.AreEquivalent(committedQuads, quads);
            var groups = quads.GroupBy(quad => quad.Subject).ToArray();
            Assert.AreEqual(wanted.Length, groups.Length, "Each solution must create its own template blank node, including duplicate solutions.");
            string[] stored = groups.Select(group =>
            {
                Assert.IsTrue(group.Key is RdfBlankNode);
                var left = group.Single(quad => quad.Predicate.Value == "urn:left");
                var right = group.SingleOrDefault(quad => quad.Predicate.Value == "urn:right");
                return ((RdfLiteral)left.Object).LexicalForm + ":" + (right is null ? "-" : ((RdfLiteral)right.Object).LexicalForm);
            }).ToArray();
            CollectionAssert.AreEquivalent(wanted, stored);
        }
    }
}
