using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("1<2||CONTAINS(\">\",\">\")")]
    [DataRow("1<2||CONTAINS('>','>')")]
    [DataRow("1<2||STR(<urn:x>)=\"urn:x\"")]
    [DataRow("1<STRLEN(\">>\")")]
    [DataRow("1<STRLEN(STR(<urn:x;y>))")]
    public async Task Sparql_update_sequence_comparisons_do_not_consume_later_quoted_delimiters(string expression)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string update = "INSERT { <urn:result> <urn:p> ?answer } WHERE { BIND(" + expression +
            " AS ?answer) }; INSERT DATA { <urn:tail> <urn:p> true }";
        Guid operation = Guid.NewGuid();
        var receipt = await database.CommitSparqlUpdateAsync(operation, update);
        Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
        var actual = database.Find(new());
        Assert.AreEqual(2, actual.Count);
        Assert.IsTrue(actual.All(quad => quad.Object is RdfLiteral { LexicalForm: "true" }));
        CollectionAssert.AreEquivalent(new[] { new RdfIri("urn:result"), new RdfIri("urn:tail") }, actual.Select(quad => quad.Subject).ToArray());
    }

    [TestMethod]
    [DataRow("1<2||3>4", "true")]
    [DataRow("2<1||4>3", "true")]
    [DataRow("2<1||3>4", "false")]
    [DataRow("1<2||3>4||5<6", "true")]
    public async Task Sparql_base_resolution_does_not_consume_comparison_and_boolean_tokens(string expression, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = "SELECT (" + expression + " AS ?answer) WHERE {}";
        foreach (string prolog in new[] { "", "BASE<https://example.test/>" })
        {
            var row = RdfSparql.Execute(database, prolog + query).Single();
            Assert.AreEqual(expected, ((RdfLiteral)row["answer"]).LexicalForm, prolog + query);
        }
    }

    [TestMethod]
    public async Task Sparql_base_operator_boundaries_preserve_update_expression_results()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "BASE<https://example.test/> INSERT { <result> <urn:p> ?answer } WHERE { BIND(1<2||3>4 AS ?answer) }");
        var quad = database.Find(new()).Single();
        Assert.AreEqual(new RdfIri("https://example.test/result"), quad.Subject);
        Assert.AreEqual("true", ((RdfLiteral)quad.Object).LexicalForm);
    }
}
