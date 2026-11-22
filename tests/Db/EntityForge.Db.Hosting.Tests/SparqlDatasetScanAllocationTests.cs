using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql_dataset_scan_rejects_excessive_openers_without_quadratic_allocation()
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.AreEqual("true", ((RdfLiteral)RdfSparql.Execute(database,
            "SELECT (1<2 AS ?answer) WHERE {}").Single()["answer"]).LexicalForm);
        string query = "SELECT " + new string('<', 8192) + " WHERE {}";
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database, query));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Copying every remaining suffix alone costs over 64 MiB for this
        // small request. Allow ample headroom for ordinary lexical rejection.
        Assert.IsTrue(allocated < 16 * 1024 * 1024,
            $"Rejecting an {query.Length}-character request allocated {allocated} bytes.");
        Assert.AreEqual("2", ((RdfLiteral)RdfSparql.Execute(database,
            "SELECT (2 AS ?answer) WHERE {}").Single()["answer"]).LexicalForm);
        Assert.AreEqual(0, database.Find(new()).Count);
    }
}
