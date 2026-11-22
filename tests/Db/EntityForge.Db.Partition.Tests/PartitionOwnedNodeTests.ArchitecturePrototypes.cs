using EntityForge.Db.Semantic;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using dotnet_etcd;
using Grpc.Core;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Logical_intent_cache_owns_identity_bytes_and_can_be_discarded()
    {
        var cache = new DatabaseLogicalPreparationCache<object>();
        Guid operation = Guid.NewGuid();
        byte[] payload = [1, 2, 3];
        var selected = new object();
        cache.Retain(operation, payload, selected);
        payload[0] = 9;
        Assert.IsTrue(cache.TryGet(operation, new byte[] { 1, 2, 3 }, out var retained));
        Assert.AreSame(selected, retained);
        Assert.ThrowsExactly<EntityForgeDbIdempotencyException>(() => cache.Retain(operation, payload, new object()));
        cache.Clear();
        Assert.IsFalse(cache.TryGet(operation, new byte[] { 1, 2, 3 }, out _));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Mvcc_certificate_detects_preparation_without_native_version_change(DatabaseProfileId profile)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "mvcc"));
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        Guid subject = Guid.NewGuid(), seed = Guid.NewGuid(), operation = Guid.NewGuid();
        await kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, create: true));
        using var initial = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateSnapshotAsync(initial.Evidence));
        await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(profile, operation, [subject], kernel.CurrentVersion));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(initial.Evidence));
        using var prepared = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        CollectionAssert.AreEqual(new[] { operation }, prepared.Evidence.Prepared.ToArray());
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateSnapshotAsync(prepared.Evidence));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(new(prepared.Evidence.Version,
            prepared.Evidence.Protocol, [], [])), "A copied fingerprint cannot certify altered transaction evidence.");
        await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, true);
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(prepared.Evidence));
        if (database is IGraphDatabase graph)
        {
            Assert.AreEqual(new DatabaseValue.String("seed"), graph.GetVertex(new(subject), initial.Evidence.Version)!.Properties["name"]);
        }

        using var committed = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        CollectionAssert.AreEqual(new[] { operation }, committed.Evidence.Committed.ToArray());
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Snapshot_validation_rejects_changed_versions_and_same_version_aborts(DatabaseProfileId profile)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "snapshot-validation"));
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        Guid subject = Guid.NewGuid(), seed = Guid.NewGuid(), operation = Guid.NewGuid();
        await kernel.CommitAsync(seed, ReservationPayload(profile, seed, [subject], null, create: true));
        using var before = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        await kernel.PartitionTransactions.PrepareAsync(operation, ReservationDescriptor, kernel.CurrentVersion,
            ReservationPayload(profile, operation, [subject], kernel.CurrentVersion));
        using var prepared = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        await kernel.PartitionTransactions.CompleteAsync(operation, ReservationDescriptor, false);
        Assert.AreEqual(prepared.Evidence.Version, kernel.CurrentVersion);
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(prepared.Evidence));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(before.Evidence));
        using var aborted = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateSnapshotAsync(aborted.Evidence));
        Guid update = Guid.NewGuid();
        await kernel.CommitAsync(update, ReservationPayload(profile, update, [subject], null));
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(aborted.Evidence));
        // The fast rejection must not bypass kernel readiness.
        kernel.RequireRecovery("snapshot_precheck_qualification");
        await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryRequiredException>(() => kernel.PartitionTransactions.ValidateSnapshotAsync(aborted.Evidence).AsTask());
        await kernel.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => kernel.PartitionTransactions.ValidateSnapshotAsync(aborted.Evidence).AsTask());
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Snapshot_precheck_does_not_hide_same_version_retention_floor(DatabaseProfileId profile)
    {
        var opened = await OpenReservationStore(profile, Path.Combine(_root, "snapshot-floor"));
        await using var database = opened.Database;
        var kernel = opened.Kernel;
        DatabasePartitionSnapshotEvidence evidence;
        using (var pin = await kernel.PartitionTransactions.CaptureSnapshotAsync())
        {
            evidence = pin.Evidence;
        }

        await kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.AreEqual(evidence.Version, kernel.CurrentVersion);
        Assert.IsFalse(await kernel.PartitionTransactions.ValidateSnapshotAsync(evidence));
        using var current = await kernel.PartitionTransactions.CaptureSnapshotAsync();
        Assert.IsTrue(await kernel.PartitionTransactions.ValidateSnapshotAsync(current.Evidence));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Serving_epoch_requires_sealed_handoff_and_survives_liveness_release(bool etcd)
    {
        using var files = new FileServingEpochStore(Path.Combine(_root, "epochs"));
        using var client = etcd ? new EtcdClient(RequireLiveEtcdEndpoint(), configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure) : null;
        string prefix = "/entityforge/epoch-prototype/" + Guid.NewGuid().ToString("N") + "/";
        IServingEpochStore store = client is null ? files : new EtcdServingEpochStore(client, prefix);
        try
        {
            var authority = new DurableServingEpochAuthority(store);
            var generation = DistributedDatabaseGeneration.New();
            var lease = await authority.AcquireAsync(new("a"), new("source"), new("https://source.test"), generation, new(1), TimeSpan.FromMinutes(5));
            await authority.ReleaseAsync(lease);
            await Assert.ThrowsExactlyAsync<ShardPlacementOwnedException>(() => authority.AcquireAsync(new("a"), new("other"),
                new("https://other.test"), generation, new(1), TimeSpan.FromMinutes(5)).AsTask());
            var transfer = new ShardTransferId(Guid.NewGuid());
            await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => authority.TransferAsync(transfer, lease,
                new("destination"), new("https://destination.test"), new(1), TimeSpan.FromMinutes(5)).AsTask());
            await authority.SealAsync(lease.Placement.Claim, transfer.Value, new string('A', 64), CancellationToken.None);
            var moved = await authority.TransferAsync(transfer, lease, new("destination"), new("https://destination.test"), new(1), TimeSpan.FromMinutes(5));
            Assert.IsFalse(await authority.ValidateAsync(lease.Placement.Claim));
            Assert.AreEqual(moved, await authority.RecoverTransferredLeaseAsync(transfer, lease.Placement.Claim,
                new("destination"), new("https://destination.test")));
            await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => authority.RenewAsync(lease, TimeSpan.FromMinutes(5)).AsTask());
        }
        finally
        {
            if (client is not null)
            {
                await client.DeleteAsync(new Etcdserverpb.DeleteRangeRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(prefix),
                    RangeEnd = Google.Protobuf.ByteString.CopyFromUtf8(prefix[..^1] + "0")
                });
            }
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Semantic, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    [DataRow(DatabaseProfileId.Semantic, true)]
    public async Task Serving_epoch_native_owner_transfers_only_after_export_and_rejects_old_image_reactivation(DatabaseProfileId profile, bool etcd)
    {
        using var store = new FileServingEpochStore(Path.Combine(_root, "epoch-authority"));
        using var client = etcd ? new EtcdClient(RequireLiveEtcdEndpoint(), configureChannelOptions: options => options.Credentials = ChannelCredentials.Insecure) : null;
        string prefix = "/entityforge/epoch-prototype/" + Guid.NewGuid().ToString("N") + "/";
        var authority = new DurableServingEpochAuthority(client is null ? store : new EtcdServingEpochStore(client, prefix));
        try
        {
            var lease = await authority.AcquireAsync(new("a"), new("source"), new("https://source.test"), DistributedDatabaseGeneration.New(),
                new(1), TimeSpan.FromMinutes(5));
            string sourcePath = Path.Combine(_root, "source"), imagePath = Path.Combine(_root, "image"), destinationPath = Path.Combine(_root, "destination");
            await using var source = await PartitionOwnedNode.CreateAsync(sourcePath, profile, lease, Routing, authority, Options);
            Guid operation = Guid.NewGuid();
            await WriteEpochNode(source, profile, operation, "before");
            var image = await source.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
            await using (var transfer = new PartitionOwnedTransfer(imagePath, destinationPath, image, Routing, authority,
                new("destination"), new("https://destination.test"), Options))
            {
                var destination = await transfer.CompleteAsync();
                Guid later = Guid.NewGuid();
                await WriteEpochNode(destination, profile, later, "after");
                Assert.IsTrue(destination.Kernel.CurrentVersion.Value > image.LocalVersion);
                Assert.AreEqual(source.Domain, destination.Domain);
                Assert.IsFalse(await authority.ValidateAsync(source.Enrollment.Claim));
            }
            // Simulate loss of the current recovery directory without deleting any live store.
            var missing = Path.Combine(_root, "missing-current-storage");
            await using var retry = new PartitionOwnedTransfer(imagePath, missing, image, Routing, authority,
                new("destination"), new("https://destination.test"), Options);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => retry.CompleteAsync().AsTask());
            Assert.IsFalse(Directory.Exists(missing));
        }
        finally
        {
            if (client is not null)
            {
                await client.DeleteAsync(new Etcdserverpb.DeleteRangeRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(prefix),
                    RangeEnd = Google.Protobuf.ByteString.CopyFromUtf8(prefix[..^1] + "0")
                });
            }
        }
    }

    [TestMethod]
    public async Task Semantic_mvcc_pin_preserves_projection_across_a_later_save()
    {
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "semantic-mvcc"), DatabaseProfileId.Semantic,
            placement.Lease, Routing, placement, Options);
        var adapter = await node.CreateSemanticExecutionAsync();
        var request = adapter.CaptureSave(new Item("one", "before"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
        await adapter.ExecuteAsync(node.Domain, request.OperationId, request.Export());
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        try
        {
            var next = adapter.CaptureSave(new Item("one", "after"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
            await adapter.ExecuteAsync(node.Domain, next.OperationId, next.Export());
            using var http = new HttpClient();
            var producer = new EntityForge.Db.Semantic.HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>();
            var (_, recipe) = producer.DescribePartitionRead("one");
            bool read = false;
            await SemanticReadExpansionWalker.WalkAsync(new(recipe, [new(0, SemanticEntityReadDecoder.SemanticIdentity(recipe.Identity))]),
                _ => true, async (projection, token) =>
                {
                    byte[] result = await node.ReadCutAsync(cut, new("semantic-project", Subject:
                        System.Text.Json.JsonSerializer.Serialize(projection, PartitionParticipantWire.Json)), token);
                    StringAssert.Contains(System.Text.Encoding.UTF8.GetString(result), "before");
                    Assert.IsFalse(System.Text.Encoding.UTF8.GetString(result).Contains("after"));
                    read = true;
                    using var json = System.Text.Json.JsonDocument.Parse(result);
                    return SemanticEntityReadDecoder.Projection(json.RootElement, projection.Query());
                }, (_, _) => throw new InvalidOperationException("Scalar recipe unexpectedly traversed relationships."), CancellationToken.None);
            Assert.IsTrue(read);
        }
        finally
        {
            await node.CompleteCutAsync(cut);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Mvcc_read_accepts_prepared_old_state_but_never_partially_applied_transaction(DatabaseProfileId profile)
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
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "cut-authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "cut-a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "cut-b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions);
        Guid operation = Guid.NewGuid();
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), operation)),
                new(second.Domain, new(0), NativePayload(profile, routing, new("b"), operation))]);
        await decisions.BindAsync(plan);
        await first.PrepareNativeAsync(plan);
        await second.PrepareNativeAsync(plan);
        var old = await PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, _) => ValueTask.FromResult(cut.Position), useSnapshots: true);
        Assert.IsTrue(old.Position.Positions.All(position => position.Version.Value == 0));
        await decisions.DecideAsync(operation, plan.Descriptor, true);
        await first.ApplyPartitionDecisionAsync(operation);
        var waiting = PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, _) => ValueTask.FromResult(cut.Position), useSnapshots: true).AsTask();
        await Task.Delay(50);
        Assert.IsFalse(waiting.IsCompleted, "A mixed transaction cut must not escape.");
        await second.ApplyPartitionDecisionAsync(operation);
        var complete = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(complete.Position.Positions.All(position => position.Version.Value == 1));
    }

    private static async ValueTask WriteEpochNode(PartitionOwnedNode node, DatabaseProfileId profile, Guid operation, string value)
    {
        if (profile == DatabaseProfileId.Semantic)
        {
            var adapter = await node.CreateSemanticExecutionAsync();
            var request = adapter.CaptureSave(new Item("one", value), new SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow));
            await adapter.ExecuteAsync(node.Domain, operation, request.Export());
        }
        else
        {
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation,
            NativePayload(profile, Routing, new("a"), operation));
                return true;
            });
        }
    }

    [TestMethod]
    public async Task Semantic_mvcc_cut_waits_for_the_unapplied_member_of_a_committed_transaction()
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
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "semantic-cut-authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "semantic-cut-a"), DatabaseProfileId.Semantic, a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "semantic-cut-b"), DatabaseProfileId.Semantic, b.Lease, routing, b, Options, transactionAuthority: decisions);
        var client = new PartitionApplicationClient(decisions, routing, [first, second]);
        using var http = new HttpClient();
        var request = await ConfinedSemanticRequest(client, first);
        var companion = (await ConfinedSemanticRequest(client, second)).ForOperation(request.OperationId);
        await client.PrepareAsync(request.OperationId, [request.Export(), companion.Export()], CancellationToken.None);
        var old = await PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, _) => ValueTask.FromResult(cut.Position), useSnapshots: true);
        var plan = await decisions.ReadPlanAsync(request.OperationId) ?? throw new InvalidDataException("Missing selected semantic plan.");
        Assert.AreEqual(2, plan.Members.Count, "This schedule requires a genuinely cross-owner transaction.");
        await decisions.DecideAsync(request.OperationId, plan.Descriptor, true);
        await first.ApplyPartitionDecisionAsync(request.OperationId);
        var pending = PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, _) => ValueTask.FromResult(cut.Position), useSnapshots: true).AsTask();
        await Task.Delay(50);
        Assert.IsFalse(pending.IsCompleted);
        await second.ApplyPartitionDecisionAsync(request.OperationId);
        var complete = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(complete.Position.Covers(old.Position));
        foreach (var position in complete.Position.Positions)
        {
            Assert.IsTrue(position.Version.Value > old.Position.Positions.Single(prior => prior.Identity == position.Identity).Version.Value);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Mvcc_snapshot_lifetime_is_drained_before_sealed_transfer(DatabaseProfileId profile)
    {
        var authority = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "snapshot-transfer"), profile,
            authority.Lease, Routing, authority, Options);
        var cut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var sealing = node.SealAndExportAsync(new(Guid.NewGuid()), Path.Combine(_root, "snapshot-image")).AsTask();
        var image = await sealing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(node.Domain.Incarnation, image.Source.Incarnation);
        await Assert.ThrowsAsync<Exception>(() => node.CompleteCutAsync(cut).AsTask());
    }

    [TestMethod]
    public async Task Serving_epoch_enrollment_rejects_protocol_change_on_reopen()
    {
        using var store = new FileServingEpochStore(Path.Combine(_root, "epoch-domain"));
        var authority = new DurableServingEpochAuthority(store);
        var lease = await authority.AcquireAsync(new("a"), new("source"), new("https://source.test"), DistributedDatabaseGeneration.New(), new(1), TimeSpan.FromMinutes(5));
        string path = Path.Combine(_root, "enrolled");
        await using (var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.PropertyGraph, lease, Routing, authority, Options))
        {
            Assert.IsNotNull(node.Enrollment.ServingAuthority);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.PropertyGraph,
            lease, Routing, new Authority { Lease = lease }, Options).AsTask());
        await using var reopened = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.PropertyGraph, lease, Routing, authority, Options);
        Assert.AreEqual(0L, reopened.Kernel.CurrentVersion.Value);
    }
}
