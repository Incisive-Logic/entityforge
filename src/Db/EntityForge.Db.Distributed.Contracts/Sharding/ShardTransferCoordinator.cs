namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Identifies one logical shard ownership transfer, including recovery of that transfer.</summary>
/// <param name="Value">The identifier retained across transfer retries and recovery.</param>
public readonly record struct ShardTransferId(Guid Value)
{
    /// <summary>Creates a transfer identifier using a fresh random GUID.</summary>
    public static ShardTransferId New() => new(Guid.NewGuid());
}

/// <summary>Carries the etcd key, revision, lease and value used to fence a transfer controller.</summary>
/// <param name="Key">The absolute controller authority key.</param>
/// <param name="ModRevision">The positive modification revision to compare atomically.</param>
/// <param name="LeaseId">The positive lease identifier that keeps the controller authority live.</param>
/// <param name="Value">The nonempty authority value to compare with the key's current value.</param>
public sealed record ShardTransferControllerAuthority(string Key, long ModRevision, long LeaseId,
    ReadOnlyMemory<byte> Value)
{
    /// <summary>Rejects a relative or blank key, nonpositive revision or lease identifier, or empty proof value.</summary>
    public void Validate()
    {
        if (String.IsNullOrWhiteSpace(Key) || !Key.StartsWith('/') || ModRevision <= 0 || LeaseId <= 0 ||
            Value.IsEmpty)
        {
            throw new InvalidOperationException("A valid controller authority proof is required.");
        }
    }
}

/// <summary>Requires a live controller authority proof when committing a shard ownership transfer.</summary>
public interface IControllerFencedShardPlacementAuthority
{
    /// <summary>Transfers placement only while both the source claim and controller proof remain authoritative.</summary>
    ValueTask<ShardPlacementLease> TransferAsync(ShardTransferId transferId, ShardPlacementLease current,
        ClusterNodeId newOwner, Uri newParticipantEndpoint, ShardMapVersion newMapVersion, TimeSpan lifetime,
        ShardTransferControllerAuthority controller, CancellationToken cancellationToken = default);
}
