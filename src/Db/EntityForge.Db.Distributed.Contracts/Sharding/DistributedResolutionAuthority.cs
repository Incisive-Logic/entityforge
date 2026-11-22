namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Identifies a particular distributed database incarnation, preventing cross-generation operations.</summary>
/// <param name="Value">The database generation identifier.</param>
public readonly record struct DistributedDatabaseGeneration(Guid Value)
{
    /// <summary>Creates a new database generation with a fresh random identifier.</summary>
    public static DistributedDatabaseGeneration New() => new(Guid.NewGuid());
}
