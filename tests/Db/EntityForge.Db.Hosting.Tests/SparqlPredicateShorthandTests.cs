using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ASK { a <urn:p> <urn:o> }")]
    [DataRow("ASK { <urn:s> <urn:p> a }")]
    [DataRow("ASK { GRAPH a { <urn:s> <urn:p> <urn:o> } }")]
    [DataRow("SELECT ?v WHERE { BIND(a AS ?v) }")]
    [DataRow("SELECT ?v WHERE { VALUES ?v { a } }")]
    [DataRow("DESCRIBE a WHERE {}")]
    public async Task Sparql_predicate_shorthand_rejects_other_term_positions(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.QuerySparqlAsync(query).AsTask());
    }

    [TestMethod]
    [DataRow("a", true)]
    [DataRow("a+", true)]
    [DataRow("(a|<urn:p>)", true)]
    [DataRow("!<urn:other>", true)]
    [DataRow("!a", false)]
    [DataRow("!(a|<urn:p>)", false)]
    [DataRow("^a", false)]
    public async Task Sparql_predicate_shorthand_remains_valid_in_predicates_and_paths(string path, bool expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:s> a <urn:Class> }");
        var result = (RdfSparqlAskResult)await database.QuerySparqlAsync("ASK { <urn:s> " + path + " <urn:Class> }");
        Assert.AreEqual(expected, result.Value);
    }
}
