using EntityForge.Db.Mapping;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Shared_clock_incremental_observers_follow_other_writers_and_retirement()
    {
        using var client = new dotnet_etcd.EtcdClient(RequireLiveEtcdEndpoint(), configureChannelOptions: options => options.Credentials = Grpc.Core.ChannelCredentials.Insecure);
        string key = "/entityforge/shared-clock-tests/" + Guid.NewGuid().ToString("N");
        var first = new DatabaseSharedTimestampService(new EtcdTimestampStore(client, key, "domain"));
        var second = new DatabaseSharedTimestampService(new EtcdTimestampStore(client, key, "domain"));
        try
        {
            await first.EnrollAsync("a", 0);
            await second.EnrollAsync("b", 0);
            var tickets = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
                (index % 2 == 0 ? first : second).ReserveAsync(EntityOperationIds.New(), new('A', 64), ["a", "b"]).AsTask()));
            CollectionAssert.AreEqual(Enumerable.Range(1, 16).Select(x => (long)x).ToArray(), tickets.Select(t => t.Timestamp).Order().ToArray());
            foreach (var ticket in tickets.OrderBy(t => t.Timestamp))
            {
                await first.AppliedAsync(ticket, "a", ticket.Timestamp);
                await second.AppliedAsync(ticket, "b", ticket.Timestamp);
            }
            Assert.AreEqual(16L, (await first.ObserveAsync()).State.Closed);
            Assert.AreEqual(16L, (await second.ObserveAsync()).State.Closed);
            await first.RetireAsync(DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.AreEqual(0, (await second.ObserveAsync()).State.Tickets.Length);
            await client.DeleteAsync(new Etcdserverpb.DeleteRangeRequest { Key = Google.Protobuf.ByteString.CopyFromUtf8(key), RangeEnd = EtcdTimestampStore.PrefixEnd(key) });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await second.ObserveAsync());
        }
        finally
        {
            await client.DeleteAsync(new Etcdserverpb.DeleteRangeRequest { Key = Google.Protobuf.ByteString.CopyFromUtf8(key), RangeEnd = EtcdTimestampStore.PrefixEnd(key) });
        }
    }

    [TestMethod]
    public async Task Shared_clock_delivers_predecessor_queued_after_a_waiting_successor()
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
        using var storage = new FileTimestampStore(Path.Combine(_root, "clock"), "domain");
        var clock = new DatabaseSharedTimestampService(storage);
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "decisions"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            a.Lease, routing, a, Options, transactionAuthority: decisions, timestamps: clock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, transactionAuthority: decisions, timestamps: clock);
        var subjects = new Dictionary<PartitionOwnedNode, Guid[]>();
        foreach (var owner in new[] { first, second })
        {
            var ids = new List<Guid>();
            while (ids.Count < 2)
            {
                var id = Guid.NewGuid();
                if (routing.RouteGraphVertex(id) == owner.Domain.Partition)
                {
                    ids.Add(id);
                }
            }
            subjects[owner] = ids.ToArray();
            var seed = Guid.NewGuid();
            await owner.Kernel.CommitAsync(seed, ReservationPayload(DatabaseProfileId.PropertyGraph, seed, ids.ToArray(), null, create: true));
            await clock.MaintainAsync(PartitionOwnedNode.TimestampParticipant(owner.Domain.Identity), owner.Kernel.CurrentVersion.Value);
        }
        int memberIndex = 0;
        async Task<PartitionTransactionPlan> Prepare()
        {
            var id = EntityOperationIds.New();
            var plan = new PartitionTransactionPlan(id, routing.MapVersion,
                [new(first.Domain, first.Kernel.CurrentVersion, ReservationPayload(DatabaseProfileId.PropertyGraph, id, [subjects[first][memberIndex]], first.Kernel.CurrentVersion)),
                    new(second.Domain, second.Kernel.CurrentVersion, ReservationPayload(DatabaseProfileId.PropertyGraph, id, [subjects[second][memberIndex]], second.Kernel.CurrentVersion))]);
            memberIndex++;
            await decisions.BindAsync(plan);
            await first.PrepareNativeAsync(plan);
            await second.PrepareNativeAsync(plan);
            await decisions.DecideAsync(id, plan.Descriptor, true);
            await clock.ReserveAsync(id, plan.Descriptor, plan.Members.Select(m => PartitionOwnedNode.TimestampParticipant(m.Domain.Identity)).ToArray());
            return plan;
        }
        var earlier = await Prepare();
        var later = await Prepare();
        foreach (var owner in new[] { first, second })
        {
            var successor = owner.ApplyPartitionDecisionAsync(later.Operation).AsTask();
            await Task.Delay(30); // Ensure the successor owns the first delivery cohort.
            var predecessor = owner.ApplyPartitionDecisionAsync(earlier.Operation).AsTask();
            await Task.WhenAll(successor, predecessor).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(predecessor.Result!.Version.Value < successor.Result!.Version.Value);
        }
        Assert.AreEqual(2L, (await clock.ObserveAsync()).State.Closed);
    }

    private sealed class LostTimestampReply(IDatabaseTimestampStore inner) : IDatabaseTimestampStore
    {
        internal bool LoseNext;
        internal bool FailApplication;

        public string Identity => inner.Identity;

        public ValueTask<DatabaseTimestampObservation> ReadAsync(CancellationToken token) => inner.ReadAsync(token);

        public async ValueTask<bool> ReplaceAsync(long revision, DatabaseTimestampState state, CancellationToken token)
        {
            if (FailApplication && state.Tickets.Any(t => t.Applied.Count != 0))
            {
                FailApplication = false;
                throw new IOException("Injected failure before clock application persistence.");
            }
            bool applied = await inner.ReplaceAsync(revision, state, token);
            if (applied && LoseNext)
            {
                LoseNext = false;
                throw new IOException("Injected loss after durable clock publication.");
            }
            return applied;
        }
    }

    [TestMethod]
    public async Task Shared_clock_reopen_recovers_native_commit_before_clock_acknowledgement()
    {
        using var storage = new FileTimestampStore(Path.Combine(_root, "clock"), "domain");
        var failed = new LostTimestampReply(storage);
        var clock = new DatabaseSharedTimestampService(failed);
        var authority = new Authority();
        string path = Path.Combine(_root, "node");
        Guid operation = EntityOperationIds.New();
        byte[] capture;
        await using (var node = await PartitionOwnedNode.CreateAsync(path, DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestamps: clock))
        {
            var adapter = await node.CreateGraphExecutionAsync();
            capture = adapter.CaptureSave(new Item("one", "committed"), operation).Export();
            failed.FailApplication = true;
            await Assert.ThrowsExactlyAsync<EntityOperationCommittedException<DatabaseCommitReceipt>>(async () =>
                await adapter.ExecuteAsync(node.Domain, operation, capture));
            Assert.AreEqual(0L, (await clock.ObserveAsync()).State.Closed);
        }
        await using var reopened = await PartitionOwnedNode.OpenAsync(path, DatabaseProfileId.PropertyGraph,
            authority.Lease, Routing, authority, Options, timestamps: new DatabaseSharedTimestampService(storage));
        var replay = await reopened.CreateGraphExecutionAsync();
        var result = await replay.ExecuteAsync(reopened.Domain, operation, capture);
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, result.Receipt.Outcome);
        Assert.AreEqual(1L, (await clock.ObserveAsync()).State.Closed);
        Assert.AreEqual("committed", (await replay.ReadAsync<Item>(reopened.Domain, "one"))!.Value.Name);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Shared_clock_lost_replies_keep_exact_ticket_and_closed_outcome(bool etcd)
    {
        using var storage = new FileTimestampStore(Path.Combine(_root, "clock"), "domain");
        using var client = etcd ? new dotnet_etcd.EtcdClient(RequireLiveEtcdEndpoint(), configureChannelOptions: options => options.Credentials = Grpc.Core.ChannelCredentials.Insecure) : null;
        string key = "/entityforge/shared-clock-tests/" + Guid.NewGuid().ToString("N");
        IDatabaseTimestampStore selected = client is null ? storage : new EtcdTimestampStore(client, key, "domain");
        try
        {
            var transport = new LostTimestampReply(selected);
            var clock = new DatabaseSharedTimestampService(transport);
            await clock.EnrollAsync("a", 0);
            Guid id = EntityOperationIds.New();
            string descriptor = new('A', 64);
            transport.LoseNext = true;
            await Assert.ThrowsExactlyAsync<IOException>(async () => await clock.ReserveAsync(id, descriptor, ["a"]));
            var ticket = await new DatabaseSharedTimestampService(selected).ReserveAsync(id, descriptor, ["a"]);
            Assert.AreEqual(1L, ticket.Timestamp);
            await Assert.ThrowsExactlyAsync<EntityForgeDbIdempotencyException>(async () => await clock.ReserveAsync(id, new('B', 64), ["a"]));
            transport.LoseNext = true;
            await Assert.ThrowsExactlyAsync<IOException>(async () => await clock.AppliedAsync(ticket, "a", 1));
            await clock.AppliedAsync(ticket, "a", 1);
            Assert.AreEqual(1L, (await clock.ObserveAsync()).State.Closed);
            var rejected = await clock.ReserveAsync(EntityOperationIds.New(), descriptor, ["a"]);
            await clock.RejectLocalAsync(rejected, "a", 1);
            await Assert.ThrowsExactlyAsync<EntityOperationAbortedException>(async () =>
                await clock.ReserveAsync(rejected.Operation, descriptor, ["a"]));
            Assert.AreEqual(2L, (await clock.ObserveAsync()).State.Closed);
            await clock.RetireAsync(DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.AreEqual(0, (await clock.ObserveAsync()).State.Tickets.Length);
            await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(async () => await clock.ReserveAsync(id, descriptor, ["a"]));
        }
        finally
        {
            if (client is not null)
            {
                await client.DeleteAsync(new Etcdserverpb.DeleteRangeRequest { Key = Google.Protobuf.ByteString.CopyFromUtf8(key), RangeEnd = EtcdTimestampStore.PrefixEnd(key) });
            }
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Shared_clock_local_profiles_reopen_retain_and_transfer(DatabaseProfileId profile)
    {
        using var epochs = new FileServingEpochStore(Path.Combine(_root, "epochs"));
        var placements = new DurableServingEpochAuthority(epochs);
        var lease = await placements.AcquireAsync(new("a"), new("source"), new("https://source.test"),
            DistributedDatabaseGeneration.New(), new(1), TimeSpan.FromMinutes(5));
        using var storage = new FileTimestampStore(Path.Combine(_root, "clock"), "domain");
        var clock = new DatabaseSharedTimestampService(storage);
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "decisions"));
        string path = Path.Combine(_root, "source"), imagePath = Path.Combine(_root, "image");
        async Task Save(PartitionOwnedNode node, string name)
        {
            var operation = EntityOperationIds.New();
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                var adapter = await node.CreateGraphExecutionAsync();
                var request = adapter.CaptureSave(new Item("one", name), operation);
                await adapter.ExecuteAsync(node.Domain, operation, request.Export());
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                var adapter = await node.CreateRdfExecutionAsync();
                var request = adapter.CaptureSave(new Item("one", name), operation);
                await adapter.ExecuteAsync(node.Domain, operation, request.Export());
            }
            else
            {
                var adapter = await node.CreateSemanticExecutionAsync();
                var request = adapter.CaptureSave(new Item("one", name), new EntityForge.Db.Semantic.SemanticEntityWriteOperation(operation, DateTimeOffset.UtcNow));
                await adapter.ExecuteAsync(node.Domain, operation, request.Export());
            }
        }
        await using (var node = await PartitionOwnedNode.CreateAsync(path, profile, lease, Routing, placements, Options,
            transactionAuthority: decisions, timestamps: clock))
        {
            await Save(node, "before");
            var old = await clock.ReadAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            await Save(node, "after");
            var cut = await node.AcquireTimestampCutAsync(Guid.NewGuid(), old);
            Assert.IsTrue(cut.Position.Version.Value < node.Kernel.CurrentVersion.Value);
            await node.CompleteCutAsync(cut);
            await clock.ReleaseAsync(old.Id);
        }
        await using var reopened = await PartitionOwnedNode.OpenAsync(path, profile, lease, Routing, placements, Options,
            transactionAuthority: decisions, timestamps: clock);
        await Save(reopened, "reopened");
        await PartitionRetention.RetainAsync(decisions, [reopened], DateTimeOffset.UtcNow);
        Assert.AreEqual(0, (await clock.ObserveAsync()).State.Tickets.Length);
        var image = await reopened.SealAndExportAsync(new(Guid.NewGuid()), imagePath);
        await using var transfer = new PartitionOwnedTransfer(imagePath, Path.Combine(_root, "destination"), image, Routing,
            placements, new("destination"), new("https://destination.test"), Options, decisions, clock);
        var destination = await transfer.CompleteAsync();
        await Save(destination, "transferred");
        Assert.IsTrue((await clock.ObserveAsync()).State.Closed > 0);
    }

    [TestMethod]
    public async Task Shared_clock_closes_only_applied_prefix_and_retains_pinned_read_floor()
    {
        using var storage = new FileTimestampStore(Path.Combine(_root, "clock"), "domain");
        var clock = new DatabaseSharedTimestampService(storage);
        await clock.EnrollAsync("a", 0);
        await clock.EnrollAsync("b", 0);
        var first = await clock.ReserveAsync(EntityOperationIds.New(), new('A', 64), ["a", "b"]);
        var next = await clock.ReserveAsync(EntityOperationIds.New(), new('B', 64), ["a"]);
        Assert.AreEqual(1L, first.Timestamp);
        Assert.AreEqual(2L, next.Timestamp);
        await clock.AppliedAsync(first, "a", 1);
        var old = await clock.ReadAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        await clock.WaitTurnAsync(next, "a");
        await clock.AppliedAsync(next, "a", 2);
        Assert.AreEqual(0L, old.Timestamp);
        Assert.AreEqual(0L, old.Positions["a"]);
        var fresh = clock.ReadAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20)).AsTask();
        await Task.Delay(20);
        Assert.IsFalse(fresh.IsCompleted, "A new read cannot hide an already completed later transaction.");
        await clock.AppliedAsync(first, "b", 1);
        var current = await fresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2L, current.Timestamp);
        Assert.AreEqual(2L, current.Positions["a"]);
        Assert.AreEqual(0L, (await clock.RetentionFloorAsync())["a"]);
        await clock.ReleaseAsync(old.Id);
        await clock.ReleaseAsync(current.Id);
        Assert.AreEqual(2L, (await clock.RetentionFloorAsync())["a"]);
        await clock.RetireAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        await Assert.ThrowsExactlyAsync<EntityOperationExpiredException>(async () =>
            await clock.ReserveAsync(first.Operation, first.Descriptor, ["a", "b"]));
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, true)]
    [DataRow(DatabaseProfileId.Rdf, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, true)]
    [DataRow(DatabaseProfileId.Rdf, true, false)]
    public async Task Shared_timestamp_reads_old_cut_during_partial_application_then_new_cut(DatabaseProfileId profile, bool etcd, bool abort)
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
        using var storage = new FileTimestampStore(Path.Combine(_root, "clock"), "domain");
        using var client = etcd ? new dotnet_etcd.EtcdClient(RequireLiveEtcdEndpoint(), configureChannelOptions: options => options.Credentials = Grpc.Core.ChannelCredentials.Insecure) : null;
        string key = "/entityforge/shared-clock-tests/" + Guid.NewGuid().ToString("N");
        var clock = new DatabaseSharedTimestampService(client is null ? storage : new EtcdTimestampStore(client, key, "domain"));
        try
        {

            await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "decisions"));
            await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a,
                Options, transactionAuthority: decisions, timestamps: clock);
            await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b,
                Options, transactionAuthority: decisions, timestamps: clock);
            Guid operation = EntityOperationIds.New();
            var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
                [new(first.Domain, new(0), NativePayload(profile, routing, new("a"), operation)),
                    new(second.Domain, new(0), NativePayload(profile, routing, new("b"), operation))]);
            await decisions.BindAsync(plan);
            await first.PrepareNativeAsync(plan);
            if (!abort)
            {
                await second.PrepareNativeAsync(plan);
            }

            await decisions.DecideAsync(operation, plan.Descriptor, !abort);
            await first.ApplyPartitionDecisionAsync(operation);
            var old = await PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, _) => ValueTask.FromResult(cut.Position));
            Assert.IsTrue(old.Position.Positions.All(p => p.Version.Value == 0));
            await second.ApplyPartitionDecisionAsync(operation);
            var current = await PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, _) => ValueTask.FromResult(cut.Position));
            Assert.IsTrue(current.Position.Positions.All(p => abort ? p.Version.Value == 0 : p.Version.Value > 0));
            Assert.AreEqual(1L, (await clock.ObserveAsync()).State.Closed);
        }
        finally
        {
            if (client is not null)
            {
                await client.DeleteAsync(new Etcdserverpb.DeleteRangeRequest { Key = Google.Protobuf.ByteString.CopyFromUtf8(key), RangeEnd = EtcdTimestampStore.PrefixEnd(key) });
            }
        }
    }
}
