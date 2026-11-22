using EntityForge.Db.Semantic;
using EntityForge.Db;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Durability;

/// <summary>Identifies the semantic mutation carried by a durable commit.</summary>
public enum DurableDatabaseCommitKind
{
    /// <summary>A transaction containing semantic session contributions.</summary>
    SemanticTransaction,
    /// <summary>A physical property-storage catalog transition.</summary>
    PropertyStorage,
    /// <summary>A governance rule catalog mutation.</summary>
    GovernanceRule,
    /// <summary>A durable snapshot lease mutation.</summary>
    SnapshotLease,
    /// <summary>A resumable-write lifecycle mutation.</summary>
    ResumableWrite,
    /// <summary>Equivalent-state physical maintenance.</summary>
    PhysicalMaintenance,
    /// <summary>A vocabulary catalog publication.</summary>
    VocabularyPublication,
    /// <summary>A hidden contribution revision lifecycle mutation.</summary>
    ContributionRevision,
    /// <summary>An entity mapping registration.</summary>
    MappingRegistration,
    /// <summary>An application operation lifecycle record.</summary>
    ApplicationOperation
}

/// <summary>Identifies a hidden contribution revision lifecycle mutation.</summary>
public enum SemanticContributionRevisionChangeKind : byte
{
    /// <summary>Starts a hidden contribution generation.</summary>
    Begin = 1,
    /// <summary>Accepts an identity-bound contribution page.</summary>
    AcceptPart = 2,
    /// <summary>Closes the contribution's exact page manifest.</summary>
    Seal = 3,
    /// <summary>Publishes the sealed contribution revision.</summary>
    Publish = 4,
    /// <summary>Abandons an unpublished contribution revision.</summary>
    Abandon = 5
}

/// <summary>Carries a contribution lifecycle mutation and its session or publication evidence.</summary>
/// <param name="Kind">The lifecycle transition being recorded.</param>
/// <param name="GenerationId">The hidden contribution generation identifier.</param>
/// <param name="Start">The session start envelope required for beginning a contribution.</param>
/// <param name="Page">The contribution page supplied by a part-acceptance transition.</param>
/// <param name="Close">The closing manifest supplied when sealing the contribution.</param>
/// <param name="ExpectedPublishedRevision">The predecessor expected by compare-and-publish, or null when none must exist.</param>
public sealed record SemanticContributionRevisionChange(
    SemanticContributionRevisionChangeKind Kind,
    Guid GenerationId,
    SemanticChangeSessionStart? Start = null,
    SemanticChangeSessionPage? Page = null,
    SemanticChangeSessionClose? Close = null,
    SemanticContributionRevision? ExpectedPublishedRevision = null);

internal enum PhysicalMaintenanceKind : byte
{
    Routine = 1, Reindex = 2
}

internal enum PhysicalMaintenanceTarget : byte
{
    None = 0, Ordered = 1, Shared = 2, Concept = 3, OutgoingRelationship = 4,
    IncomingRelationship = 5, FieldEvidence = 6, FieldEquality = 7, SubjectField = 8
}

/// <summary>One source contribution within an ordinary atomic Semantic transaction.</summary>
/// <param name="Session">The complete semantic session contributed to this transaction.</param>
/// <param name="Read">Optional immutable partition-read dependencies captured during selection.</param>
/// <param name="LocalOwnerCoverage">Optional conservative coverage of locally affected source owners.</param>
public sealed record SemanticTransactionContribution(SemanticSessionEnvelope Session,
    SemanticPartitionReadEvidence? Read = null, SemanticOwnerCoverage? LocalOwnerCoverage = null);

/// <summary>Represents one semantic transaction or catalog/lifecycle mutation to be durably recorded.</summary>
public sealed record DurableDatabaseCommit
{
    private DurableDatabaseCommit(DatabaseVersion version, DurableDatabaseCommitKind kind,
        SemanticSessionEnvelope? session, SemanticPropertyStorageChange? propertyStorage,
        SemanticGovernanceRuleChange? governanceRule, SnapshotLeaseChange? snapshotLease,
        ResumableWriteChange? resumableWrite, Guid? maintenanceId,
        SemanticVocabularyPublicationChange? vocabularyPublication = null,
        SemanticContributionRevisionChange? contributionRevision = null,
        PhysicalMaintenanceKind? physicalMaintenanceKind = null,
        PhysicalMaintenanceTarget physicalMaintenanceTarget = PhysicalMaintenanceTarget.None,
        bool isInitialBulkLoad = false,
        bool usesAuthoritativeVersion = false)
    {
        Version = version;
        Kind = kind;
        _session = session;
        PropertyStorage = propertyStorage;
        GovernanceRule = governanceRule;
        SnapshotLease = snapshotLease;
        ResumableWrite = resumableWrite;
        MaintenanceId = maintenanceId;
        VocabularyPublication = vocabularyPublication;
        ContributionRevision = contributionRevision;
        PhysicalMaintenanceKind = physicalMaintenanceKind;
        PhysicalMaintenanceTarget = physicalMaintenanceTarget;
        IsInitialBulkLoad = isInitialBulkLoad;
        UsesAuthoritativeVersion = usesAuthoritativeVersion;
    }

