using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Initial_projection_is_private_until_all_validations_and_drains_failure(bool fail)
    {
        var (routing, a, b) = SnapshotParticipants();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = Batch([new("vertex", Guid.NewGuid())]);
        int reads = 0;
        bool projected = false;
        a.Reading = (request, _) =>
        {
            Assert.AreEqual("validate-project", request.Kind);
            Assert.AreSame(query, request.Projection);
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
            return ValueTask.FromResult(JsonSerializer.SerializeToUtf8Bytes(new PartitionValidatedRead(true,
                new(JsonSerializer.SerializeToElement(new[] { 17 }))), PartitionParticipantWire.Json));
        };
        b.Validating = async token =>
        {
            await release.Task.WaitAsync(token);
            if (fail)
            {
                throw new IOException("lost validation");
            }

            return true;
        };
        var task = PartitionRemoteRead.ExecuteAsync([a, b], routing, async (cut, token) =>
        {
            projected = true;
            var value = JsonSerializer.Deserialize<int[]>(await cut.ReadAsync("a", query, token))!;
            Assert.AreEqual(17, value.Single());
            return true;
        }, timeout.Token, useSnapshots: true, initial: new Dictionary<string, PartitionReadQuery> { ["a"] = query }).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        Assert.IsFalse(projected);
        Assert.IsFalse(task.IsCompleted);
        release.SetResult();
        if (fail)
        {
            await Assert.ThrowsExactlyAsync<AggregateException>(() => task);
            Assert.IsFalse(projected);
        }
        else
        {
            Assert.IsTrue((await task).Value);
        }

        Assert.AreEqual(1, reads);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public void Read_only_projection_extensions_preserve_ordinary_planning_fact_bytes()
    {
        Guid id = Guid.NewGuid();
        byte[] value = "null"u8.ToArray();
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Owner = "a",
            Query = new
            {
                Kind = "vertex",
                Identity = id,
                Subject = (string?)null,
                Label = (string?)null,
                Batch = (PartitionReadBatch?)null
            },
            Value = value
        }, PartitionParticipantWire.Json);
        byte[] current = JsonSerializer.SerializeToUtf8Bytes(new PartitionGraphPlanning.Fact("a", new("vertex", id), value), PartitionParticipantWire.Json);
        CollectionAssert.AreEqual(original, current);
    }

    [TestMethod]
    public async Task Validated_null_fallback_is_a_result_not_a_failed_validation()
    {
        var (routing, a, b) = SnapshotParticipants();
        var query = new PartitionReadQuery("graph-source", Guid.NewGuid(), Source: new([], null, []));
        a.Reading = (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToUtf8Bytes(
            new PartitionValidatedRead(true, new(JsonSerializer.SerializeToElement<object?>(null))), PartitionParticipantWire.Json));
        var result = await PartitionRemoteRead.ExecuteAsync([a, b], routing,
            (cut, token) => cut.ReadAsync("a", query, token), useSnapshots: true,
            initial: new Dictionary<string, PartitionReadQuery> { ["a"] = query });
        Assert.AreEqual(JsonValueKind.Null, JsonSerializer.Deserialize<JsonElement>(result.Value).ValueKind);
        Assert.AreEqual(0, a.Abandoned + b.Abandoned);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public async Task Invalid_initial_projection_is_discarded_and_cleanup_failure_is_preserved()
    {
        var (routing, a, b) = SnapshotParticipants();
        var query = Batch([new("vertex", Guid.NewGuid())]);
        a.Reading = (_, _) => ValueTask.FromResult(JsonSerializer.SerializeToUtf8Bytes(
            new PartitionValidatedRead(false, null), PartitionParticipantWire.Json));
        b.Abandoning = () => ValueTask.FromException(new IOException("cleanup failed"));
        bool projected = false;
        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => PartitionRemoteRead.ExecuteAsync([a, b], routing,
            (_, _) =>
            {
                projected = true;
                return ValueTask.FromResult(true);
            }, useSnapshots: true,
            initial: new Dictionary<string, PartitionReadQuery> { ["a"] = query }).AsTask());
        Assert.IsFalse(projected);
        Assert.IsTrue(failure.Flatten().InnerExceptions.Any(e => e.Message == "cleanup failed"));
        Assert.AreEqual(2, a.Abandoned + b.Abandoned);
    }

    [TestMethod]
    public async Task Independent_projection_overlaps_owners_and_preserves_order_and_duplicates()
    {
        var (routing, a, b) = SnapshotParticipants();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        var qa = new PartitionReadQuery("vertex", Guid.NewGuid());
        var qb = new PartitionReadQuery("vertex", Guid.NewGuid());
        async ValueTask<byte[]> Read(PartitionReadQuery request, CancellationToken token)
        {
            if (request.Kind == "validate-snapshot")
            {
                return "true"u8.ToArray();
            }

            Assert.AreEqual("batch", request.Kind);
            if (Interlocked.Increment(ref entered) == 2)
            {
                both.TrySetResult();
            }

            await both.Task.WaitAsync(token);
            return JsonSerializer.SerializeToUtf8Bytes(request.Batch!.Queries.Select(q => q == qa ? 11 : 22));
        }
        a.Reading = Read;
        b.Reading = Read;
        var projection = new PartitionReadProjection([("b", qb), ("a", qa), ("b", qb)]);
        var result = await PartitionRemoteRead.ExecuteAsync([a, b], routing,
            (cut, token) => projection.ExecuteAsync(cut, token), timeout.Token, useSnapshots: true);
        CollectionAssert.AreEqual(new[] { 22, 11, 22 }, result.Value.Select(v => JsonSerializer.Deserialize<int>(v)).ToArray());
        Assert.AreEqual(2, entered);
        Assert.AreEqual(0, a.Active.Count + b.Active.Count);
    }

    [TestMethod]
    public async Task Source_capacity_fallback_preserves_the_pin_and_ordinary_adjacency()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "source-fallback"),
            DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        var from = GraphVertexId.New();
        var to = GraphVertexId.New();
        var mutations = new List<GraphMutation> { new CreateVertex(from, []), new CreateVertex(to, []) };
        for (int index = 0; index < EntityReadRecipe.MaximumObservations; index++)
        {
            mutations.Add(new CreateEdge(GraphEdgeId.New(), from, to, "peer"));
        }

        Guid operation = Guid.NewGuid();
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations)));
            return true;
        });
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var source = await node.ReadCutAsync(cut, new("graph-source", from.Value, Source: new([], null, ["peer"])));
        Assert.AreEqual(JsonValueKind.Null, JsonSerializer.Deserialize<JsonElement>(source).ValueKind);
        using var adjacency = JsonDocument.Parse(await node.ReadCutAsync(cut, new("outgoing", from.Value, Label: "peer")));
        Assert.AreEqual(EntityReadRecipe.MaximumObservations, adjacency.RootElement.GetArrayLength());
        await node.CompleteCutAsync(cut);
    }

    [TestMethod]
    public async Task Validation_projection_rejects_stale_cut_without_running_the_projection()
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "validation-source"),
            DatabaseProfileId.PropertyGraph, authority.Lease, Routing, authority, Options);
        var old = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        Guid operation = Guid.NewGuid();
        byte[] payload = NativePayload(DatabaseProfileId.PropertyGraph, Routing, new("a"), operation);
        Guid vertex = ((CreateVertex)GraphTransactionCodec.Decode(operation, payload.AsSpan()).Mutations[0]).Vertex.Value;
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.CommitAsync(operation, payload);
            return true;
        });
        // Invalid leaf would throw if speculative projection ran after failed validation.
        var rejected = PartitionParticipantBinary.Decode<PartitionValidatedRead>(await node.ReadCutAsync(old,
            new("validate-project", SnapshotPrepared: [], Projection: Batch([new("unsupported")]))))!;
        Assert.IsFalse(rejected.Valid);
        Assert.IsNull(rejected.Projection);
        var source = new PartitionReadQuery("graph-source", vertex, Source: new([], null, ["peer"]));
        var historical = JsonSerializer.Deserialize<PartitionSourceObservation[]>(await node.ReadCutAsync(old, source), PartitionParticipantWire.Json)!;
        Assert.AreEqual(1, historical.Length);
        Assert.AreEqual(JsonValueKind.Null, historical[0].Value.ValueKind);
        await node.CompleteCutAsync(old);
        var current = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var accepted = PartitionParticipantBinary.Decode<PartitionValidatedRead>(await node.ReadCutAsync(current,
            new("validate-project", SnapshotPrepared: [], Projection: source)))!;
        Assert.IsTrue(accepted.Valid);
        Assert.AreEqual(2, accepted.Projection!.Value.Element.GetArrayLength());
        var mismatch = source with
        {
            Source = new([], "missing-label", ["peer"])
        };
        Assert.AreEqual(1, JsonSerializer.Deserialize<PartitionSourceObservation[]>(await node.ReadCutAsync(current, mismatch), PartitionParticipantWire.Json)!.Length);
        var sourceBatch = JsonSerializer.Deserialize<JsonElement>(await node.ReadCutAsync(current, Batch([source])));
        Assert.AreEqual(1, sourceBatch.GetArrayLength());
        Assert.AreEqual(2, sourceBatch[0].GetArrayLength());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadCutAsync(current, Batch([Batch([source])])).AsTask());
        await node.CompleteCutAsync(current);
    }
}
