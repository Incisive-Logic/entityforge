using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Identifies a cluster node independently of its network endpoint.</summary>
/// <param name="Value">The stable node identifier used in placement claims.</param>
public readonly record struct ClusterNodeId(string Value);

/// <summary>Identifies an ownership epoch so stale owners can be fenced out.</summary>
/// <param name="Value">The authority-issued fencing value carried by a placement claim.</param>
public readonly record struct ShardFencingToken(long Value);

/// <summary>Binds one logical shard to an owner, database generation, routing map and fencing epoch.</summary>
/// <param name="Shard">The logical shard covered by this claim.</param>
/// <param name="Owner">The node claiming ownership.</param>
/// <param name="Generation">The database incarnation to which this claim belongs.</param>
/// <param name="MapVersion">The routing map version associated with this placement.</param>
/// <param name="FencingToken">The ownership epoch used to reject stale operations.</param>
public sealed record ShardPlacementClaim(LogicalShardId Shard, ClusterNodeId Owner,
    DistributedDatabaseGeneration Generation, ShardMapVersion MapVersion, ShardFencingToken FencingToken);

/// <summary>Combines a shard ownership claim with its participant protocol endpoint.</summary>
/// <param name="Claim">The authority-issued ownership claim.</param>
/// <param name="ParticipantEndpoint">The absolute endpoint at which the owner serves participant requests.</param>
public sealed record ShardPlacement(ShardPlacementClaim Claim, Uri ParticipantEndpoint);

/// <summary>Combines a placement with the authority lease that keeps it live.</summary>
/// <param name="Placement">The placement held by the lease.</param>
/// <param name="LeaseId">The authority's lease identifier; it is distinct from the fencing token.</param>
public sealed record ShardPlacementLease(ShardPlacement Placement, long LeaseId);

/// <summary>Reports the current placement lease and any transfer associated with it.</summary>
/// <param name="Lease">The current authority lease.</param>
/// <param name="TransferId">The transfer that produced the placement, or null for a placement without transfer metadata.</param>
public sealed record ShardPlacementAuthorityState(ShardPlacementLease Lease, ShardTransferId? TransferId);

/// <summary>Records a completed ownership cutover so its destination lease can be recovered.</summary>
/// <param name="TransferId">The transfer whose cutover was committed.</param>
/// <param name="SourceClaim">The source ownership claim superseded by the transfer.</param>
/// <param name="Destination">The destination placement established by cutover.</param>
/// <param name="LeaseLifetime">The destination lease lifetime recorded for recovery.</param>
public sealed record ShardTransferCutoverReceipt(ShardTransferId TransferId,
    ShardPlacementClaim SourceClaim, ShardPlacement Destination, TimeSpan LeaseLifetime);

/// <summary>Recovers transferred placements from durable cutover evidence without repeating the transfer.</summary>
public interface IRecoverableShardTransferPlacementAuthority
{
    /// <summary>Observes an existing matching lease without renewing it, or recreates
    /// an absent placement from its durable cutover receipt. Renewal belongs to the
    /// destination's supervised lifetime.</summary>
    ValueTask<ShardPlacementLease> RecoverTransferredLeaseAsync(ShardTransferId transferId,
        ShardPlacementClaim sourceClaim, ClusterNodeId destinationOwner, Uri participantEndpoint,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides authoritative shard placement, lease renewal and fencing validation.</summary>
public interface IShardPlacementAuthority
{
    /// <summary>Resolves the current placement for a logical shard, returning null when no live placement exists.</summary>
    ValueTask<ShardPlacement?> ResolveAsync(LogicalShardId shard,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves lease-aware placement state for transfer recovery. Authorities without this capability fail with NotSupportedException.</summary>
    ValueTask<ShardPlacementAuthorityState?> ResolveAuthorityStateAsync(LogicalShardId shard,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShardPlacementAuthorityState?>(new NotSupportedException(
            "This placement authority does not expose lease-aware transfer recovery."));

    /// <summary>Acquires a bounded placement lease for an unowned shard using the supplied node, generation and routing version.</summary>
    ValueTask<ShardPlacementLease> AcquireAsync(LogicalShardId shard, ClusterNodeId owner,
        Uri participantEndpoint, DistributedDatabaseGeneration generation, ShardMapVersion mapVersion,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>Renews a current placement lease for the requested lifetime; stale ownership must not be revived.</summary>
    ValueTask<ShardPlacementLease> RenewAsync(ShardPlacementLease lease, TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether the supplied claim still matches authoritative live ownership.</summary>
    ValueTask<bool> ValidateAsync(ShardPlacementClaim claim, CancellationToken cancellationToken = default);

    /// <summary>Releases the supplied ownership lease without releasing a successor owner's lease.</summary>
    ValueTask ReleaseAsync(ShardPlacementLease lease, CancellationToken cancellationToken = default);

    /// <summary>Atomically transfers the current placement to a new owner and endpoint. Authorities without transfer support fail with NotSupportedException.</summary>
    ValueTask<ShardPlacementLease> TransferAsync(ShardTransferId transferId, ShardPlacementLease current,
        ClusterNodeId newOwner,
        Uri newParticipantEndpoint, ShardMapVersion newMapVersion, TimeSpan lifetime,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShardPlacementLease>(new NotSupportedException(
            "This placement authority does not support atomic ownership transfer."));
}

/// <summary>Configures etcd endpoints, the placement namespace and bounded authority request retries.</summary>
/// <param name="Endpoints">One or more absolute etcd endpoint URIs.</param>
/// <param name="KeyPrefix">The absolute placement key prefix, including a leading and trailing slash.</param>
/// <param name="MaximumCompareRetries">The positive maximum number of compare-and-swap attempts under contention.</param>
/// <param name="EndpointTimeout">The positive finite timeout per endpoint request, or null to use DefaultEndpointTimeout.</param>
public sealed record EtcdShardPlacementOptions(IReadOnlyList<Uri> Endpoints, string KeyPrefix,
    int MaximumCompareRetries = 32, TimeSpan? EndpointTimeout = null)
{
    /// <summary>The default authority request timeout of ten seconds.</summary>
    public static readonly TimeSpan DefaultEndpointTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Rejects missing endpoints, malformed key prefixes, nonpositive retry limits and nonfinite or nonpositive timeouts.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Endpoints);
        if (Endpoints.Count == 0 || Endpoints.Any(endpoint => endpoint is null || !endpoint.IsAbsoluteUri))
        {
            throw new ArgumentException("At least one absolute etcd endpoint is required.");
        }

        if (String.IsNullOrWhiteSpace(KeyPrefix) || !KeyPrefix.StartsWith('/') || !KeyPrefix.EndsWith('/'))
        {
            throw new ArgumentException("The placement key prefix must be an absolute path ending in '/'.");
        }

        if (MaximumCompareRetries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCompareRetries));
        }

        TimeSpan timeout = EndpointTimeout ?? DefaultEndpointTimeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(EndpointTimeout));
        }
    }
}
