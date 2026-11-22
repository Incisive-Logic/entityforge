using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ADD", "default-to-named")]
    [DataRow("ADD", "named-to-default")]
    [DataRow("ADD", "named-to-named")]
    [DataRow("COPY", "default-to-named")]
    [DataRow("COPY", "named-to-default")]
    [DataRow("COPY", "named-to-named")]
    [DataRow("MOVE", "default-to-named")]
    [DataRow("MOVE", "named-to-default")]
    [DataRow("MOVE", "named-to-named")]
    public async Task Sparql_graph_transfer_preserves_shared_blank_identity_and_replay_after_reopen(string command, string direction)
    {
        // SPARQL Update 3.2.3-3.2.5: set union for ADD, replacement for COPY,
        // replacement plus source removal for MOVE. Bound blank nodes retain identity.
        string directory = CreateDirectory();
        RdfIri? source = direction == "default-to-named" ? null : new("urn:source");
        RdfIri? destination = direction == "named-to-default" ? null : new("urn:destination");
        string Graph(RdfIri? name) => name is null ? "DEFAULT" : $"GRAPH <{name.Value}>";
        string update = $"{command} {Graph(source)} TO {Graph(destination)}";
        var first = new RdfBlankNode("transfer-first");
        var second = new RdfBlankNode("transfer-second");
        var sourceSelf = new RdfQuad(first, new RdfIri("urn:self"), first, source);
        var sourceLink = new RdfQuad(first, new RdfIri("urn:link"), second, source);
        var destinationOnly = new RdfQuad(second, new RdfIri("urn:back"), first, destination);
        var unrelated = new RdfQuad(first, new RdfIri("urn:external"), second, new RdfIri("urn:untouched"));
        RdfQuad[] expected = command switch
        {
            "ADD" => [sourceSelf, sourceLink, sourceSelf with { Graph = destination }, sourceLink with { Graph = destination }, destinationOnly, unrelated],
            "COPY" => [sourceSelf, sourceLink, sourceSelf with { Graph = destination }, sourceLink with { Graph = destination }, unrelated],
            "MOVE" => [sourceSelf with { Graph = destination }, sourceLink with { Graph = destination }, unrelated],
            _ => throw new InvalidOperationException()
        };
        var marker = new RdfQuad(new RdfIri("urn:later"), new RdfIri("urn:p"), new RdfLiteral("keep"), destination);
        Guid operation = Guid.NewGuid();
        RdfSparqlUpdateCommit receipt;
        await using (var database = await OpenAsync(directory))
        {
            RdfQuad[] seed = [sourceSelf, sourceLink, sourceSelf with { Graph = destination }, destinationOnly, unrelated];
            await database.CommitAsync(new(Guid.NewGuid(), seed.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
            receipt = await database.CommitSparqlUpdateAsync(operation, update);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(marker)]));
            var afterMarker = database.CurrentVersion;
            Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
            Assert.AreEqual(afterMarker, database.CurrentVersion);
            CollectionAssert.AreEquivalent(expected.Append(marker).ToArray(), database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        var reopenedVersion = reopened.CurrentVersion;
        Assert.AreEqual(receipt, await reopened.CommitSparqlUpdateAsync(operation, update));
        Assert.AreEqual(reopenedVersion, reopened.CurrentVersion);
        CollectionAssert.AreEquivalent(expected.Append(marker).ToArray(), reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("ADD", true)]
    [DataRow("COPY", false)]
    [DataRow("MOVE", false)]
    public async Task Sparql_graph_transfer_from_empty_default_preserves_or_replaces_destination(string command, bool retainsDestination)
    {
        string directory = CreateDirectory();
        var destination = new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("existing"), new RdfIri("urn:g"));
        var unrelated = destination with
        {
            Graph = new RdfIri("urn:other")
        };
        RdfQuad[] expected = retainsDestination ? [destination, unrelated] : [unrelated];
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(destination), new AssertRdfQuad(unrelated)]));
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), $"{command} DEFAULT TO GRAPH <urn:g>");
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
        Assert.IsTrue(RdfSparql.ExecuteAsk(reopened, "ASK {}"));
    }
}
