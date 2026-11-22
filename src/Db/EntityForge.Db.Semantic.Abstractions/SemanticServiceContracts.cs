using EntityForge.Semantics;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Semantic;

/// <summary>Applies complete, provenance-bearing semantic change sessions.</summary>
public interface ISemanticChangeWriter
{
    /// <summary>Validates and atomically applies a complete semantic change session.</summary>
    ValueTask<SessionCommitResult> ApplyAsync(
        SemanticSessionEnvelope session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies one bounded canonical session as the initial contents of an empty semantic database.
    /// All ordinary semantic validation, provenance, identity and change-feed rules remain in force.
    /// </summary>
    /// <remarks>After an uncertain attempt, retry the exact initial-load session. This specialised
    /// lifecycle is not represented by the ordinary application operation catalog's ResolveAsync.</remarks>
    ValueTask<SessionCommitResult> BulkLoadAsync(
        SemanticSessionEnvelope session,
        CancellationToken cancellationToken = default);

}

/// <summary>Identifies the independently replaceable contribution owned by one source partition.</summary>
public sealed record SemanticContributionScope
{
    /// <summary>Creates a contribution scope without loading or modifying its current state.</summary>
    /// <param name="sourceSystem">The nonnull absolute source-system URI.</param>
    /// <param name="sourcePartition">The nonnull absolute source-partition URI.</param>
    /// <exception cref="ArgumentException">Either URI is null or relative.</exception>
    public SemanticContributionScope(Uri sourceSystem, Uri sourcePartition)
    {
        SourceSystem = sourceSystem ?? throw new ArgumentNullException(nameof(sourceSystem));
        SourcePartition = sourcePartition ?? throw new ArgumentNullException(nameof(sourcePartition));
        if (!sourceSystem.IsAbsoluteUri || !sourcePartition.IsAbsoluteUri)
        {
            throw new ArgumentException("Contribution scope URIs must be absolute.");
        }
    }

    /// <summary>Gets the absolute source-system identifier.</summary>
    public Uri SourceSystem
    {
        get;
    }

    /// <summary>Gets the absolute source-partition identifier.</summary>
    public Uri SourcePartition
    {
        get;
    }
}

/// <summary>An exact token for one immutable source-contribution revision.</summary>
public sealed record SemanticContributionRevision
{
    /// <summary>Creates an exact contribution revision token without publishing or resolving it.</summary>
    /// <param name="generationId">The nonempty revision generation identity.</param>
    /// <param name="revision">The nonblank revision label.</param>
    /// <param name="definitionHash">The nonblank definition fingerprint; this constructor does not validate its digest format.</param>
    /// <exception cref="ArgumentException">The generation is empty or a required string is null or blank.</exception>
    public SemanticContributionRevision(Guid generationId, string revision, string definitionHash)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("A contribution revision identifier is required.", nameof(generationId));
        }

        GenerationId = generationId;
        Revision = !string.IsNullOrWhiteSpace(revision) ? revision : throw new ArgumentException(
            "A contribution revision label is required.", nameof(revision));
        DefinitionHash = !string.IsNullOrWhiteSpace(definitionHash) ? definitionHash : throw new ArgumentException(
            "A contribution definition hash is required.", nameof(definitionHash));
    }

    /// <summary>Gets the revision generation identity.</summary>
    public Guid GenerationId
    {
        get;
    }

    /// <summary>Gets the revision label.</summary>
    public string Revision
    {
        get;
    }

    /// <summary>Gets the exact definition fingerprint supplied by the producer.</summary>
    public string DefinitionHash
    {
        get;
    }

}

/// <summary>Reports the durable lifecycle state of one contribution revision.</summary>
public enum SemanticContributionRevisionState
{
    /// <summary>The hidden revision is accepting parts.</summary>
    Building,

    /// <summary>The complete revision has been sealed for conditional publication.</summary>
    Sealed,

    /// <summary>The revision is the published source contribution.</summary>
    Published,

    /// <summary>The revision has been superseded by another published contribution.</summary>
    Retired,

    /// <summary>The hidden revision was abandoned instead of published.</summary>
    Abandoned
}

