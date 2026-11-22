using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>A mapped entity and the selected application's contribution guard captured with its retained snapshot.
/// Initial means this writer has no prior contribution progress; After identifies its exact predecessor.</summary>
/// <param name="Value">The materialized entity, which may include assertions from other sources.</param>
/// <param name="SnapshotVersion">The retained database snapshot used for the entire entity closure.</param>
/// <param name="ContributionGuard">Pass to PrepareSave or PrepareDelete to guard this writer's contribution.</param>
public sealed record SemanticEntityReadResult<T>(T Value, DatabaseVersion SnapshotVersion,
    SemanticSourceProgressPrecondition ContributionGuard);