    /// <summary>Gets or initializes the native database version associated with this commit.</summary>
    public DatabaseVersion Version
    {
        get; init;
    }

    /// <summary>Gets the semantic mutation category.</summary>
    public DurableDatabaseCommitKind Kind
    {
        get;
    }

    private readonly SemanticSessionEnvelope? _session;
    private IReadOnlyList<SemanticTransactionContribution>? _contributions;

    // Session ingestion is an adapter to the same transaction. Never silently return
    // one contribution from a transaction containing several sources.
    /// <summary>Gets the session payload when this commit contains a single semantic session.</summary>
    public SemanticSessionEnvelope? Session => _contributions is null ? _session :
        _contributions.Count == 1 ? _contributions[0].Session :
        throw new InvalidOperationException("This transaction contains more than one source contribution.");

    /// <summary>Gets the semantic session contributions grouped into this transaction.</summary>
    public IReadOnlyList<SemanticTransactionContribution> Contributions => _contributions ??
        (_session is null ? [] : [new(_session, PartitionRead, LocalOwnerCoverage)]);

    /// <summary>Gets the identity used to recognize this transaction during replay.</summary>
    public Guid TransactionId => Contributions.Count != 0 ? Contributions[0].Session.Start.SessionId :
        throw new InvalidOperationException("This commit is not a Semantic transaction.");

    /// <summary>The maximum number of semantic session contributions in one transaction, 256.</summary>
    public const int MaximumTransactionContributions = 256;

    /// <summary>Creates one transaction from a bounded set of session contributions and optional bulk-load or authoritative-version flags.</summary>
    public static DurableDatabaseCommit ForSemanticTransaction(
        IReadOnlyList<SemanticTransactionContribution> contributions,
        bool isInitialBulkLoad = false, bool usesAuthoritativeVersion = false)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        if (contributions.Count is < 1 or > MaximumTransactionContributions)
        {
            throw new ArgumentException("Invalid Semantic transaction contribution count.", nameof(contributions));
        }

        var owned = contributions.ToArray();
        if (owned.Any(c => c?.Session is null) || owned[0].Session.Start.SessionId == Guid.Empty ||
            owned.Any(c => c.Session.Start.SessionId != owned[0].Session.Start.SessionId))
        {
            throw new ArgumentException("All contributions must belong to the same transaction.", nameof(contributions));
        }

        if (owned.Select(c => (c.Session.Start.Source.SourceSystem, c.Session.Start.Source.SourcePartition)).Distinct().Count() != owned.Length)
        {
            throw new ArgumentException("A transaction must provide one complete contribution per source partition.", nameof(contributions));
        }

        if (owned.Length == 1)
        {
            return ForSemanticSession(owned[0].Session, isInitialBulkLoad, usesAuthoritativeVersion)
                with
            {
                PartitionRead = owned[0].Read,
                LocalOwnerCoverage = owned[0].LocalOwnerCoverage
            };
        }

