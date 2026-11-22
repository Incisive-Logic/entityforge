using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [EntityType("partition-node-tests", "routing-discovery-aggregate")]
    public sealed class RoutingDiscoveryAggregate
    {
        public string Id { get; set; } = "";

        [EntityOwned]
        public PartitionSemanticTree? Tree
        {
            get; set;
        }

        [EntityReference] public List<PartitionSemanticLeaf> Peers { get; set; } = [];

        public List<string> Tags { get; set; } = [];
    }

    [TestMethod]
    public async Task Semantic_routing_discovery_matches_full_compilation_for_scalar_owned_reference_and_delete_inputs()
    {
        using var http = new HttpClient();
        var client = new HttpSemanticEntityClient(http, Guid.NewGuid());
        var operation = SemanticEntityWriteOperation.Create();
        var trees = client.EntityStore<PartitionSemanticTree>();
        var references = client.EntityStore<SemanticReferenceParent>();
        SemanticEntityRequest[] requests = [
            client.EntityStore<Item>().CaptureSave(new("scalar", "value"), operation),
            client.EntityStore<BinaryOriginal>().CaptureSave(new("binary", [1, 2, 3]), operation),
            trees.CaptureSave(new() { Id = "tree", Name = "root", Children = [new("a", "A"), new("b", "B")] }, operation),
            trees.CaptureSave(new() { Id = "empty", Name = "root" }, operation),
            references.CaptureSave(new() { Id = "ref", Name = "root", Peer = new("peer", "target") }, operation),
            references.CaptureSave(new() { Id = "null-ref", Name = "root" }, operation),
            client.EntityStore<RoutingDiscoveryAggregate>().CaptureSave(new()
            {
                Id = "nested",
                Tree = new()
                {
                    Id = "owned-tree",
                    Name = "branch",
                    Children = [new("leaf", "value")]
                },
                Peers = [new("first", "A"), new("second", "B")],
                Tags = ["one", "two"]
            }, operation),
            trees.CaptureDelete("tree", operation),
            references.CaptureDelete("ref", operation)
        ];
        foreach (var request in requests)
        {
            var restored = SemanticEntityRequest.Restore(request.Export());
            var metadata = restored._metadata;
            var plan = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application, metadata.Limits, metadata.RootBinding?.ToBinding());
            var discovery = plan.CaptureRouting(EntityValueCapture.Decode(restored._payload)!, metadata.Delete);
            var full = await PartitionSemanticPlanning.CompileOriginalAsync(restored);
            var changes = full.Session.Pages.SelectMany(page => page.Changes).ToArray();
            Assert.AreEqual(full.Session.Start.Source.SourceSystem, discovery.SourceSystem);
            Assert.AreEqual(full.Session.Start.Source.SourcePartition, discovery.SourcePartition);
            CollectionAssert.AreEqual(changes.Select(change => change.Subject).ToArray(), discovery.Subjects.ToArray());
            CollectionAssert.AreEqual(changes.SelectMany(change => change.Relationships).Select(link => link.Target).OfType<SemanticEntityIdentity>().ToArray(), discovery.Targets.ToArray());
            CollectionAssert.AreEqual(full.Metadata.Manifests.Select(m => m.Fingerprint).ToArray(), plan.Manifests.Select(m => m.Fingerprint).ToArray());
        }
        int compilations = 0;
        var previous = DistributedReadTelemetry.ObserveElapsedTicks;
        DistributedReadTelemetry.ObserveElapsedTicks = (stage, ticks) =>
        {
            previous?.Invoke(stage, ticks);
            if (stage == "partition.semantic-planning.compile-original")
            {
                compilations++;
            }
        };
        try
        {
            var representatives = await PartitionSemanticPlanning.MappingRepresentativesAsync(requests.Select(r => (ReadOnlyMemory<byte>)r.Export()).ToArray(), default);
            Assert.IsTrue(representatives.Length > 1);
            Assert.AreEqual(0, compilations, "Discovery must not invoke full compilation, including on cache misses.");
        }
        finally
        {
            DistributedReadTelemetry.ObserveElapsedTicks = previous;
        }
    }

    [TestMethod]
    public void Semantic_routing_discovery_rejects_bad_values_bounds_and_cancellation_then_remains_usable()
    {
        using var http = new HttpClient();
        var request = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<PartitionSemanticTree>()
            .CaptureSave(new()
            {
                Id = "tree",
                Name = "root",
                Children = [new("child", "value")]
            }, SemanticEntityWriteOperation.Create());
        var metadata = request._metadata;
        var plan = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application, metadata.Limits, metadata.RootBinding?.ToBinding());
        var value = (Entity)EntityValueCapture.Decode(request._payload)!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => plan.CaptureRouting(value, false, cancellation.Token));
        var values = value.Members.ToDictionary(p => p.Key, p => p.Value);
        values["unexpected-member"] = "invalid";
        Assert.Throws<ArgumentException>(() => plan.CaptureRouting(new Entity(values), false));
        var limited = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application,
            new(1, metadata.Limits.Depth, metadata.Limits.Bytes), metadata.RootBinding?.ToBinding());
        Assert.Throws<InvalidOperationException>(() => limited.CaptureRouting(value, false));
        Assert.IsTrue(plan.CaptureRouting(value, false).Subjects.Count > 1);
    }

    [TestMethod]
    public async Task Semantic_routing_discovery_does_not_allow_oversized_sessions_to_be_selected()
    {
        await WithOrdinaryTransactions(DatabaseProfileId.Semantic, async (remote, client, nodes, authority) =>
        {
            using var http = new HttpClient();
            var store = new HttpSemanticEntityClient(http, client.Generation).EntityStore<Item>();
            var original = store.CaptureSave(new("bounded", "value"), SemanticEntityWriteOperation.Create());
            var m = original._metadata;
            const int InsufficientSessionBytes = 1;
            var invalid = new SemanticEntityRequest(m.DatabaseIncarnation, m.Definition, m.Application,
                new(bytes: InsufficientSessionBytes), m.RootBinding?.ToBinding(), EntityValueCapture.Decode(original._payload)!,
                m.Operation, m.Predecessor, m.Guarded, m.Delete);
            var versions = nodes.Select(node => node.Kernel.CurrentVersion).ToArray();
            Exception? rejected = null;
            try
            {
                await client.DeliverAsync(invalid.OperationId, invalid.Export());
            }
            catch (Exception failure)
            {
                rejected = failure;
            }
            Assert.IsNotNull(rejected, "Full owner compilation must enforce the encoded session limit.");
            CollectionAssert.AreEqual(versions, nodes.Select(node => node.Kernel.CurrentVersion).ToArray());
            Assert.IsNull(await authority.ReadApplicationAsync(invalid.OperationId));
            Assert.IsNull(await authority.ReadPlanAsync(invalid.OperationId));
            var transaction = remote.CreateTransaction();
            transaction.Save(new Item("bounded", "healthy"));
            await transaction.CommitAsync();
            Assert.AreEqual("healthy", (await remote.ReadSemanticAsync<Item>("bounded")).Value!.Name);
        });
    }

}
