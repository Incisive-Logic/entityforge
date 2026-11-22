using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("EXISTS { VALUES ?c {1} }")]
    [DataRow("(COUNT(*) > 1)")]
    public async Task Sparql_having_subquery_updates_preserve_scope_replay_and_restart(string condition)
    {
        string directory = CreateDirectory();
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 1 }");
            string update = "INSERT DATA { <urn:marker> <urn:ok> true }; " +
                "INSERT { <urn:result> <urn:count> ?c } WHERE { { SELECT (COUNT(*) AS ?c) { ?s <urn:p> ?v } HAVING " + condition + " } }";
            Guid operation = Guid.NewGuid();
            var receipt = await database.CommitSparqlUpdateAsync(operation, update);
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
            var rows = RdfSparql.Execute(database, "SELECT ?c { <urn:result> <urn:count> ?c }");
            Assert.AreEqual("2", ((RdfLiteral)rows.Single()["c"]).LexicalForm);
        }
        await using var reopened = await OpenAsync(directory);
        Assert.IsTrue(RdfSparql.ExecuteAsk(reopened, "ASK { <urn:marker> <urn:ok> true }"));
        var persisted = RdfSparql.Execute(reopened, "SELECT ?c { <urn:result> <urn:count> ?c }");
        Assert.AreEqual("2", ((RdfLiteral)persisted.Single()["c"]).LexicalForm);
    }

    [TestMethod]
    public async Task Sparql_having_invalid_alias_rejects_entire_update_sequence()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 . <urn:b> <urn:p> 1 }");
        const string update = "INSERT DATA { <urn:marker> <urn:ok> true }; " +
            "INSERT { <urn:result> <urn:count> ?c } WHERE { { SELECT (COUNT(*) AS ?c) { ?s <urn:p> ?v } HAVING (?c > 1) } }";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update));
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, "ASK { <urn:marker> ?p ?o }"));
        Assert.IsFalse(RdfSparql.ExecuteAsk(database, "ASK { <urn:result> ?p ?o }"));
        Assert.AreEqual(2, RdfSparql.Execute(database, "SELECT ?s { ?s <urn:p> 1 }").Count);
    }
}
