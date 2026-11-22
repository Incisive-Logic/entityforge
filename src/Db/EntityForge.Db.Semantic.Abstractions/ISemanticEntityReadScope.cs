using EntityForge.Semantics;
using EntityForge.Query.Projection;

namespace EntityForge.Db.Semantic;

/// <summary>One retained snapshot for the entire recursive materialization. Unavailable state must throw,
/// never appear absent. Disposal releases retained resources even when reads fail.</summary>
public interface ISemanticEntityReadScope : IAsyncDisposable
{
    /// <summary>Gets the single retained version used by every read.</summary>
    DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets the writer's progress at that same snapshot.</summary>
    SemanticSourcePosition? ContributionGuard
    {
        get;
    }

    /// <summary>Reads one entity's scalar projection at the retained snapshot.</summary>
    ValueTask<EntityProjectionProjectionResult> ProjectAsync(SemanticEntityProjectionQuery query, CancellationToken cancellationToken = default);

    /// <summary>Reads relationships at the retained snapshot.</summary>
    ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(SemanticRelationshipTraversalQuery query, CancellationToken cancellationToken = default);

    /// <summary>Checks that the retained view remains valid before returning a completed materialization.</summary>
    ValueTask ValidateAsync(CancellationToken cancellationToken = default);
}
