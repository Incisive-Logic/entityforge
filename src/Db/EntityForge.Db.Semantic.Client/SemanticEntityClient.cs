using EntityForge.Db.Mapping;
using EntityForge.Semantics;
using EntityForge.Query.Projection;

namespace EntityForge.Db.Semantic;

/// <summary>Application execution and retained reads for Semantic mapped entities.</summary>
public interface ISemanticEntityClient : IEntityOperationClient<SemanticEntityRequest, SemanticEntityWrite, SessionCommitResult>, ISemanticChangeWriter
{
    /// <summary>Gets the destination incarnation carried by captured and prepared operations.</summary>
    Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Gets the dedicated contribution-revision lifecycle for this same destination.</summary>
    ISemanticContributionRevisionWriter Revisions
    {
        get;
    }

    /// <summary>Lowers a captured operation without executing or reserving it.</summary>
    ValueTask<SemanticEntityWrite> PrepareAsync(SemanticEntityRequest request, CancellationToken cancellationToken = default);

    /// <summary>Validates an established mapping closure and retains one snapshot and contribution guard. Must not register or change mappings.</summary>
    ValueTask<ISemanticEntityReadScope> OpenReadAsync(IReadOnlyList<EntityMappingManifest> manifests,
        Uri sourceSystem, Uri sourcePartition, TimeSpan duration, CancellationToken cancellationToken = default);
}
