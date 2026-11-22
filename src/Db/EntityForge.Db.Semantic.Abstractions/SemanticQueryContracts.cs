using EntityForge.Semantics;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Semantic;

/// <summary>Represents a versioned, provenance-bearing semantic field assertion.</summary>
/// <param name="AssertionId">The assertion identity.</param>
/// <param name="Subject">The subject entity.</param>
/// <param name="Field">The asserted property term.</param>
/// <param name="Value">The asserted typed value.</param>
/// <param name="Provenance">The source and derivation metadata.</param>
/// <param name="BeginVersion">The inclusive visibility start.</param>
/// <param name="EndVersion">The exclusive visibility end, or null when not ended.</param>
public sealed record SemanticFieldEvidence(
    Guid AssertionId,
    SemanticEntityIdentity Subject,
    SemanticTerm Field,
    SemanticValue Value,
    SemanticProvenance Provenance,
    DatabaseVersion BeginVersion,
    DatabaseVersion? EndVersion);

/// <summary>Selects constraints and policies to evaluate during a semantic query.</summary>
/// <param name="ConstraintIdentifiers">The constraint identifiers to evaluate.</param>
/// <param name="PolicyIdentifiers">The policy identifiers to evaluate.</param>
public sealed record SemanticQueryGovernance(
    IReadOnlyList<Uri> ConstraintIdentifiers,
    IReadOnlyList<Uri> PolicyIdentifiers)
{
    /// <summary>Gets a selection that performs no query-time governance evaluation.</summary>
    public static SemanticQueryGovernance None { get; } = new(Array.Empty<Uri>(), Array.Empty<Uri>());
}

/// <summary>Selects semantic field evidence equal to a typed semantic value.</summary>
/// <param name="Field">The property term to query.</param>
/// <param name="Value">The typed equality operand.</param>
/// <param name="Governance">Optional query-time governance selection.</param>
public sealed record SemanticFieldEqualityQuery(
    SemanticTerm Field,
    SemanticValue Value,
    SemanticQueryGovernance? Governance = null);

/// <summary>Groups equality queries so one immutable snapshot can batch physical and evidence reads.</summary>
/// <param name="Queries">The ordered equality queries.</param>
public sealed record SemanticFieldEqualityBatchQuery(
    IReadOnlyList<SemanticFieldEqualityQuery> Queries);

/// <summary>Returns one ordered evidence collection for each query in a semantic equality batch.</summary>
/// <param name="Evidence">Evidence collections corresponding to input query order.</param>
public sealed record SemanticFieldEqualityBatchResult(
    IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>> Evidence);

/// <summary>Selects semantic string values within optional inclusive bounds.</summary>
/// <param name="Field">The property term to query.</param>
/// <param name="LowerInclusive">The inclusive lower string bound, or null when unbounded below.</param>
/// <param name="UpperInclusive">The inclusive upper string bound, or null when unbounded above.</param>
/// <param name="Governance">Optional query-time governance selection.</param>
public sealed record SemanticStringRangeQuery(
    SemanticTerm Field,
    string? LowerInclusive,
    string? UpperInclusive,
    SemanticQueryGovernance? Governance = null);

/// <summary>Returns semantic field evidence together with the catalog placement used to read it.</summary>
/// <param name="Evidence">The matching assertions.</param>
/// <param name="CandidateCount">The reported candidate count.</param>
/// <param name="StorageKind">The physical property placement used.</param>
/// <param name="CatalogVersion">The property placement catalog version used.</param>
public sealed record SemanticFieldQueryResult(
    IReadOnlyList<SemanticFieldEvidence> Evidence,
    long CandidateCount,
    SemanticPropertyStorageKind StorageKind,
    SemanticPropertyStorageCatalogVersion CatalogVersion);

/// <summary>Selects entities supported as members of a semantic concept.</summary>
public enum SemanticConceptMatchMode
{
    /// <summary>Matches only assertions made directly against the requested concept.</summary>
    Direct,

    /// <summary>Includes instances of narrower and equivalent concepts under the supported entailment profile.</summary>
    Entailed
}

/// <summary>Selects entities supported as members of a semantic concept.</summary>
/// <param name="Concept">The concept whose members are selected.</param>
/// <param name="Governance">Optional query-time governance selection.</param>
/// <param name="MatchMode">Whether supported concept entailments may contribute matches.</param>
public sealed record SemanticConceptMembershipQuery(
    SemanticTerm Concept,
    SemanticQueryGovernance? Governance = null,
    SemanticConceptMatchMode MatchMode = SemanticConceptMatchMode.Entailed);

