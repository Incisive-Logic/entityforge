using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("VALUES ?x { 1 }", "true,false,true")]
    [DataRow("VALUES ?x { 2 }", "false,true,false")]
    [DataRow("VALUES ?x { UNDEF }", "true,true,true")]
    [DataRow("VALUES ?x {}", "false,false,false")]
    [DataRow("{ VALUES ?x { 1 1 } }", "true,false,true")]
    [DataRow("VALUES (?x ?inner) { (1 'match') (2 'skip') } FILTER(?inner='match')", "true,false,true")]
    [DataRow("VALUES ?x { '01'^^<http://www.w3.org/2001/XMLSchema#integer> }", "false,false,false")]
    [DataRow("{ SELECT ?inner WHERE { VALUES (?x ?inner) { (1 'match') } } }", "true,false,true")]
    [DataRow("{ SELECT ?inner WHERE { VALUES (?localX ?inner) { (1 'match') } } }", "true,true,true")]
    public async Task Sparql_exists_VALUES_boundaries_preserve_term_compatibility_and_local_scope(string body, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT ?s ?inner (EXISTS { " + body + " } AS ?yes) (NOT EXISTS { " + body +
            " } AS ?no) WHERE { VALUES (?s ?x) { (<urn:a> 1) (<urn:b> 2) (<urn:c> 1) } } ORDER BY ?s";
        string[] truth = expected.Split(',');
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(3, rows.Count);
            for (int index = 0; index < rows.Count; index++)
            {
                Assert.AreEqual(new RdfIri("urn:" + (char)('a' + index)), rows[index]["s"]);
                Assert.AreEqual(new RdfLiteral(truth[index], "http://www.w3.org/2001/XMLSchema#boolean"), rows[index]["yes"]);
                Assert.AreEqual(new RdfLiteral(truth[index] == "true" ? "false" : "true",
                    "http://www.w3.org/2001/XMLSchema#boolean"), rows[index]["no"]);
                Assert.IsFalse(rows[index].ContainsKey("inner"), "EXISTS must not export its local binding.");
            }
        }
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Verify(result.Bindings);
        // EXISTS performs graph reads and is intentionally outside the sorted
        // scan expression subset; qualify the ordinary query/update route here.
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT { ?s <urn:yes> ?yes ; <urn:no> ?no ; <urn:leak> ?inner } WHERE { { " + query + " } }");
        var quads = database.Find(new()).ToArray();
        Assert.AreEqual(6, quads.Length);
        for (int index = 0; index < truth.Length; index++)
        {
            var subject = new RdfIri("urn:" + (char)('a' + index));
            Assert.AreEqual(truth[index], ((RdfLiteral)quads.Single(quad => quad.Subject == subject && quad.Predicate.Value == "urn:yes").Object).LexicalForm);
            Assert.AreEqual(truth[index] == "true" ? "false" : "true",
                ((RdfLiteral)quads.Single(quad => quad.Subject == subject && quad.Predicate.Value == "urn:no").Object).LexicalForm);
        }
        Assert.IsFalse(quads.Any(quad => quad.Predicate.Value == "urn:leak"));
    }
}
