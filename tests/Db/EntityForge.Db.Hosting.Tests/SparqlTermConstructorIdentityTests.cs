using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("STRDT('00012', xsd:integer)", "00012", "http://www.w3.org/2001/XMLSchema#integer", null)]
    [DataRow("STRDT('not a number', xsd:integer)", "not a number", "http://www.w3.org/2001/XMLSchema#integer", null)]
    [DataRow("STRDT('iiii', <urn:roman>)", "iiii", "urn:roman", null)]
    [DataRow("STRDT('', <urn:custom>)", "", "urn:custom", null)]
    [DataRow("STRDT('雪😀', xsd:string)", "雪😀", "http://www.w3.org/2001/XMLSchema#string", null)]
    [DataRow("STRLANG('chat', 'EN-gb')", "chat", null, "en-gb")]
    [DataRow("STRLANG('', 'fr')", "", null, "fr")]
    [DataRow("STRLANG('雪😀', 'zh-Hant')", "雪😀", null, "zh-hant")]
    public async Task Sparql_term_constructors_preserve_lexical_identity_in_query_update_and_restart(
        string expression, string lexicalForm, string? datatype, string? language)
    {
        const string prolog = "PREFIX xsd: <http://www.w3.org/2001/XMLSchema#> ";
        var expected = new RdfLiteral(lexicalForm, datatype, language);
        string directory = CreateDirectory();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            var row = RdfSparql.Execute(database, prolog + "SELECT ?value { BIND(" + expression + " AS ?value) }").Single();
            Assert.AreEqual(expected, row["value"]);
            if (restart == 0)
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog +
                    "INSERT { <urn:constructor> <urn:value> ?value } WHERE { BIND(" + expression + " AS ?value) }");
            }
            Assert.AreEqual(expected, database.Find(new()).Single().Object);
        }
    }

    [TestMethod]
    [DataRow("STRDT('chat'@en, <urn:custom>)")]
    [DataRow("STRDT(12, <urn:custom>)")]
    [DataRow("STRDT(<urn:lexical>, <urn:custom>)")]
    [DataRow("STRDT('chat', 'urn:custom')")]
    [DataRow("STRDT('chat', BNODE())")]
    [DataRow("STRDT(?missing, <urn:custom>)")]
    [DataRow("STRDT('chat', ?missing)")]
    [DataRow("STRLANG('chat'@en, 'fr')")]
    [DataRow("STRLANG(12, 'en')")]
    [DataRow("STRLANG('chat', 'en'@en)")]
    [DataRow("STRLANG('chat', <urn:en>)")]
    [DataRow("STRLANG('chat', ?missing)")]
    public async Task Sparql_term_constructor_errors_leave_unbound_values_without_aborting_valid_update(string expression)
    {
        string directory = CreateDirectory();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                string group = "BIND(" + expression + " AS ?bad) BIND(COALESCE(?bad, 'fallback') AS ?value)";
                var row = RdfSparql.Execute(database, "SELECT ?bad ?value { " + group + " }").Single();
                Assert.IsFalse(row.ContainsKey("bad"));
                Assert.AreEqual(new RdfLiteral("fallback"), row["value"]);
                Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT * { FILTER(" + expression + ") }").Count);
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                    "INSERT { <urn:constructor> <urn:bad> ?bad; <urn:value> ?value } WHERE { " + group + " }");
            }
            var quad = database.Find(new()).Single();
            Assert.AreEqual(new RdfIri("urn:value"), quad.Predicate);
            Assert.AreEqual(new RdfLiteral("fallback"), quad.Object);
        }
    }
}