/// <summary>Describes one contribution revision without exposing its hidden payload.</summary>
/// <param name="Scope">The source contribution scope.</param>
/// <param name="Revision">The exact revision token.</param>
/// <param name="ExpectedPublishedRevision">The predecessor expected at conditional publication, or null for an initially unpublished scope.</param>
/// <param name="State">The retained lifecycle state.</param>
public sealed record SemanticContributionRevisionInfo(
    SemanticContributionScope Scope,
    SemanticContributionRevision Revision,
    SemanticContributionRevision? ExpectedPublishedRevision,
    SemanticContributionRevisionState State);

/// <summary>Bounds database-owned hidden contribution assembly.</summary>
public sealed record SemanticContributionRevisionLimits
{
    internal const int FormatMaximumCollectionEntries = 1_000_000;
    internal const int FormatMaximumEmbeddedPartBytes = 64 * 1024 * 1024;

    /// <summary>The default maximum number of active revisions.</summary>
    public const int DefaultMaximumActiveRevisions = 128;

    /// <summary>The default maximum number of retained revision records.</summary>
    public const int DefaultMaximumRetainedRevisions = 65_536;

    /// <summary>The default maximum number of parts in one revision.</summary>
    public const int DefaultMaximumPartsPerRevision = 4096;

    /// <summary>The default encoded byte budget for one revision.</summary>
    public const long DefaultMaximumBytesPerRevision = 256L * 1024L * 1024L;

    /// <summary>The default encoded byte budget across retained revisions.</summary>
    public const long DefaultMaximumTotalRetainedBytes = 1024L * 1024L * 1024L;

    /// <summary>Gets the default hidden-revision retention limits.</summary>
    public static SemanticContributionRevisionLimits Default { get; } = new();

    /// <summary>Gets or initializes the maximum number of active revisions.</summary>
    public int MaximumActiveRevisions { get; init; } = DefaultMaximumActiveRevisions;

    /// <summary>Gets or initializes the maximum number of retained revision records.</summary>
    public int MaximumRetainedRevisions { get; init; } = DefaultMaximumRetainedRevisions;

    /// <summary>Gets or initializes the maximum part count for one revision.</summary>
    public int MaximumPartsPerRevision { get; init; } = DefaultMaximumPartsPerRevision;

    /// <summary>Maximum encoded payload and lifecycle metadata retained for one revision.</summary>
    public long MaximumBytesPerRevision { get; init; } = DefaultMaximumBytesPerRevision;

    /// <summary>Maximum encoded payload and lifecycle metadata retained across all revisions.</summary>
    public long MaximumTotalRetainedBytes { get; init; } = DefaultMaximumTotalRetainedBytes;

}

/// <summary>Reports an invalid or conflicting source-contribution revision operation.</summary>
public class SemanticContributionRevisionException : InvalidOperationException
{
    /// <summary>Describes a revision lifecycle failure.</summary>
    /// <param name="generationId">The affected revision generation.</param>
    /// <param name="message">The failure explanation.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    public SemanticContributionRevisionException(Guid generationId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        GenerationId = generationId;
    }

    /// <summary>Gets the affected revision generation.</summary>
    public Guid GenerationId
    {
        get;
    }
}

/// <summary>Reports that conditional publication found a different currently published revision.</summary>
public sealed class SemanticContributionRevisionConflictException : SemanticContributionRevisionException
{
    /// <summary>Reports the expected and observed publication predecessors of a rejected conditional revision.</summary>
    /// <param name="generationId">The revision whose publication conflicted.</param>
    /// <param name="expected">The expected predecessor, or null if none was expected.</param>
    /// <param name="actual">The observed published predecessor, or null if none existed.</param>
    /// <param name="innerException">The underlying failure, when available.</param>
    public SemanticContributionRevisionConflictException(Guid generationId,
        SemanticContributionRevision? expected, SemanticContributionRevision? actual, Exception? innerException = null)
        : base(generationId, "The published source contribution changed before this revision could be applied.",
            innerException)
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Gets the expected published predecessor.</summary>
    public SemanticContributionRevision? Expected
    {
        get;
    }

    /// <summary>Gets the observed published predecessor.</summary>
    public SemanticContributionRevision? Actual
    {
        get;
    }
}

/// <summary>
/// Builds a complete source contribution in hidden database-owned state and conditionally publishes it.
/// Use this only when a replacement is too large for one ordinary atomic semantic session.
/// </summary>
public interface ISemanticContributionRevisionWriter
{
    /// <summary>Reads the currently published complete contribution for a source scope.</summary>
    /// <param name="scope">The source-system and source-partition pair to inspect.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The published revision, or null when the scope has none.</returns>
    ValueTask<SemanticContributionRevision?> GetPublishedRevisionAsync(
        SemanticContributionScope scope, CancellationToken cancellationToken = default);