/// <summary>Identifies an entity and the assertions supporting its concept membership.</summary>
/// <param name="Entity">The matched entity identity.</param>
/// <param name="SupportingAssertionIds">The assertions supporting membership.</param>
/// <param name="RequestedConcept">The requested concept, when separately recorded.</param>
/// <param name="SupportingEntailment">The supporting named-term entailments, when supplied.</param>
public sealed record SemanticEntityMembershipEvidence(
    SemanticEntityIdentity Entity,
    IReadOnlyList<Guid> SupportingAssertionIds,
    SemanticTerm? RequestedConcept = null,
    IReadOnlyList<SemanticAxiom>? SupportingEntailment = null)
{
    /// <summary>Gets whether concept membership required standards-based entailment.</summary>
    public bool IsInferred => RequestedConcept is not null && Entity.Concept != RequestedConcept;
}

/// <summary>Represents a versioned, provenance-bearing semantic relationship assertion.</summary>
/// <param name="AssertionId">The assertion identity.</param>
/// <param name="Subject">The source entity.</param>
/// <param name="Relationship">The asserted relationship term.</param>
/// <param name="Target">The target entity.</param>
/// <param name="Provenance">The source and derivation metadata.</param>
/// <param name="BeginVersion">The inclusive visibility start.</param>
/// <param name="EndVersion">The exclusive visibility end, or null when not ended.</param>
public sealed record SemanticRelationshipEvidence(
    Guid AssertionId,
    SemanticEntityIdentity Subject,
    SemanticTerm Relationship,
    SemanticEntityIdentity Target,
    SemanticProvenance Provenance,
    DatabaseVersion BeginVersion,
    DatabaseVersion? EndVersion);

/// <summary>Selects outgoing or incoming semantic relationships.</summary>
public enum SemanticRelationshipDirection
{
    /// <summary>Traverses from the entity as source toward relationship targets.</summary>
    Outgoing,

    /// <summary>Traverses from the entity as target toward relationship sources.</summary>
    Incoming
}

/// <summary>Selects relationships adjacent to one semantic entity.</summary>
/// <param name="Entity">The entity at the selected endpoint.</param>
/// <param name="Direction">The traversal direction relative to that entity.</param>
/// <param name="Relationship">A relationship term restriction, or null for all terms.</param>
public sealed record SemanticRelationshipTraversalQuery(
    SemanticEntityIdentity Entity,
    SemanticRelationshipDirection Direction,
    SemanticTerm? Relationship = null);

/// <summary>Selects relationships adjacent to several semantic entities through one read snapshot.</summary>
/// <param name="Entities">The entities at the selected endpoints.</param>
/// <param name="Direction">The traversal direction relative to those entities.</param>
/// <param name="Relationship">A relationship term restriction, or null for all terms.</param>
public sealed record SemanticRelationshipTraversalBatchQuery(
    IReadOnlyList<SemanticEntityIdentity> Entities,
    SemanticRelationshipDirection Direction,
    SemanticTerm? Relationship = null);

/// <summary>Selects one semantic identity and a representation-neutral entity document projection.</summary>
public sealed record SemanticEntityProjectionQuery
{
    /// <summary>Selects an identity and a matching-concept projection without executing it.</summary>
    /// <param name="identity">The nonnull entity identity.</param>
    /// <param name="projection">The nonnull projection whose concept matches the identity's concept.</param>
    /// <exception cref="ArgumentException">An argument is null or the concepts differ.</exception>
    public SemanticEntityProjectionQuery(SemanticEntityIdentity identity, EntityProjectionDefinition projection)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        if (projection.Concept != identity.Concept)
        {
            throw new ArgumentException("The projection concept must match the entity identity concept.",
                nameof(projection));
        }
    }

    /// <summary>Gets the identity to project.</summary>
    public SemanticEntityIdentity Identity
    {
        get;
    }

    /// <summary>Gets the requested partial entity view.</summary>
    public EntityProjectionDefinition Projection
    {
        get;
    }
}

/// <summary>Explains physical identity and relationship access for an entity document projection.</summary>
/// <param name="Identity">Entity selected for projection.</param>
/// <param name="SnapshotVersion">Exact snapshot used to plan or execute the projection.</param>
/// <param name="LogicalOperation">Logical projection operation description.</param>
/// <param name="IdentityAccessPath">Physical access path used to locate the entity.</param>
/// <param name="RelationshipAccessPaths">Physical access paths for requested relationships.</param>
/// <param name="EstimatedRoots">Estimated number of projected root entities.</param>
/// <param name="ActualRoots">Observed root count when execution analysis was requested.</param>
/// <param name="ExecutionDuration">Measured execution duration when available.</param>
public sealed record SemanticEntityProjectionExplanation(
    SemanticEntityIdentity Identity,
    DatabaseVersion SnapshotVersion,
    string LogicalOperation,
    string IdentityAccessPath,
    IReadOnlyList<string> RelationshipAccessPaths,
    long EstimatedRoots,
    long? ActualRoots,
    TimeSpan? ExecutionDuration);

