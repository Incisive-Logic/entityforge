using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT ?x { VALUES ?x {1} } GROUP BY (?x AS ?x)")]
    [DataRow("SELECT * { { SELECT * {} VALUES ?x {1} } BIND(2 AS ?x) }")]
    [DataRow("SELECT (1 AS ?endpoint) { SERVICE SILENT ?endpoint {} }")]
    public async Task Sparql_scope_errors_reject_prior_sequence_writes_across_restart(string subquery)
    {
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        string update = "DELETE DATA { <urn:seed> <urn:p> 7 }; INSERT DATA { <urn:marker> <urn:p> 1 }; " +
            "INSERT { <urn:result> <urn:p> 2 } WHERE { { " + subquery + " } }; " +
            "INSERT DATA { <urn:tail> <urn:p> 3 }";
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:seed> <urn:p> 7 }");
            }
            for (int retry = 0; retry < 2; retry++)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    database.CommitSparqlUpdateAsync(operation, update).AsTask());
                var quads = database.Find(new());
                Assert.AreEqual(1, quads.Count);
                Assert.AreEqual(new RdfIri("urn:seed"), quads[0].Subject);
                Assert.AreEqual(new RdfIri("urn:p"), quads[0].Predicate);
                Assert.AreEqual("7", ((RdfLiteral)quads[0].Object).LexicalForm);
            }
        }
    }
}
