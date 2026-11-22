using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Rdf_load_materialization_obeys_preceding_failure(bool silent)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapGet("/graph", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("<urn:loaded> <urn:p> <urn:value> .", "text/turtle");
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid();
        var owners = routing.Shards.Select(shard => new PartitionExecutionDomain(generation, shard, routing.Fingerprint, DatabaseProfileId.Rdf, Guid.NewGuid())).ToArray();
        var captured = new PartitionQueryUpdate.Evidence(new(owners.Select(owner => new DatabasePartitionPosition(owner.Identity, new(0))).ToArray()),
            owners.Select(owner => new PartitionQueryUpdate.Fact(owner.Identity, JsonSerializer.SerializeToUtf8Bytes(
                new PartitionQueryRelation(Quads: []), PartitionQueryJson.Options))).ToArray());
        var request = new PartitionQueryWrite(generation, DatabaseProfileId.Rdf, EntityOperationIds.New(), "sparql",
            "INSERT DATA { <urn:marker> <urn:p> <urn:value> }; COPY " + (silent ? "SILENT " : "") +
            $"GRAPH <urn:missing> TO DEFAULT; LOAD <{source}>");
        if (silent)
        {
            var result = await PartitionQueryUpdate.MaterializeAsync(request, captured, routing, default,
                new([source], allowPrivateNetwork: true));
            CollectionAssert.AreEquivalent(new RdfMutation[]
            {
                new AssertRdfQuad(new(new RdfIri("urn:marker"), new("urn:p"), new RdfIri("urn:value"))),
                new AssertRdfQuad(new(new RdfIri("urn:loaded"), new("urn:p"), new RdfIri("urn:value")))
            }, result.RdfMutations!);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PartitionQueryUpdate.MaterializeAsync(request, captured, routing, default,
                new([source], allowPrivateNetwork: true)).AsTask());
        }
        Assert.AreEqual(silent ? 1 : 0, fetched);
        await server.StopAsync();
    }

    [TestMethod]
    public async Task Rdf_correlated_SERVICE_subqueries_use_coordinator_bindings_and_durable_update_replay()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", async (HttpContext context) =>
        {
            Interlocked.Increment(ref fetched);
            using var reader = new StreamReader(context.Request.Body);
            string query = await reader.ReadToEndAsync(context.RequestAborted);
            var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(
                [new(new RdfIri("urn:r"), new RdfIri("urn:remote"), new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer"))], query, default);
            return Results.Json(new
            {
                head = new
                {
                    vars = new[] { "remote" }
                },
                results = new
                {
                    bindings = result.Bindings.Select(row => row.ToDictionary(pair => pair.Key,
                        pair => new { type = "uri", value = ((RdfIri)pair.Value).Value }))
                }
            }, contentType: "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await WithProfileFragments(DatabaseProfileId.Rdf, async (_, candidate, _, _, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { <urn:a> <urn:local> 1 . <urn:b> <urn:local> 2 }"), default);
            string filter = $"FILTER EXISTS {{ SERVICE <{endpoint}> {{ {{ SELECT ?remote {{ ?remote <urn:remote> ?n }} LIMIT 1 }} }} }}";
            var result = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql",
                $"SELECT ?s WHERE {{ ?s <urn:local> ?n {filter} }}"), default);
            Assert.AreEqual(1, result.Value.GetProperty("bindings").GetArrayLength());
            StringAssert.Contains(result.Value.GetRawText(), "urn:a");
            Assert.AreEqual(2, fetched);
            var update = new PartitionQueryWrite(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                $"INSERT {{ ?s <urn:selected> ?n }} WHERE {{ ?s <urn:local> ?n {filter} }}");
            await candidate.UpdateQueryAsync(update, default);
            Assert.AreEqual(4, fetched);
            await candidate.UpdateQueryAsync(update, default);
            Assert.AreEqual(4, fetched);
            var copied = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql",
                "SELECT ?s { ?s <urn:selected> ?n }"), default);
            Assert.AreEqual(1, copied.Value.GetProperty("bindings").GetArrayLength());
            StringAssert.Contains(copied.Value.GetRawText(), "urn:a");
        }, rdfHttpResources: new([endpoint], allowPrivateNetwork: true));
        await server.StopAsync();
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Rdf_SERVICE_executes_once_at_the_coordinator_and_joins_owned_facts(bool variable, bool xml)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return xml ? Results.Text("""
                <sparql xmlns="http://www.w3.org/2005/sparql-results#"><head><variable name="s"/><variable name="v"/></head><results>
                <result><binding name="s"><uri>urn:a</uri></binding><binding name="v"><literal>remote</literal></binding></result>
                </results></sparql>
                """, "application/sparql-results+xml") : Results.Text("""{"head":{"vars":["s","v"]},"results":{"bindings":[{"s":{"type":"uri","value":"urn:a"},"v":{"type":"literal","value":"remote"}}]}}""", "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        string service = variable ? $"BIND(<{endpoint}> AS ?endpoint) SERVICE ?endpoint" : $"SERVICE <{endpoint}>";
        await WithProfileFragments(DatabaseProfileId.Rdf, async (_, candidate, _, _, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { <urn:a> <urn:local> 1 . <urn:b> <urn:local> 2 }"), default);
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql",
                $"SELECT ?s ?v WHERE {{ ?s <urn:local> ?n . {service} {{ ?s <urn:remote> ?v }} }}");
            var result = await candidate.QueryAsync(request, default);
            var bindings = result.Value.GetProperty("bindings");
            Assert.AreEqual(1, bindings.GetArrayLength());
            StringAssert.Contains(bindings.GetRawText(), "remote");
            Assert.AreEqual(1, fetched);
            var update = new PartitionQueryWrite(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                $"INSERT {{ ?s <urn:copied> ?v }} WHERE {{ ?s <urn:local> ?n {service} {{ ?s <urn:remote> ?v }} }}");
            await candidate.UpdateQueryAsync(update, default);
            Assert.AreEqual(2, fetched);
            await candidate.UpdateQueryAsync(update, default);
            Assert.AreEqual(2, fetched, "A distributed update retry must use durable materialized SERVICE results.");
        }, rdfHttpResources: new([endpoint], allowPrivateNetwork: true));
        await server.StopAsync();
    }

    [TestMethod]
    public async Task Rdf_load_captures_one_document_for_all_owners_and_replays_without_fetching()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapGet("/graph", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("_:a <urn:p> 1 . _:b <urn:p> 2 .", "text/turtle");
        });
        await server.StartAsync();
        var source = new Uri(server.Urls.Single() + "/graph");
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid(), operation = EntityOperationIds.New();
        var existing = new RdfQuad(new RdfBlankNode(operation.ToString("N") + "-operation-0-load:a"),
            new("urn:keep"), new RdfLiteral("native"));
        var owners = routing.Shards.Select(shard => new PartitionExecutionDomain(generation, shard, routing.Fingerprint, DatabaseProfileId.Rdf, Guid.NewGuid())).ToArray();
        var request = new PartitionQueryWrite(generation, DatabaseProfileId.Rdf, operation, "sparql",
            $"LOAD <{source}> INTO GRAPH <urn:g>; INSERT {{ ?s <urn:copied> ?o }} WHERE {{ GRAPH <urn:g> {{ ?s <urn:p> ?o }} }}");
        var captured = new PartitionQueryUpdate.Evidence(new(owners.Select(owner => new DatabasePartitionPosition(owner.Identity, new(0))).ToArray()),
            owners.Select(owner => new PartitionQueryUpdate.Fact(owner.Identity, JsonSerializer.SerializeToUtf8Bytes(
                new PartitionQueryRelation(Quads: new[] { existing }.Where(quad =>
                    routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(quad.Subject)) == owner.Partition).ToArray()),
                PartitionQueryJson.Options))).ToArray());
        var evidence = await PartitionQueryUpdate.MaterializeAsync(request, captured, routing, default,
            new([source], allowPrivateNetwork: true));
        Assert.AreEqual(1, fetched);
        Assert.AreEqual(4, evidence.RdfMutations!.Length);
        Assert.IsTrue(evidence.RdfMutations.All(mutation => mutation.Quad.Subject != existing.Subject),
            "LOAD blank nodes must be disjoint from captured native facts before owner selection.");
        await server.StopAsync();
        var application = new PartitionApplicationBinding(operation, owners.Select(owner => owner.Identity).ToArray(), [PartitionQueryUpdate.Encode(request)],
            planningEvidence: JsonSerializer.SerializeToUtf8Bytes(evidence, PartitionParticipantWire.Json));
        var combined = new List<RdfMutation>();
        foreach (var owner in owners)
        {
            var first = await PartitionQueryUpdate.CompileAsync(application, owner, routing, default);
            var replay = await PartitionQueryUpdate.CompileAsync(application, owner, routing, default);
            CollectionAssert.AreEqual(first.Payload.ToArray(), replay.Payload.ToArray());
            combined.AddRange(RdfTransactionCodec.DecodeTransaction(operation, first.Payload.Span).Mutations);
        }
        CollectionAssert.AreEquivalent(evidence.RdfMutations, combined.ToArray());
        Assert.AreEqual(1, fetched);
    }
}
