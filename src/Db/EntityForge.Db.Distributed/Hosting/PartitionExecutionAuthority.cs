using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Shared by profile adapters. Composition owns this supervisor and durable fence
// until every admitted partition job has drained. This is not a TTL authority cache.
internal sealed class PartitionExecutionAuthority
{
    internal Func<Guid, ReadOnlyMemory<byte>, Func<ValueTask<PartitionCommitResult>>, CancellationToken, ValueTask<PartitionCommitResult>>? TimestampWrite
    {
        get; set;
    }

    private readonly PartitionExecutionDomain _domain;
    private readonly ShardPlacementClaim _claim;
    private readonly ShardPlacementLeaseSupervisor _supervisor;
    private readonly FileShardFencedOperationGate _fence;
    private readonly Func<bool>? _ready;

    internal PartitionExecutionAuthority(PartitionExecutionDomain domain, DistributedRoutingBinding routing,
        ShardPlacementLeaseSupervisor supervisor, FileShardFencedOperationGate fence, Func<bool>? ready = null)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(fence);
        _claim = supervisor.CurrentLease.Placement.Claim;
        if (_claim.Generation.Value != domain.Generation || _claim.Shard != domain.Partition ||
            _claim.MapVersion != routing.MapVersion || routing.Fingerprint != domain.RoutingFingerprint ||
            !routing.Shards.Contains(domain.Partition))
        {
            throw new InvalidDataException("Placement and partition execution domain differ.");
        }

        _domain = domain;
        _supervisor = supervisor;
        _fence = fence;
        _ready = ready;
    }

    internal void RequireDomain(PartitionExecutionDomain domain)
    {
        if (domain != _domain)
        {
            throw new InvalidDataException("The supervised authority belongs to another partition.");
        }
    }

    internal ValueTask<T> ExecuteAsync<T>(Func<ValueTask<T>> operation) => ExecuteCoreAsync(operation, false);

    // Only bounded, read-only query jobs may defer admission validation. Their
    // intermediate pages are provisional; the coordinator must join completion
    // before exposing a result. Fresh completion validation still owns the fence.
    // No authority evidence is cached or transferred to another query job.
    internal ValueTask<T> ExecuteQueryAsync<T>(Func<ValueTask<T>> operation) => ExecuteCoreAsync(operation, true);

    internal ValueTask<T> ExecutePendingQueryAsync<T>(Func<ValueTask<T>> operation) => ExecuteCoreAsync(operation, true, true);

    private async ValueTask<T> ExecuteCoreAsync<T>(Func<ValueTask<T>> operation, bool deferredRead, bool pending = false)
    {
        if (_ready?.Invoke() == false)
        {
            throw new InvalidOperationException("Native home participant recovery is incomplete.");
        }

        using var measured = DistributedReadTelemetry.Measure("partition.authority.execution");
        if (_supervisor.CurrentLease.Placement.Claim != _claim)
        {
            throw new StaleShardPlacementException(_claim.Shard);
        }

        await using var lease = await _supervisor.AcquireOperationAsync(
            validateAuthority: !deferredRead && !_supervisor.HasDurableServingEpoch).ConfigureAwait(false);
        if (_supervisor.CurrentLease.Placement.Claim != _claim)
        {
            throw new StaleShardPlacementException(_claim.Shard);
        }

        return await _fence.ExecuteAsync(_claim, async token =>
        {
            token.ThrowIfCancellationRequested();
            lease.VerifyLocalAuthority();
            T result = await operation().ConfigureAwait(false);
            // Ordinary completion retains the fence through fresh validation.
            // Qualified query-home jobs release only provisional read evidence;
            // their caller must observe all completed claims before public success.
            using (DistributedReadTelemetry.Measure("partition.authority.completion-check"))
            {
                if (pending || _supervisor.HasDurableServingEpoch)
                {
                    lease.VerifyLocalAuthority();
                }
                else
                {
                    await lease.VerifyAuthorityAsync().ConfigureAwait(false);
                }
            }

            return result;
        }, lease.Token).ConfigureAwait(false);
    }
}
