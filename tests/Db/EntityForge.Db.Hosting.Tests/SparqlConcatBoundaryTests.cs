using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("CONCAT()", "", null)]
    [DataRow("CONCAT('café')", "café", null)]
    [DataRow("CONCAT('café'^^xsd:string)", "café", null)]
    [DataRow("CONCAT(''@EN)", "", "en")]
    [DataRow("CONCAT('café'@FR)", "café", "fr")]
    [DataRow("CONCAT('a'@EN,'b'@en)", "ab", "en")]
    [DataRow("CONCAT('a'@en,'b'@fr)", "ab", null)]
    [DataRow("CONCAT('a'@en,'')", "a", null)]
    [DataRow("CONCAT(CONCAT(),'x')", "x", null)]
    [DataRow("CONCAT(1)", "fallback", null)]
    [DataRow("CONCAT(<urn:value>)", "fallback", null)]
    [DataRow("CONCAT(?unbound)", "fallback", null)]
    public async Task Sparql_concat_preserves_zero_single_and_mixed_argument_terms_across_query_update_and_restart(
        string expression, string lexical, string? language)
    {
        const string prolog = "PREFIX xsd:<http://www.w3.org/2001/XMLSchema#> ";
        string selected = "COALESCE(" + expression + ",'fallback')";
        string query = prolog + "SELECT (" + selected + " AS ?value) WHERE {}";
        string update = prolog + "INSERT { <urn:concat> <urn:p> ?value } WHERE { BIND(" + selected + " AS ?value) }";
        string directory = CreateDirectory();
        Guid request = Guid.NewGuid();
        var expected = new RdfLiteral(lexical, language: language);
        RdfSparqlUpdateCommit? receipt = null;
        for (int round = 0; round < 2; round++)
        {
            await using var database = await OpenAsync(directory);
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
            Assert.AreEqual(1, result.Bindings.Count);
            Assert.AreEqual(expected, result.Bindings[0]["value"]);
            var committed = await database.CommitSparqlUpdateAsync(request, update);
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            CollectionAssert.AreEqual(new[] { new RdfQuad(new RdfIri("urn:concat"), new("urn:p"), expected) }, database.Find(new()).ToArray());
        }
    }
}
