namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Coordinates admitted shard work with fencing advancement and ownership transfer.</summary>
public interface IShardFencedOperationGate
{
    /// <summary>Excludes fence advancement from admitted work. Callbacks must own their native
    /// synchronization; a gate may execute requests under the same fence concurrently.</summary>
    ValueTask<T> ExecuteAsync<T>(ShardPlacementClaim claim,
        Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>Seals the source ownership epoch against new work and returns a handle that must be disposed or retired.</summary>
    ValueTask<IShardFenceSeal> SealAsync(ShardPlacementClaim source,
        CancellationToken cancellationToken = default);
}

/// <summary>Owns a source epoch seal while an ownership transfer is completed or abandoned.</summary>
public interface IShardFenceSeal : IAsyncDisposable
{
    /// <summary>Permanently retires the sealed source epoch in favour of the supplied destination claim.</summary>
    ValueTask RetireAsync(ShardPlacementClaim destination, CancellationToken cancellationToken = default);
}
