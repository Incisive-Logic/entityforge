using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql_create_preserves_exact_existing_graph_terms_and_atomic_sequences()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var shared = new RdfBlankNode("shared");
        RdfQuad[] expected =
        [
            new(shared, new("urn:self"), shared, new RdfIri("urn:g")),
            new(shared, new("urn:value"), new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"), new RdfIri("urn:g")),
            new(shared, new("urn:value"), new RdfLiteral("01")),
            new(shared, new("urn:value"), new RdfLiteral("other", language: "en"), new RdfIri("urn:other"))
        ];
        await database.CommitAsync(new(Guid.NewGuid(), expected.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
        var before = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "CREATE GRAPH <urn:g>"));
        Assert.AreEqual(before, database.CurrentVersion);
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        var silent = await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "CREATE SILENT GRAPH <urn:g>");
        Assert.AreEqual(0, silent.MutationCount);
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        before = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { <urn:must-not-commit> <urn:p> 'x' }; CREATE GRAPH <urn:g>"));
        Assert.AreEqual(before, database.CurrentVersion);
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "CREATE GRAPH <urn:absent>");
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        CollectionAssert.AreEquivalent(new RdfTerm[] { new RdfIri("urn:g"), new RdfIri("urn:other") },
            RdfSparql.Execute(database, "SELECT ?g { GRAPH ?g {} }").Select(row => row["g"]).ToArray());
    }

    [TestMethod]
    [DataRow("CREATE DEFAULT")]
    [DataRow("CREATE NAMED")]
    [DataRow("CREATE ALL")]
    [DataRow("CLEAR GRAPH ?g")]
    [DataRow("DROP GRAPH _:g")]
    [DataRow("CLEAR ALL GRAPH <urn:g>")]
    public async Task Sparql_graph_management_rejects_invalid_targets_before_sequence_commit(string invalid)
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfQuad[] expected = [new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("default")),
            new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g"))];
        await database.CommitAsync(new(Guid.NewGuid(), expected.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
        var version = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await database.CommitSparqlUpdateAsync(
            Guid.NewGuid(), "INSERT DATA { <urn:must-not-commit> <urn:p> 'x' }; " + invalid));
        Assert.AreEqual(version, database.CurrentVersion);
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("CLEAR ALL", 0)]
    [DataRow("CLEAR ALL", 1)]
    [DataRow("CLEAR SILENT ALL", 0)]
    [DataRow("CLEAR SILENT ALL", 1)]
    [DataRow("DROP ALL", 0)]
    [DataRow("DROP ALL", 1)]
    [DataRow("DROP SILENT ALL", 0)]
    [DataRow("DROP SILENT ALL", 1)]
    public async Task Sparql_clear_and_drop_all_enforce_combined_graph_budget_before_commit(string update, int excess)
    {
        string directory = CreateDirectory();
        int count = RdfSparqlUpdate.MaximumMutations + excess;
        var quads = Enumerable.Range(0, count).Select(index => new RdfQuad(
            new RdfIri("urn:scope:" + index), new("urn:p"), new RdfLiteral("value"),
            index % 2 == 0 ? null : new RdfIri("urn:named"))).ToArray();
        var marker = new RdfQuad(new RdfIri("urn:later"), new("urn:p"), new RdfLiteral("keep"));
        Guid operation = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        RdfQuad[] expected = excess == 0 ? [] : quads;
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), quads.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
            var before = database.CurrentVersion;
            if (excess == 0)
            {
                receipt = await database.CommitSparqlUpdateAsync(operation, update);
                Assert.AreEqual(count, receipt.MutationCount);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
                    await database.CommitSparqlUpdateAsync(operation, update));
                Assert.AreEqual(before, database.CurrentVersion);
            }
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            Assert.IsTrue(RdfSparql.ExecuteAsk(database, "ASK {}"));
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:later> <urn:p> 'keep' }");
            var afterMarker = database.CurrentVersion;
            if (receipt is not null)
            {
                Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
            }
            else
            {
                await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
                    await database.CommitSparqlUpdateAsync(operation, update));
            }
            Assert.AreEqual(afterMarker, database.CurrentVersion);
            CollectionAssert.AreEquivalent(expected.Append(marker).ToArray(), database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        var reopenedVersion = reopened.CurrentVersion;
        if (receipt is not null)
        {
            Assert.AreEqual(receipt, await reopened.CommitSparqlUpdateAsync(operation, update));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
                await reopened.CommitSparqlUpdateAsync(operation, update));
        }
        Assert.AreEqual(reopenedVersion, reopened.CurrentVersion);
        CollectionAssert.AreEquivalent(expected.Append(marker).ToArray(), reopened.Find(new()).ToArray());
    }
}