    /// <summary>Reads the retained lifecycle state of a contribution revision.</summary>
    /// <param name="generationId">Non-empty identifier of the revision generation.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The retained revision information, or null when no revision is known.</returns>
    ValueTask<SemanticContributionRevisionInfo?> GetRevisionAsync(
        Guid generationId, CancellationToken cancellationToken = default);

    /// <summary>Begins a hidden replacement contribution with a conditional publication predecessor.</summary>
    /// <param name="start">The full-replacement session start identifying the source and generation.</param>
    /// <param name="expectedPublishedRevision">The revision expected to be published when this replacement is committed, or null when none is expected.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The durable receipt for beginning the revision.</returns>
    ValueTask<DatabaseCommitReceipt> BeginRevisionAsync(SemanticChangeSessionStart start,
        SemanticContributionRevision? expectedPublishedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Retains one part of a hidden contribution revision without publishing it to readers.</summary>
    /// <param name="page">The generation-identified session page to accept.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The durable receipt for the accepted part.</returns>
    ValueTask<DatabaseCommitReceipt> AcceptRevisionPartAsync(SemanticChangeSessionPage page,
        CancellationToken cancellationToken = default);

    /// <summary>Closes the staged contribution and validates its declared completion before publication.</summary>
    /// <param name="close">The generation-identified session close record.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The durable receipt for sealing the revision.</returns>
    ValueTask<DatabaseCommitReceipt> SealRevisionAsync(SemanticChangeSessionClose close,
        CancellationToken cancellationToken = default);

    /// <summary>Conditionally publishes a sealed contribution as the source scope's complete replacement.</summary>
    /// <param name="generationId">Non-empty identifier of the sealed generation.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The durable publication receipt.</returns>
    ValueTask<DatabaseCommitReceipt> PublishRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default);

    /// <summary>Abandons a staged contribution generation instead of publishing it.</summary>
    /// <param name="generationId">Non-empty identifier of the generation to abandon.</param>
    /// <param name="cancellationToken">Cancels waiting for the operation; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The durable abandonment receipt.</returns>
    ValueTask<DatabaseCommitReceipt> AbandonRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Declares whether reprojection is physical-only or changes materialized semantic truth.</summary>
public enum SemanticReprojectionKind
{
    /// <summary>Rebuilds physical query state without changing canonical semantic assertions.</summary>
    ReplaceablePhysicalView,

