using System.Net;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, 2, true)]
    [DataRow(DatabaseProfileId.Rdf, 2, true)]
    [DataRow(DatabaseProfileId.Semantic, 1, true)]
    [DataRow(DatabaseProfileId.Semantic, 2, false)]
    [DataRow(DatabaseProfileId.Semantic, 2, true)]
    public async Task Default_saves_select_route_and_preserve_exact_capture_after_lost_response(
        DatabaseProfileId profile, int shards, bool owner)
    {
        Guid generation = Guid.NewGuid();
        using var handler = new AutomaticRoutingHandler(generation, profile, shards, owner);
        using var http = new HttpClient(handler);
        var client = new HttpPartitionEntityClient(http, new("https://example.test"), "token", generation, profile);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var failure = await Assert.ThrowsExactlyAsync<PartitionApplicationUncertainException>(async () =>
            {
                if (profile == DatabaseProfileId.PropertyGraph)
                {
                    await client.SaveGraphAsync(new Item("one", "value"));
                }
                else if (profile == DatabaseProfileId.Rdf)
                {
                    await client.SaveRdfAsync(new Item("one", "value"));
                }
                else
                {
                    await client.SaveSemanticAsync(new Item("one", "value"));
                }
            });
            Assert.AreEqual(owner, PartitionOwnerOperation.IsOwnerLocal(failure.Retry.OperationId));
            byte[] original = handler.Submissions.Last();
            await Assert.ThrowsExactlyAsync<PartitionApplicationUncertainException>(() => failure.Retry.DeliverAsync(client).AsTask());
            CollectionAssert.AreEqual(original, handler.Submissions.Last());
        }
        Assert.AreEqual(1, handler.Discoveries);
    }

    [TestMethod]
    public async Task Default_multi_owner_Graph_delete_retains_ordinary_route()
    {
        Guid generation = Guid.NewGuid();
        using var handler = new AutomaticRoutingHandler(generation, DatabaseProfileId.PropertyGraph, 2);
        using var http = new HttpClient(handler);
        var client = new HttpPartitionEntityClient(http, new("https://example.test"), "token", generation, DatabaseProfileId.PropertyGraph);
        var failure = await Assert.ThrowsExactlyAsync<PartitionApplicationUncertainException>(() => client.DeleteGraphAsync<Item>("one").AsTask());
        Assert.IsFalse(PartitionOwnerOperation.IsOwnerLocal(failure.Retry.OperationId));
    }

    private sealed class AutomaticRoutingHandler(Guid generation, DatabaseProfileId profile, int shards, bool eligible = true) : HttpMessageHandler
    {
        public int Discoveries;

        public List<byte[]> Submissions { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
            {
                Discoveries++;
                var routing = new StableHashDistributedShardRouter(new(new(1),
                    shards == 1 ? [new("a")] : [new("a"), new("b")])).Binding;
                var capability = new PartitionOwnerCapabilities(generation, profile, PartitionOwnerOperation.Policy,
                    routing, (int)EntityOperationIds.MaximumOwnerFutureSkew.TotalSeconds);
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(capability,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                };
            }
            if (request.RequestUri!.AbsolutePath == PartitionProtocolRoutes.SemanticProbePath)
            {
                var captured = EntityForge.Db.Semantic.SemanticEntityRequest.Restore(await request.Content!.ReadAsByteArrayAsync(token));
                var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
                    new PartitionProtocolRoutes.OwnerProbeResult(generation, routing.Fingerprint, captured.OperationId, eligible),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)))
                };
            }
            Submissions.Add(await request.Content!.ReadAsByteArrayAsync(token));
            throw new HttpRequestException("Injected lost submission response.");
        }
    }
}
