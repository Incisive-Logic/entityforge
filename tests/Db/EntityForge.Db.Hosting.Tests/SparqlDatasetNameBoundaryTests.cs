using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("a\\(FROM", "a(FROM")]
    [DataRow("a\\)FROM", "a)FROM")]
    [DataRow("a\\(WHERE", "a(WHERE")]
    [DataRow("a-ORDER", "a-ORDER")]
    public async Task Sparql_protocol_dataset_insertion_preserves_prefixed_name_tokens(string localName, string iriLocal)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { GRAPH <urn:selected> { <urn:" + iriLocal + "> <urn:p> 'selected' }" +
            " <urn:" + iriLocal + "> <urn:p> 'default' }");
        string query = RdfSparql.WithProtocolDataset("PREFIX ex:<urn:> DESCRIBE ex:" + localName + " WHERE {}", ["urn:selected"], []);
        var result = RdfSparql.ExecuteDescribe(database, query);
        Assert.AreEqual(1, result.Count, query);
        Assert.AreEqual(new EntityForge.Db.Rdf.Contracts.RdfLiteral("selected"), result.Single().Object);
    }

    [TestMethod]
    [DataRow("a-FROM", "a-FROM")]
    [DataRow("a.FROM", "a.FROM")]
    [DataRow("a%20FROM", "a%20FROM")]
    [DataRow("a\\-FROM", "a-FROM")]
    [DataRow("a\\(FROM", "a(FROM")]
    [DataRow("a\\)FROM", "a)FROM")]
    public async Task Sparql_describe_names_containing_from_are_not_dataset_clauses(string localName, string iriLocal)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { <urn:" + iriLocal + "> <urn:p> 'first' . <urn:target> <urn:p> 'second' }");
        var result = RdfSparql.ExecuteDescribe(database,
            "PREFIX ex:<urn:> DESCRIBE ex:" + localName + " <urn:target> WHERE {}");
        Assert.AreEqual(2, result.Count);
    }
}