/// <summary>Describes one physical hop in a neutral recursive entity projection.</summary>
/// <param name="Depth">Depth of this traversal step in the projection.</param>
/// <param name="Relationship">Relationship term traversed.</param>
/// <param name="Direction">Traversal direction relative to the current entity.</param>
/// <param name="TargetConcept">Concept selected at the target.</param>
/// <param name="PhysicalAccessPath">Physical relationship lookup used for this step.</param>
public sealed record SemanticEntityTraversalStepExplanation(
    int Depth,
    SemanticTerm Relationship,
    EntityRelationshipDirection Direction,
    SemanticTerm TargetConcept,
    string PhysicalAccessPath);

/// <summary>Explains candidate acquisition and execution for a representation-neutral entity query.</summary>
/// <param name="SnapshotVersion">Exact snapshot used by the query.</param>
/// <param name="StatisticsVersion">Version of planning statistics, when available.</param>
/// <param name="LogicalOperation">Logical query operation description.</param>
/// <param name="CandidateAccessPaths">Candidate acquisition paths considered by the plan.</param>
/// <param name="EstimatedCandidateCounts">Estimated counts corresponding to candidate access paths; null denotes an unavailable estimate.</param>
/// <param name="CandidateEstimates">Candidate estimates with their quality and source.</param>
/// <param name="AcquiredCandidateCounts">Measured candidate counts by access path when execution analysis is available.</param>
/// <param name="EstimatedCandidates">Estimated combined candidate count.</param>
/// <param name="ActualCandidates">Measured combined candidate count.</param>
/// <param name="ActualRoots">Measured root result count.</param>
/// <param name="OrderedEntriesScanned">Measured count of entries scanned through an ordered access path.</param>
/// <param name="ExecutionDuration">Measured execution duration.</param>
/// <param name="AppliedScopes">Scope restrictions applied to the query.</param>
/// <param name="RelationshipTraversalSteps">Planned relationship traversal steps.</param>
public sealed record SemanticEntityQueryExplanation(
    DatabaseVersion SnapshotVersion,
    DatabaseVersion? StatisticsVersion,
    string LogicalOperation,
    IReadOnlyList<string> CandidateAccessPaths,
    IReadOnlyList<long?> EstimatedCandidateCounts,
    IReadOnlyList<SemanticQueryEstimate> CandidateEstimates,
    IReadOnlyList<long>? AcquiredCandidateCounts,
    long? EstimatedCandidates,
    long? ActualCandidates,
    long? ActualRoots,
    long? OrderedEntriesScanned,
    TimeSpan? ExecutionDuration,
    IReadOnlyList<string> AppliedScopes,
    IReadOnlyList<SemanticEntityTraversalStepExplanation> RelationshipTraversalSteps);

/// <summary>Classifies how strongly a semantic query estimate is supported.</summary>
public enum SemanticQueryEstimateQuality
{
    /// <summary>No usable estimate is available.</summary>
    Unavailable = 0,

    /// <summary>The reported count is exact for the estimate's source context.</summary>
    Exact = 1,

    /// <summary>The count is an upper bound rather than a predicted exact count.</summary>
    UpperBound = 2,

    /// <summary>The count is modelled from available statistics.</summary>
    Modelled = 3
}

/// <summary>Reports a semantic query estimate with its bounded source and quality.</summary>
/// <param name="Value">The estimated count, or null when unavailable.</param>
/// <param name="Quality">The strength of the estimate.</param>
/// <param name="Source">The estimate source description.</param>
public sealed record SemanticQueryEstimate(
    long? Value,
    SemanticQueryEstimateQuality Quality,
    string Source);

/// <summary>Names the bounded aggregate operations supported by Semantic analytical queries.</summary>
public enum SemanticAggregationFunction
{
    /// <summary>Counts contributing entities.</summary>
    CountEntities,

    /// <summary>Counts contributing values.</summary>
    CountValues,

    /// <summary>Counts distinct contributing values.</summary>
    CountDistinctValues,

    /// <summary>Sums contributing numeric values.</summary>
    Sum,

    /// <summary>Computes the mean of contributing numeric values.</summary>
    Average,

    /// <summary>Selects the least contributing value.</summary>
    Minimum,

    /// <summary>Selects the greatest contributing value.</summary>
    Maximum
}

/// <summary>Groups Semantic entities by the aligned values of one property.</summary>
/// <param name="Property">The grouping property term.</param>
/// <param name="Name">The output grouping-column name.</param>
public sealed record SemanticAggregationDimension(SemanticTerm Property, string Name);

