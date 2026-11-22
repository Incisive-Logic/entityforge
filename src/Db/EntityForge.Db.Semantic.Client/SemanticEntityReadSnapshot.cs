namespace EntityForge.Db.Semantic;

/// <summary>A materialized entity and its snapshot version, without a contribution precondition for subsequent writes.</summary>
/// <param name="Value">The materialized entity.</param>
/// <param name="SnapshotVersion">The consistent snapshot used to read the entity.</param>
public sealed record SemanticEntityReadSnapshot<T>(T Value, DatabaseVersion SnapshotVersion) where T : class;
