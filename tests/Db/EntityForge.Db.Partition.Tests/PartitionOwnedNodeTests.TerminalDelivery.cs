using System.Reflection;
using System.Text.Json;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Terminal_etcd_splits_large_read_batches_and_rejects_changed_decision_and_domain()
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_TEST_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_TEST_ETCD_ENDPOINT.");
            return;
        }
        const int ReceiveLimit = 512 * 1024;
        const int MemberBytes = 128 * 1024;
        using var client = new EtcdClient(endpoint, configureChannelOptions: options =>
        {
            options.Credentials = ChannelCredentials.Insecure;
            options.MaxReceiveMessageSize = ReceiveLimit;
        });
        string prefix = "/entityforge/terminal-observation-test/" + Guid.NewGuid().ToString("N") + "/";
        try
        {
            var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
            Guid generation = Guid.NewGuid();
            var authority = await GrpcEtcdPartitionTransactionAuthority.OpenAsync(client, prefix, generation, DatabaseProfileId.PropertyGraph, routing);
            var a = new PartitionExecutionDomain(generation, new("a"), routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid());
            var b = new PartitionExecutionDomain(generation, new("b"), routing.Fingerprint, DatabaseProfileId.PropertyGraph, Guid.NewGuid());
            var plans = new List<PartitionTransactionPlan>();
            using var watchDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var notifications = authority.ReadNotificationsAsync(watchDeadline.Token).GetAsyncEnumerator();
            Assert.IsTrue(await notifications.MoveNextAsync());
            Assert.IsTrue(notifications.Current.Reset);
            var nextNotification = notifications.MoveNextAsync().AsTask();
            for (int index = 0; index < PartitionTerminalObservation.MaximumBatch; index++)
            {
                var plan = new PartitionTransactionPlan(EntityOperationIds.New(), routing.MapVersion,
                    [new(a, new(0), new byte[MemberBytes]), new(b, new(0), new byte[] { 1 })]);
                await authority.BindAsync(plan);
                await authority.DecideAsync(plan.Operation, plan.Descriptor, false);
                plans.Add(plan);
            }
            Assert.IsTrue(await nextNotification.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(plans[0].Operation, notifications.Current.Operation);
            var ids = plans.Select(plan => plan.Operation).ToArray();
            // The combined body exceeds the deliberately small channel receive limit;
            // singleton bodies fit. Splitting must preserve every exact association.
            var observations = await authority.ReadTerminalAsync(ids);
            CollectionAssert.AreEqual(ids, observations.Select(value => value.Operation).ToArray());
            Assert.IsTrue(observations.All(value => value.Failure is null && value.Decision is { Commit: false }));
            var compact = await authority.ReadTerminalAsync(ids, selected: plans.ToDictionary(plan => plan.Operation, plan => plan.Descriptor));
            Assert.IsTrue(compact.All(value => value.Failure is null && value.Plan is null && value.Application is null && value.Decision is { Commit: false }));
            await client.PutAsync(new PutRequest
            {
                Key = ByteString.CopyFromUtf8(prefix + "decisions/" + ids[0].ToString("N")),
                Value = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(new PartitionTransactionDecision(ids[1], plans[0].Descriptor, false)))
            });
            observations = await authority.ReadTerminalAsync([ids[0], ids[1]]);
            Assert.IsInstanceOfType<InvalidDataException>(observations[0].Failure);
            Assert.IsNull(observations[1].Failure);
            compact = await authority.ReadTerminalAsync([ids[1]], selected: new Dictionary<Guid, string> { [ids[1]] = new('F', 64) });
            Assert.IsInstanceOfType<InvalidDataException>(compact[0].Failure);
            // Even replacing the domain with the SAME bytes changes its authoritative revision.
            var domain = await client.GetAsync(prefix + "domain");
            await client.PutAsync(new PutRequest { Key = domain.Kvs[0].Key, Value = domain.Kvs[0].Value });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => authority.ReadTerminalAsync([ids[1]]).AsTask());
        }
        finally
        {
            await client.DeleteAsync(new DeleteRangeRequest
            {
                Key = ByteString.CopyFromUtf8(prefix),
                RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0")
            });
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Terminal_observation_preserves_identity_missing_and_expired_member_outcomes(bool live)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var placement = new Authority();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            placement.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.PropertyGraph, routing, live);
        var authority = lifetime.Authority;
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, transactionAuthority: authority);
        Guid id = EntityOperationIds.New();
        var other = new PartitionExecutionDomain(node.Domain.Generation, new("b"), routing.Fingerprint, node.Domain.Profile, Guid.NewGuid());
        var plan = new PartitionTransactionPlan(id, routing.MapVersion,
            [new(node.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), id)),
                new(other, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), id))]);
        await authority.BindAsync(plan);
        await authority.DecideAsync(id, plan.Descriptor, false);
        Guid missing = EntityOperationIds.New();
        var observed = await authority.ReadTerminalAsync([id, missing]);
        Assert.AreEqual(plan.Descriptor, observed[0].Plan!.Descriptor);
        Assert.AreEqual(id, observed[0].Application!.Operation);
        Assert.IsFalse(observed[0].Decision!.Commit);
        Assert.IsNull(observed[0].Failure);
        Assert.AreEqual(missing, observed[1].Operation);
        Assert.IsNull(observed[1].Plan);
        Assert.IsNull(observed[1].Failure);
        var compact = await authority.ReadTerminalAsync([id], selected: new Dictionary<Guid, string> { [id] = plan.Descriptor });
        Assert.IsNull(compact[0].Plan);
        Assert.IsNull(compact[0].Application);
        Assert.AreEqual(plan.Descriptor, compact[0].Decision!.Descriptor);
        var wrong = await authority.ReadTerminalAsync([id], selected: new Dictionary<Guid, string> { [id] = new('F', 64) });
        Assert.IsInstanceOfType<InvalidDataException>(wrong[0].Failure);
        await authority.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        Guid expired = EntityOperationIds.New(DateTimeOffset.UtcNow.AddMinutes(-5));
        observed = await authority.ReadTerminalAsync([expired, id]);
        Assert.IsNotNull(observed[0].Failure);
        Assert.IsNull(observed[1].Failure);
        Assert.AreEqual(plan.Descriptor, observed[1].Decision!.Descriptor);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => authority.ReadTerminalAsync([id, id]).AsTask());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Terminal_worker_drains_owned_cohort_and_isolates_missing_and_cancelled_members(bool live, bool shutdown)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var placement = new Authority();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            placement.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.PropertyGraph, routing, live);
        var authority = new TerminalAuthority(lifetime.Authority);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, transactionAuthority: authority);
        var other = new PartitionExecutionDomain(node.Domain.Generation, new("b"), routing.Fingerprint, node.Domain.Profile, Guid.NewGuid());
        var plans = new List<PartitionTransactionPlan>();
        for (int index = 0; index < 3; index++)
        {
            Guid id = EntityOperationIds.New();
            var selected = GraphTransactionCodec.Decode(id, NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), id));
            var payload = GraphTransactionCodec.Encode(new(id, selected.Mutations, new(0),
                [new VertexRead(((CreateVertex)selected.Mutations[0]).Vertex)]));
            var plan = new PartitionTransactionPlan(id, routing.MapVersion,
                [new(node.Domain, new(0), payload),
                    new(other, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), id))]);
            await authority.BindAsync(plan);
            await node.PrepareNativeAsync(plan);
            await authority.DecideAsync(id, plan.Descriptor, true);
            plans.Add(plan);
        }
        authority.Batches.Clear(); // Only the terminal-worker cohorts are asserted below.
        authority.Pause = true;
        var first = node.ApplyPartitionDecisionAsync(plans[0].Operation).AsTask();
        await authority.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var cancelled = node.ApplyPartitionDecisionAsync(plans[1].Operation, cancellation.Token).AsTask();
        var missing = node.ApplyPartitionDecisionAsync(EntityOperationIds.New()).AsTask();
        var last = node.ApplyPartitionDecisionAsync(plans[2].Operation).AsTask();
        Task? closing = null;
        Task<PartitionOwnedNode.Maintenance>? maintenance = null;
        try
        {
            await WaitTerminalQueueAsync(node, 3);
            cancellation.Cancel();
            if (shutdown)
            {
                closing = node.DisposeAsync().AsTask();
            }
            else
            {
                maintenance = node.DrainForMaintenanceAsync(CancellationToken.None).AsTask();
            }

            Assert.IsFalse((closing ?? maintenance!).IsCompleted, "Seal/close must drain every owned terminal callback.");
        }
        finally
        {
            authority.Resume.TrySetResult();
        }
        async Task<DatabaseCommitReceipt?> Receipt(Task<DatabaseCommitReceipt?> result)
        {
            try
            {
                return await result.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (EntityOperationCommittedException<DatabaseCommitReceipt> failure) when (shutdown)
            {
                return failure.Receipt;
            } // Shutdown can revoke completion placement, never erase known commitment.
        }
        Assert.AreEqual(plans[0].Operation, (await Receipt(first))!.TransactionId);
        Assert.AreEqual(plans[2].Operation, (await Receipt(last))!.TransactionId);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => missing);
        try
        {
            await cancelled;
            Assert.Fail("Queued cancellation was ignored.");
        }
        catch (OperationCanceledException)
        {

        }
        CollectionAssert.AreEqual(new[] { 1, 2 }, authority.Batches.ToArray());
        if (closing is not null)
        {
            await closing.WaitAsync(TimeSpan.FromSeconds(15));
        }
        else
        {
            await using (var handle = await maintenance!.WaitAsync(TimeSpan.FromSeconds(15)))
            {
                Assert.AreEqual(2, (await handle.ReadPreparationsAsync(CancellationToken.None)).Count(entry => entry.State == DurablePartitionTransactionState.Committed));
            }
            // The cancelled member's authoritative commit remains recoverable by exact delivery.
            var recovered = await node.ApplyPartitionDecisionAsync(plans[1].Operation);
            Assert.AreEqual(plans[1].Operation, recovered!.TransactionId);
            Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, (await node.ApplyPartitionDecisionAsync(plans[0].Operation))!.Outcome);
        }
    }

    private static async Task WaitTerminalQueueAsync(PartitionOwnedNode node, int count)
    {
        var delivery = typeof(PartitionOwnedNode).GetField("_terminalDelivery", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node)!;
        var sync = delivery.GetType().GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(delivery)!;
        var queue = (System.Collections.ICollection)delivery.GetType().GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(delivery)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (sync)
            {
                if (queue.Count == count)
                {
                    return;
                }
            }
            await Task.Delay(5, deadline.Token);
        }
    }

    private sealed class TerminalAuthority(IPartitionTransactionAuthority inner) : IPartitionTransactionAuthority, IPartitionDecisionNotifications
    {
        internal readonly System.Threading.Channels.Channel<PartitionDecisionNotification> Notifications =
            System.Threading.Channels.Channel.CreateBounded<PartitionDecisionNotification>(64);

        internal readonly TaskCompletionSource StreamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<PartitionDecisionNotification> ReadNotificationsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            yield return new(Guid.Empty, true);
            StreamStarted.TrySetResult();
            await foreach (var notification in Notifications.Reader.ReadAllAsync(token))
            {
                yield return notification;
            }
        }

        internal bool Pause;
        internal List<int> Batches = [];
        internal TaskCompletionSource Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Identity => inner.Identity;

        public ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default) => inner.RequireUnenrolledAsync(partition, token);

        public ValueTask EnrollAsync(PartitionExecutionDomain d, bool create, CancellationToken t = default) => inner.EnrollAsync(d, create, t);

        public ValueTask BindApplicationAsync(PartitionApplicationBinding b, CancellationToken t = default) => inner.BindApplicationAsync(b, t);

        public ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid id, CancellationToken t = default) => inner.ReadApplicationAsync(id, t);

        public ValueTask BindAsync(PartitionTransactionPlan p, CancellationToken t = default) => inner.BindAsync(p, t);

        public ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid id, CancellationToken t = default) => inner.ReadPlanAsync(id, t);

        public ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid id, CancellationToken t = default) => inner.ReadDecisionAsync(id, t);

        public async ValueTask<IReadOnlyList<PartitionTerminalObservation>> ReadTerminalAsync(IReadOnlyList<Guid> ids, CancellationToken t = default,
            IReadOnlyDictionary<Guid, string>? selected = null)
        {
            Batches.Add(ids.Count);
            if (Pause)
            {
                Pause = false;
                Observed.TrySetResult();
                await Resume.Task.WaitAsync(TimeSpan.FromSeconds(20));
            }
            return await inner.ReadTerminalAsync(ids, t, selected);
        }

        public ValueTask<PartitionTransactionDecision> DecideAsync(Guid id, string d, bool commit, CancellationToken t = default) => inner.DecideAsync(id, d, commit, t);

        public ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid id, string d, CancellationToken t = default) => inner.AbortApplicationAsync(id, d, t);

        public ValueTask RetainAsync(long floor, CancellationToken t = default) => inner.RetainAsync(floor, t);
    }
}
