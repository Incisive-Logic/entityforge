using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Mapped_frontier_batches_owners_and_joins_failure_before_returning(bool fail)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var placement = new Authority();
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var other = new Authority
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
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "frontier-authority"));
        await using var a = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "frontier-a"), DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var b = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "frontier-b"), DatabaseProfileId.PropertyGraph,
            other.Lease, routing, other, Options, transactionAuthority: authority, timestampClock: ReadClock);
        var writer = new PartitionApplicationClient(authority, routing, [a, b], ReadClock);
        using var http = new HttpClient();
        var map = GraphEntityMap.For<GraphLinkedItem>().VertexLabel("linked").Identity(value => value.Id, "id")
            .Property(value => value.Name, "name").Edge(value => value.Peers, "peers")
            .Materialize(context => new(context.IdentityAs<Guid>(), context.Required<string>("Name"), context.Optional<Guid[]>("Peers") ?? []));
        var store = new HttpGraphEntityClient(http, a.Domain.Generation).EntityStore(map);
        GraphEntityRequest Capture(Guid id, Guid[] peers) => store.CaptureSave(new(id, "name", peers), new(EntityOperationIds.New()));
        Guid Child(string owner)
        {
            while (true)
            {
                Guid id = Guid.NewGuid();
                if (routing.RouteGraphVertex(PartitionApplicationRouting.RootRead(DatabaseProfileId.PropertyGraph, Capture(id, []).Export()).Identity).Value == owner)
                {
                    return id;
                }
            }
        }
        var children = new[] { Child("a"), Child("b") };
        foreach (var id in children)
        {
            var child = Capture(id, []);
            await writer.DeliverAsync(child.OperationId, child.Export(), deadline.Token);
        }
        var root = Capture(Guid.NewGuid(), children);
        await writer.DeliverAsync(root.OperationId, root.Export(), deadline.Token);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var readers = new[] { new PhaseParticipant(a), new PhaseParticipant(b) };
        foreach (var owner in readers)
        {
            owner.BeforeRead = async (query, token) =>
            {
                if (query.Projection?.Batch?.Queries.All(item => item.Kind == "graph-source") != true)
                {
                    return;
                }

                if (Interlocked.Increment(ref entered) == readers.Length)
                {
                    both.TrySetResult();
                }

                await both.Task.WaitAsync(token);
                if (fail && owner.Domain.Partition.Value == "a")
                {
                    faulted.TrySetResult();
                    throw new IOException("frontier owner failed");
                }
                await release.Task.WaitAsync(token);
            };
        }

        var reader = new PartitionApplicationClient(authority, routing, readers, ReadClock);
        var identity = PartitionApplicationRouting.RootRead(DatabaseProfileId.PropertyGraph, root.Export()).Identity;
        var recipe = new EntityReadRecipe(Guid.NewGuid(), JsonSerializer.SerializeToElement(identity),
            [new("vertex", "linked", null, [new("peers", 1)]), new("vertex", "linked", null, [new("peers", -1)])]);
        var reading = reader.ReadMappedAsync(new(a.Domain.Generation, DatabaseProfileId.PropertyGraph, [store.Mapping.Manifest], recipe), deadline.Token).AsTask();
        try
        {
            await both.Task.WaitAsync(deadline.Token);
            if (fail)
            {
                await faulted.Task.WaitAsync(deadline.Token);
            }

            Assert.IsFalse(reading.IsCompleted, "A sibling owner still owns work at the retained cut.");
            release.TrySetResult();
            if (fail)
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => reading);
            }
            else
            {
                Assert.AreEqual(3, (await reading).Observations.Count(item => item.Query.Kind == "vertex"));
            }

            Assert.AreEqual(2, entered);
        }
        finally
        {
            release.TrySetResult();
            await reader.CloseReadEpochAsync();
        }
    }

    [TestMethod]
    public void Projection_transport_owns_decoded_values_and_bounds_final_encoding()
    {
        byte[] input = "{\"name\":\"owned\"}"u8.ToArray();
        var value = PartitionProjectionValue.Decode(input);
        Array.Fill(input, (byte)0);
        var nested = new PartitionProjectionValue(new[] { value, new PartitionProjectionValue(true) });
        using var encoded = JsonDocument.Parse(nested.Encode());
        Assert.AreEqual("owned", encoded.RootElement[0].GetProperty("name").GetString());
        Assert.IsTrue(encoded.RootElement[1].GetBoolean());
        var empty = JsonSerializer.Deserialize<PartitionParticipantWire.ReadResult>("{\"value\":null}", PartitionParticipantWire.Json)!;
        Assert.AreEqual(JsonValueKind.Null, empty.Value.Element.ValueKind);
        Assert.AreEqual(nested.Encode().Length, PartitionProjectionValue.Measure(new[] { value, new PartitionProjectionValue(true) }, PartitionParticipantWire.MaximumResponseBytes));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new PartitionProjectionValue(new string('x', PartitionParticipantWire.MaximumResponseBytes)).Encode());
        foreach (object large in new object[] { new string('x', 100_000), new string('<', 100_000), Enumerable.Range(0, 10_000).ToArray() })
        {
            byte[] owned = new PartitionProjectionValue(large).Encode();
            Assert.AreEqual(owned.Length, PartitionProjectionValue.Measure(large, owned.Length));
            CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(large, PartitionParticipantWire.Json), owned);
            Assert.ThrowsExactly<InvalidOperationException>(() => PartitionProjectionValue.Measure(large, owned.Length - 1));
            _ = new PartitionProjectionValue(new string('z', 200_000)).Encode();
            CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(large, PartitionParticipantWire.Json), owned,
                "Returning pooled encoding segments must not alter a previously returned response.");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Certified_source_reference_survives_checkpoint_reopen_and_historical_delete(bool checkpoint)
    {
        var placement = new Authority();
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid source;
        do
        {
            source = Guid.NewGuid();
        } while (routing.RouteGraphVertex(source).Value != "a");
        Guid edge;
        do
        {
            edge = Guid.NewGuid();
        } while (routing.RouteGraphEdge(edge).Value != "b");
        var target = Guid.NewGuid();
        string path = Path.Combine(_root, "reference-projection");
        var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, timestampClock: ReadClock);
        var role = GraphEdgeStorageRole.OutgoingAdjacency | GraphEdgeStorageRole.ReferenceProjection;
        async Task Commit(PartitionOwnedNode owner, params GraphMutation[] mutations)
        {
            Guid id = Guid.NewGuid();
            await owner.Authority.ExecuteAsync(async () =>
            {
                await owner.Kernel.CommitAsync(id, GraphTransactionCodec.Encode(new(id, mutations)));
                return true;
            });
        }
        await using (node)
        {
            await Commit(node, new CreateVertex(new(source), ["source"]),
                new CreateEdgeReplica(new(edge), new(source), new(target), "peer", role));
            if (checkpoint)
            {
                await node.Authority.ExecuteAsync(async () =>
                {
                    await node.Kernel.CreateCheckpointAsync();
                    return true;
                });
            }
        }
        await using var reopened = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, timestampClock: ReadClock);
        var query = new PartitionReadQuery("graph-source", source, Source: new([], "source", ["peer"]));
        var cut = await reopened.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        try
        {
            var before = JsonSerializer.Deserialize<PartitionSourceObservation[]>(await reopened.ReadCutAsync(cut, query), PartitionParticipantWire.Json)!;
            Assert.IsTrue(before.Single(item => item.Query.Kind == "outgoing").CertifiedReference);
            Assert.IsFalse(before.Any(item => item.Query.Kind == "edge"), "A remote authoritative edge lookup is unnecessary for certified references.");
            await Assert.ThrowsExactlyAsync<EntityForgeDbCommitException>(() => Commit(reopened,
                new DeleteEdgeReplica(new(edge), new(source), new(target), "peer", (GraphEdgeStorageRole)34)));
            await Commit(reopened, new DeleteEdgeReplica(new(edge), new(source), new(target), "peer", GraphEdgeStorageRole.OutgoingAdjacency));
            var retained = JsonSerializer.Deserialize<PartitionSourceObservation[]>(await reopened.ReadCutAsync(cut, query), PartitionParticipantWire.Json)!;
            Assert.AreEqual(1, retained.Single(item => item.Query.Kind == "outgoing").Value.GetArrayLength());
        }
        finally
        {
            await reopened.CompleteCutAsync(cut);
        }
        var current = await reopened.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        try
        {
            var after = JsonSerializer.Deserialize<PartitionSourceObservation[]>(await reopened.ReadCutAsync(current, query), PartitionParticipantWire.Json)!;
            Assert.AreEqual(0, after.Single(item => item.Query.Kind == "outgoing").Value.GetArrayLength());
        }
        finally
        {
            await reopened.CompleteCutAsync(current);
        }
        await Commit(reopened, new CreateEdgeReplica(new(edge), new(source), new(target), "peer", GraphEdgeStorageRole.OutgoingAdjacency));
        var raw = await reopened.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        try
        {
            var fallback = JsonSerializer.Deserialize<PartitionSourceObservation[]>(await reopened.ReadCutAsync(raw, query), PartitionParticipantWire.Json)!;
            Assert.IsFalse(fallback.Single(item => item.Query.Kind == "outgoing").CertifiedReference,
                "An older/raw replica must not acquire certification merely by being read.");
        }
        finally
        {
            await reopened.CompleteCutAsync(raw);
        }
        await Commit(reopened,
            new DeleteEdgeReplica(new(edge), new(source), new(target), "peer", GraphEdgeStorageRole.OutgoingAdjacency),
            new CreateEdgeReplica(new(edge), new(source), new(target), "peer", role));
        await reopened.Authority.ExecuteAsync(async () =>
        {
            await reopened.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            return true;
        });
        string imagePath = Path.Combine(_root, "reference-image");
        var image = await reopened.SealAndExportAsync(new ShardTransferId(Guid.NewGuid()), imagePath);
        await using var transfer = new PartitionOwnedTransfer(imagePath, Path.Combine(_root, "reference-destination"),
            image, routing, placement, new("destination"), new("https://destination.test"), Options, timestampClock: ReadClock);
        placement.LoseNextCutoverReply = true;
        await Assert.ThrowsExactlyAsync<IOException>(async () => await transfer.CompleteAsync());
        var destination = await transfer.CompleteAsync();
        var transferred = await destination.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        try
        {
            var restored = JsonSerializer.Deserialize<PartitionSourceObservation[]>(await destination.ReadCutAsync(transferred, query), PartitionParticipantWire.Json)!;
            Assert.IsTrue(restored.Single(item => item.Query.Kind == "outgoing").CertifiedReference);
            Assert.AreEqual(1, restored.Single(item => item.Query.Kind == "outgoing").Value.GetArrayLength());
        }
        finally
        {
            await destination.CompleteCutAsync(transferred);
        }
    }
}
