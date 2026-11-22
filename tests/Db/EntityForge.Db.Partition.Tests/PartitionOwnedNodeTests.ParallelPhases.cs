using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Parallel_conflicting_coordinated_operations_converge_without_stranded_reservations()
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
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            a.Lease, routing, a, Options, transactionAuthority: authority);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, transactionAuthority: authority);
        var templates = new[] { new LogicalShardId("a"), new LogicalShardId("b") }.Select(owner =>
        {
            var id = Guid.NewGuid();
            return GraphTransactionCodec.Decode(id, NativePayload(DatabaseProfileId.PropertyGraph, routing, owner, id));
        }).ToArray();
        byte[] Payload(Guid id, int index)
        {
            var root = ((CreateVertex)templates[index].Mutations[0]).Vertex;
            return GraphTransactionCodec.Encode(new(id, templates[index].Mutations, new(0), [new VertexRead(root)]));
        }
        var plans = Enumerable.Range(0, 16).Select(_ =>
        {
            var id = EntityOperationIds.New();
            return new PartitionTransactionPlan(id, routing.MapVersion,
                [new(first.Domain, new(0), Payload(id, 0)), new(second.Domain, new(0), Payload(id, 1))]);
        }).ToArray();
        var results = await Task.WhenAll(plans.Select(plan => new PartitionTransactionCoordinator(authority)
            .ExecuteAsync(plan, [first, second]).AsTask())).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(results.Count(result => result.Decision.Commit) <= 1);
        foreach (var node in new[] { first, second })
        {
            Assert.IsFalse((await node.Kernel.PartitionTransactions.ReadAsync()).Any(entry =>
                entry.State == EntityForge.Db.Durability.DurablePartitionTransactionState.Prepared));
            await using var freeze = await node.Kernel.PartitionTransactions.FreezeAsync();
        }
        foreach (var plan in plans)
        {
            Assert.IsNotNull(await authority.ReadDecisionAsync(plan.Operation));
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Parallel_partition_phase_drains_every_started_owner_before_returning_failure(bool live, bool delivery)
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
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            a.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.PropertyGraph, routing, live);
        var authority = lifetime.Authority;
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            a.Lease, routing, a, Options, transactionAuthority: authority);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph,
            b.Lease, routing, b, Options, transactionAuthority: authority);
        Guid operation = EntityOperationIds.New();
        var plan = new PartitionTransactionPlan(operation, routing.MapVersion,
            [new(first.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("a"), operation)),
                new(second.Domain, new(0), NativePayload(DatabaseProfileId.PropertyGraph, routing, new("b"), operation))]);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Fail()
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            failed.TrySetResult();
            throw new IOException("First owner reply failed while its peer still owns work.");
        }
        async ValueTask Pause()
        {
            blocked.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        var left = new PhaseParticipant(first) { BeforePrepare = delivery ? null : Fail, AfterApply = delivery ? Fail : null };
        var right = new PhaseParticipant(second) { AfterPrepare = delivery ? null : Pause, AfterApply = delivery ? Pause : null };
        var running = new PartitionTransactionCoordinator(authority).ExecuteAsync(plan, [left, right]).AsTask();
        try
        {
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsFalse(running.IsCompleted, "A failed peer cannot abandon a started owner call.");
            var decision = await authority.ReadDecisionAsync(operation);
            if (delivery)
            {
                Assert.IsTrue(decision!.Commit);
            }
            else
            {
                Assert.IsNull(decision, "Abort selection must wait for the durable preparation handoff.");
            }
        }
        finally
        {
            release.TrySetResult();
        }
        if (delivery)
        {
            var failure = await Assert.ThrowsExactlyAsync<PartitionDecisionDeliveryException>(() => running);
            Assert.IsTrue(failure.Decision.Commit);
            Assert.AreEqual(1L, second.Kernel.CurrentVersion.Value);
        }
        else
        {
            Assert.IsFalse((await running).Decision.Commit);
            Assert.IsFalse((await second.Kernel.PartitionTransactions.ReadAsync()).Any(entry => entry.State == EntityForge.Db.Durability.DurablePartitionTransactionState.Prepared));
        }
    }

    private sealed class PhaseParticipant(IPartitionTransactionParticipant inner) : IPartitionTransactionParticipant, IPartitionReadParticipant
    {
        internal Func<ValueTask>? BeforePrepare, AfterPrepare, AfterApply;
        internal Func<PartitionReadQuery, CancellationToken, ValueTask>? BeforeRead;
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? BeforeMapping;

        public ValueTask<PartitionReadCut> AcquireCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => ((IPartitionReadParticipant)inner).AcquireCutAsync(id, expires, token);

        public ValueTask<PartitionReadCut> AcquireHistoryProtectionAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => ((IPartitionReadParticipant)inner).AcquireHistoryProtectionAsync(id, expires, token);

        public ValueTask<PartitionReadCut> AcquireSnapshotCutAsync(Guid id, DateTimeOffset expires, CancellationToken token = default) => ((IPartitionReadParticipant)inner).AcquireSnapshotCutAsync(id, expires, token);

        public async ValueTask<byte[]> ReadCutAsync(PartitionReadCut cut, PartitionReadQuery query, CancellationToken token = default)
        {
            if (BeforeRead is not null)
            {
                await BeforeRead(query, token);
            }

            return await ((IPartitionReadParticipant)inner).ReadCutAsync(cut, query, token);
        }

        public ValueTask CompleteCutAsync(PartitionReadCut cut, CancellationToken token = default) => ((IPartitionReadParticipant)inner).CompleteCutAsync(cut, token);

        public ValueTask AbandonCutAsync(Guid id, CancellationToken token = default) => ((IPartitionReadParticipant)inner).AbandonCutAsync(id, token);

        public PartitionExecutionDomain Domain => inner.Domain;

        public string? TransactionAuthorityIdentity => inner.TransactionAuthorityIdentity;

        public async ValueTask PrepareNativeAsync(PartitionTransactionPlan plan, CancellationToken token = default)
            => _ = await PrepareWithTimestampAsync(plan, token);

        public async ValueTask<EntityForge.Db.Durability.DurableTimestampState?> PrepareWithTimestampAsync(PartitionTransactionPlan plan, CancellationToken token)
        {
            if (BeforePrepare is not null)
            {
                await BeforePrepare();
            }

            var vote = await inner.PrepareWithTimestampAsync(plan, token);
            if (AfterPrepare is not null)
            {
                await AfterPrepare();
            }

            return vote;
        }

        public async ValueTask<DatabaseCommitReceipt?> ApplyPartitionDecisionAsync(Guid operation, CancellationToken token = default)
        {
            var receipt = await inner.ApplyPartitionDecisionAsync(operation, token);
            if (AfterApply is not null)
            {
                await AfterApply();
            }

            return receipt;
        }

        public ValueTask<PartitionTransactionMember> PlanApplicationAsync(Guid operation, CancellationToken token = default) => inner.PlanApplicationAsync(operation, token);

        public ValueTask<PartitionCommitResult> ExecuteMappedAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token = default) => inner.ExecuteMappedAsync(operation, captured, token);

        public ValueTask<PartitionCommitResult> ExecuteApplicationAsync(Guid operation, CancellationToken token = default) => inner.ExecuteApplicationAsync(operation, token);

        public ValueTask PrepareLocalApplicationAsync(Guid operation, CancellationToken token = default) => inner.PrepareLocalApplicationAsync(operation, token);

        public async ValueTask InitializeApplicationMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default)
        {
            if (BeforeMapping is not null)
            {
                await BeforeMapping(captured, token);
            }

            await inner.InitializeApplicationMappingAsync(captured, token);
        }

        public ValueTask<PartitionMemberResolution> ResolveApplicationAsync(Guid operation, bool abort, CancellationToken token = default) => inner.ResolveApplicationAsync(operation, abort, token);
    }
}
