using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("a._:b", "a._:b")]
    [DataRow("a-_:b", "a-_:b")]
    [DataRow("a\\!_:b", "a!_:b")]
    [DataRow("a\\#_:b", "a#_:b")]
    public async Task Sparql_update_data_label_scope_does_not_split_prefixed_iri_tokens(string local, string value)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string update = "PREFIX ex:<urn:example:> INSERT DATA { ex:" + local + " ex:p 1 }; " +
            "INSERT DATA { ex:" + local + " ex:q 2 }";
        var operation = Guid.NewGuid();
        var receipt = await database.CommitSparqlUpdateAsync(operation, update);
        RdfQuad[] expected =
        [
            new(new RdfIri("urn:example:" + value), new RdfIri("urn:example:p"), new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer")),
            new(new RdfIri("urn:example:" + value), new RdfIri("urn:example:q"), new RdfLiteral("2", "http://www.w3.org/2001/XMLSchema#integer"))
        ];
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
    }
}
