using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

// Database-owned execution, deliberately separate from application queries and local engine types.
// Complete-write validation runs after source replacement expansion and before any commit effects.
internal interface ISemanticMappedExecution
{
    Guid DatabaseIncarnation
    {
        get;
    }

    ValueTask<ISemanticEntityReadScope> AcquireMappedReadAsync(
        Uri sourceSystem, Uri sourcePartition, TimeSpan duration, CancellationToken cancellationToken);

    ValueTask<SessionCommitResult?> ResolveMappedReplayAsync(SemanticSessionEnvelope session,
        CancellationToken cancellationToken);

    ValueTask<SessionCommitResult> ApplyMappedSessionAsync(SemanticSessionEnvelope session,
        Action<IReadOnlyList<SemanticEntityChange>> validateCompleteChanges, CancellationToken cancellationToken);
}
