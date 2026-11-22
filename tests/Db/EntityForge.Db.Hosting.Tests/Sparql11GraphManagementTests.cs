using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql11_update_resolves_repeated_base_and_prefix_declarations_in_order()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string prolog = "BASE <https://example.test/a/> PREFIX ex:<old/> BASE <https://example.test/b/> ";
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog + "INSERT DATA { GRAPH <g> { <s> ex:p <o> } }");
        var quad = database.Find(new()).Single();
        Assert.AreEqual(new RdfIri("https://example.test/b/s"), quad.Subject);
        Assert.AreEqual(new RdfIri("https://example.test/a/old/p"), quad.Predicate);
        Assert.AreEqual(new RdfIri("https://example.test/b/o"), quad.Object);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog + "CLEAR GRAPH <g>");
        Assert.AreEqual(0, database.Find(new()).Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" \t\r\n")]
    [DataRow("# no changes")]
    [DataRow("BASE <https://example.test/> # no operation")]
    [DataRow("PREFIX ex:<urn:> # no operation")]
    public async Task Sparql11_empty_update_requests_preserve_data_and_replay(string update)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:s> <urn:p> 1 }");
        Guid operation = Guid.NewGuid();
        var receipt = await database.CommitSparqlUpdateAsync(operation, update);
        Assert.AreEqual(0, receipt.MutationCount);
        Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
        Assert.AreEqual(1, database.Find(new()).Count);
        Assert.AreEqual(0, RdfSparqlUpdate.Parse(update, "empty").Count);
    }

    [TestMethod]
    [DataRow("CREATE GRAPH <urn:empty>")]
    [DataRow("CREATE SILENT GRAPH <urn:empty>")]
    [DataRow("CLEAR GRAPH <urn:empty>")]
    [DataRow("DROP GRAPH <urn:empty>")]
    public async Task Sparql11_empty_graph_management_succeeds_without_retaining_empty_graphs(string update)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Guid operation = Guid.NewGuid();
        var result = await database.CommitSparqlUpdateAsync(operation, update);
        Assert.AreEqual(0, result.MutationCount);
        Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT ?g { GRAPH ?g {} }").Count);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { GRAPH <urn:empty> { <urn:s> <urn:p> 1 } }");
        Assert.AreEqual(result, await database.CommitSparqlUpdateAsync(operation, update));
        Assert.AreEqual(1, database.Find(new()).Count);
    }

    [TestMethod]
    public async Task Sparql11_create_preserves_existing_graphs_and_validates_atomic_sequences()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "CREATE GRAPH <urn:g>; INSERT DATA { GRAPH <urn:g> { <urn:s> <urn:p> 1 } }");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), "CREATE GRAPH <urn:g>").AsTask());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "CREATE SILENT GRAPH <urn:g>");
        Assert.AreEqual(1, database.Find(new()).Count);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { <urn:new> <urn:p> 2 }; CREATE GRAPH <urn:g>").AsTask());
        Assert.AreEqual(1, database.Find(new()).Count);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "CLEAR GRAPH <urn:g>; CREATE GRAPH <urn:g>; DROP GRAPH <urn:g>; CLEAR GRAPH <urn:g>");
        Assert.AreEqual(0, database.Find(new()).Count);
    }

    [TestMethod]
    [DataRow("ADD DEFAULT TO GRAPH <urn:g>")]
    [DataRow("ADD GRAPH <urn:g> TO DEFAULT")]
    [DataRow("ADD GRAPH <urn:g> TO GRAPH <urn:h>")]
    [DataRow("COPY DEFAULT TO GRAPH <urn:g>")]
    [DataRow("COPY GRAPH <urn:g> TO DEFAULT")]
    [DataRow("COPY GRAPH <urn:g> TO GRAPH <urn:h>")]
    [DataRow("MOVE DEFAULT TO GRAPH <urn:g>")]
    [DataRow("MOVE GRAPH <urn:g> TO DEFAULT")]
    [DataRow("MOVE GRAPH <urn:g> TO GRAPH <urn:h>")]
    [DataRow("COPY GRAPH <urn:g> TO GRAPH <urn:g>")]
    [DataRow("MOVE GRAPH <urn:missing> TO GRAPH <urn:missing>")]
    [DataRow("COPY SILENT GRAPH <urn:missing> TO DEFAULT")]
    [DataRow("MOVE SILENT GRAPH <urn:missing> TO GRAPH <urn:g>")]
    [DataRow("ADD GRAPH <urn:g> TO GRAPH <urn:new>")]
    [DataRow("CLEAR GRAPH <urn:g>")]
    [DataRow("CLEAR SILENT GRAPH <urn:missing>")]
    [DataRow("PREFIX ex:<urn:> COPY ex:g TO ex:h")]
    [DataRow("BASE <https://example.test/> PREFIX ex:<urn:> ADD ex:g TO <target>")]
    public async Task Sparql11_graph_management_matches_reference_and_replays_atomically(string update)
    {
        const string seed = "INSERT DATA { <urn:s> <urn:p> 'default' . GRAPH <urn:g> { <urn:s> <urn:p> 'source' } GRAPH <urn:h> { <urn:t> <urn:p> 'destination' } }";
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(seed, "graph-management")));
        using var reference = new VDS.RDF.TripleStore();
        var processor = new VDS.RDF.Update.LeviathanUpdateProcessor(reference);
        var parser = new VDS.RDF.Parsing.SparqlUpdateParser();
        processor.ProcessCommandSet(parser.ParseFromString(seed));
        processor.ProcessCommandSet(parser.ParseFromString(update));
        var transaction = Guid.NewGuid();
        var result = await database.CommitSparqlUpdateAsync(transaction, update);
        Assert.AreEqual(result, await database.CommitSparqlUpdateAsync(transaction, update));
        string[] expected = reference.Graphs.SelectMany(graph => graph.Triples.Select(triple =>
            ((graph.Name as VDS.RDF.IUriNode)?.Uri.AbsoluteUri ?? "default") + ":" + ((VDS.RDF.ILiteralNode)triple.Object).Value)).ToArray();
        string[] actual = database.Find(new()).Select(quad =>
            ((quad.Graph as RdfIri)?.Value ?? "default") + ":" + ((RdfLiteral)quad.Object).LexicalForm).ToArray();
        CollectionAssert.AreEquivalent(expected, actual);
    }

    [TestMethod]
    [DataRow("COPY GRAPH <urn:missing> TO DEFAULT")]
    [DataRow("ADD GRAPH <urn:missing> TO GRAPH <urn:g>")]
    [DataRow("MOVE GRAPH <urn:missing> TO DEFAULT")]
    public async Task Sparql11_missing_graph_management_fails_without_changes(string update)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse("INSERT DATA { <urn:s> <urn:p> 1 }", "test")));
        var version = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
        Assert.AreEqual(version, database.CurrentVersion);
        Assert.AreEqual(1, database.Find(new()).Count);
    }

    [TestMethod]
    [DataRow("CLEAR GRAPH <urn:missing>", true)]
    [DataRow("DROP GRAPH <urn:missing>", true)]
    [DataRow("DROP DEFAULT; COPY DEFAULT TO GRAPH <urn:g>", false)]
    [DataRow("DROP DEFAULT; MOVE DEFAULT TO GRAPH <urn:g>", false)]
    [DataRow("DROP DEFAULT; ADD DEFAULT TO GRAPH <urn:g>", true)]
    public async Task Sparql11_empty_graph_policy_and_default_restoration_survive_restart(string update, bool retained)
    {
        string directory = CreateDirectory();
        var expected = new RdfQuad(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral("value"), new RdfIri("urn:g"));
        Guid request = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(expected)]));
            }
            var result = await database.CommitSparqlUpdateAsync(request, update);
            receipt ??= result;
            Assert.AreEqual(receipt, result);
            CollectionAssert.AreEquivalent(retained ? new[] { expected } : Array.Empty<RdfQuad>(), database.Find(new()).ToArray());
        }
    }

    [TestMethod]
    [DataRow("COPY", 2)]
    [DataRow("ADD", 2)]
    [DataRow("MOVE", 1)]
    public async Task Sparql11_graph_transfer_preserves_blank_node_identity_across_restart_and_replay(string operation, int count)
    {
        string directory = CreateDirectory();
        var node = new RdfBlankNode("shared");
        var quad = new RdfQuad(node, new RdfIri("urn:p"), node, new RdfIri("urn:g"));
        Guid request = Guid.NewGuid();
        string update = operation + " GRAPH <urn:g> TO GRAPH <urn:h>";
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(quad)]));
            }
            var result = await database.CommitSparqlUpdateAsync(request, update);
            receipt ??= result;
            Assert.AreEqual(receipt, result);
            var copies = database.Find(new());
            Assert.AreEqual(count, copies.Count);
            Assert.IsTrue(copies.All(item => item.Subject == node && item.Object == node));
            Assert.IsTrue(copies.Contains(quad with
            {
                Graph = new RdfIri("urn:h")
            }));
            Assert.AreEqual(operation != "MOVE", copies.Contains(quad));
        }
    }

    [TestMethod]
    public async Task Sparql11_graph_copy_budget_failure_preserves_destination()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var seed = Enumerable.Range(0, 4097).Select(index => (RdfMutation)new AssertRdfQuad(new(
            new RdfIri("urn:s:" + index), new RdfIri("urn:p"), new RdfLiteral("value"),
            new RdfIri(index < 2048 ? "urn:source" : "urn:destination")))).ToArray();
        await database.CommitAsync(new(Guid.NewGuid(), seed));
        var version = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(() => database.CommitSparqlUpdateAsync(
            Guid.NewGuid(), "COPY SILENT GRAPH <urn:source> TO GRAPH <urn:destination>").AsTask());
        Assert.AreEqual(version, database.CurrentVersion);
        Assert.AreEqual(4097, database.Find(new()).Count);
    }
}
