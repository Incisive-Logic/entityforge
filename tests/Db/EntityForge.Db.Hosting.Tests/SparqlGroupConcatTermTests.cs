using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("'a' 'a' 'b'", "", "a a b|a b a|b a a")]
    [DataRow("'a' 'a' 'b'", "; SEPARATOR=''", "aab|aba|baa")]
    [DataRow("'a' 'a' 'b'", "; SEPARATOR='😀'", "a😀a😀b|a😀b😀a|b😀a😀a")]
    [DataRow("'a'@en 'a'@fr 'b'@en", "", "a a b|a b a|b a a")]
    [DataRow("", "", "")]
    public async Task Sparql_group_concat_separator_and_unordered_multiset_contract(
        string values, string separator, string alternatives)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT (GROUP_CONCAT(?v" + separator + ") AS ?text) WHERE { VALUES ?v { " + values + " } }";
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
        Assert.AreEqual(1, result.Bindings.Count);
        var text = result.Bindings[0]["text"];
        Assert.IsTrue(alternatives.Split('|').Select(value => new RdfLiteral(value)).Contains(text),
            "Concatenation preserves the input multiset and separator without promising an input order: " + text);
    }

    [TestMethod]
    [DataRow("", "?v", false, "")]
    [DataRow("'a'@en", "?v", false, "a")]
    [DataRow("'a'@en 'a'@en", "?v", false, "a|a")]
    [DataRow("'a'@en 'a'@en", "?v", true, "a")]
    [DataRow("'a'@en 'a'@fr", "?v", true, "a|a")]
    [DataRow("'a' 'a'^^<http://www.w3.org/2001/XMLSchema#string>", "?v", true, "a")]
    [DataRow("1", "?v", false, null)]
    [DataRow("'a' 1", "?v", false, null)]
    [DataRow("<urn:a>", "?v", false, null)]
    [DataRow("1 1", "STR(?v)", false, "1|1")]
    [DataRow("<urn:a> <urn:a>", "STR(?v)", true, "urn:a")]
    public async Task Sparql_group_concat_preserves_term_distinctness_and_string_errors_across_restart(
        string values, string expression, bool distinct, string? expected)
    {
        // Repeated lexical forms avoid imposing an ordering on GROUP_CONCAT.
        string query = "SELECT (COUNT(*) AS ?count) (GROUP_CONCAT(" +
            (distinct ? "DISTINCT " : "") + expression + "; SEPARATOR='|') AS ?text) " +
            "WHERE { VALUES ?v { " + values + " } }";
        string update = "INSERT { <urn:concat-result> <urn:text> ?text; <urn:present> true } WHERE { { " + query + " } }";
        string directory = CreateDirectory();
        Guid request = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query);
            Assert.AreEqual(1, result.Bindings.Count);
            var row = result.Bindings[0];
            Assert.IsTrue(row.ContainsKey("count"));
            Assert.AreEqual(expected is not null, row.ContainsKey("text"));
            if (expected is not null)
            {
                Assert.AreEqual(new RdfLiteral(expected), row["text"]);
            }
            var committed = await database.CommitSparqlUpdateAsync(request, update);
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            var quads = new List<RdfQuad>
            {
                new(new RdfIri("urn:concat-result"), new("urn:present"),
                    new RdfLiteral("true", "http://www.w3.org/2001/XMLSchema#boolean"))
            };
            if (expected is not null)
            {
                quads.Add(new(new RdfIri("urn:concat-result"), new("urn:text"), new RdfLiteral(expected)));
            }
            CollectionAssert.AreEquivalent(quads.ToArray(), database.Find(new()).ToArray());
        }
    }
}
