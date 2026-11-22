using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public async Task Sparql_large_values_preserve_bags_distinct_and_update_results_after_restart()
    {
        string directory = CreateDirectory();
        var random = new Random(91437);
        byte[] bytes = new byte[64 * 1024];
        random.NextBytes(bytes);
        string text = Convert.ToHexString(bytes);
        var original = new RdfLiteral(text);
        var updated = new RdfLiteral(text + "-updated");
        var quads = Enumerable.Range(0, 3).Select(index => new RdfQuad(new RdfIri("urn:large:" + index),
            new("urn:payload"), original, new RdfIri("urn:source"))).ToArray();
        await using (var database = await OpenAsync(directory))
        {
            await database.BulkLoadAsync(Guid.NewGuid(), quads);
            var bag = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?o { GRAPH <urn:source> { ?s <urn:payload> ?o } }");
            Assert.AreEqual(3, bag.Bindings.Count);
            Assert.IsTrue(bag.Bindings.All(row => row["o"] == original));
            var distinct = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT DISTINCT ?o { GRAPH <urn:source> { ?s <urn:payload> ?o } }");
            Assert.AreEqual(original, distinct.Bindings.Single()["o"]);
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), """
                DELETE { GRAPH <urn:source> { ?s <urn:payload> ?o } }
                INSERT { GRAPH <urn:target> { ?s <urn:payload> ?next } }
                WHERE { GRAPH <urn:source> { ?s <urn:payload> ?o }
                        BIND(CONCAT(STR(?o), "-updated") AS ?next) }
                """);
            await Verify(database);
            await database.CreateCheckpointAsync();
        }
        await using var reopened = await OpenAsync(directory);
        await Verify(reopened);

        async Task Verify(IRdfDatabase database)
        {
            var empty = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?o { GRAPH <urn:source> { ?s <urn:payload> ?o } }");
            Assert.AreEqual(0, empty.Bindings.Count);
            var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(
                "SELECT ?s ?o { GRAPH <urn:target> { ?s <urn:payload> ?o } } ORDER BY ?s");
            Assert.AreEqual(3, result.Bindings.Count);
            for (int index = 0; index < result.Bindings.Count; index++)
            {
                Assert.AreEqual(quads[index].Subject, result.Bindings[index]["s"]);
                Assert.AreEqual(updated, result.Bindings[index]["o"]);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Large_RDF_terms_survive_reindex_snapshots_checkpoint_and_restart(bool bulk)
    {
        string directory = CreateDirectory();
        var random = new Random(17431);
        var quads = Enumerable.Range(0, 8).Select(index =>
        {
            byte[] payload = new byte[index % 2 == 0 ? 32 * 1024 : 128 * 1024];
            random.NextBytes(payload);
            string text = Convert.ToHexString(payload);
            return new RdfQuad(new RdfIri("urn:large:" + index), new("urn:payload"),
                index % 3 == 0 ? new RdfIri("urn:object:" + text) : new RdfLiteral(text),
                index % 2 == 0 ? null : new RdfIri("urn:named"));
        }).ToArray();
        await using (var database = await OpenAsync(directory))
        {
            var version = bulk
                ? await database.BulkLoadAsync(Guid.NewGuid(), quads)
                : await database.CommitAsync(new(Guid.NewGuid(), quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray()));
            using var pinned = await database.AcquireSnapshotAsync(version);
            foreach (var quad in quads)
            {
                Assert.AreEqual(quad, database.Find(new(Object: quad.Object)).Single());
            }
            await database.ReindexAsync();
            await database.CommitAsync(new(Guid.NewGuid(), [new RetractRdfQuad(quads[0])]));
            Assert.AreEqual(0, database.Find(new(Subject: quads[0].Subject)).Count);
            Assert.AreEqual(quads[0], database.Find(new(Subject: quads[0].Subject), pinned).Single());
            await Assert.ThrowsExactlyAsync<RdfSnapshotRetentionException>(async () => await database.CreateCheckpointAsync());
            pinned.Dispose();
            await database.CreateCheckpointAsync();
        }
        await using (var reopened = await OpenAsync(directory))
        {
            CollectionAssert.AreEquivalent(quads[1..], reopened.Find(new(Predicate: new("urn:payload"))).ToArray());
            await reopened.ReindexAsync();
            await reopened.CreateCheckpointAsync();
        }
        await using var final = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(quads[1..], final.Find(new(Predicate: new("urn:payload"))).ToArray());
    }
}
