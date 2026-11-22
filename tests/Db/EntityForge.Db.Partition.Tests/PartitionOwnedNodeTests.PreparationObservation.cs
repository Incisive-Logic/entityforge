using EntityForge.Db.Durability;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false, "initial-association")]
    [DataRow(true, "initial-association")]
    [DataRow(false, "post-association")]
    [DataRow(true, "post-association")]
    [DataRow(false, "post-failure")]
    [DataRow(true, "post-failure")]
    public async Task Preparation_observation_failure_preserves_exact_owned_state(bool live, string fault)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var placement = new Authority();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            placement.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.PropertyGraph, routing, live);
        var authority = new PreparationObservationAuthority(lifetime.Authority);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph,
            placement.Lease, routing, placement, Options, transactionAuthority: authority);
        var other = new PartitionExecutionDomain(node.Domain.Generation, new("b"), routing.Fingerprint, node.Domain.Profile, Guid.NewGuid());
        var id = EntityOperationIds.New();
        var plan = new PartitionTransactionPlan(id, routing.MapVersion,
            [new(node.Domain, new(0), NativePayload(node.Domain.Profile, routing, new("a"), id)),
                new(other, new(0), NativePayload(node.Domain.Profile, routing, new("b"), id))]);
        await authority.BindAsync(plan);
        authority.Fault = fault;
        if (fault == "post-failure")
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => node.PrepareNativeAsync(plan).AsTask());
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.PrepareNativeAsync(plan).AsTask());
        }

        var entries = await node.Kernel.PartitionTransactions.ReadAsync();
        Assert.AreEqual(fault == "initial-association" ? 0 : 1, entries.Count);
        if (entries.Count != 0)
        {
            Assert.AreEqual(DurablePartitionTransactionState.Prepared, entries[0].State);
        }

        Assert.AreEqual(1, authority.Full);
        Assert.AreEqual(fault == "initial-association" ? 0 : 1, authority.Compact);
        authority.Fault = null;
        await node.PrepareNativeAsync(plan);
        await authority.DecideAsync(id, plan.Descriptor, true);
        var receipt = await node.ApplyPartitionDecisionAsync(id);
        Assert.AreEqual(id, receipt!.TransactionId);
        var version = node.Kernel.CurrentVersion;
        await node.PrepareNativeAsync(plan); // Exact committed retry must not become another mutation.
        Assert.AreEqual(DatabaseCommitOutcome.IdempotentReplay, (await node.ApplyPartitionDecisionAsync(id))!.Outcome);
        Assert.AreEqual(version, node.Kernel.CurrentVersion);
    }

    private sealed class PreparationObservationAuthority(IPartitionTransactionAuthority inner) : IPartitionTransactionAuthority
    {
        internal string? Fault;
        internal int Full, Compact;

        public string Identity => inner.Identity;

        public ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default) => inner.RequireUnenrolledAsync(partition, token);

        public ValueTask EnrollAsync(PartitionExecutionDomain d, bool create, CancellationToken t = default) => inner.EnrollAsync(d, create, t);

        public ValueTask BindApplicationAsync(PartitionApplicationBinding b, CancellationToken t = default) => inner.BindApplicationAsync(b, t);

        public ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid id, CancellationToken t = default) => inner.ReadApplicationAsync(id, t);

        public ValueTask BindAsync(PartitionTransactionPlan p, CancellationToken t = default) => inner.BindAsync(p, t);

        public ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid id, CancellationToken t = default) => inner.ReadPlanAsync(id, t);

        public ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid id, CancellationToken t = default) => inner.ReadDecisionAsync(id, t);

        public async ValueTask<IReadOnlyList<PartitionTerminalObservation>> ReadTerminalAsync(IReadOnlyList<Guid> ids,
            CancellationToken t = default, IReadOnlyDictionary<Guid, string>? selected = null)
        {
            var result = await inner.ReadTerminalAsync(ids, t, selected);
            if (selected is null)
            {
                Full++;
                if (Fault == "initial-association")
                {
                    return [result[0] with { Operation = Guid.NewGuid() }];
                }
            }
            else
            {
                Compact++;
                if (Fault == "post-association")
                {
                    return [result[0] with
                    {
                        Decision = new(Guid.NewGuid(), selected[ids[0]], true)
                    }];
                }

                if (Fault == "post-failure")
                {
                    return [result[0] with { Failure = new IOException("Lost post-prepare observation.") }];
                }
            }
            return result;
        }

        public ValueTask<PartitionTransactionDecision> DecideAsync(Guid id, string d, bool commit, CancellationToken t = default) => inner.DecideAsync(id, d, commit, t);

        public ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid id, string d, CancellationToken t = default) => inner.AbortApplicationAsync(id, d, t);

        public ValueTask RetainAsync(long floor, CancellationToken t = default) => inner.RetainAsync(floor, t);
    }
}
