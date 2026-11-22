using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityOperationTransportTests
{
    [EntityType("http-client-tests", "optional")]
    public sealed record OptionalItem(string Id, string Name, string? Note, int? Count);

    [EntityType("http-client-tests", "aggregate")]
    public sealed class HttpAggregate
    {
        public string Id { get; set; } = "";

        public List<string> Tags { get; set; } = [];

        [EntityOwned] public List<HttpChild> Children { get; set; } = [];

        [EntityReference] public List<Item> Peers { get; set; } = [];

        [EntityReference]
        public HttpAggregate? Parent
        {
            get; set;
        }
    }

    [EntityType("http-client-tests", "child")]
    public sealed record HttpChild(string Id, string Name);

    private sealed class OmitSecondObservation : DelegatingHandler
    {
        internal int Reads, Releases;

        internal OmitSecondObservation() : base(new HttpClientHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/release"))
            {
                Releases++;
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri.AbsolutePath.EndsWith("/complete"))
            {
                Reads++;
                var body = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                body["observations"]!.AsArray().RemoveAt(1);
                response.Content.Dispose();
                response.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            }
            return response;
        }
    }

    private static async Task ExerciseApplicationClients(EntityForgeDbRuntime runtime, HttpClient http, DatabaseProfileId profile, Guid incarnation)
    {
        Guid operation = Guid.NewGuid();
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            IGraphEntityClient client = new HttpGraphEntityClient(http, incarnation);
            var store = client.EntityStore<Item>();
            var first = await store.SaveAsync(new("http", "first"), operation);
            Assert.AreEqual(new Item("http", "first"), await store.FindAsync("http"));
            await using var prepared = await store.PrepareSaveAsync(new("http", "prepared"), Guid.NewGuid());
            await client.DeliverAsync(prepared);
            Assert.AreEqual(new Item("http", "prepared"), await runtime.GraphDatabase.EntityStore<Item>().FindAsync("http"));
            await using var batch = store.CreateWriteBatch().Save(new("batch1", "one")).Save(new("batch2", "two"));
            await batch.CommitAsync();
            Assert.AreEqual("two", (await store.FindAsync("batch2"))!.Name);
            await store.DeleteAsync("batch1");
            Assert.IsNull(await store.FindAsync("batch1"));
            Assert.AreEqual(first.Version, (await client.ResolveAsync(operation)).Receipt!.Version);
            var aborted = Guid.NewGuid();
            Assert.AreEqual(EntityOperationState.NotFound, (await client.AbortAsync(aborted)).State);
            await store.SaveAsync(new("abort", "x"), aborted);
            Assert.AreEqual(EntityOperationState.Committed, (await client.AbortAsync(aborted)).State);
            var nativeId = GraphVertexId.New();
            var native = GraphChangeSession.Incremental(new(Guid.NewGuid(), [new CreateVertex(nativeId, ["Native"])]));
            await client.ApplyAsync(native);
            await client.ApplyAsync(native);
            await client.ApplyAsync(GraphChangeSession.Replace(Guid.NewGuid(), [new(nativeId, new(nativeId, new HashSet<string> { "Replaced" }, new Dictionary<string, DatabaseValue>()))], []));
            Assert.IsTrue(runtime.GraphDatabase.GetVertex(nativeId)!.Labels.Contains("Replaced"));
        }
        else if (profile == DatabaseProfileId.Rdf)
        {
            IRdfEntityClient client = new HttpRdfEntityClient(http, incarnation);
            var store = client.EntityStore<Item>();
            var first = await store.SaveAsync(new("http", "first"), operation);
            Assert.AreEqual(new Item("http", "first"), await store.FindAsync("http"));
            await using var prepared = await store.PrepareSaveAsync(new("http", "prepared"), Guid.NewGuid());
            await client.DeliverAsync(prepared);
            Assert.AreEqual(new Item("http", "prepared"), await runtime.RdfDatabase.EntityStore<Item>().FindAsync("http"));
            await using var batch = store.CreateWriteBatch().Save(new("batch1", "one")).Save(new("batch2", "two"));
            await batch.CommitAsync();
            Assert.AreEqual("two", (await store.FindAsync("batch2"))!.Name);
            await store.DeleteAsync("batch1");
            Assert.IsNull(await store.FindAsync("batch1"));
            Assert.AreEqual(first.Version, (await client.ResolveAsync(operation)).Receipt!.Version);
            var aborted = Guid.NewGuid();
            Assert.AreEqual(EntityOperationState.NotFound, (await client.AbortAsync(aborted)).State);
            await store.SaveAsync(new("abort", "x"), aborted);
            Assert.AreEqual(EntityOperationState.Committed, (await client.AbortAsync(aborted)).State);
            var quad = new RdfQuad(new RdfIri("urn:http:native"), new("urn:p"), new RdfLiteral("before"));
            var native = RdfChangeSession.Incremental(new(Guid.NewGuid(), [new AssertRdfQuad(quad)]));
            await client.ApplyAsync(native);
            await client.ApplyAsync(native);
            await client.ApplyAsync(RdfChangeSession.Replace(Guid.NewGuid(), [new(quad.Subject, null, [])]));
            Assert.AreEqual(0, runtime.RdfDatabase.Find(new(Subject: quad.Subject)).Count);
        }
        else
        {
            ISemanticEntityClient client = new HttpSemanticEntityClient(http, incarnation);
            var optional = new OptionalItem("optional", "present", null, null);
            await client.EntityStore<OptionalItem>().SaveAsync(optional);
            Assert.AreEqual(optional, await client.EntityStore<OptionalItem>().FindAsync(optional.Id));
            var store = client.EntityStore<Item>();
            var first = await store.SaveAsync(new("http", "first"), new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow));
            Assert.AreEqual(new Item("http", "first"), await store.FindAsync("http"));
            await using var prepared = await store.PrepareSaveAsync(new("http", "prepared"), SemanticEntityWriteOperation.Create());
            await client.DeliverAsync(prepared);
            Assert.AreEqual(new Item("http", "prepared"), await runtime.Database.EntityStore<Item>().FindAsync("http"));
            var read = (await store.ReadAsync("http"))!;
            await store.SaveAsync(new("http", "guarded"), operation: SemanticEntityWriteOperation.Create(), guard: read.ContributionGuard);
            var stale = store.CaptureSave(new("http", "stale"), SemanticEntityWriteOperation.Create(), read.ContributionGuard);
            await Assert.ThrowsExceptionAsync<HttpRequestException>(async () => await client.DeliverAsync(stale));
            Assert.AreEqual(EntityOperationState.PlanBound, (await client.ResolveAsync(stale.OperationId)).State);
            Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(stale.OperationId)).State);
            await Assert.ThrowsExceptionAsync<EntityOperationAbortedException>(async () => await client.DeliverAsync(stale));
            Assert.AreEqual(first.Version, (await client.ResolveAsync(operation)).Receipt!.Version);
            var aborted = Guid.NewGuid();
            Assert.AreEqual(EntityOperationState.NotFound, (await client.AbortAsync(aborted)).State);
            await store.SaveAsync(new("abort", "x"), new SemanticEntityWriteOperation(aborted, DateTimeOffset.UtcNow));
            Assert.AreEqual(EntityOperationState.Committed, (await client.AbortAsync(aborted)).State);
            await store.DeleteAsync("http");
            Assert.IsNull(await store.FindAsync("http"));
            // Native sessions have the same operation authority, independently of mapped delivery.
            var nativeStore = runtime.Database.EntityStore<Item>();
            var nativePrepared = nativeStore.PrepareSave(new("native-http", "native"), SemanticEntityWriteOperation.Create());
            var session = SemanticEntityDeliveryCodec.Session(nativePrepared.Export());
            await runtime.Database.Operations().ApplyAsync(session);
            await client.ApplyAsync(session);
            await client.ApplyAsync(session);
            Assert.AreEqual(new Item("native-http", "native"), await store.FindAsync("native-http"));
            var aggregateStore = client.EntityStore<HttpAggregate>();
            var aggregate = new HttpAggregate
            {
                Id = "aggregate",
                Tags = ["b", "a", "b"],
                Children = [new("same", "first"), new("same", "second")],
                Peers = [new("native-http", "native"), new("native-http", "native")]
            };
            aggregate.Parent = aggregate;
            await aggregateStore.SaveAsync(aggregate);
            var loaded = (await aggregateStore.FindAsync(aggregate.Id))!;
            CollectionAssert.AreEqual(aggregate.Tags, loaded.Tags);
            CollectionAssert.AreEqual(new[] { "first", "second" }, loaded.Children.Select(c => c.Name).ToArray());
            Assert.AreSame(loaded, loaded.Parent);
            Assert.AreEqual(2, loaded.Peers.Count);
            Assert.AreSame(loaded.Peers[0], loaded.Peers[1]);
            using var handler = new OmitSecondObservation();
            using var failingHttp = new HttpClient(handler) { BaseAddress = http.BaseAddress };
            failingHttp.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
            await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
                await new HttpSemanticEntityClient(failingHttp, incarnation).EntityStore<HttpAggregate>().FindAsync(aggregate.Id));
            Assert.AreEqual(1, handler.Reads, "Missing observations must not trigger replacement reads.");
            Assert.AreEqual(0, handler.Releases, "The receiver already released the complete read.");
        }
    }
}
