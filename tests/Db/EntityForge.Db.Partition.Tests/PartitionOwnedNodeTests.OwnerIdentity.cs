using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(409)]
    [DataRow(410)]
    [DataRow(422)]
    [DataRow(507)]
    public async Task Explicit_submission_rejection_retains_capture_without_claiming_transport_uncertainty(int status)
    {
        using var http = new HttpClient(new RejectOwnerSubmission(status));
        var client = new HttpPartitionEntityClient(http, new("https://example.test"), "token", Guid.NewGuid(), DatabaseProfileId.PropertyGraph);
        Guid operation = EntityOperationIds.New();
        var failure = await Assert.ThrowsExactlyAsync<PartitionApplicationRejectedException>(() =>
            client.SaveGraphAsync(new Item("one", "value"), operation).AsTask());
        Assert.AreEqual((System.Net.HttpStatusCode)status, failure.StatusCode);
        Assert.AreEqual(operation, PartitionApplicationRetry.Restore(failure.Retry.Export()).OperationId);
    }

    private sealed class RejectOwnerSubmission(int status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Direct_adapter_rejects_foreign_home_before_native_binding(DatabaseProfileId profile)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), profile,
            placement.Lease, routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        Guid foreign = PartitionOwnerOperation.New(routing, new("b"));
        var before = node.Kernel.CurrentVersion;
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var adapter = await node.CreateGraphExecutionAsync();
            var capture = Enumerable.Range(0, 100).Select(i => adapter.CaptureSave(new Item(i.ToString(), "x"), foreign))
                .First(value => adapter.CapturedOwner(value) == node.Domain.Partition);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adapter.ExecuteAsync(node.Domain, foreign, capture.Export()).AsTask());
        }
        else
        {
            var adapter = await node.CreateRdfExecutionAsync();
            var capture = Enumerable.Range(0, 100).Select(i => adapter.CaptureSave(new Item(i.ToString(), "x"), foreign))
                .First(value => adapter.CapturedOwner(value) == node.Domain.Partition);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adapter.ExecuteAsync(node.Domain, foreign, capture.Export()).AsTask());
        }
        Assert.AreEqual(before, node.Kernel.CurrentVersion);
    }

    [TestMethod]
    public async Task Future_owner_identity_is_rejected_without_binding()
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var adapter = await node.CreateGraphExecutionAsync();
        Guid operation = PartitionOwnerOperation.New(Routing, new("a"), DateTimeOffset.UtcNow.AddHours(1));
        var request = adapter.CaptureSave(new Item("one", "future"), operation);
        await Assert.ThrowsAsync<ArgumentException>(() => adapter.ExecuteAsync(node.Domain, operation, request.Export()).AsTask());
        Assert.AreEqual(EntityOperationState.NotFound, (await node.ResolveApplicationAsync(operation, false, default)).State);
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Owner_identity_uses_native_arbitration_for_each_eligible_profile(DatabaseProfileId profile)
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), profile,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var http = new HttpClient();
        var operation = PartitionOwnerOperation.New(Routing, new("a"));
        byte[] request = profile switch
        {
            DatabaseProfileId.PropertyGraph => new HttpGraphEntityClient(http, node.Domain.Generation).EntityStore<Item>()
                .CaptureSave(new("one", "value"), new(operation)).Export(),
            DatabaseProfileId.Rdf => new HttpRdfEntityClient(http, node.Domain.Generation).EntityStore<Item>()
                .CaptureSave(new("one", "value"), new(operation)).Export(),
            _ => new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>()
                .CaptureSave(new("one", "value"), new(operation, DateTimeOffset.UtcNow)).Export()
        };
        var client = new PartitionApplicationClient(decisions, Routing, [node], ReadClock);
        var result = await client.DeliverAsync(operation, request);
        Assert.IsNull(await decisions.ReadApplicationAsync(operation));
        var replay = await client.DeliverAsync(operation, request);
        Assert.AreEqual(result.Members[0].Receipt.Version, replay.Members[0].Receipt.Version);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members[0].Receipt.Outcome);
        Assert.AreEqual(EntityOperationState.Committed, (await client.ResolveAsync(operation, default)).State);
    }

    [TestMethod]
    public async Task Owner_identity_rejects_cross_owner_and_coordinated_retries_and_replays_after_reopen()
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var http = new HttpClient();
        var client = new HttpPartitionEntityClient(http, new("https://localhost"), "token", first.Domain.Generation, DatabaseProfileId.PropertyGraph);
        var store = new HttpGraphEntityClient(http, first.Domain.Generation).EntityStore<Item>();
        var request = client.CaptureOwnerGraphSave(new Item("one", "first"), routing);
        var execution = new PartitionApplicationClient(decisions, routing, [first, second], ReadClock);
        var raced = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => execution.DeliverAsync(request.OperationId, request.Export()).AsTask()));
        var receipt = raced[0];
        Assert.AreEqual(1, raced.Select(value => value.Members[0].Receipt.Version).Distinct().Count());
        Assert.IsNull(await decisions.ReadApplicationAsync(request.OperationId), "The owner path must not create a central binding.");
        Assert.IsTrue(EntityOperationIds.TryGetCreatedAt(request.OperationId, out _));
        var owner = PartitionOwnerOperation.Owner(request.OperationId, routing);
        var node = owner.Value == "a" ? first : second;
        var wrong = owner.Value == "a" ? second : first;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrong.ExecuteMappedAsync(request.OperationId, request.Export()).AsTask());
        var wrongAdapter = await wrong.CreateGraphExecutionAsync();
        var wrongCapture = Enumerable.Range(0, 100).Select(i => wrongAdapter.CaptureSave(new Item(i.ToString(), "wrong"), request.OperationId))
            .First(value => wrongAdapter.CapturedOwner(value) == wrong.Domain.Partition);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => wrongAdapter.ExecuteAsync(wrong.Domain,
            request.OperationId, wrongCapture.Export()).AsTask());
        Assert.ThrowsExactly<InvalidDataException>(() => new PartitionApplicationBinding(request.OperationId, [node.Domain.Identity], [request.Export()]));
        Assert.ThrowsExactly<InvalidDataException>(() => new PartitionTransactionPlan(request.OperationId, routing.MapVersion,
            [new(first.Domain, new(0), new byte[] { 1 }), new(second.Domain, new(0), new byte[] { 1 })]));
        var changed = store.CaptureSave(new("one", "changed"), new(request.OperationId));
        await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(() => execution.DeliverAsync(request.OperationId, changed.Export()).AsTask());
        var other = Enumerable.Range(0, 100).Select(i => store.CaptureSave(new(i.ToString(), "other"), new(request.OperationId)))
            .First(value => PartitionApplicationRouting.Owner(first.Domain.Generation, DatabaseProfileId.PropertyGraph, routing,
                request.OperationId, value.Export()) != owner);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => execution.DeliverAsync(request.OperationId, other.Export()).AsTask());
        var next = client.CaptureOwnerGraphSave(new Item("one", "later"), routing);
        await execution.DeliverAsync(next.OperationId, next.Export());
        var replay = await execution.DeliverAsync(request.OperationId, request.Export());
        Assert.AreEqual(receipt.Members[0].Receipt.Version, replay.Members[0].Receipt.Version);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, replay.Members[0].Receipt.Outcome);
        Assert.AreEqual(EntityOperationState.Committed, (await execution.ResolveAsync(request.OperationId, default)).State);
        await node.DisposeAsync();
        var placement = owner.Value == "a" ? a : b;
        await using var reopened = await PartitionOwnedNode.OpenAsync(Path.Combine(_root, owner.Value), DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var restored = new PartitionApplicationClient(decisions, routing, owner.Value == "a" ? [reopened, second] : [first, reopened], ReadClock);
        var recovered = await restored.DeliverAsync(request.OperationId, request.Export());
        Assert.AreEqual(receipt.Members[0].Receipt.Version, recovered.Members[0].Receipt.Version);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, recovered.Members[0].Receipt.Outcome);
    }
}