        var result = new DurableDatabaseCommit(default, DurableDatabaseCommitKind.SemanticTransaction,
            null, null, null, null, null, null, isInitialBulkLoad: isInitialBulkLoad,
            usesAuthoritativeVersion: usesAuthoritativeVersion);
        result._contributions = Array.AsReadOnly(owned);
        return result;
    }

    /// <summary>Gets or initializes the captured partition-read dependencies for the selected mutation.</summary>
    public SemanticPartitionReadEvidence? PartitionRead
    {
        get; init;
    }

    /// <summary>Gets or initializes conservative source-owner coverage captured for local application.</summary>
    public SemanticOwnerCoverage? LocalOwnerCoverage
    {
        get; init;
    }

    /// <summary>Gets the property-storage transition when selected by the commit kind.</summary>
    public SemanticPropertyStorageChange? PropertyStorage
    {
        get;
    }

    /// <summary>Gets the governance rule mutation when selected by the commit kind.</summary>
    public SemanticGovernanceRuleChange? GovernanceRule
    {
        get;
    }

    /// <summary>Gets the snapshot lease mutation when selected by the commit kind.</summary>
    public SnapshotLeaseChange? SnapshotLease
    {
        get;
    }

    /// <summary>Gets the resumable-write lifecycle mutation when selected by the commit kind.</summary>
    public ResumableWriteChange? ResumableWrite
    {
        get;
    }

    /// <summary>Gets the physical maintenance identity, when this commit records maintenance.</summary>
    public Guid? MaintenanceId
    {
        get;
    }

    /// <summary>Gets the vocabulary publication mutation, when present.</summary>
    public SemanticVocabularyPublicationChange? VocabularyPublication
    {
        get;
    }

    /// <summary>Gets the hidden contribution lifecycle mutation, when present.</summary>
    public SemanticContributionRevisionChange? ContributionRevision
    {
        get;
    }

    internal EntityOperationBinding? ApplicationOperation
    {
        get; init;
    }

    internal static DurableDatabaseCommit ForApplicationOperation(EntityOperationBinding value) =>
        new(default, DurableDatabaseCommitKind.ApplicationOperation, null, null, null, null, null, null)
        {
            ApplicationOperation = value
        };

    /// <summary>Gets the mapping registration payload, when present.</summary>
    public EntityMappingRegistration? MappingRegistration
    {
        get; init;
    }

    /// <summary>Creates a durable semantic mapping-registration mutation.</summary>
    public static DurableDatabaseCommit ForMappingRegistration(EntityMappingRegistration value) =>
        new(default, DurableDatabaseCommitKind.MappingRegistration, null, null, null, null, null, null)
        {
            MappingRegistration = value
        };

    internal PhysicalMaintenanceKind? PhysicalMaintenanceKind
    {
        get;
    }

    internal PhysicalMaintenanceTarget PhysicalMaintenanceTarget
    {
        get;
    }

    /// <summary>Gets whether the semantic transaction is marked as an initial bulk load.</summary>
    public bool IsInitialBulkLoad
    {
        get;
    }

    /// <summary>Gets whether the commit uses an externally assigned authoritative version.</summary>
    public bool UsesAuthoritativeVersion
    {
        get;
    }

    /// <summary>Creates a durable transaction from one semantic session and optional load/versioning flags.</summary>
    public static DurableDatabaseCommit ForSemanticSession(SemanticSessionEnvelope value,
        bool isInitialBulkLoad = false, bool usesAuthoritativeVersion = false) =>
        new(default, DurableDatabaseCommitKind.SemanticTransaction, value, null, null, null, null, null,
            isInitialBulkLoad: isInitialBulkLoad, usesAuthoritativeVersion: usesAuthoritativeVersion);

    /// <summary>Creates a durable property-storage catalog transition.</summary>
    public static DurableDatabaseCommit ForPropertyStorage(SemanticPropertyStorageChange value) =>
        new(default, DurableDatabaseCommitKind.PropertyStorage, null, value, null, null, null, null);

    /// <summary>Creates a durable governance-rule catalog mutation.</summary>
    public static DurableDatabaseCommit ForGovernanceRule(SemanticGovernanceRuleChange value) =>
        new(default, DurableDatabaseCommitKind.GovernanceRule, null, null, value, null, null, null);

    /// <summary>Creates a durable snapshot lease lifecycle mutation.</summary>
    public static DurableDatabaseCommit ForSnapshotLease(SnapshotLeaseChange value) =>
        new(default, DurableDatabaseCommitKind.SnapshotLease, null, null, null, value, null, null);

    /// <summary>Creates a durable resumable-write lifecycle mutation.</summary>
    public static DurableDatabaseCommit ForResumableWrite(ResumableWriteChange value) =>
        new(default, DurableDatabaseCommitKind.ResumableWrite, null, null, null, null, value, null);

    /// <summary>Creates a durable vocabulary catalog publication.</summary>
    public static DurableDatabaseCommit ForVocabularyPublication(SemanticVocabularyPublicationChange value) =>
        new(default, DurableDatabaseCommitKind.VocabularyPublication, null, null, null, null, null, null, value);

    /// <summary>Creates a durable contribution revision lifecycle mutation.</summary>
    public static DurableDatabaseCommit ForContributionRevision(SemanticContributionRevisionChange value) =>
        new(default, DurableDatabaseCommitKind.ContributionRevision, null, null, null, null, null, null,
            contributionRevision: value);

    internal static DurableDatabaseCommit ForPhysicalMaintenance(Guid maintenanceId) =>
        ForPhysicalMaintenance(maintenanceId,
            global::EntityForge.Db.Semantic.Durability.PhysicalMaintenanceKind.Routine);

    internal static DurableDatabaseCommit ForPhysicalMaintenance(Guid maintenanceId,
        PhysicalMaintenanceKind kind,
        PhysicalMaintenanceTarget target = PhysicalMaintenanceTarget.None) =>
        new(default, DurableDatabaseCommitKind.PhysicalMaintenance, null, null, null, null, null,
            maintenanceId, physicalMaintenanceKind: kind, physicalMaintenanceTarget: target);
}

