using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    public async Task Sparql_data_commands_enforce_exact_mutation_bound_without_partial_commit(bool delete, int excess)
    {
        string directory = CreateDirectory();
        int count = RdfSparqlUpdate.MaximumMutations + excess;
        var sentinel = new RdfQuad(new RdfIri("urn:keep"), new("urn:p"), new RdfLiteral("original"));
        RdfQuad[] data = Enumerable.Range(0, count).Select(i =>
            new RdfQuad(new RdfIri("urn:s" + i), new("urn:p"), new RdfIri("urn:o"), new RdfIri("urn:g"))).ToArray();
        RdfQuad[] seed = delete ? [sentinel, .. data] : [sentinel];
        string command = (delete ? "DELETE" : "INSERT") + " DATA { GRAPH <urn:g> { " +
            String.Join(' ', Enumerable.Range(0, count).Select(i => $"<urn:s{i}> <urn:p> <urn:o> .")) + " } }";
        Guid operation = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), seed.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
            var before = database.CurrentVersion;
            if (excess != 0)
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() =>
                    database.CommitSparqlUpdateAsync(operation, command).AsTask());
                Assert.AreEqual(before, database.CurrentVersion);
                CollectionAssert.AreEquivalent(seed, database.Find(new()).ToArray());
            }
            else
            {
                receipt = await database.CommitSparqlUpdateAsync(operation, command);
                Assert.AreEqual(count, receipt.MutationCount);
                Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, command));
                CollectionAssert.AreEquivalent(delete ? new[] { sentinel } : [sentinel, .. data], database.Find(new()).ToArray());
            }
        }
        await using var reopened = await OpenAsync(directory);
        if (receipt is not null)
        {
            Assert.AreEqual(receipt, await reopened.CommitSparqlUpdateAsync(operation, command));
        }
        CollectionAssert.AreEquivalent(excess != 0 ? seed : delete ? [sentinel] : [sentinel, .. data], reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_empty_data_commands_preserve_dataset_and_do_not_create_named_graphs(bool delete, bool named)
    {
        string directory = CreateDirectory();
        RdfQuad[] seed =
        [
            new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("default")),
            new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g"))
        ];
        string command = (delete ? "DELETE" : "INSERT") + " DATA { " + (named ? "GRAPH <urn:empty> {}" : "") + " }";
        Guid operation = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitAsync(new(Guid.NewGuid(), seed.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
            }
            var actual = await database.CommitSparqlUpdateAsync(operation, command);
            receipt ??= actual;
            Assert.AreEqual(receipt, actual);
            Assert.AreEqual(0, actual.MutationCount);
            CollectionAssert.AreEquivalent(seed, database.Find(new()).ToArray());
            CollectionAssert.AreEquivalent(new RdfTerm[] { new RdfIri("urn:g") },
                RdfSparql.Execute(database, "SELECT ?g { GRAPH ?g {} }").Select(row => row["g"]).ToArray());
        }
    }
}
