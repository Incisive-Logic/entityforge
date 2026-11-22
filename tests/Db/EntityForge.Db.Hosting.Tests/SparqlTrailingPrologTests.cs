using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("PREFIX ex:<urn:unused:>", true)]
    [DataRow("BASE <https://example.test/a/> BASE <https://example.test/b/> PREFIX ex:<c/>", true)]
    [DataRow("# final declarations\nPREFIX étape:<urn:unused:> # done", true)]
    [DataRow("PREFIX ex:<urn:unused:>;", false)]
    [DataRow("PREFIX ex:<urn:unused:>; INSERT DATA {}", false)]
    [DataRow("BASE <https://example.test/>; # done", false)]
    public async Task Sparql_trailing_prolog_is_not_an_operation_but_cannot_precede_a_separator(string tail, bool valid)
    {
        int count = valid ? RdfSparqlUpdate.MaximumCommands : 1;
        string update = String.Join(';', Enumerable.Range(0, count).Select(i =>
            $"INSERT DATA {{ <urn:item{i}> <urn:p> <urn:value> }}")) + ";" + tail;
        RdfQuad[] expected = valid ? Enumerable.Range(0, count).Select(i =>
            new RdfQuad(new RdfIri("urn:item" + i), new("urn:p"), new RdfIri("urn:value"))).ToArray() : [];
        string directory = CreateDirectory();
        Guid operation = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        await using (var database = await OpenAsync(directory))
        {
            var before = database.CurrentVersion;
            if (valid)
            {
                Assert.AreEqual(count, RdfSparqlUpdate.Parse(update, "trailing-prolog").Count);
                receipt = await database.CommitSparqlUpdateAsync(operation, update);
                Assert.AreEqual(count, receipt.MutationCount);
                Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
            }
            else
            {
                Assert.ThrowsExactly<InvalidDataException>(() => RdfSparqlUpdate.Parse(update, "trailing-prolog"));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(operation, update).AsTask());
                Assert.AreEqual(before, database.CurrentVersion);
            }
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        if (receipt is not null)
        {
            Assert.AreEqual(receipt, await reopened.CommitSparqlUpdateAsync(operation, update));
        }
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }
}