/// <summary>Coordinates semantic commit durability, replay, recovery and checkpoint maintenance.</summary>
public interface ISemanticCommitLog : IAsyncDisposable
{
    /// <summary>Commits one semantic mutation and returns its assigned native version.</summary>
    ValueTask<DatabaseVersion> CommitAsync(DurableDatabaseCommit commit, CancellationToken cancellationToken = default);

    /// <summary>Commits one semantic mutation and returns its durable receipt.</summary>
    ValueTask<DatabaseCommitReceipt> CommitWithReceiptAsync(DurableDatabaseCommit commit,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves an exact previously committed mutation without executing it again; returns null when no matching receipt is available.</summary>
    ValueTask<DatabaseCommitReceipt?> ResolveReplayReceiptAsync(DurableDatabaseCommit commit,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a semantic mutation at the supplied authoritative version.</summary>
    ValueTask<DatabaseVersion> ApplyAuthoritativeCommitAsync(DurableDatabaseCommit commit,
        DatabaseVersion authoritativeVersion, CancellationToken cancellationToken = default);

    /// <summary>Applies a semantic mutation at an authoritative version and returns its durable receipt.</summary>
    ValueTask<DatabaseCommitReceipt> ApplyAuthoritativeCommitWithReceiptAsync(DurableDatabaseCommit commit,
        DatabaseVersion authoritativeVersion, CancellationToken cancellationToken = default);

    /// <summary>Commits an ordered group of semantic mutations and returns their assigned versions.</summary>
    ValueTask<IReadOnlyList<DatabaseVersion>> CommitGroupAsync(
        IReadOnlyList<DurableDatabaseCommit> commits, CancellationToken cancellationToken = default);

    /// <summary>Commits an ordered group of semantic mutations and returns their individual receipts.</summary>
    ValueTask<IReadOnlyList<DatabaseCommitReceipt>> CommitGroupWithReceiptsAsync(
        IReadOnlyList<DurableDatabaseCommit> commits, CancellationToken cancellationToken = default);

    /// <summary>Restores checkpoint state and replays durable semantic history.</summary>
    ValueTask RecoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Binds the profile participant that plans, applies and publishes semantic state.</summary>
    void BindProfile(ISemanticProfileStateParticipant participant);

    /// <summary>Captures and durably publishes semantic recovery state, returning its database version.</summary>
    ValueTask<DatabaseVersion> CreateCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Rebuilds semantic physical indexes through the maintenance protocol and returns the resulting version.</summary>
    ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default);
}

/// <summary>Captures immutable physical read access independently of commit-log durability.</summary>
public interface ISemanticCandidateSnapshotProvider
{
    /// <summary>Captures owned immutable physical candidates, optionally bounded by a maximum native version; dispose the returned snapshot.</summary>
    ISemanticCandidateSnapshot CaptureCandidateSnapshot(DatabaseVersion? maximumVersion = null);
}

/// <summary>Provides immutable physical candidates from one ordered access-path generation.</summary>
public interface ISemanticCandidateSnapshot : IDisposable
{
    /// <summary>Checks whether an identity exists in this immutable physical snapshot.</summary>
    ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the supplied identities that exist in this immutable physical snapshot.</summary>
    ValueTask<IReadOnlySet<SemanticEntityIdentity>> FindExistingIdentitiesAsync(
        IReadOnlyList<SemanticEntityIdentity> identities, CancellationToken cancellationToken = default);

    /// <summary>Returns concept members, optionally bounded by a maximum result count.</summary>
    ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept,
        int? maximumResults = null, CancellationToken cancellationToken = default);

    /// <summary>Counts members of the selected concept in this snapshot.</summary>
    ValueTask<long> CountConceptMembersAsync(SemanticTerm concept,
        CancellationToken cancellationToken = default);

