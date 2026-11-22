namespace EntityForge.Db.Distributed.Sharding;

/// <summary>A renewal may have replaced the previous lease. Both exact identities
/// remain owned cleanup obligations; this is not proof of either success or failure.</summary>
/// <remarks>Admission must stop. Keep this exception until ReleaseAsync succeeds,
/// including when it is an inner exception of failed startup cleanup. Cleanup is
/// retryable and revokes only the grants minted or owned by this attempt.</remarks>
public sealed class ShardPlacementRenewalUncertainException : Exception
{
    private readonly IShardPlacementAuthority _authority;

    internal ShardPlacementRenewalUncertainException(IShardPlacementAuthority authority,
        ShardPlacementLease previous, ShardPlacementLease candidate, Exception cause)
        : base("The placement renewal outcome is unknown; its exact lease identities require cleanup.", cause)
    {
        _authority = authority;
        Previous = previous;
        Candidate = candidate;
    }

    /// <summary>Gets the lease known before the uncertain renewal.</summary>
    public ShardPlacementLease Previous
    {
        get;
    }

    /// <summary>Gets the candidate lease that may have replaced the previous lease.</summary>
    public ShardPlacementLease Candidate
    {
        get;
    }

    internal bool BelongsTo(IShardPlacementAuthority authority) => ReferenceEquals(authority, _authority);

    /// <summary>Releases this attempt's exact leases, including fencing a delayed
    /// replacement. Does not renew or release a separately observed current owner.</summary>
    public ValueTask ReleaseAsync(CancellationToken cancellationToken = default) =>
        _authority is IShardPlacementRenewalRecovery recovery
            ? recovery.ReleaseUncertainRenewalAsync(this, cancellationToken)
            : ValueTask.FromException(new NotSupportedException("The authority cannot reconcile uncertain renewal cleanup."));
}

/// <summary>Releases a candidate lease after a renewal whose result could not be established.</summary>
public interface IShardPlacementRenewalRecovery
{
    /// <summary>Fences delayed publication and releases the attempt's exact grants.
    /// Failure must remain observable; observing a newer lease does not authorize
    /// releasing that lease.</summary>
    ValueTask ReleaseUncertainRenewalAsync(ShardPlacementRenewalUncertainException attempt,
        CancellationToken cancellationToken = default);
}
