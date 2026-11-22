using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed partial class PartitionOwnedNodeTests
{
    private readonly SafeReadClock _readClock = new();

    private EntityForge.Db.Kernel.IDatabaseBoundedClock ReadClock =>
        EntityForge.Db.Kernel.DatabaseTimestampCostOptions.QualificationOverride.Value is { } configured
            ? configured.BoundedClock ?? new QualificationReadClock(configured) : _readClock;

    private sealed class QualificationReadClock(EntityForge.Db.Kernel.DatabaseTimestampCostOptions options) : EntityForge.Db.Kernel.IDatabaseBoundedClock
    {
        public Guid Domain => options.Domain;

        public EntityForge.Db.Kernel.DatabaseClockInterval Read()
        {
            if (!options.Healthy())
            {
                throw new InvalidOperationException("Qualification clock is unhealthy.");
            }

            long now = options.Now();
            return new(now, now);
        }
    }

    private readonly Dictionary<Guid, EntityForge.Db.Durability.DurableTimestampState> _queryTimes = [];

    private EntityForge.Db.Durability.DurableTimestampState TestQueryTime(PartitionReadCut cut)
    {
        lock (_queryTimes)
        {
            if (!_queryTimes.TryGetValue(cut.Id, out var time))
            {
                _queryTimes[cut.Id] = time = PartitionTimestampClock.Read(cut.ClockDomain!.Value, ReadClock);
            }

            return time;
        }
    }

    private static async Task<PartitionTransactionDecision> SelectTimestampDecisionAsync(
        IPartitionTransactionAuthority authority, PartitionTransactionPlan plan, params PartitionOwnedNode[] nodes)
    {
        var votes = await Task.WhenAll(nodes.Select(n => n.Kernel.PartitionTransactions.ReadTimestampVoteAsync(plan.Operation, default).AsTask()));
        var maximum = votes.Aggregate((a, b) => a!.CompareTo(b!) >= 0 ? a : b)!;
        return await authority.DecideTimestampedAsync(plan.Operation, plan.Descriptor,
            new(maximum.Domain, maximum.Physical, checked(maximum.Logical + 1), 0, true));
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ef-owned-node-" + Guid.NewGuid().ToString("N"));
    private static readonly ShardPlacementLeaseSupervisorOptions Options = new(TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));

    private static DistributedRoutingBinding Routing => new StableHashDistributedShardRouter(new(new(1), [new("a")])).Binding;

    [EntityType("partition-node-tests", "item")]
    public sealed record Item(string Id, string Name);

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [TestMethod]
    public async Task Prepared_transaction_survives_transfer_then_finishes_at_destination_local_position()
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
            a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, transactionAuthority: decisions);
        Guid operation = Guid.NewGuid();
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), operation)),
                new(second.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), operation))]);
        await decisions.BindAsync(plan);
        await first.PrepareNativeAsync(plan);
        await second.PrepareNativeAsync(plan);
        string imagePath = Path.Combine(_root, "image"), destinationPath = Path.Combine(_root, "destination");
        var image = await first.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
        await decisions.DecideAsync(operation, plan.Descriptor, true);
        await using var transfer = new PartitionOwnedTransfer(imagePath, destinationPath, image, routing, a,
            new("destination"), new("https://localhost:22001"), Options, decisions);
        var destination = await transfer.CompleteAsync();
        Assert.AreEqual(first.Domain, destination.Domain);
        Assert.AreEqual(1L, destination.Kernel.CurrentVersion.Value);
        var result = await new PartitionTransactionCoordinator(decisions).ExecuteAsync(plan, [destination, second]);
        Assert.IsTrue(result.Decision.Commit);
        Assert.IsTrue(result.Members.All(m => m.Receipt.Version.Value == 1));
        await first.DisposeAsync();
        await transfer.RetireSourceAsync(Path.Combine(_root, "a"));
        await transfer.RetireSourceAsync(Path.Combine(_root, "a"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PartitionOwnedNode.OpenAsync(Path.Combine(_root, "a"),
            DatabaseProfileId.PropertyGraph, a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = image.Source.Claim
                }
            }, routing, a,
            Options, transactionAuthority: decisions).AsTask());
    }

    [TestMethod]
    public async Task Failed_later_preparation_durably_aborts_and_releases_earlier_partition()
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
            a.Lease, routing, a, Options, transactionAuthority: decisions);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, transactionAuthority: decisions);
        Guid operation = Guid.NewGuid();
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), operation)),
                new(second.Domain, new(1), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), operation))]);
        var result = await new PartitionTransactionCoordinator(decisions).ExecuteAsync(plan, [first, second]);
        Assert.IsFalse(result.Decision.Commit);
        Assert.AreEqual(0, result.Members.Count);
        Assert.AreEqual(result.Decision, (await new PartitionTransactionCoordinator(decisions).ExecuteAsync(plan, [first, second])).Decision);
        var cut = await PartitionGlobalRead.ExecuteAsync([first, second], (_, _) => ValueTask.FromResult(true));
        Assert.IsTrue(cut.Positions.All(p => p.Version.Value == 0));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Multi_owner_decision_recovers_partial_delivery_and_replays_after_local_work(DatabaseProfileId profile)
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
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease,
            routing, a, Options, transactionAuthority: decisions);
        string secondPath = Path.Combine(_root, "b");
        await using var second = await PartitionOwnedNode.CreateAsync(secondPath, profile, b.Lease,
            routing, b, Options, transactionAuthority: decisions);
        Guid operation = Guid.NewGuid();
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), operation)),
                new(second.Domain, new(0), NativePayload(profile, routing, new("b"), operation))]);
        await decisions.BindAsync(plan);
        await first.PrepareNativeAsync(plan);
        await second.PrepareNativeAsync(plan);
        await decisions.DecideAsync(operation, plan.Descriptor, true);
        var applied = await first.ApplyPartitionDecisionAsync(operation);
        Assert.AreEqual(1L, applied!.Version.Value);
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var blocked = await second.CreateGraphExecutionAsync();
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await blocked.ReadAsync<Item>(second.Domain, "not-read-before-resolution"));
            StringAssert.Contains(failure.Message, "must be resolved");
        }
        else
        {
            var blocked = await second.CreateRdfExecutionAsync();
            var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                await blocked.ReadAsync<Item>(second.Domain, "not-read-before-resolution"));
            StringAssert.Contains(failure.Message, "must be resolved");
        }
        await Assert.ThrowsExactlyAsync<AggregateException>(async () =>
            await PartitionGlobalRead.ExecuteAsync([first, second], (_, _) => ValueTask.FromResult(true)));
        await second.DisposeAsync();
        await using var recovered = await PartitionOwnedNode.OpenAsync(secondPath, profile, b.Lease,
            routing, b, Options, transactionAuthority: decisions);
        Assert.AreEqual(1L, recovered.Kernel.CurrentVersion.Value, "Opening must finish the durable decision before admission.");
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            var adapter = await first.CreateGraphExecutionAsync();
            GraphEntityRequest request;
            for (int i = 0; ; i++)
            {
                request = adapter.CaptureSave(new Item("local-" + i, "later"), Guid.NewGuid());
                if (adapter.CapturedOwner(request) == first.Domain.Partition)
                {
                    break;
                }
            }
            await adapter.ExecuteAsync(first.Domain, request.OperationId, request.Export());
        }
        var result = await new PartitionTransactionCoordinator(decisions).ExecuteAsync(plan, [first, recovered]);
        Assert.IsTrue(result.Decision.Commit);
        Assert.IsTrue(result.Members.All(m => m.Receipt.Version.Value == 1));
        var cut = await PartitionGlobalRead.ExecuteAsync([first, recovered], (positions, _) => ValueTask.FromResult(positions.Count));
        Assert.AreEqual(2, cut.Value);
        var changed = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), operation)), plan.Members[1]]);
        await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(async () => await decisions.BindAsync(changed));
        Assert.IsTrue((await decisions.ReadDecisionAsync(operation))!.Commit);
    }

    private static byte[] NativePayload(DatabaseProfileId profile, DistributedRoutingBinding routing, LogicalShardId owner, Guid operation)
    {
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            Guid vertex;
            do
            {
                vertex = Guid.NewGuid();
            } while (routing.RouteGraphVertex(vertex) != owner);
            return GraphTransactionCodec.Encode(new(operation,
                [new CreateVertex(new(vertex), []), new SetVertexProperty(new(vertex), "name", new DatabaseValue.String("value"))]));
        }
        RdfIri subject;
        do
        {
            subject = new("urn:test:" + Guid.NewGuid().ToString("N"));
        } while (routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(subject)) != owner);
        return RdfTransactionCodec.Encode(new(operation,
            [new AssertRdfQuad(new(subject, new RdfIri("urn:name"), new RdfLiteral("value")))]));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Native_profile_adapters_deliver_replay_and_reopen(DatabaseProfileId profile)
    {
        var authority = new Authority();
        string path = Path.Combine(_root, "node");
        byte[] original;
        Guid operation;
        PartitionCommitResult first;
        PartitionExecutionDomain domain;
        await using (var node = await PartitionOwnedNode.CreateAsync(path, profile, authority.Lease, Routing, authority, Options))
        {
            domain = node.Domain;
            if (profile == DatabaseProfileId.Rdf)
            {
                var adapter = await node.CreateRdfExecutionAsync();
                var request = adapter.CaptureSave(new Item("one", "before"), Guid.NewGuid());
                operation = request.OperationId;
                original = request.Export();
                first = await adapter.ExecuteAsync(domain, operation, original);
                var next = adapter.CaptureSave(new Item("one", "after"), Guid.NewGuid());
                await adapter.ExecuteAsync(domain, next.OperationId, next.Export());
                Assert.AreEqual("after", (await adapter.ReadAsync<Item>(domain, "one", first.ReadToken))!.Value.Name);
            }
            else
            {
                var adapter = await node.CreateSemanticExecutionAsync();
                var request = adapter.CaptureSave(new Item("one", "before"), SemanticEntityWriteOperation.Create());
                operation = request.OperationId;
                original = request.Export();
                first = await adapter.ExecuteAsync(domain, operation, original);
                var next = adapter.CaptureSave(new Item("one", "after"), SemanticEntityWriteOperation.Create());
                await adapter.ExecuteAsync(domain, next.OperationId, next.Export());
                Assert.AreEqual("after", (await adapter.ReadAsync<Item>(domain, "one", first.ReadToken))!.Value.Name);
            }
        }
        await using var reopened = await PartitionOwnedNode.OpenAsync(path, profile, authority.Lease, Routing, authority, Options);
        if (profile == DatabaseProfileId.Rdf)
        {
            var adapter = await reopened.CreateRdfExecutionAsync();
            Assert.AreEqual(first.Receipt.Version, (await adapter.ExecuteAsync(domain, operation, original)).Receipt.Version);
            Assert.AreEqual("after", (await adapter.ReadAsync<Item>(domain, "one", first.ReadToken))!.Value.Name);
        }
        else
        {
            var adapter = await reopened.CreateSemanticExecutionAsync();
            Assert.AreEqual(first.Receipt.Version, (await adapter.ExecuteAsync(domain, operation, original)).Receipt.Version);
            Assert.AreEqual("after", (await adapter.ReadAsync<Item>(domain, "one", first.ReadToken))!.Value.Name);
        }
    }

    [TestMethod]
    public async Task Global_cut_freezes_writes_and_does_not_confuse_partition_positions()
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
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph, a.Lease, routing, a, Options);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph, b.Lease, routing, b, Options);
        var adapter = await first.CreateGraphExecutionAsync();
        string identity = "";
        GraphEntityRequest request;
        for (int index = 0; ; index++)
        {
            identity = "item-" + index;
            request = adapter.CaptureSave(new Item(identity, "before"), Guid.NewGuid());
            if (adapter.CapturedOwner(request) == first.Domain.Partition)
            {
                break;
            }
        }
        await adapter.ExecuteAsync(first.Domain, request.OperationId, request.Export());
        Task<PartitionCommitResult>? writer = null;
        var cut = await PartitionGlobalRead.ExecuteAsync([second, first], async (positions, token) =>
        {
            Assert.AreNotEqual(positions[0].Version, positions[1].Version);
            var update = adapter.CaptureSave(new Item(identity, "after"), Guid.NewGuid());
            writer = adapter.ExecuteAsync(first.Domain, update.OperationId, update.Export()).AsTask();
            await Task.Delay(30, token);
            Assert.IsFalse(writer.IsCompleted, "Native writes must wait until the vector cut releases.");
            return (await adapter.ReadAsync<Item>(first.Domain, identity, positions[0], token))!.Value.Name;
        });
        Assert.AreEqual("before", cut.Value);
        await writer!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("after", (await adapter.ReadAsync<Item>(first.Domain, identity))!.Value.Name);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await PartitionGlobalRead.ExecuteAsync([first], (_, _) => ValueTask.FromResult(true)));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task All_profiles_pin_enrollment_and_reopen_only_complete_matching_sets(DatabaseProfileId profile)
    {
        var authority = new Authority();
        string path = Path.Combine(_root, "node");
        PartitionExecutionDomain domain;
        await using (var node = await PartitionOwnedNode.CreateAsync(path, profile, authority.Lease, Routing, authority, Options))
        {
            domain = node.Domain;
            int renewals = authority.Renewals;
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await PartitionOwnedNode.OpenAsync(path, profile, authority.Lease, Routing, authority, Options));
            Assert.AreEqual(renewals, authority.Renewals);
        }
        await using (var node = await PartitionOwnedNode.OpenAsync(path, profile, authority.Lease, Routing, authority, Options))
        {
            Assert.AreEqual(domain, node.Domain);
        }

        var wrong = authority.Lease with
        {
            Placement = authority.Lease.Placement with
            {
                Claim = authority.Lease.Placement.Claim with
                {
                    Generation = new(Guid.NewGuid())
                }
            }
        };
        int before = authority.Renewals;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await PartitionOwnedNode.OpenAsync(path, profile, wrong, Routing, authority, Options));
        Assert.AreEqual(before, authority.Renewals);
        File.Delete(Path.Combine(path, "database", "entityforge.checkpoint"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await PartitionOwnedNode.OpenAsync(path, profile, authority.Lease, Routing, authority, Options));
        Assert.AreEqual(before, authority.Renewals);
    }

    [TestMethod]
    public async Task Transfer_preserves_graph_data_exact_replay_and_read_tokens_after_lost_cutover_reply() =>
        await TransferGraphReplayAsync(null);

    private async Task TransferGraphReplayAsync(EntityForge.Db.Kernel.IDatabaseBoundedClock? clock, int clockLossStage = 0)
    {
        var authority = new Authority();
        string sourcePath = Path.Combine(_root, "source");
        var sourceLease = authority.Lease;
        await using var source = await PartitionOwnedNode.CreateAsync(sourcePath, DatabaseProfileId.PropertyGraph,
            sourceLease, Routing, authority, Options, timestampClock: clock);
        var graph = await source.CreateGraphExecutionAsync();
        Guid id = Guid.NewGuid();
        var request = graph.CaptureSave(new Item("one", "before"), id);
        var receipt = await graph.ExecuteAsync(source.Domain, id, request.Export());
        var transferId = new ShardTransferId(Guid.NewGuid());
        string imagePath = Path.Combine(_root, "image"), destinationPath = Path.Combine(_root, "destination");
        var image = await source.SealAndExportAsync(transferId, imagePath);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () =>
            await graph.ExecuteAsync(source.Domain, id, request.Export()));
        await using var transfer = new PartitionOwnedTransfer(imagePath, destinationPath, image, Routing,
            authority, new("destination"), new("https://destination.test"), Options, timestampClock: clock);
        if (clockLossStage == 1)
        {
            ((IndependentTestClock)clock!).Healthy = false;
            var failure = await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryException>(async () => await transfer.CompleteAsync());
            Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
            Assert.AreEqual(0, authority.Transfers);
            ((IndependentTestClock)clock!).Healthy = true;
        }
        authority.LoseNextCutoverReply = true;
        await Assert.ThrowsExactlyAsync<IOException>(async () => await transfer.CompleteAsync());
        Assert.AreEqual(1, authority.Transfers);
        if (clockLossStage == 2)
        {
            ((IndependentTestClock)clock!).Healthy = false;
            var failure = await Assert.ThrowsExactlyAsync<EntityForgeDbRecoveryException>(async () => await transfer.CompleteAsync());
            Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
            Assert.AreEqual(1, authority.Transfers);
            ((IndependentTestClock)clock!).Healthy = true;
        }
        var destination = await transfer.CompleteAsync();
        var remote = await destination.CreateGraphExecutionAsync();
        Assert.AreEqual(source.Domain, destination.Domain);
        Assert.AreEqual("before", (await remote.ReadAsync<Item>(source.Domain, "one", receipt.ReadToken))!.Value.Name);
        var update = remote.CaptureSave(new Item("one", "after"), Guid.NewGuid());
        await remote.ExecuteAsync(destination.Domain, update.OperationId, update.Export());
        var replay = await remote.ExecuteAsync(destination.Domain, id, request.Export());
        Assert.AreEqual(receipt.Receipt.Version, replay.Receipt.Version);
        Assert.AreEqual("after", (await remote.ReadAsync<Item>(source.Domain, "one", receipt.ReadToken))!.Value.Name);
        int renewed = authority.Renewals;
        Assert.AreSame(destination, await transfer.CompleteAsync());
        Assert.AreEqual(renewed, authority.Renewals, "Activation retry must not renew behind the live supervisor.");
        Assert.AreEqual(1, authority.Transfers);
        await transfer.DisposeAsync();
        // A new activation owner after later destination commits must not demand
        // equality with the older source image's version.
        await using var restarted = new PartitionOwnedTransfer(imagePath, destinationPath, image, Routing,
            authority, new("destination"), new("https://destination.test"), Options, timestampClock: clock);
        var reopened = await restarted.CompleteAsync();
        Assert.AreEqual("after", (await (await reopened.CreateGraphExecutionAsync()).ReadAsync<Item>(source.Domain, "one", receipt.ReadToken))!.Value.Name);
        await source.DisposeAsync();
        int before = authority.Renewals;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await PartitionOwnedNode.OpenAsync(sourcePath, DatabaseProfileId.PropertyGraph, sourceLease, Routing, authority, Options));
        Assert.AreEqual(before, authority.Renewals);
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Sealed_restart_is_terminal_only_and_all_profile_images_recover_before_cutover(DatabaseProfileId profile)
    {
        var authority = new Authority();
        string path = Path.Combine(_root, "source"), imagePath = Path.Combine(_root, "image");
        var id = new ShardTransferId(Guid.NewGuid());
        PartitionTransferImage image;
        await using (var source = await PartitionOwnedNode.CreateAsync(path, profile, authority.Lease, Routing, authority, Options))
        {
            image = await source.SealAndExportAsync(id, imagePath);
        }

        int before = authority.Renewals;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await PartitionOwnedNode.OpenAsync(path, profile, authority.Lease, Routing, authority, Options));
        Assert.AreEqual(before, authority.Renewals);
        await using (var recovery = await PartitionOwnedNode.OpenSealedForTransferAsync(path, profile, authority.Lease, Routing, authority, id, Options))
        {
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await recovery.CreateGraphExecutionAsync());
            Assert.AreEqual(image, await recovery.SealAndExportAsync(id, imagePath));
        }
        await using var transfer = new PartitionOwnedTransfer(imagePath, Path.Combine(_root, "destination"), image,
            Routing, authority, new("destination"), new("https://destination.test"), Options);
        Assert.AreEqual(image.Source.Domain, (await transfer.CompleteAsync()).Domain);
    }

    [TestMethod]
    public async Task Bad_map_or_corrupt_staged_native_set_never_moves_ownership()
    {
        var authority = new Authority();
        string imagePath = Path.Combine(_root, "image"), dest = Path.Combine(_root, "destination");
        await using var source = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "source"), DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options);
        var image = await source.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
        var badRouting = new StableHashDistributedShardRouter(new(new(2), [new("a")])).Binding;
        Assert.ThrowsExactly<InvalidDataException>(() => new PartitionOwnedTransfer(imagePath, dest, image,
            badRouting, authority, new("destination"), new("https://destination.test"), Options));
        var destination = image.Source.Claim with
        {
            Owner = new("destination"),
            FencingToken = new(2)
        };
        await image.StageAsync(imagePath, dest, destination, Routing);
        File.Delete(Path.Combine(dest, "database", "entityforge.wal"));
        await using var transfer = new PartitionOwnedTransfer(imagePath, dest, image, Routing, authority,
            destination.Owner, new("https://destination.test"), Options);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await transfer.CompleteAsync());
        Assert.AreEqual(0, authority.Transfers);
    }

    private static string RequireLiveEtcdEndpoint()
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT to run live etcd coverage.");
        }

        return endpoint!;
    }

    private sealed class Authority : IShardPlacementAuthority
    {
        internal ShardPlacementLease Lease = new(new(new(new("a"), new("source"), new(Guid.NewGuid()), new(1), new(1)), new("https://source.test")), 1);
        private ShardTransferId? _transfer;
        internal int Renewals, Transfers, Validations;
        internal bool LoseNextCutoverReply;
        internal bool RejectRenewal;
        internal bool HideAuthorityState;

        public ValueTask<ShardPlacement?> ResolveAsync(LogicalShardId shard, CancellationToken cancellationToken = default) => ValueTask.FromResult<ShardPlacement?>(Lease.Placement);

        public ValueTask<ShardPlacementAuthorityState?> ResolveAuthorityStateAsync(LogicalShardId shard, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ShardPlacementAuthorityState?>(HideAuthorityState ? null : new(Lease, _transfer));

        public ValueTask<ShardPlacementLease> AcquireAsync(LogicalShardId shard, ClusterNodeId owner, Uri endpoint,
            DistributedDatabaseGeneration generation, ShardMapVersion version, TimeSpan lifetime, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<ShardPlacementLease> RenewAsync(ShardPlacementLease lease, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            if (RejectRenewal || lease != Lease)
            {
                throw new StaleShardPlacementException(lease.Placement.Claim.Shard);
            }

            Renewals++;
            Lease = lease with
            {
                LeaseId = lease.LeaseId + 1
            };
            return ValueTask.FromResult(Lease);
        }

        public ValueTask<bool> ValidateAsync(ShardPlacementClaim claim, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Validations);
            return ValueTask.FromResult(claim == Lease.Placement.Claim);
        }

        // Tests preserve the authoritative receipt across owner disposal/reopen.
        public ValueTask ReleaseAsync(ShardPlacementLease lease, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<ShardPlacementLease> TransferAsync(ShardTransferId transfer, ShardPlacementLease current,
            ClusterNodeId owner, Uri endpoint, ShardMapVersion map, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            if (current != Lease)
            {
                throw new StaleShardPlacementException(current.Placement.Claim.Shard);
            }

            Transfers++;
            _transfer = transfer;
            Lease = new(new(current.Placement.Claim with
            {
                Owner = owner,
                MapVersion = map,
                FencingToken = new(current.Placement.Claim.FencingToken.Value + 1)
            }, endpoint), current.LeaseId + 1);
            if (LoseNextCutoverReply)
            {
                LoseNextCutoverReply = false;
                throw new IOException("Lost cutover response.");
            }
            return ValueTask.FromResult(Lease);
        }
    }
}
