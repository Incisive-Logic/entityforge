using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("BASE<https://example.test/base/>")]
    [DataRow("BASE<https://example.test/old/>BASE<https://example.test/base/>")]
    [DataRow("BASE<https://example.test/base/>PREFIX ex:<terms/>")]
    public async Task Sparql_compact_base_prolog_preserves_resolution_across_query_forms_and_updates(string prolog)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var subject = new RdfIri("https://example.test/base/item");
        var rows = RdfSparql.Execute(database, prolog + "SELECT(IRI('item')AS ?value){}");
        Assert.AreEqual(subject, rows.Single()["value"]);
        Guid transaction = Guid.NewGuid();
        string update = prolog + "INSERT DATA{<item><urn:p>1}";
        await database.CommitSparqlUpdateAsync(transaction, update);
        await database.CommitSparqlUpdateAsync(transaction, update);
        Assert.AreEqual(subject, database.Find(new()).Single().Subject);
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, prolog + "ASK{<item><urn:p>1}"));
        Assert.AreEqual(subject, RdfSparql.ExecuteConstruct(database,
            prolog + "CONSTRUCT{?s <urn:copy> ?o}WHERE{?s <urn:p> ?o}").Single().Subject);
        Assert.AreEqual(subject, RdfSparql.ExecuteDescribe(database, prolog + "DESCRIBE<item>").Single().Subject);
    }
}
