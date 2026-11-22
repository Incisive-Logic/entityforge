using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Compiles evaluated object graphs through the same local planner as the resolved Semantic entity store.</summary>
public sealed class SemanticEntityInputCompiler
{
    /// <summary>Builds the pure source-contribution compiler from configuration, without a database connection.</summary>
    public static SemanticEntityInputCompiler FromStructure(Guid destination, SemanticEntityStructure structure,
        SemanticApplicationIdentity application, SemanticEntityStoreLimits? limits = null)
    {
        if (destination == Guid.Empty)
        {
            throw new ArgumentException("A destination identity is required.", nameof(destination));
        }

        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(application);
        return new(SemanticEntityPlan.FromDefinition(structure.ExportDefinition(), application,
            limits ?? new SemanticEntityStoreLimits(), null), destination);
    }

    private readonly SemanticEntityPlan _plan;
    private readonly Guid _destination;

    internal SemanticEntityInputCompiler(SemanticEntityPlan plan, Guid destination)
    {
        _plan = plan;
        _destination = destination;
        RootManifest = plan.Root.Manifest!;
        Manifests = plan.Manifests;
    }

    /// <summary>Gets the root contract within the mapping closure.</summary>
    public EntityMappingManifest RootManifest
    {
        get;
    }

    /// <summary>Gets the same durable mapping closure required by the POCO producer.</summary>
    public IReadOnlyList<EntityMappingManifest> Manifests
    {
        get;
    }

    /// <summary>Freezes complete replacement. Omitted guard is unguarded; use Initial or After(position) explicitly.</summary>
    public SemanticEntityWrite PrepareSave(Entity input, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null) => _plan.PrepareSave(_destination, input, operation, guard?.Predecessor, guard is not null);

    /// <summary>Freezes root and owned-state removal while retaining independent references.</summary>
    public SemanticEntityWrite PrepareDelete(object identity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null) => _plan.PrepareDeleteCore(_destination, identity, operation, guard?.Predecessor, guard is not null);
}
