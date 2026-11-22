using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("CLEAR DEFAULT", 1, new int[] { 1, 2 })]
    [DataRow("DROP DEFAULT", 1, new int[] { 1, 2 })]
    [DataRow("DROP GRAPH <urn:g>", 1, new int[] { 0, 2 })]
    [DataRow("DROP NAMED", 2, new int[] { 0 })]
    [DataRow("DROP ALL", 3, new int[] { })]
    [DataRow("DROP SILENT GRAPH <urn:absent>", 0, new int[] { 0, 1, 2 })]
    [DataRow("CLEAR NAMED", 2, new int[] { 0 })]
    [DataRow("CLEAR ALL", 3, new int[] { })]
    [DataRow("CLEAR SILENT ALL", 3, new int[] { })]
    [DataRow("DELETE WHERE { ?s <urn:p> ?o }", 1, new int[] { 1, 2 })]
    [DataRow("PREFIX ex: <urn:> DELETE WHERE { GRAPH ex:g { ?s ex:p ?o } }", 1, new int[] { 0, 2 })]
    [DataRow("DELETE WHERE { GRAPH ?g { ?s <urn:p> ?o } }", 2, new int[] { 0 })]
    [DataRow("DELETE WHERE { <urn:missing> ?p ?o }", 0, new int[] { 0, 1, 2 })]
    [DataRow("DELETE WHERE {}", 0, new int[] { 0, 1, 2 })]
    [DataRow("DELETE WHERE { ?s ?p ?o }; DELETE WHERE { ?s ?p ?o }", 1, new int[] { 1, 2 })]
    public async Task Sparql11_delete_where_preserves_graph_scope_and_replay(string update, int expected, int[] retained)
    {
        await using var database = await OpenAsync(CreateDirectory());
        RdfQuad[] quads =
        [
            new(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("default")),
            new(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g")),
            new(new RdfIri("urn:t"), new RdfIri("urn:p"), new RdfLiteral("other"), new RdfIri("urn:h"))
        ];
        await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
        Guid transaction = Guid.NewGuid();
        var result = await database.CommitSparqlUpdateAsync(transaction, update);
        Assert.AreEqual(expected, result.MutationCount);
        Assert.AreEqual(3 - expected, database.Find(new()).Count);
        RdfQuad[] expectedQuads = retained.Select(index => quads[index]).ToArray();
        CollectionAssert.AreEquivalent(expectedQuads, database.Find(new()).ToArray());
        Assert.AreEqual(result, await database.CommitSparqlUpdateAsync(transaction, update));
        Assert.AreEqual(3 - expected, database.Find(new()).Count);
        CollectionAssert.AreEquivalent(expectedQuads, database.Find(new()).ToArray());
        using var reference = new VDS.RDF.TripleStore();
        new VDS.RDF.Parsing.TriGParser().Load(reference, new StringReader("""
            { <urn:s> <urn:p> "default" }
            <urn:g> { <urn:s> <urn:p> "named" }
            <urn:h> { <urn:t> <urn:p> "other" }
            """));
        new VDS.RDF.Update.LeviathanUpdateProcessor(reference).ProcessCommandSet(
            new VDS.RDF.Parsing.SparqlUpdateParser().ParseFromString(update));
        CollectionAssert.AreEquivalent(reference.Triples.Select(triple => ((VDS.RDF.ILiteralNode)triple.Object).Value).ToArray(),
            database.Find(new()).Select(quad => ((RdfLiteral)quad.Object).LexicalForm).ToArray());
    }

    [TestMethod]
    [DataRow("DELETE WHERE { ?s ?p ?o FILTER(true) }")]
    [DataRow("DELETE WHERE { _:b ?p ?o }")]
    [DataRow("DELETE WHERE { ?s ?p ?o OPTIONAL { ?s ?p ?other } }")]
    public async Task Sparql11_delete_where_rejects_non_quad_patterns_without_commit(string update)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var version = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update));
        Assert.AreEqual(version, database.CurrentVersion);
    }
}
