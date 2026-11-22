using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(@"\u0053ELECT (1 AS ?answer) {}", "1")]
    [DataRow(@"SELECT (\u0031 AS ?answer) {}", "1")]
    [DataRow(@"SELECT (1 AS ?\u0061nswer) {}", "1")]
    [DataRow(@"PREFIX \u03B1:<urn:example:> SELECT (\u03B1:a AS ?answer) {}", "urn:example:a")]
    [DataRow(@"PREFIX ex:<urn:example:> SELECT (ex\u003Aa AS ?answer) {}", "urn:example:a")]
    [DataRow(@"SELECT (1 AS ?answer) \u007B\u007D", "1")]
    [DataRow(@"SELECT (1 AS ?answer) {} \u0023 ignored ' \u000A", "1")]
    [DataRow(@"# ignored \u000ASELECT (1 AS ?answer) {}", "1")]
    [DataRow(@"SELECT ('\u0022' AS ?answer) {}", "\"")]
    [DataRow(@"SELECT ('\\u0031' AS ?answer) {}", @"\u0031")]
    [DataRow(@"SELECT (<urn:example:\u0061> AS ?answer) {}", "urn:example:a")]
    [DataRow(@"PREFIX ex:<urn:example:> SELECT (ex:a\u005C#b AS ?answer) {}", "urn:example:a#b")]
    [DataRow(@"SELECT (1 AS ?answer) { { \u0053ELECT (1 AS ?x) {} } }", "1")]
    public async Task Sparql_codepoint_escapes_are_processed_in_query_syntax(string query, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var answer = RdfSparql.Execute(database, query).Single()["answer"];
        Assert.AreEqual(expected, answer is RdfLiteral literal ? literal.LexicalForm : ((RdfIri)answer).Value);
    }

    [TestMethod]
    [DataRow(@"SELECT * WHERE { ?s ?p \u005cU00000031 }")]
    [DataRow(@"SELECT * WHERE { ?s ?p \U0000005cu0031 }")]
    public async Task Sparql_codepoint_decoding_is_not_recursive(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.Throws<InvalidDataException>(() => RdfSparql.Execute(database, query));
    }

    [TestMethod]
    [DataRow(@"\u0049NSERT DATA { <urn:s> <urn:p> 1 }\u003B INSERT DATA { <urn:s> <urn:q> 2 }")]
    [DataRow(@"INSERT DATA \u007B <urn:s> <urn:p> 1 \u007D; INSERT DATA { <urn:s> <urn:q> 2 }")]
    [DataRow(@"PREFIX ex:<urn:> INSERT DATA { ex\u003As ex:p 1 }; INSERT DATA { ex:s ex:q 2 }")]
    [DataRow(@"INSERT DATA { <urn:s> <urn:p> 1 }; # ignored \u000A INSERT DATA { <urn:s> <urn:q> 2 }")]
    public async Task Sparql_codepoint_update_sequences_preserve_terms_and_replay(string update)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var operation = Guid.NewGuid();
        var receipt = await database.CommitSparqlUpdateAsync(operation, update);
        RdfQuad[] expected =
        [
            new(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer")),
            new(new RdfIri("urn:s"), new RdfIri("urn:q"), new RdfLiteral("2", "http://www.w3.org/2001/XMLSchema#integer"))
        ];
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
    }
}
