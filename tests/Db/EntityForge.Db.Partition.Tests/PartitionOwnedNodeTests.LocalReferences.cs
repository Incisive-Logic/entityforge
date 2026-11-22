using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [EntityType("partition-node-tests", "graph-reference")]
    public sealed record GraphLinkedItem(Guid Id, string Name, Guid[] Peers);

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Partition_graph_owns_reference_replacement_and_deletion(bool distributed)
    {
        var placement = new Authority();
        var routing = distributed ? new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding : Routing;
        var otherPlacement = new Authority
        {
            Lease = placement.Lease with
            {
                Placement = placement.Lease.Placement with
                {
                    Claim = placement.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = distributed ? await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "second"), DatabaseProfileId.PropertyGraph,
            otherPlacement.Lease, routing, otherPlacement, Options, transactionAuthority: authority, timestampClock: ReadClock) : null;
        PartitionOwnedNode[] owners = second is null ? [node] : [node, second];
        using var certificate = PartitionCertificate();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint });
        var map = GraphEntityMap.For<GraphLinkedItem>().VertexLabel("linked").Identity(value => value.Id, "id")
            .Property(value => value.Name, "name").Edge(value => value.Peers, "peers")
            .Materialize(context => new(context.IdentityAs<Guid>(), context.Required<string>("Name"), context.Optional<Guid[]>("Peers") ?? []));
        var store = new HttpGraphEntityClient(http, node.Domain.Generation).EntityStore(map);
        var client = new PartitionApplicationClient(authority, routing, owners, ReadClock);
        await using var applicationService = new PartitionApplicationHttpService(client);
        await using var gateway = await StartApplicationHostAsync(applicationService, certificate);
        var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, DatabaseProfileId.PropertyGraph);
        var left = Guid.NewGuid();
        var right = Guid.NewGuid();
        var sourceProbe = store.CaptureSave(new(left, "source", []), new(EntityOperationIds.New()));
        var expectedRootOwner = routing.RouteGraphVertex(PartitionApplicationRouting.RootRead(DatabaseProfileId.PropertyGraph, sourceProbe.Export()).Identity);
        var target = store.CaptureSave(new(right, "target", []), new(EntityOperationIds.New()));
        while (distributed && routing.RouteGraphVertex(PartitionApplicationRouting.RootRead(DatabaseProfileId.PropertyGraph, target.Export()).Identity) == expectedRootOwner)
        {
            right = Guid.NewGuid();
            target = store.CaptureSave(new(right, "target", []), new(EntityOperationIds.New()));
        }
        await client.DeliverAsync(target.OperationId, target.Export());
        var source = store.CaptureSave(new(left, "source", [right]), new(EntityOperationIds.New()));
        await client.DeliverAsync(source.OperationId, source.Export());
        if (distributed)
        {
            var automatic = await remote.SaveGraphAsync(new GraphLinkedItem(left, "automatic", [right]), map);
            Assert.IsTrue(PartitionOwnerOperation.IsOwnerLocal(automatic.OperationId));
            Assert.AreEqual(1, automatic.Members.Count);
            Assert.IsNull(await authority.ReadApplicationAsync(automatic.OperationId), "Owner execution must not create a central binding.");
            var replay = await remote.ResolveAsync(automatic.OperationId);
            Assert.AreEqual(EntityOperationState.Committed, replay.State);
            // Same captured shape, different actual work: removing the edge must coordinate.
            var automaticRemoval = await remote.SaveGraphAsync(new GraphLinkedItem(left, "removed", []), map);
            Assert.IsFalse(PartitionOwnerOperation.IsOwnerLocal(automaticRemoval.OperationId));
            Assert.AreEqual(2, automaticRemoval.Members.Count);
            var exact = store.CaptureSave(new(left, "automatic", [right]), new(automatic.OperationId));
            var exactResult = await remote.DeliverAsync(automatic.OperationId, [exact.Export()]);
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, exactResult.Members.Single().Receipt.Outcome);
            Assert.AreEqual("removed", (await remote.ReadGraphAsync(left, map)).Value!.Name);
            // A forged/stale owner selection cannot use this route to recreate the edge.
            Guid stale = PartitionOwnerOperation.New(routing, expectedRootOwner);
            var attempted = store.CaptureSave(new(left, "unsafe", [right]), new(stale));
            await Assert.ThrowsExactlyAsync<PartitionApplicationRejectedException>(() =>
                remote.DeliverAsync(stale, [attempted.Export()]).AsTask());
            // Restore via automatic ordinary selection, then verify the original recipe below.
            var restored = await remote.SaveGraphAsync(new GraphLinkedItem(left, "source", [right]), map);
            Assert.IsFalse(PartitionOwnerOperation.IsOwnerLocal(restored.OperationId));
            Assert.AreEqual(2, restored.Members.Count);
        }
        var mapped = await remote.ReadGraphAsync(left, map);
        Assert.AreEqual("source", mapped.Value!.Name);
        CollectionAssert.AreEqual(new[] { right }, mapped.Value.Peers);
        var root = PartitionApplicationRouting.RootRead(DatabaseProfileId.PropertyGraph, source.Export()).Identity;
        var rootOwner = routing.RouteGraphVertex(root).Value;
        var first = await PartitionRemoteRead.ExecuteAsync(owners, routing,
            (cut, token) => cut.ReadAsync(rootOwner, new("outgoing", root, Label: "peers"), token));
        using (var document = JsonDocument.Parse(first.Value))
        {
            Assert.AreEqual(1, document.RootElement.GetArrayLength());
        }

        var sourceProjection = await PartitionRemoteRead.ExecuteAsync(owners, routing,
            (cut, token) => cut.ReadAsync(rootOwner, new("graph-source", root,
                Source: new([store.Mapping.Manifest], "linked", ["peers"])), token));
        var sourceValues = JsonSerializer.Deserialize<PartitionSourceObservation[]>(sourceProjection.Value, PartitionParticipantWire.Json)!;
        var adjacencyValue = sourceValues.Single(value => value.Query.Kind == "outgoing");
        CollectionAssert.AreEqual(first.Value, JsonSerializer.SerializeToUtf8Bytes(adjacencyValue.Value));
        Assert.AreEqual(distributed, adjacencyValue.CertifiedReference);
        var expectedEdges = adjacencyValue.Value.EnumerateArray().Select(GraphWireJson.EdgeReference)
            .Where(edge => !adjacencyValue.CertifiedReference && routing.RouteGraphEdge(edge.Id.Value).Value == rootOwner).Select(edge => edge.Id.Value).ToArray();
        CollectionAssert.AreEquivalent(expectedEdges, sourceValues.Where(value => value.Query.Kind == "edge").Select(value => value.Query.Identity).ToArray());
        var changed = store.CaptureSave(new(left, "changed", [right]), new(EntityOperationIds.New()));
        await client.DeliverAsync(changed.OperationId, changed.Export());
        var unchanged = await PartitionRemoteRead.ExecuteAsync(owners, routing,
            (cut, token) => cut.ReadAsync(rootOwner, new("outgoing", root, Label: "peers"), token));
        CollectionAssert.AreEqual(first.Value, unchanged.Value);
        var cleared = store.CaptureSave(new(left, "cleared", []), new(EntityOperationIds.New()));
        await client.DeliverAsync(cleared.OperationId, cleared.Export());
        var deleted = store.CaptureDelete(left, new(EntityOperationIds.New()));
        await client.DeliverAsync(deleted.OperationId, deleted.Export());
        var missing = await PartitionRemoteRead.ExecuteAsync(owners, routing,
            (cut, token) => cut.ReadAsync(rootOwner, new("vertex", root), token));
        // Mapped deletion removes this interpretation, not the shared native
        // vertex identity that may carry another mapping's state.
        using (var document = JsonDocument.Parse(missing.Value))
        {
            Assert.AreEqual(0, document.RootElement.GetProperty("labels").GetArrayLength());
            Assert.AreEqual(0, document.RootElement.GetProperty("properties").EnumerateObject().Count());
        }
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, (await client.DeliverAsync(deleted.OperationId, deleted.Export())).Members[0].Receipt.Outcome);
    }
}
