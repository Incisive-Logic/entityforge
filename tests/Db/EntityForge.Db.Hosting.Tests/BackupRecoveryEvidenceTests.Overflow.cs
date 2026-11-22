using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class BackupRecoveryEvidenceTests
{
    [TestMethod]
    public async Task Graph_overflow_backup_restores_properties_and_writable_adjacency()
    {
        string source = At("large-graph-source"), restored = At("large-graph-restored");
        var from = GraphVertexId.New();
        var to = GraphVertexId.New();
        var edge = GraphEdgeId.New();
        byte[] bytes = new byte[64 * 1024];
        new Random(93615).NextBytes(bytes);
        var original = new DatabaseValue.String(Convert.ToHexString(bytes));
        var replacement = new DatabaseValue.String(new string('r', 192 * 1024));
        await using (var graph = await GraphDatabase.OpenLocalAsync(new(source)))
        {
            await graph.CommitAsync(new(Guid.NewGuid(),
                [new CreateVertex(from, ["Node"]), new CreateVertex(to, ["Node"]),
                 new SetVertexProperty(from, "payload", original), new CreateEdge(edge, from, to, "links"),
                 new SetEdgeProperty(edge, "payload", original)]));
            await graph.CreateCheckpointAsync();
        }
        var manager = new LocalDatabaseBackupManager();
        await manager.CreateAsync(Path.Combine(source, LocalGraphDatabaseOptions.DefaultCheckpointFileName),
            Path.Combine(source, LocalGraphDatabaseOptions.DefaultChangeJournalDirectoryName),
            Path.Combine(source, LocalGraphDatabaseOptions.DefaultPageFileName), At("large-graph.backup"));
        await manager.RestoreAsync(At("large-graph.backup"), restored, LocalGraphDatabaseOptions.DefaultCheckpointFileName,
            LocalGraphDatabaseOptions.DefaultCommitLogFileName, LocalGraphDatabaseOptions.DefaultManifestFileName,
            LocalGraphDatabaseOptions.DefaultChangeJournalDirectoryName, LocalGraphDatabaseOptions.DefaultPageFileName,
            new GroupedRedoRestoreLogInitializer());
        await using (var graph = await GraphDatabase.OpenLocalAsync(new(restored)))
        {
            Assert.AreEqual(original, graph.GetVertex(from)!.Properties["payload"]);
            Assert.AreEqual(original, graph.GetEdge(edge)!.Properties["payload"]);
            Assert.AreEqual(1, graph.Outgoing(from).Count);
            Assert.AreEqual(1, graph.Incoming(to).Count);
            await graph.CommitAsync(new(Guid.NewGuid(),
                [new SetVertexProperty(from, "payload", replacement), new DeleteEdge(edge)]));
            await graph.CreateCheckpointAsync();
        }
        await using var final = await GraphDatabase.OpenLocalAsync(new(restored));
        Assert.AreEqual(replacement, final.GetVertex(from)!.Properties["payload"]);
        Assert.IsNull(final.GetEdge(edge));
        Assert.AreEqual(0, final.Outgoing(from).Count);
        Assert.AreEqual(0, final.Incoming(to).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rdf_overflow_backup_restores_exact_terms_and_remains_writable(bool bulk)
    {
        string source = At("rdf-source"), restored = At("rdf-restored"), second = At("rdf-second");
        var random = new Random(18219);
        var quads = Enumerable.Range(0, 6).Select(index =>
        {
            byte[] bytes = new byte[index % 2 == 0 ? 16 * 1024 : 64 * 1024];
            random.NextBytes(bytes);
            string value = Convert.ToHexString(bytes);
            return new RdfQuad(new RdfIri("urn:large:" + index), new("urn:payload"),
                index % 3 == 0 ? new RdfIri("urn:object:" + value) : new RdfLiteral(value),
                index % 2 == 0 ? null : new RdfIri("urn:named"));
        }).ToArray();
        await using (var database = await RdfDatabase.OpenLocalAsync(new(source)))
        {
            if (bulk)
            {
                await database.BulkLoadAsync(Guid.NewGuid(), quads);
            }
            else
            {
                await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
            }
            await database.CreateCheckpointAsync();
        }
        var manager = new LocalDatabaseBackupManager();
        await Backup(source, At("original.backup"));
        // A backup must retain its own checkpoint state after the source changes.
        await using (var database = await RdfDatabase.OpenLocalAsync(new(source)))
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(quads[0])]));
            await database.CreateCheckpointAsync();
        }
        await Restore(At("original.backup"), restored);
        var replacement = new RdfQuad(quads[1].Subject, quads[1].Predicate,
            new RdfLiteral(new string('x', 192 * 1024)), quads[1].Graph);
        var expected = quads.Where((_, index) => index != 0 && index != 1).Append(replacement).ToArray();
        await using (var database = await RdfDatabase.OpenLocalAsync(new(restored)))
        {
            CollectionAssert.AreEquivalent(quads, database.Find(new()).ToArray());
            foreach (var quad in quads)
            {
                Assert.AreEqual(quad, database.Find(new(Object: quad.Object)).Single());
            }
            await database.CommitAsync(new(Guid.NewGuid(),
                [new RetractRdfQuad(quads[0]), new RetractRdfQuad(quads[1]), new AssertRdfQuad(replacement)]));
            await database.CreateCheckpointAsync();
        }
        await using (var database = await RdfDatabase.OpenLocalAsync(new(restored)))
        {
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
            var rows = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?o { GRAPH <urn:named> { <urn:large:1> <urn:payload> ?o } }");
            Assert.AreEqual(replacement.Object, rows.Bindings.Single()["o"]);
        }
        await Backup(restored, At("second.backup"));
        await Restore(At("second.backup"), second);
        await using var final = await RdfDatabase.OpenLocalAsync(new(second));
        CollectionAssert.AreEquivalent(expected, final.Find(new()).ToArray());

        async Task Backup(string directory, string artifact) => await manager.CreateAsync(
            Path.Combine(directory, LocalRdfDatabaseOptions.DefaultCheckpointFileName),
            Path.Combine(directory, LocalRdfDatabaseOptions.DefaultChangeJournalDirectoryName),
            Path.Combine(directory, LocalRdfDatabaseOptions.DefaultPageFileName), artifact);

        async Task Restore(string artifact, string directory) => await manager.RestoreAsync(artifact, directory,
            LocalRdfDatabaseOptions.DefaultCheckpointFileName, LocalRdfDatabaseOptions.DefaultCommitLogFileName,
            LocalRdfDatabaseOptions.DefaultManifestFileName, LocalRdfDatabaseOptions.DefaultChangeJournalDirectoryName,
            LocalRdfDatabaseOptions.DefaultPageFileName, new GroupedRedoRestoreLogInitializer());
    }
}
