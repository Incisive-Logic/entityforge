using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    private sealed class GovernanceClock : IDatabaseBoundedClock
    {
        public Guid Domain { get; } = Guid.NewGuid();

        public DatabaseClockInterval Read()
        {
            long now = DateTime.UtcNow.Ticks;
            return new(now, now);
        }
    }

    private sealed class SessionGovernanceEvaluator : ISemanticGovernanceEvaluator
    {
        internal int Calls;
        internal bool Reject;
        internal int LargestSession;

        public bool Supports(SemanticGovernanceRule rule) => rule.Identifier == new Uri("urn:test:partition-policy");

        public ValueTask<SemanticGovernanceDecision> EvaluateAsync(SemanticGovernanceRule rule,
            SemanticSessionEnvelope candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LargestSession = Math.Max(LargestSession, candidate.Pages.Sum(page => page.Changes.Count));
            return ValueTask.FromResult(new SemanticGovernanceDecision(rule.Identifier,
                Reject ? SemanticGovernanceDecisionKind.Violated : SemanticGovernanceDecisionKind.Satisfied, []));
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Governed_partition_sessions_enforce_complete_request_at_every_owner(bool live, bool missingEvaluator)
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
            a.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.Semantic, routing, live);
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, a.Lease.Placement.Claim.Generation.Value).EntityStore<PartitionSemanticTree>();
        var request = store.CaptureSave(new PartitionSemanticTree
        {
            Id = "governed",
            Name = "before",
            Children = [new("child", "value")]
        }, SemanticEntityWriteOperation.Create());
        var original = SemanticEntityDeliveryCodec.Session(await PartitionSemanticPlanning.OriginalAsync(request));
        var concepts = original.Pages.SelectMany(page => page.Changes).Select(change => change.Subject.Concept).Distinct().ToArray();
        var clock = new GovernanceClock();
        var ea = new SessionGovernanceEvaluator();
        var eb = new SessionGovernanceEvaluator();
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Semantic,
            a.Lease, routing, a, Options, transactionAuthority: lifetime.Authority, governanceEvaluators: [ea], timestampClock: clock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Semantic,
            b.Lease, routing, b, Options, transactionAuthority: lifetime.Authority, governanceEvaluators: missingEvaluator ? [] : [eb], timestampClock: clock);
        var client = new PartitionApplicationClient(lifetime.Authority, routing, [first, second], ReadClock);
        foreach (var owner in new[] { first, second })
        {
            await client.PublishSemanticGovernanceAsync(SemanticEntityWriteOperation.Create().OperationId, owner.Domain.Partition, new(new("urn:test:partition-policy"),
                SemanticGovernanceRuleKind.Policy, new("urn:test:definition"), "1", true,
                new(concepts, SemanticGovernanceEnforcement.RejectViolation)));
        }

        if (missingEvaluator)
        {
            await Assert.ThrowsExceptionAsync<EntityOperationAbortedException>(async () =>
                await client.DeliverAsync(request.OperationId, request.Export()));
            Assert.AreEqual(EntityOperationState.Aborted, (await client.ResolveAsync(request.OperationId, default)).State);
            Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(request.OperationId, default)).State);
            return;
        }
        await client.DeliverAsync(request.OperationId, request.Export());
        Assert.IsTrue(ea.Calls > 0 && eb.Calls > 0);
        Assert.AreEqual(original.Pages.Sum(page => page.Changes.Count), ea.LargestSession);
        Assert.AreEqual(ea.LargestSession, eb.LargestSession);
        eb.Reject = true;
        int callsBeforeRejection = eb.Calls;
        var rejected = store.CaptureSave(new PartitionSemanticTree
        {
            Id = "governed",
            Name = "rejected",
            Children = [new("child", "changed")]
        }, SemanticEntityWriteOperation.Create());
        await Assert.ThrowsExceptionAsync<EntityOperationAbortedException>(async () =>
            await client.DeliverAsync(rejected.OperationId, rejected.Export()));
        Assert.IsTrue(eb.Calls > callsBeforeRejection, "Preparation must enforce policy before selecting the abort.");
        Assert.AreEqual(EntityOperationState.Aborted, (await client.ResolveAsync(rejected.OperationId, default)).State);
        Assert.AreEqual(EntityOperationState.Aborted, (await client.AbortAsync(rejected.OperationId, default)).State);
    }
}
