using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Hosting;
using System.Text.Json;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Rdf_frontier_selection_and_cache_key_own_mutable_bindings()
    {
        var row = new Dictionary<string, RdfTerm> { ["s"] = new RdfIri("urn:before") };
        var frontier = new RdfBindingFrontier(1, [row]);
        var request = new PartitionQueryExchangeRequest("sparql", "SELECT ?v WHERE { <urn:start> <urn:p> ?s . ?s <urn:q> ?v }", ProfileFragments: true, RdfFrontier: frontier);
        var owned = PartitionQueryOwnedSelection.Capture(request);
        var key = new PartitionQueryWorkKey(null, frontier);
        row["s"] = new RdfIri("urn:after");
        Assert.AreEqual(new RdfIri("urn:before"), owned.Value.RdfFrontier!.Bindings[0]["s"]);
        Assert.AreEqual(key, new PartitionQueryWorkKey(null, owned.Value.RdfFrontier));
        Assert.AreNotEqual(key, new PartitionQueryWorkKey(null, frontier));
        Assert.AreNotEqual(key, new PartitionQueryWorkKey(null, owned.Value.RdfFrontier with
        {
            Pattern = 2
        }));
        Assert.AreNotEqual(key, new PartitionQueryWorkKey(null));
        var decoded = JsonSerializer.Deserialize<PartitionQueryExchangeRequest>(JsonSerializer.Serialize(owned.Value, PartitionQueryJson.Options), PartitionQueryJson.Options)!;
        Assert.AreEqual(key, new PartitionQueryWorkKey(null, decoded.RdfFrontier));
        var interpretation = new PartitionQueryInterpretation(owned);
        interpretation.Require(PartitionQueryOwnedSelection.Capture(owned.Value with
        {
            RdfFrontier = frontier with
            {
                Pattern = 2
            }
        }));
        Assert.ThrowsExactly<InvalidDataException>(() => interpretation.Require(PartitionQueryOwnedSelection.Capture(owned.Value with { Statement = "SELECT ?s WHERE { ?s ?p ?o }" })));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rdf_frontier_cross_owner_paging_matches_global_bag_and_releases_jobs(bool commands)
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (control, candidate, _, _, _) =>
        {
            const int count = PartitionQueryLimits.ExchangePageFacts + 1;
            const int insertBatchSize = 16;
            foreach (var batch in Enumerable.Range(0, count).Chunk(insertBatchSize))
            {
                string facts = string.Join(" ", batch.Select(i =>
                    $"<urn:start> <urn:peer> <urn:branch:{i}> . <urn:branch:{i}> <urn:peer> <urn:end:{i}> . <urn:end:{i}> <urn:value> \"same\" ."));
                await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql", "INSERT DATA { " + facts + " }"), default);
            }
            const string query = "SELECT ?v WHERE { ?end <urn:value> ?v . <urn:start> <urn:peer> ?mid . ?mid <urn:peer> ?end }";
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql", query);
            var expected = await control.QueryAsync(request, default);
            var actual = await candidate.QueryAsync(request, default);
            Assert.AreEqual(Canonical(expected.Value), Canonical(actual.Value));
            Assert.AreEqual(count, actual.Value.GetProperty("bindings").GetArrayLength());
            Assert.AreEqual("rdf-binding-frontier", actual.Statistics!.Strategy);
            Assert.IsTrue(actual.Statistics.ExchangePages > 6, "The first stage exceeds a single owner page.");
            Assert.AreEqual(3 * count, actual.Statistics.InputFacts);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await candidate.QueryAsync(request, cancelled.Token));
            Assert.AreEqual(count, (await candidate.QueryAsync(request, default)).Value.GetProperty("bindings").GetArrayLength());
        }, commands: commands);
    }
}