    /// <summary>Finds assertion identities matching property equality queries; batched results preserve query grouping.</summary>
    ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value,
        CancellationToken cancellationToken = default);

    /// <summary>Finds assertion identities matching property equality queries; batched results preserve query grouping.</summary>
    ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default);

    /// <summary>Finds property assertions within the inclusive string bounds; null endpoints are unbounded.</summary>
    ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field, string? lowerInclusive,
        string? upperInclusive, CancellationToken cancellationToken = default);

    /// <summary>Hydrates field evidence for the supplied assertion identifiers.</summary>
    ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default);

    /// <summary>Reads field evidence for all assertions of the selected property.</summary>
    ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(
        SemanticTerm field, CancellationToken cancellationToken = default);

    /// <summary>Finds incident relationship assertions for the selected entities and traversal direction.</summary>
    ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(SemanticEntityIdentity entity,
        SemanticRelationshipDirection direction, SemanticTerm? relationship,
        CancellationToken cancellationToken = default);

    /// <summary>Finds incident relationship assertions for the selected entities and traversal direction.</summary>
    ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        CancellationToken cancellationToken = default);

    /// <summary>Hydrates relationship evidence for the supplied assertion identifiers.</summary>
    ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default);

    /// <summary>Reads supporting assertion information for the selected entities.</summary>
    ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default);

    /// <summary>Reads field and relationship evidence for the selected entities.</summary>
    ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default);

    /// <summary>Reads evidence owned by the supplied subjects, excluding relationships owned by other subjects.</summary>
    ValueTask<SemanticEvidenceSlice> ReadOwnedEntityEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> subjects, CancellationToken cancellationToken = default);
}

/// <summary>Optional complete owned-evidence hydration at the captured version ceiling for recovery,
/// independent of query batching. Returns active assertions with full values and provenance.</summary>
public interface ISemanticRecoverySnapshot
{
    /// <summary>Hydrates complete active owned evidence with values and provenance for the selected subjects at the captured recovery ceiling.</summary>
    ValueTask<SemanticEvidenceSlice> ReadRecoveryEvidenceAsync(
        IReadOnlySet<SemanticEntityIdentity> subjects, CancellationToken cancellationToken = default);
}

/// <summary>Connects semantic logical state planning and publication to commit-log durability and physical recovery.</summary>
public interface ISemanticProfileStateParticipant
{
    /// <summary>Captures logical effects for the operation when supported; the default performs no capture.</summary>
    void CaptureLogicalEffects(Guid operation)
    {
    }

    /// <summary>Releases retained logical effects for the selected operation; implementations may interpret null as releasing all retained effects.</summary>
    void ReleaseLogicalEffects(Guid? operation)
    {
    }

    /// <summary>Serializes maintenance publication with native validation. Acquire before physical writer admission.</summary>
    ValueTask<IDisposable> EnterMaintenancePublicationAsync(CancellationToken cancellationToken);

    /// <summary>Pins a native semantic snapshot until disposal; the default reports that transient pins are unsupported.</summary>
    IDisposable PinSnapshot(DatabaseVersion version) => throw new NotSupportedException("Transient snapshot pins are unavailable.");

    /// <summary>Gets the oldest version protected by semantic snapshot retention.</summary>
    DatabaseVersion GetSnapshotRetentionFloor();

    /// <summary>Gets the oldest version whose physical history must remain available.</summary>
    DatabaseVersion GetPhysicalHistoryRetentionFloor();

    /// <summary>Gets the currently published semantic database version.</summary>
    DatabaseVersion GetCurrentVersion();

    /// <summary>Reports whether any active snapshot retention prevents reclamation.</summary>
    bool HasActiveSnapshotRetention();

    /// <summary>Plans committed semantic effects and source metadata replacements before durable publication.</summary>
    ValueTask<SemanticCommittedChangePlan> PlanCommittedChangesAsync(
        DurableDatabaseCommit commit, CancellationToken cancellationToken);

    /// <summary>Applies a durable semantic commit, distinguishing normal execution from recovery replay.</summary>
    ValueTask ApplyAsync(DurableDatabaseCommit commit, bool isRecovery, CancellationToken cancellationToken);

    /// <summary>Makes the prepared semantic state visible after durability succeeds.</summary>
    ValueTask PublishStagedStateAsync(CancellationToken cancellationToken);

    /// <summary>Stages prospective semantic state for an ordered commit group without publishing it.</summary>
    ValueTask StagePreparedStateAsync(DurableDatabaseCommit commit, CancellationToken cancellationToken);

