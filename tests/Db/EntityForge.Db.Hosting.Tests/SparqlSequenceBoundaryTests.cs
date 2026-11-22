using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(99_999, false, false)]
    [DataRow(100_000, false, true)]
    [DataRow(100_000, true, false)]
    [DataRow(100_001, true, true)]
    public async Task Sparql_mixed_sequence_dataset_boundary_is_atomic_and_persistent(int count, bool deleteFirst, bool refused)
    {
        string directory = CreateDirectory();
        var input = Enumerable.Range(0, count).Select(index => new RdfQuad(new RdfIri("urn:boundary:" + index),
            new("urn:p"), new RdfIri("urn:value"))).ToArray();
        var added = new RdfQuad(new RdfIri("urn:added"), new("urn:p"), new RdfIri("urn:value"));
        const string insert = "INSERT DATA { <urn:added> <urn:p> <urn:value> }";
        const string delete = "DELETE WHERE { <urn:boundary:0> <urn:p> <urn:value> }";
        string update = deleteFirst ? delete + ";" + insert : insert + ";" + delete;
        Guid request = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        RdfQuad[] expected = refused ? input : [.. input.Skip(1), added];
        await using (var database = await OpenAsync(directory))
        {
            await database.BulkLoadAsync(Guid.NewGuid(), input.Take(100_000).ToArray());
            if (count > 100_000)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(input[^1])]));
            }
            var before = database.CurrentVersion;
            if (refused)
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() =>
                    database.CommitSparqlUpdateAsync(request, update).AsTask());
                Assert.AreEqual(before, database.CurrentVersion);
            }
            else
            {
                receipt = await database.CommitSparqlUpdateAsync(request, update);
                Assert.AreEqual(2, receipt.MutationCount);
                Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(request, update));
            }
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        if (receipt is not null)
        {
            Assert.AreEqual(receipt, await reopened.CommitSparqlUpdateAsync(request, update));
        }
        else
        {
            var before = reopened.CurrentVersion;
            await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() =>
                reopened.CommitSparqlUpdateAsync(request, update).AsTask());
            Assert.AreEqual(before, reopened.CurrentVersion);
        }
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_sequence_operation_boundary_preserves_order_atomicity_and_replay(bool excess, bool trailing)
    {
        string directory = CreateDirectory();
        int count = RdfSparqlUpdate.MaximumCommands + (excess ? 1 : 0);
        string update = String.Join(';', Enumerable.Range(0, count).Select(i => i == 0
            ? "INSERT DATA { <urn:sequence> <urn:step0> <urn:value> }"
            : $"INSERT {{ <urn:sequence> <urn:step{i}> ?v }} WHERE {{ <urn:sequence> <urn:step{i - 1}> ?v }}")) + (trailing ? "; # end" : "");
        var sentinel = new RdfQuad(new RdfIri("urn:keep"), new("urn:p"), new RdfLiteral("unchanged"));
        RdfQuad[] expected = excess ? [sentinel] : [sentinel, .. Enumerable.Range(0, count).Select(i =>
            new RdfQuad(new RdfIri("urn:sequence"), new("urn:step" + i), new RdfIri("urn:value")))];
        Guid operation = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(sentinel)]));
            var before = database.CurrentVersion;
            if (excess)
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() =>
                    database.CommitSparqlUpdateAsync(operation, update).AsTask());
                Assert.AreEqual(before, database.CurrentVersion);
            }
            else
            {
                receipt = await database.CommitSparqlUpdateAsync(operation, update);
                Assert.AreEqual(count, receipt.MutationCount);
                Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
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
