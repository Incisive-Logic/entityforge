namespace EntityForge.Db.Routing;

/// <summary>Identifies a shard within a routing topology.</summary>
/// <param name="Value">The topology-defined shard identifier; this value type does not validate its format.</param>
public readonly record struct ShardId(string Value);

/// <summary>Identifies the routing-map version used to discover transaction participants.</summary>
/// <param name="Value">The version assigned by the routing implementation.</param>
public readonly record struct ShardMapVersion(long Value);

/// <summary>Associates discovered transaction participants with the routing-map version used to select them.</summary>
/// <param name="MapVersion">The captured routing-map version to validate before using the participants.</param>
/// <param name="Participants">The shard identifiers selected by the router. The record retains the supplied list.</param>
public sealed record RoutedParticipantSet(ShardMapVersion MapVersion, IReadOnlyList<ShardId> Participants);

/// <summary>Discovers transaction participants from logical dependencies and validates captured routing decisions.</summary>
public interface IShardRouter
{
    /// <summary>Selects the participants required by a set of logical dependencies.</summary>
    /// <typeparam name="TDependency">The logical dependency representation understood by the caller and routing implementation.</typeparam>
    /// <param name="dependencies">The dependencies whose owning shards must participate.</param>
    /// <returns>The participants and the map version used to discover them.</returns>
    RoutedParticipantSet Route<TDependency>(IReadOnlyList<TDependency> dependencies);

    /// <summary>Checks whether a previously captured participant set remains valid for this router.</summary>
    /// <param name="routed">The captured participant set and routing-map version.</param>
    /// <remarks>Validation does not reserve participants or commit a transaction. A changed map requires participant discovery to be retried.</remarks>
    void Validate(RoutedParticipantSet routed);
}

/// <summary>Routes every nonempty dependency set to the single local shard.</summary>
/// <remarks>The local map has a fixed version. This router does not provide distributed participant discovery.</remarks>
public sealed class LocalShardRouter : IShardRouter
{
    private const int MinimumDependencyCount = 1;
    private const long InitialShardMapVersion = 1;
    private static readonly RoutedParticipantSet LocalRoute =
        new(new(InitialShardMapVersion), new[] { LocalShardTopology.Shard });

    /// <summary>Returns the local participant set for a nonempty dependency list.</summary>
    /// <typeparam name="TDependency">The dependency type; local routing does not inspect individual values.</typeparam>
    /// <param name="dependencies">A non-null list containing at least one logical dependency.</param>
    /// <returns>The local shard with the fixed local routing-map version.</returns>
    /// <exception cref="ArgumentException">The dependency list is empty.</exception>
    public RoutedParticipantSet Route<TDependency>(IReadOnlyList<TDependency> dependencies)
    {
        if (dependencies.Count < MinimumDependencyCount)
        {
            throw new ArgumentException("A write requires logical dependencies.", nameof(dependencies));
        }

        return LocalRoute;
    }

    /// <summary>Requires the captured map version and participants to match the local topology.</summary>
    /// <param name="routed">The routing decision to validate.</param>
    /// <exception cref="ArgumentNullException">The routing decision is null.</exception>
    /// <exception cref="ShardMapChangedException">The captured map version differs from the fixed local version.</exception>
    /// <exception cref="EntityForgeDatabaseCapabilityException">The participant list is not exactly the single local shard.</exception>
    public void Validate(RoutedParticipantSet routed)
    {
        ArgumentNullException.ThrowIfNull(routed);
        if (routed.MapVersion != LocalRoute.MapVersion)
        {
            throw new ShardMapChangedException(routed.MapVersion, LocalRoute.MapVersion);
        }

        LocalShardTopology.RequireLocalParticipant(routed);
    }
}

/// <summary>Reports that a transaction's captured routing-map version is no longer current.</summary>
/// <param name="captured">The version used for participant discovery.</param>
/// <param name="current">The version required by the router.</param>
/// <remarks>Rediscover participants before retrying the affected operation.</remarks>
public sealed class ShardMapChangedException(ShardMapVersion captured, ShardMapVersion current)
    : EntityForgeDbException(
        $"Shard map changed from version {captured.Value} to {current.Value}; retry participant discovery.")
{
    /// <summary>Gets the routing-map version captured during participant discovery.</summary>
    public ShardMapVersion Captured { get; } = captured;

    /// <summary>Gets the routing-map version required when validation failed.</summary>
    public ShardMapVersion Current { get; } = current;
}

/// <summary>Defines and validates the topology accepted by single-shard local execution.</summary>
public static class LocalShardTopology
{
    /// <summary>Gets the identifier of the single local shard.</summary>
    public static ShardId Shard { get; } = new("local-0");

    /// <summary>Requires exactly one participant with the local shard identifier.</summary>
    /// <param name="routed">The participant set to inspect.</param>
    /// <remarks>This check validates participant identity only; it does not validate the routing-map version.</remarks>
    /// <exception cref="ArgumentNullException">The participant set is null.</exception>
    /// <exception cref="EntityForgeDatabaseCapabilityException">Execution requires a participant set other than the single local shard.</exception>
    public static void RequireLocalParticipant(RoutedParticipantSet routed)
    {
        ArgumentNullException.ThrowIfNull(routed);
        if (routed.Participants.Count != 1 || routed.Participants[0] != Shard)
        {
            throw new EntityForgeDatabaseCapabilityException(
                "The routed transaction requires a distributed coordinator.");
        }
    }
}