    /// <summary>Changes materialized semantic assertions through a canonical semantic session.</summary>
    MaterializedLogicalFacts
}

/// <summary>Describes a versioned semantic reprojection operation.</summary>
/// <param name="ProjectionId">The nonnull projection identifier.</param>
/// <param name="DefinitionVersion">The nonblank projection definition version.</param>
/// <param name="Kind">Whether reprojection affects physical state or logical facts.</param>
/// <param name="MaterializedChanges">The canonical change session for a logical reprojection, when applicable.</param>
public sealed record SemanticReprojectionRequest(
    Uri ProjectionId,
    string DefinitionVersion,
    SemanticReprojectionKind Kind,
    SemanticSessionEnvelope? MaterializedChanges = null)
{
    /// <summary>Gets the projection identifier.</summary>
    public Uri ProjectionId { get; } = ProjectionId ?? throw new ArgumentNullException(nameof(ProjectionId));

    /// <summary>Gets the projection definition version.</summary>
    public string DefinitionVersion
    {
        get;
    }
    = !string.IsNullOrWhiteSpace(DefinitionVersion)
        ? DefinitionVersion : throw new ArgumentException("A projection definition version is required.",
            nameof(DefinitionVersion));
}

/// <summary>Reports the durable boundary and whether reprojection emitted canonical logical effects.</summary>
/// <param name="ProjectionId">The affected projection identifier.</param>
/// <param name="DefinitionVersion">The applied definition version.</param>
/// <param name="Kind">The executed reprojection kind.</param>
/// <param name="PublishedVersion">The durable published database version.</param>
/// <param name="EmittedLogicalChanges">Whether canonical logical effects were emitted.</param>
public sealed record SemanticReprojectionResult(
    Uri ProjectionId,
    string DefinitionVersion,
    SemanticReprojectionKind Kind,
    DatabaseVersion PublishedVersion,
    bool EmittedLogicalChanges);

/// <summary>Rebuilds physical projections or applies provenance-bearing materialized reprojections.</summary>
public interface ISemanticReprojectionService
{
    /// <summary>
    /// Rebuilds a replaceable physical view, or commits a materialized reprojection through the canonical
    /// provenance-bearing semantic transaction boundary.
    /// </summary>
    /// <param name="request">The projection identity, definition and optional canonical change session.</param>
    /// <param name="cancellationToken">Cancels waiting; cancellation after submission does not prove that nothing committed.</param>
    /// <returns>The durable publication boundary and logical-change indicator.</returns>
    ValueTask<SemanticReprojectionResult> ReprojectAsync(
        SemanticReprojectionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads durable, cursor-addressable semantic changes.</summary>
public interface ISemanticChangeFeed
{
    /// <summary>Runs a multiplexed command/event session suitable for duplex transports.</summary>
    IAsyncEnumerable<SemanticEntityQuerySubscriptionSessionEvent> RunEntityQuerySubscriptionSessionAsync(
        IAsyncEnumerable<SemanticEntityQuerySubscriptionCommand> commands,
        SemanticEntityQuerySubscriptionSessionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Streams cursor-addressable progress until a retained change may invalidate a query result.</summary>
    IAsyncEnumerable<SemanticEntityQuerySubscriptionEvent> SubscribeToEntityQueryInvalidationAsync(
        EntityQueryDefinition query,
        DatabaseVersion observedSnapshotVersion,
        DatabaseChangeCursor? cursor = null,
        DatabaseChangeSubscriptionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Checks resumable retained changes for effects that may alter an entity-query result.</summary>
    ValueTask<SemanticEntityQueryInvalidation> CheckEntityQueryInvalidationAsync(
        EntityQueryDefinition query,
        DatabaseVersion observedSnapshotVersion,
        DatabaseChangeCursor? cursor = null,
        int maximumChanges = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the retained committed-change cursor bounds.</summary>
    ValueTask<DatabaseChangeFeedBounds> GetChangeFeedBoundsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Reads decoded semantic changes strictly after the supplied cursor.</summary>
    ValueTask<SemanticCommittedChangePage> ReadSemanticChangePageAfterAsync(
        DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

/// <summary>Executes application-facing Semantic entity and aggregation queries.</summary>
public interface ISemanticQueryService
{
    /// <summary>Executes a bounded, evidence-aware Semantic grouping and aggregation query.</summary>
    ValueTask<SemanticAggregationResult> AggregateEntitiesAsync(
        SemanticAggregationQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Explains a bounded Semantic grouping and aggregation query.</summary>
    ValueTask<SemanticAggregationExplanation> ExplainAggregationAsync(
        SemanticAggregationQuery query,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Executes a representation-neutral entity query using planned physical candidate access.</summary>
    ValueTask<SemanticEntityQueryResult> QueryEntitiesAsync(
        EntityQueryDefinition query,
        EntityProjectionProjectionOptions? options = null,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Explains candidate acquisition for a representation-neutral entity query.</summary>
    ValueTask<SemanticEntityQueryExplanation> ExplainEntitiesAsync(
        EntityQueryDefinition query,
        EntityProjectionProjectionOptions? options = null,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Returns a projected entity document together with the exact snapshot that produced it.</summary>
/// <param name="Projection">Neutral projected entity document.</param>
/// <param name="SnapshotVersion">Exact semantic snapshot used for the projection.</param>
public sealed record SemanticEntityQueryResult(
    EntityProjectionProjectionResult Projection,
    DatabaseVersion SnapshotVersion)
{
    /// <summary>Gets the projected neutral document.</summary>
    public EntityProjection Document => Projection.Document;

    /// <summary>Gets projection diagnostics.</summary>
    public IReadOnlyList<EntityProjectionProjectionDiagnostic> Diagnostics => Projection.Diagnostics;

    /// <summary>Gets the continuation for the next bounded page, when present.</summary>
    public string? NextContinuation => Projection.NextContinuation;

}

/// <summary>
/// Executes native evidence and access-path queries. Ordinary applications should prefer
/// <see cref="ISemanticQueryService"/> and the typed <c>Entities(...)</c> fluent API.
/// </summary>
public interface ISemanticEvidenceQueryService
{
    /// <summary>Queries semantic field evidence equal to a typed value.</summary>
    ValueTask<IReadOnlyList<SemanticFieldEvidence>> QueryAsync(
        SemanticFieldEqualityQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Executes ordered equality queries through one immutable physical snapshot.</summary>
    ValueTask<SemanticFieldEqualityBatchResult> QueryBatchAsync(
        SemanticFieldEqualityBatchQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Explains a semantic equality query, optionally executing it for actual measurements.</summary>
    ValueTask<SemanticQueryExplanation> ExplainAsync(
        SemanticFieldEqualityQuery query,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Queries semantic string field evidence within inclusive bounds.</summary>
    ValueTask<IReadOnlyList<SemanticFieldEvidence>> QueryRangeAsync(
        SemanticStringRangeQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Queries semantic concept membership and its supporting assertions.</summary>
    ValueTask<IReadOnlyList<SemanticEntityMembershipEvidence>> QueryConceptAsync(
        SemanticConceptMembershipQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Counts entities belonging to a semantic concept without hydrating supporting assertions.</summary>
    ValueTask<long> CountConceptMembersAsync(
        SemanticConceptMembershipQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Traverses semantic relationships adjacent to one entity.</summary>
    ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(
        SemanticRelationshipTraversalQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Traverses semantic relationships adjacent to several entities through one read snapshot.</summary>
    ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseBatchAsync(
        SemanticRelationshipTraversalBatchQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Projects one entity and its selected relationships through indexed identity access.</summary>
    ValueTask<EntityProjectionProjectionResult> ProjectEntityAsync(
        SemanticEntityProjectionQuery query,
        EntityProjectionProjectionOptions? options = null,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);

    /// <summary>Explains the indexed access paths used to project one entity.</summary>
    ValueTask<SemanticEntityProjectionExplanation> ExplainEntityAsync(
        SemanticEntityProjectionQuery query,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads the authoritative logical Semantic vocabulary snapshot.</summary>
public interface ISemanticCatalogReader
{
    /// <summary>Gets an immutable representation-neutral snapshot of the semantic catalog.</summary>
    ValueTask<SemanticCatalogSnapshot> GetSemanticCatalogAsync(
        CancellationToken cancellationToken = default);

}

/// <summary>Reads the resolved application-facing Semantic catalogue graph.</summary>
public interface ISemanticResolvedCatalogReader
{
    /// <summary>Gets the resolved logical catalogue without exposing physical placement or governance state.</summary>
    ValueTask<SemanticResolvedCatalog> GetResolvedSemanticCatalogAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Publishes and reads immutable logical semantic vocabularies.</summary>
public interface ISemanticVocabularyCatalog
{
    /// <summary>
    /// Publishes an immutable vocabulary. Re-publishing identical content is idempotent; changing an
    /// already-published identifier/version is rejected.
    /// </summary>
    ValueTask<SemanticVocabulary> PublishVocabularyAsync(
        SemanticVocabulary vocabulary,
        CancellationToken cancellationToken = default);

}

/// <summary>Configures physical storage for logical semantic properties.</summary>
public interface ISemanticPropertyStorageCatalog
{
    /// <summary>Gets the currently selected physical access path for a logical property.</summary>
    ValueTask<SemanticPropertyStorage> GetPropertyStorageAsync(
        SemanticTerm property,
        CancellationToken cancellationToken = default);

    /// <summary>Configures a semantic property's physical access path.</summary>
    ValueTask<SemanticPropertyStorage> ConfigurePropertyStorageAsync(
        SemanticTerm property,
        SemanticPropertyStorageKind storageKind,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a property to optimizer control without immediately changing its physical placement.</summary>
    ValueTask<SemanticPropertyStorage> EnableAutomaticPropertyStorageAsync(
        SemanticTerm property,
        CancellationToken cancellationToken = default);
}

/// <summary>Analyzes runtime property access without changing physical placement.</summary>
public interface ISemanticPropertyStorageAdvisor
{
    /// <summary>Returns evidence-backed property placement recommendations without applying them.</summary>
    ValueTask<SemanticPropertyOptimizationReport> AnalyzePropertyStorageAsync(
        SemanticPropertyOptimizationPolicy? policy = null,
        CancellationToken cancellationToken = default);

    /// <summary>Analyzes and applies a bounded number of recommendations to automatically managed properties.</summary>
    ValueTask<SemanticPropertyOptimizationResult> ApplyPropertyStorageOptimizationAsync(
        SemanticPropertyOptimizationPolicy? policy = null,
        int maximumChanges = 1,
        CancellationToken cancellationToken = default);

    /// <summary>Clears the bounded runtime evidence used by subsequent analyses.</summary>
    ValueTask ResetPropertyStorageObservationsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Publishes and reads semantic governance rules.</summary>
public interface ISemanticGovernanceCatalog
{
    /// <summary>Publishes a versioned semantic governance rule.</summary>
    ValueTask<SemanticGovernanceRule> PublishGovernanceRuleAsync(
        SemanticGovernanceRuleDefinition definition,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the current published form of a governance rule.</summary>
    ValueTask<SemanticGovernanceRule?> GetGovernanceRuleAsync(
        Uri identifier,
        CancellationToken cancellationToken = default);
}

/// <summary>Evaluates candidate writes against the active logical vocabulary catalogue.</summary>
public interface ISemanticConformanceService
{
    /// <summary>Returns structured conformance findings without changing database state.</summary>
    ValueTask<SemanticConformanceReport> ValidateConformanceAsync(
        SemanticSessionEnvelope session,
        CancellationToken cancellationToken = default);
}

/// <summary>Protects explicit database snapshots from retention and checkpoint reclamation.</summary>
public interface ISnapshotLeaseService
{
    /// <summary>
    /// Atomically acquires the current snapshot and its committed-change boundary for bootstrap consumers.
    /// </summary>
    ValueTask<SemanticChangeBootstrap> AcquireChangeBootstrapAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires a lease for the current semantic snapshot.</summary>
    ValueTask<SnapshotLease> AcquireSnapshotLeaseAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>Gets an active snapshot lease.</summary>
    ValueTask<SnapshotLease> GetSnapshotLeaseAsync(
        Guid leaseId,
        CancellationToken cancellationToken = default);

    /// <summary>Renews an existing snapshot lease using optimistic lease revision validation.</summary>
    ValueTask<SnapshotLease> RenewSnapshotLeaseAsync(
        Guid leaseId,
        long expectedRevision,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a snapshot lease.</summary>
    ValueTask ReleaseSnapshotLeaseAsync(
        Guid leaseId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the oldest semantic version protected by active snapshot leases.</summary>
    ValueTask<DatabaseVersion> GetSnapshotRetentionFloorAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>Aggregates logical vocabulary, resolved catalogue, placement and governance metadata.</summary>
public interface ISemanticCatalogService : ISemanticCatalogReader, ISemanticResolvedCatalogReader,
    ISemanticVocabularyCatalog, ISemanticPropertyStorageCatalog, ISemanticPropertyStorageAdvisor,
    ISemanticGovernanceCatalog, ISemanticConformanceService;

/// <summary>Runs explicit Semantic storage maintenance.</summary>
public interface ISemanticMaintenanceService
{
    /// <summary>Creates a durable checkpoint and reclaims write-ahead data covered by it.</summary>
    ValueTask<DatabaseVersion> CreateCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Rebuilds derived semantic access paths and atomically publishes their compact generations.</summary>
    ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default);
}

/// <summary>Aggregates the supported embedded Semantic-profile capabilities.</summary>
public interface ISemanticDatabase : IDatabaseOperationalStatus, ISemanticChangeWriter,
    ISemanticContributionRevisionWriter, ISemanticChangeFeed, ISemanticQueryService,
    ISemanticCatalogService, ISnapshotLeaseService, ISemanticMaintenanceService, ISemanticReprojectionService,
    IAsyncDisposable
{
    /// <summary>Gets the advanced native evidence-query surface.</summary>
    ISemanticEvidenceQueryService Evidence
    {
        get;
    }

    /// <summary>Gets logical vocabulary, catalogue, placement and governance capabilities.</summary>
    ISemanticCatalogService Catalog => this;

    /// <summary>Gets the durable committed-change feed.</summary>
    ISemanticChangeFeed Changes => this;

    /// <summary>Gets durable snapshot-retention lease operations.</summary>
    ISnapshotLeaseService Snapshots => this;

    /// <summary>Gets explicit checkpoint and reindex operations.</summary>
    ISemanticMaintenanceService Maintenance => this;
}
