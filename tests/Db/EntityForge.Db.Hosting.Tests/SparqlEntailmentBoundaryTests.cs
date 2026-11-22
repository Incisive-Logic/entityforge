using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("<urn:Cat> <http://www.w3.org/2000/01/rdf-schema#subClassOf> <urn:Animal> . <urn:pet> a <urn:Cat>", "<urn:pet> a <urn:Animal>")]
    [DataRow("<urn:owns> <http://www.w3.org/2000/01/rdf-schema#domain> <urn:Person> . <urn:owner> <urn:owns> <urn:pet>", "<urn:owner> a <urn:Person>")]
    [DataRow("<urn:pet> <http://www.w3.org/2002/07/owl#sameAs> <urn:other> . <urn:pet> <urn:name> 'Cat'", "<urn:other> <urn:name> 'Cat'")]
    public async Task Sparql_rdf_profile_does_not_infer_unasserted_schema_consequences(string assertions, string consequence)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { " + assertions + " }");
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, "ASK { " + consequence + " }"));
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, "ASK { " + assertions + " }"));
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { " + consequence + " }");
        Assert.IsTrue(RdfSparql.ExecuteAsk(database, "ASK { " + consequence + " }"));
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "DELETE DATA { " + consequence + " }");
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, "ASK { " + consequence + " }"));
    }
}
