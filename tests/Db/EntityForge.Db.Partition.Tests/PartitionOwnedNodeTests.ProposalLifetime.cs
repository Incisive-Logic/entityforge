using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, "maintenance")]
    [DataRow(DatabaseProfileId.Rdf, "maintenance")]
    [DataRow(DatabaseProfileId.PropertyGraph, "shutdown")]
    [DataRow(DatabaseProfileId.Rdf, "shutdown")]
    [DataRow(DatabaseProfileId.PropertyGraph, "transfer")]
    [DataRow(DatabaseProfileId.Rdf, "transfer")]
    [DataRow(DatabaseProfileId.Semantic, "maintenance")]
    [DataRow(DatabaseProfileId.Semantic, "shutdown")]
    [DataRow(DatabaseProfileId.Semantic, "transfer")]
    public async Task Proposals_overlap_completion_checks_but_exclusive_lifecycle_drains_them(DatabaseProfileId profile, string action)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var placement = new Authority();
        var paused = new PausedProposalPlacement(placement);
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile,
            placement.Lease, routing, paused, Options, transactionAuthority: decisions);
        using var http = new HttpClient();
        var other = new DatabasePartitionIdentity(node.Domain.Generation, profile, routing.Fingerprint, "b", Guid.NewGuid());
        PartitionApplicationBinding Binding()
        {
            var operation = EntityOperationIds.New();
            ReadOnlyMemory<byte> Request(string owner)
            {
                for (int i = 0; ; i++)
                {
                    byte[] bytes = profile == DatabaseProfileId.PropertyGraph
                        ? new HttpGraphEntityClient(http, node.Domain.Generation).EntityStore<Item>().CaptureSave(new(i.ToString(), "proposal"), new GraphEntityWriteOptions(operation)).Export()
                        : new HttpRdfEntityClient(http, node.Domain.Generation).EntityStore<Item>().CaptureSave(new(i.ToString(), "proposal"), new RdfEntityWriteOptions(operation)).Export();
                    if (PartitionApplicationRouting.Owner(node.Domain.Generation, profile, routing, operation, bytes).Value == owner)
                    {
                        return bytes;
                    }
                }
            }
            return new(operation, [node.Domain.Identity, other], [Request("a"), Request("b")]);
        }
        var semanticRequest = profile == DatabaseProfileId.Semantic
            ? new HttpSemanticEntityClient(http, node.Domain.Generation).EntityStore<Item>()
                .CaptureSave(new("local-proposal", "value"), SemanticEntityWriteOperation.Create()).Export() : null;
        if (semanticRequest is not null)
        {
            await node.InitializeApplicationMappingAsync(semanticRequest);
        }

        async Task Propose(CancellationToken token = default)
        {
            if (semanticRequest is null)
            {
                await node.ProposeApplicationAsync(Binding(), token);
            }
            else
            {
                await node.ProposeSemanticAsync(PartitionSemanticRequests.Encode([semanticRequest]),
                PartitionReadCuts.MaximumDuration, token);
            }
        }
        paused.Arm();
        var first = Propose();
        Task? exclusive = null;
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The first still owns its completion placement check. A disjoint
            // proposal must be able to finish without releasing that check.
            await Propose().WaitAsync(TimeSpan.FromSeconds(10));
            exclusive = action switch
            {
                "shutdown" => node.DisposeAsync().AsTask(),
                "transfer" => node.SealAndExportAsync(new(Guid.NewGuid()), Path.Combine(_root, "image")).AsTask(),
                _ => node.DrainForMaintenanceAsync(default).AsTask()
            };
            Assert.IsFalse(exclusive.IsCompleted, "Exclusive work must join accepted proposals.");
            using var cancellation = new CancellationTokenSource();
            var queued = Propose(cancellation.Token);
            Assert.IsFalse(queued.IsCompleted, "Exclusive admission must prevent another proposal entering.");
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        }
        finally
        {
            paused.Release.TrySetResult();
        }
        await first;
        await exclusive!.WaitAsync(TimeSpan.FromSeconds(10));
        if (action == "maintenance")
        {
            await ((Task<PartitionOwnedNode.Maintenance>)exclusive).Result.DisposeAsync();
            await Propose();
        }
        else
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => Propose());
        }
    }

    [TestMethod]
    public async Task Proposal_lifetime_bounds_cancellation_and_idempotent_release()
    {
        var lifetime = new PartitionProposalLifetime();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => lifetime.Enter(1, cancelled.Token));
        var leases = Enumerable.Range(0, PartitionOwnedExecution.DefaultMaximumJobs).Select(_ => lifetime.Enter(1, default)).ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() => lifetime.Enter(1, default));
        var drained = lifetime.DrainAsync();
        Assert.IsFalse(drained.IsCompleted);
        leases[0].Dispose();
        leases[0].Dispose();
        Assert.IsFalse(drained.IsCompleted);
        foreach (var lease in leases.Skip(1))
        {
            lease.Dispose();
        }

        await drained;
        using (lifetime.Enter(PartitionOwnedExecution.DefaultMaximumBytes, default))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => lifetime.Enter(1, default));
        }

        await lifetime.DrainAsync();
    }

    private sealed class PausedProposalPlacement(Authority inner) : IShardPlacementAuthority
    {
        private int _remaining;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Arm() => _remaining = 2;

        public async ValueTask<bool> ValidateAsync(ShardPlacementClaim claim, CancellationToken token = default)
        {
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return await inner.ValidateAsync(claim, token);
        }

        public ValueTask<ShardPlacement?> ResolveAsync(LogicalShardId shard, CancellationToken token = default) => inner.ResolveAsync(shard, token);

        public ValueTask<ShardPlacementAuthorityState?> ResolveAuthorityStateAsync(LogicalShardId shard, CancellationToken token = default) => inner.ResolveAuthorityStateAsync(shard, token);

        public ValueTask<ShardPlacementLease> AcquireAsync(LogicalShardId shard, ClusterNodeId owner, Uri endpoint, DistributedDatabaseGeneration generation, ShardMapVersion version, TimeSpan lifetime, CancellationToken token = default) => inner.AcquireAsync(shard, owner, endpoint, generation, version, lifetime, token);

        public ValueTask<ShardPlacementLease> RenewAsync(ShardPlacementLease lease, TimeSpan lifetime, CancellationToken token = default) => inner.RenewAsync(lease, lifetime, token);

        public ValueTask ReleaseAsync(ShardPlacementLease lease, CancellationToken token = default) => inner.ReleaseAsync(lease, token);

        public ValueTask<ShardPlacementLease> TransferAsync(ShardTransferId transfer, ShardPlacementLease current, ClusterNodeId owner, Uri endpoint, ShardMapVersion map, TimeSpan lifetime, CancellationToken token = default) => inner.TransferAsync(transfer, current, owner, endpoint, map, lifetime, token);
    }
}
