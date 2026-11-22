using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("+01", "integer")]
    [DataRow("+.50", "decimal")]
    [DataRow("+01E-0", "double")]
    public async Task Sparql_signed_modify_templates_preserve_named_graph_identity_and_replay(string token, string datatype)
    {
        string directory = CreateDirectory();
        Guid transaction = Guid.NewGuid();
        var expected = new RdfQuad(new RdfIri("urn:literal"), new RdfIri("urn:updated"),
            new RdfLiteral(token, "http://www.w3.org/2001/XMLSchema#" + datatype), new RdfIri("urn:g"));
        string update = "WITH <urn:g> DELETE { <urn:literal> <urn:value> " + token +
            " } INSERT { <urn:literal> <urn:updated> " + token +
            " } WHERE { <urn:literal> <urn:value> " + token + " }";
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                    "INSERT DATA { GRAPH <urn:g> { <urn:literal> <urn:value> " + token + " } }");
            }
            await database.CommitSparqlUpdateAsync(transaction, update);
            CollectionAssert.AreEquivalent(new[] { expected }, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        await reopened.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "DELETE WHERE { GRAPH <urn:g> { <urn:literal> <urn:updated> " + token + " } }");
        Assert.AreEqual(0, reopened.Find(new()).Count());
    }

    [TestMethod]
    [DataRow("1+2*3", "7")]
    [DataRow("1-2*3", "-5")]
    [DataRow("1 +2 * 3", "7")]
    [DataRow("1 -2 * 3", "-5")]
    [DataRow("1+ 2*3", "7")]
    [DataRow("1- 2*3", "-5")]
    [DataRow("1--2", "3")]
    [DataRow("1+-2", "-1")]
    [DataRow("1- -2*3", "7")]
    [DataRow("1+2/2", "2")]
    public async Task Sparql_signed_numeric_tokens_preserve_additive_precedence(string expression, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (" + expression + " AS ?answer) {}").Single();
        Assert.AreEqual(expected, ((RdfLiteral)row["answer"]).LexicalForm);
    }

    [TestMethod]
    public async Task Sparql_positive_numeric_object_is_distinct_from_path_repetition()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { <urn:start> <urn:p> <urn:middle> . <urn:middle> <urn:p> +01 }");
        Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT * { <urn:start> <urn:p> +01 }").Count);
        Assert.AreEqual(1, RdfSparql.Execute(database, "SELECT * { <urn:start> <urn:p>+ +01 }").Count);
        Assert.AreEqual(1, RdfSparql.Execute(database, "SELECT * { <urn:start> (<urn:p>)+ +01 }").Count);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RdfSparql.ExecuteConstruct(database, "CONSTRUCT { <urn:s> <urn:p> + 1 } WHERE {}"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RdfSparql.Execute(database, "SELECT ?v { VALUES ?v { + 1 } }"));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RdfSparql.Execute(database, "SELECT ?v { VALUES ?v { - 1 } }"));
    }

    [TestMethod]
    [DataRow("+01", "+01", "integer")]
    [DataRow("-0", "-0", "integer")]
    [DataRow("001", "001", "integer")]
    [DataRow(".50", ".50", "decimal")]
    [DataRow("+.50", "+.50", "decimal")]
    [DataRow("-0.00", "-0.00", "decimal")]
    [DataRow("1.e+02", "1.e+02", "double")]
    [DataRow("+01E-0", "+01E-0", "double")]
    [DataRow("-.5e2", "-.5e2", "double")]
    public async Task Sparql_numeric_shorthand_retains_term_identity_across_query_templates_update_and_restart(
        string token, string lexical, string datatype)
    {
        string directory = CreateDirectory();
        var expected = new RdfLiteral(lexical, "http://www.w3.org/2001/XMLSchema#" + datatype);
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                    "INSERT DATA { <urn:literal> <urn:value> " + token + " }");
            }
            var row = RdfSparql.Execute(database,
                "SELECT ?value { VALUES ?value { " + token + " } <urn:literal> <urn:value> ?value }").Single();
            Assert.AreEqual(expected, row["value"]);
            var constructed = RdfSparql.ExecuteConstruct(database,
                "CONSTRUCT { <urn:literal> <urn:value> " + token + " } WHERE {}").Single();
            Assert.AreEqual(expected, constructed.Object);
            var stored = database.Find(new()).Single();
            Assert.AreEqual(expected, stored.Object);
            Assert.AreEqual(1, RdfSparql.Execute(database,
                "SELECT * { <urn:literal> <urn:value> " + token + " }").Count);
            if (restart == 1)
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                    "DELETE DATA { <urn:literal> <urn:value> " + token + " }");
                Assert.AreEqual(0, database.Find(new()).Count());
            }
        }
        await using var reopened = await OpenAsync(directory);
        Assert.AreEqual(0, reopened.Find(new()).Count());
    }
}