/// <summary>Defines one evidence-aware aggregate measure.</summary>
/// <param name="Function">The aggregate operation.</param>
/// <param name="Name">The output measure-column name.</param>
/// <param name="Property">The selected property, when required by the operation.</param>
public sealed record SemanticAggregationMeasure(
    SemanticAggregationFunction Function,
    string Name,
    SemanticTerm? Property = null);

/// <summary>Defines a bounded analytical query over a Semantic entity selection.</summary>
/// <param name="Source">The entity selection supplying inputs.</param>
/// <param name="Dimensions">The grouping dimensions.</param>
/// <param name="Measures">The aggregate measures.</param>
/// <param name="MaximumInputEntities">The maximum input-entity budget, subject to service limits.</param>
public sealed record SemanticAggregationQuery(
    EntityQueryDefinition Source,
    IReadOnlyList<SemanticAggregationDimension> Dimensions,
    IReadOnlyList<SemanticAggregationMeasure> Measures,
    int MaximumInputEntities = 10_000);

/// <summary>Defines safety bounds for Semantic analytical queries.</summary>
public static class SemanticAggregationLimits
{
    /// <summary>The maximum permitted input-entity budget.</summary>
    public const int MaximumInputEntities = 10_000;

    /// <summary>The maximum number of materialized groups.</summary>
    public const int MaximumGroups = 10_000;
}

/// <summary>Reports that a Semantic analytical query exceeded a declared safety bound.</summary>
public sealed class SemanticAggregationLimitException(string message) : EntityForgeDbException(message);

/// <summary>Represents one grouped analytical row with the evidence that contributed to it.</summary>
/// <param name="Values">Named grouped and aggregate result values.</param>
/// <param name="Evidence">Evidence supporting this aggregate row.</param>
public sealed record SemanticAggregationRow(
    IReadOnlyDictionary<string, QueryValue> Values,
    IReadOnlyList<EvidenceReference> Evidence);

/// <summary>Returns a snapshot-consistent Semantic analytical result.</summary>
/// <param name="QueryId">Identity of the aggregation execution.</param>
/// <param name="SnapshotVersion">Exact semantic snapshot used for aggregation.</param>
/// <param name="Rows">Grouped aggregate rows.</param>
/// <param name="Evidence">Evidence included with the result.</param>
/// <param name="InputEntityCount">Number of source entities contributing to aggregation.</param>
public sealed record SemanticAggregationResult(
    Guid QueryId,
    DatabaseVersion SnapshotVersion,
    IReadOnlyList<SemanticAggregationRow> Rows,
    IReadOnlyList<EvidenceRecord> Evidence,
    long InputEntityCount);

/// <summary>Explains bounded Semantic aggregation planning and optional measured execution.</summary>
/// <param name="SnapshotVersion">Exact semantic snapshot used.</param>
/// <param name="LogicalOperation">Logical aggregation operation description.</param>
/// <param name="SourcePlan">Plan used to acquire source entities.</param>
/// <param name="MaximumInputEntities">Configured bound on aggregation inputs.</param>
/// <param name="ActualInputEntities">Measured input entity count when execution analysis is available.</param>
/// <param name="ActualGroups">Measured group count when execution analysis is available.</param>
/// <param name="ExecutionDuration">Measured aggregation execution duration when available.</param>
public sealed record SemanticAggregationExplanation(
    DatabaseVersion SnapshotVersion,
    string LogicalOperation,
    SemanticEntityQueryExplanation SourcePlan,
    int MaximumInputEntities,
    long? ActualInputEntities,
    long? ActualGroups,
    TimeSpan? ExecutionDuration);

/// <summary>Explains the logical and physical plan for a semantic query and optional execution measurements.</summary>
/// <param name="Field">Logical semantic property queried.</param>
/// <param name="StorageKind">Physical property placement used.</param>
/// <param name="CatalogVersion">Property-storage catalog version used.</param>
/// <param name="SnapshotVersion">Exact data snapshot queried.</param>
/// <param name="LogicalOperation">Logical query operation description.</param>
/// <param name="PhysicalOperation">Physical access operation description.</param>
/// <param name="EstimatedCandidates">Estimated candidate count.</param>
/// <param name="ActualCandidates">Measured candidate count when analysis executes the query.</param>
/// <param name="ActualMatches">Measured matching assertion count when available.</param>
/// <param name="ExecutionDuration">Measured execution duration when available.</param>
public sealed record SemanticQueryExplanation(
    SemanticTerm Field,
    SemanticPropertyStorageKind StorageKind,
    SemanticPropertyStorageCatalogVersion CatalogVersion,
    DatabaseVersion SnapshotVersion,
    string LogicalOperation,
    string PhysicalOperation,
    long EstimatedCandidates,
    long? ActualCandidates,
    long? ActualMatches,
    TimeSpan? ExecutionDuration);
