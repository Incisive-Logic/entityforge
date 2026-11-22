using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Identifies a logical shard independently of the node currently serving it.</summary>
/// <param name="Value">The stable, ordinally compared shard identifier.</param>
public readonly record struct LogicalShardId(string Value)
{
    /// <summary>Returns the logical shard identifier.</summary>
    public override string ToString() => Value;
}

/// <summary>Identifies the version of the logical shard routing map.</summary>
/// <param name="Value">The routing map version shared by participants.</param>
public readonly record struct ShardMapVersion(long Value);

/// <summary>Captures the routing map version and the logical shards available to the router.</summary>
/// <param name="Version">The version associated with the captured shard list.</param>
/// <param name="Shards">The logical shards in routing order.</param>
public sealed record ShardMapSnapshot(
    ShardMapVersion Version,
    IReadOnlyList<LogicalShardId> Shards)
{
    /// <summary>Creates a snapshot from distinct shards sorted by ordinal identifier; rejects an empty shard set.</summary>
    public ShardMapSnapshot(ShardMapVersion version, IEnumerable<LogicalShardId> shards)
        : this(version, shards.Distinct().OrderBy(shard => shard.Value, StringComparer.Ordinal).ToArray())
    {
        if (Shards.Count == 0)
        {
            throw new ArgumentException("A shard map requires at least one shard.", nameof(shards));
        }
    }
}