    /// <summary>Discards prospective states for the supplied transaction identities.</summary>
    ValueTask AbandonPreparedStatesAsync(IReadOnlyList<Guid> transactionIds,
        CancellationToken cancellationToken);

    /// <summary>Begins publication of the prepared semantic state chain.</summary>
    ValueTask BeginPreparedPublicationAsync(CancellationToken cancellationToken);

    /// <summary>Captures semantic recovery metadata; implementations may use the source-metadata option to choose embedded or physical-reference representation.</summary>
    ValueTask<SemanticCheckpoint> CaptureCheckpointAsync(CancellationToken cancellationToken);

    /// <summary>Captures semantic recovery metadata; implementations may use the source-metadata option to choose embedded or physical-reference representation.</summary>
    ValueTask<SemanticCheckpoint> CaptureCheckpointAsync(bool includeSourceMetadata,
        CancellationToken cancellationToken) => CaptureCheckpointAsync(cancellationToken);

    /// <summary>Prunes replay entries for the supplied safely expired session identities.</summary>
    void RetainAppliedSessions(IReadOnlySet<Guid> expired);

    /// <summary>Restores semantic logical state and catalogs from checkpoint metadata.</summary>
    ValueTask RestoreCheckpointAsync(SemanticCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>Advances the validation recovery floor after checkpoint publication while preserving newer history.</summary>
    void AdvanceCheckpointRecoveryFloor(DatabaseVersion version);
}

/// <summary>Carries the encoded change feed and typed semantic effects prepared for a transaction.</summary>
/// <param name="Payload">The encoded committed-change payload, or null when no user effects are emitted.</param>
/// <param name="Effects">The typed committed semantic effects, if present.</param>
/// <param name="PropertyStorage">Optional property-storage catalog metadata required by the change.</param>
public sealed record SemanticCommittedChangePlan(
    CommittedChangePayload? Payload,
    SemanticCommittedEffects? Effects,
    PropertyStorageCatalogCheckpoint? PropertyStorage = null)
{
    internal IReadOnlyList<SemanticEntityChange>? CompleteChanges
    {
        get; init;
    }

    /// <summary>Exact source metadata replacements for the prepared transaction. This is
    /// preparation data, not a replacement for durable logical or physical redo.</summary>
    public SemanticSourceMetadataChanges SourceMetadata { get; init; } = SemanticSourceMetadataChanges.Empty;

    /// <summary>Gets a plan without a user change-feed payload or typed effects.</summary>
    public static SemanticCommittedChangePlan NoChanges { get; } = new(null, null);
}

/// <summary>Describes an exact before/after replacement of one source metadata entry.</summary>
/// <param name="Before">The previous entry, or null for insertion.</param>
/// <param name="After">The replacement entry, or null for removal.</param>
public sealed record SemanticMetadataReplacement<T>(T? Before, T? After) where T : class;

/// <summary>Keyed source metadata changes captured where reconciliation decides ownership.
/// Before and after include complete provenance; a null after value removes the key.</summary>
/// <param name="Memberships">The exact source-membership entry replacements.</param>
/// <param name="Progress">The exact source-progress entry replacements.</param>
public sealed record SemanticSourceMetadataChanges(
    IReadOnlyList<SemanticMetadataReplacement<CheckpointSourceMembership>> Memberships,
    IReadOnlyList<SemanticMetadataReplacement<CheckpointSourcePartitionProgress>> Progress)
{
    /// <summary>Gets a change set without source metadata replacements.</summary>
    public static SemanticSourceMetadataChanges Empty { get; } = new([], []);
}

/// <summary>Persists the identity, content hash and version needed to recognize an applied session.</summary>
/// <param name="SessionId">The applied semantic session identifier.</param>
/// <param name="ContentHash">The exact session content hash.</param>
/// <param name="Version">The database version that applied the session.</param>
public sealed record CheckpointAppliedSession(Guid SessionId, string ContentHash, DatabaseVersion Version);

/// <summary>Captures semantic recovery metadata, catalogs, source ownership and retained lifecycle state.</summary>
/// <param name="Version">The native database version of the checkpoint.</param>
/// <param name="FieldCount">The number of current field assertions.</param>
/// <param name="RelationshipCount">The number of current relationship assertions.</param>
/// <param name="Catalog">The semantic model, term, identity-scheme and vocabulary catalog.</param>
/// <param name="PropertyStorage">The physical property-storage catalog.</param>
/// <param name="Governance">The governance rule catalog.</param>
/// <param name="SnapshotLeases">The snapshot leases retained for recovery.</param>
/// <param name="AppliedSessions">The session replay evidence retained in the checkpoint.</param>
/// <param name="SourceMemberships">Embedded source ownership entries, when not stored through a physical reference.</param>
/// <param name="SourceProgress">Embedded source partition progress, when not stored through a physical reference.</param>
/// <param name="QueryStatistics">Optional versioned query-planning statistics.</param>
public sealed record SemanticCheckpoint(DatabaseVersion Version,
    long FieldCount, long RelationshipCount, SemanticCatalogSnapshot Catalog,
    PropertyStorageCatalogCheckpoint PropertyStorage, GovernanceCatalogCheckpoint Governance,
    IReadOnlyList<SnapshotLease> SnapshotLeases, IReadOnlyList<CheckpointAppliedSession> AppliedSessions,
    IReadOnlyList<CheckpointSourceMembership> SourceMemberships,
    IReadOnlyList<CheckpointSourcePartitionProgress> SourceProgress,
    SemanticQueryStatisticsCheckpoint? QueryStatistics = null)
{
    /// <summary>Gets or initializes retained hidden and published contribution revisions.</summary>
    public IReadOnlyList<SemanticContributionRevisionCheckpoint> ContributionRevisions { get; init; } = [];

    /// <summary>Gets or initializes the encoded entity mapping catalog.</summary>
    public ReadOnlyMemory<byte> MappingCatalog { get; init; } = "[]"u8.ToArray();

    /// <summary>Gets or initializes an authenticated physical reference to complete source metadata, when stored outside the embedded lists.</summary>
    public SemanticSourceMetadataReference? SourceMetadataReference
    {
        get; init;
    }
}

/// <summary>Complete keyed source state in the authenticated shared physical image at this ceiling.
/// The access path may be equivalently rewritten without changing this logical binding.</summary>
/// <param name="AccessPathId">The physical access path containing the complete source metadata.</param>
/// <param name="MinimumGeneration">The minimum acceptable physical generation of that access path.</param>
/// <param name="Version">The logical version ceiling of the referenced source state.</param>
/// <param name="MembershipCount">The expected number of source membership entries.</param>
/// <param name="ProgressCount">The expected number of source progress entries.</param>
public sealed record SemanticSourceMetadataReference(Guid AccessPathId, long MinimumGeneration,
    DatabaseVersion Version, int MembershipCount, int ProgressCount);

/// <summary>Describes a retained contribution revision's build and publication lifecycle.</summary>
public enum SemanticContributionRevisionStatus : byte
{
    /// <summary>Parts may still be accepted into the hidden generation.</summary>
    Building = 0,
    /// <summary>The exact part manifest has been verified and closed.</summary>
    Sealed = 1,
    /// <summary>This generation is visible as the published generation for its scope.</summary>
    Published = 2,
    /// <summary>A successor has replaced this generation.</summary>
    Retired = 3,
    /// <summary>The unpublished generation has been abandoned.</summary>
    Abandoned = 4
}

/// <summary>Persists one contribution revision and the exact page evidence needed to recover its lifecycle.</summary>
/// <param name="Scope">The source contribution ownership scope.</param>
/// <param name="Revision">The immutable candidate revision identity.</param>
/// <param name="Start">The retained start envelope, if available.</param>
/// <param name="Pages">The accepted contribution pages.</param>
/// <param name="Close">The retained closing manifest, if sealed.</param>
/// <param name="ExpectedPublishedRevision">The predecessor expected at publication.</param>
/// <param name="Status">The recovered contribution lifecycle state.</param>
/// <param name="PartHashes">The content hashes keyed by accepted part identity.</param>
/// <param name="ManifestFingerprint">The sealed manifest fingerprint, or null before sealing.</param>
public sealed record SemanticContributionRevisionCheckpoint(
    SemanticContributionScope Scope,
    SemanticContributionRevision Revision,
    SemanticChangeSessionStart? Start,
    IReadOnlyList<SemanticChangeSessionPage> Pages,
    SemanticChangeSessionClose? Close,
    SemanticContributionRevision? ExpectedPublishedRevision,
    SemanticContributionRevisionStatus Status,
    IReadOnlyDictionary<string, string> PartHashes,
    string? ManifestFingerprint);

/// <summary>Persists a tracked count for one semantic term.</summary>
/// <param name="Term">The concept, property or relationship term.</param>
/// <param name="Cardinality">The tracked occurrence count.</param>
public sealed record SemanticTermCardinality(SemanticTerm Term, long Cardinality);

/// <summary>Persists versioned semantic cardinalities and bounded property value sketches.</summary>
/// <param name="Version">The database version represented by these statistics.</param>
/// <param name="Concepts">The tracked concept cardinalities.</param>
/// <param name="Properties">The tracked property cardinalities.</param>
/// <param name="Relationships">The tracked relationship cardinalities.</param>
/// <param name="PropertyValues">The bounded property value-frequency sketches.</param>
public sealed record SemanticQueryStatisticsCheckpoint(DatabaseVersion Version,
    IReadOnlyList<SemanticTermCardinality> Concepts,
    IReadOnlyList<SemanticTermCardinality> Properties,
    IReadOnlyList<SemanticTermCardinality> Relationships,
    IReadOnlyList<SemanticValueFrequencyCheckpoint> PropertyValues);

/// <summary>Persists an occurrence count for a semantic value fingerprint.</summary>
/// <param name="Fingerprint">The stable value fingerprint used by the statistics sketch.</param>
/// <param name="Cardinality">The tracked occurrence count.</param>
public sealed record SemanticValueCardinality(string Fingerprint, long Cardinality);

/// <summary>Persists one property's bounded value-frequency sketch.</summary>
/// <param name="Property">The property whose values are counted.</param>
/// <param name="Values">The tracked value fingerprints and counts.</param>
/// <param name="OverflowOccurrences">Occurrences outside the bounded set of tracked fingerprints.</param>
public sealed record SemanticValueFrequencyCheckpoint(SemanticTerm Property,
    IReadOnlyList<SemanticValueCardinality> Values, long OverflowOccurrences);

/// <summary>Persists one source record's ownership of a semantic subject and its provenance.</summary>
/// <param name="Subject">The contributed semantic subject.</param>
/// <param name="SourceSystem">The source-system identifier.</param>
/// <param name="SourceRecord">The source record's stable identity.</param>
/// <param name="SourcePartition">The containing source partition.</param>
/// <param name="Provenance">The complete contribution provenance.</param>
public sealed record CheckpointSourceMembership(
    SemanticEntityIdentity Subject, Uri SourceSystem, string SourceRecord, Uri SourcePartition,
    SemanticProvenance Provenance);

/// <summary>Persists the last accepted position and owner coverage for a source partition.</summary>
/// <param name="SourceSystem">The source-system identifier.</param>
/// <param name="SourcePartition">The source partition identifier.</param>
/// <param name="GenerationId">The contribution generation associated with this progress.</param>
/// <param name="SourceVersion">The source-defined version token.</param>
/// <param name="SourcePosition">The source-defined position token.</param>
/// <param name="Version">The native database version recording this progress.</param>
/// <param name="Coverage">Optional conservative source-owner coverage.</param>
public sealed record CheckpointSourcePartitionProgress(Uri SourceSystem, Uri SourcePartition,
    Guid GenerationId, string SourceVersion, string SourcePosition, DatabaseVersion Version, SemanticOwnerCoverage? Coverage = null);

/// <summary>Persists semantic property storage bindings at one catalog version.</summary>
/// <param name="Version">The property-storage catalog version.</param>
/// <param name="Storage">The explicit property storage bindings.</param>
public sealed record PropertyStorageCatalogCheckpoint(
    SemanticPropertyStorageCatalogVersion Version, IReadOnlyList<SemanticPropertyStorage> Storage);

/// <summary>Persists governance rules at one catalog version.</summary>
/// <param name="Version">The governance catalog version.</param>
/// <param name="Rules">The retained governance rule definitions.</param>
public sealed record GovernanceCatalogCheckpoint(
    GovernanceCatalogVersion Version, IReadOnlyList<SemanticGovernanceRule> Rules);

/// <summary>Loads and saves semantic checkpoint metadata.</summary>
public interface ISemanticCheckpointStore : IAsyncDisposable
{
    /// <summary>Loads the latest checkpoint, or returns null when none exists.</summary>
    ValueTask<SemanticCheckpoint?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists the supplied semantic checkpoint.</summary>
    ValueTask SaveAsync(SemanticCheckpoint checkpoint, CancellationToken cancellationToken = default);
}

/// <summary>Exposes the encoded size of the latest saved semantic checkpoint.</summary>
public interface ISemanticCheckpointDiagnostics
{
    /// <summary>Gets the number of encoded bytes in the last saved checkpoint.</summary>
    long LastSavedBytes
    {
        get;
    }
}
