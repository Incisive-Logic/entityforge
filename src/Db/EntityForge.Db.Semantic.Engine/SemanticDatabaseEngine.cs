using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Query;
using EntityForge.Db.Routing;
using EntityForge.Db.Storage;
using EntityForge.Db;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics.Protobuf;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Semantics;
using EntityForge.Values;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Semantic.Engine;

/// <summary>Summarizes the current visible state of an embedded semantic database.</summary>
internal sealed record LocalDatabaseStatus(
    DatabaseVersion Version,
    long FieldAssertions,
    long RelationshipAssertions,
    int ActiveSnapshotLeases,
    int ResumableWrites,
    int TrackedSourcePartitions);

/// <summary>
/// Provides the embedded semantics-integrated database profile, including semantic writes, queries,
/// governance, snapshots and committed logical changes.
/// </summary>
/// <remarks>Open instances with <see cref="OpenAsync"/> and dispose them asynchronously.</remarks>
internal sealed partial class SemanticDatabaseEngine : ISemanticDatabase, ISemanticEvidenceQueryService, IEntityMappingHost, ISemanticMappedExecution, IEntityOperationHost, IDatabaseOperationRetention
{
    Guid ISemanticMappedExecution.DatabaseIncarnation => TransferRuntime.DatabaseIncarnation;

    ValueTask<ISemanticEntityReadScope> ISemanticMappedExecution.AcquireMappedReadAsync(
        Uri sourceSystem, Uri sourcePartition, TimeSpan duration, CancellationToken cancellationToken) =>
        AcquireMappedReadAsync(sourceSystem, sourcePartition, duration, cancellationToken);

    ValueTask<SessionCommitResult?> ISemanticMappedExecution.ResolveMappedReplayAsync(
        SemanticSessionEnvelope session, CancellationToken cancellationToken) =>
        ResolvePreparedReplayAsync(session, cancellationToken);

    ValueTask<SessionCommitResult> ISemanticMappedExecution.ApplyMappedSessionAsync(
        SemanticSessionEnvelope session, Action<IReadOnlyList<SemanticEntityChange>> validateCompleteChanges,
        CancellationToken cancellationToken) => ApplySessionAsync(session, validateCompleteChanges, cancellationToken);

    private readonly EntityOperationRegistry? _operations;

    EntityOperationRegistry IEntityOperationHost.Operations => _operations ?? throw new NotSupportedException("Durable request binding is unavailable.");

    private readonly DatabaseCommitQueue<EntityOperationBinding> _bindingCommits;

    private async ValueTask CommitOperationAsync(EntityOperationBinding binding, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _bindingCommits.CommitAsync(binding, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<DatabaseCommitReceipt> CommitBindingAsync(EntityOperationBinding binding) =>
        _log.CommitWithReceiptAsync(DurableDatabaseCommit.ForApplicationOperation(binding), CancellationToken.None);

    private ValueTask<IReadOnlyList<DatabaseCommitReceipt>> CommitBindingsAsync(IReadOnlyList<EntityOperationBinding> bindings) =>
        _log.CommitGroupWithReceiptsAsync(bindings.Select(DurableDatabaseCommit.ForApplicationOperation).ToArray(),
            CancellationToken.None);

    private readonly EntityMappingRegistry? _mappingRegistry;

    public EntityMappingRegistry MappingRegistry => _mappingRegistry ?? throw new NotSupportedException(
        "The configured Semantic durability service does not support mapping registration.");

    private async ValueTask<DatabaseCommitReceipt> CommitMappingAsync(EntityMappingRegistration registration,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _log.CommitWithReceiptAsync(DurableDatabaseCommit.ForMappingRegistration(registration),
            cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    internal IDatabaseTransferRuntime TransferRuntime =>
        _log as IDatabaseTransferRuntime ?? throw new InvalidOperationException(
            "The configured Semantic durability service does not expose transfer support.");

    public ISemanticEvidenceQueryService Evidence => this;

    public DatabaseVersion CurrentVersion => new(_state.Current.Version.Value);

    public DatabaseVersion OldestReadableVersion => new(_state.RetentionFloor(_state.Current, _timeProvider.GetUtcNow()).Value);

    internal ValueTask<IDisposable> RetainDistributedSnapshotAsync(DatabaseVersion version,
        CancellationToken cancellationToken = default) =>
        (_log as ISemanticTransientSnapshotAdmission ?? throw new InvalidOperationException(
            "The semantic durability provider cannot retain a transient distributed snapshot."))
        .RetainTransientSnapshotAsync(version, cancellationToken);

    internal async ValueTask<SemanticDistributedState> AcquireDistributedStateAsync(DatabaseVersion version,
        CancellationToken cancellationToken)
    {
        if (_state.TimestampRead is { } logical)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(this, logical, null, new TimestampReadBorrow(), _state.TimestampServingView(logical));
        }
        IDisposable retention = await RetainDistributedSnapshotAsync(version, cancellationToken).ConfigureAwait(false);
        ISemanticHistoricalAccessPath? physical = null;
        try
        {
            var state = _state.CapturePreparationState(version);
            if (state.Version.Value > version.Value)
            {
                throw new EntityForgeSnapshotUnavailableException(version, state.Version);
            }

            if (state.Version != version)
            {
                throw new InvalidOperationException("The resolved Semantic boundary no longer has a current source-membership snapshot.");
            }

            physical = _physicalCandidates.CaptureCurrent(version);
            return new(this, state, physical, retention);
        }
        catch (Exception failure)
        {
            try
            {
                physical?.Dispose();
                retention.Dispose();
            }
            catch (Exception cleanup)
            {
                throw new SemanticRetainedReadCleanupException([failure, cleanup]);
            }
            throw;
        }
    }

    private sealed class TimestampReadBorrow : IDisposable
    {
        public void Dispose()
        {
        }
    }

    internal ValueTask<EntityProjectionProjectionResult> ProjectRetainedAsync(SemanticDistributedState retained,
        SemanticEntityProjectionQuery query, CancellationToken token) =>
        _queries.ProjectRetainedAsync(retained.RequireReadView(this), query, token);

    internal ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseRetainedAsync(SemanticDistributedState retained,
        SemanticRelationshipTraversalQuery query, CancellationToken token) =>
        _queries.TraverseRetainedAsync(retained.RequireReadView(this), query, token);

    /// <summary>Default maximum number of commits waiting to enter the durability frontier.</summary>
    public const int DefaultMaximumQueuedPublications = 1024;
    private const long InitialValidationFloor = 0;
    private const int MaximumOrderedCommitGroupCount = 16;
    private readonly SemanticCommitPreparationCoordinator _commitGate;
    private readonly ISemanticCommitLog _log;
    private readonly SemanticCommitLogCandidateAccessPath _physicalCandidates;
    private readonly IShardRouter _router;
    private readonly SemanticDependencyTracker _dependencies = new();
    private readonly SemanticStateProjector _projector = new();
    private readonly SourceSnapshotReconciler _sourceSnapshots = new();

    internal Action? BeforeSourceMembershipCheck
    {
        set => _sourceSnapshots.BeforeActiveOwnerCheck = value;
    }

    private readonly ResumableWriteStateProjector _resumableState = new();
    private readonly SemanticChangeSessionTransportCodec _codec = new();
    private readonly SemanticDatabaseStateStore _state;
    private readonly SemanticDatabaseQueryService _queries;
    private readonly SemanticPropertyStorageAdvisor _storageAdvisor;
    private readonly TransactionalSemanticQueryService _transactionQueries;
    private readonly LocalCheckpointManager _checkpointManager;
    private readonly SnapshotLeaseManager _snapshotLeases;
    private readonly CatalogMutationManager _catalogMutations;
    private readonly SemanticProfileStateCoordinator _profileState;
    private readonly ResumableWriteManager _resumableWrites;
    private readonly IEntityForgeCommitObserver _commitObserver;
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly TimeProvider _timeProvider;
    private readonly JsonValueAdmissionPolicy _jsonAdmissionPolicy;
    private readonly SemanticConformanceMode _conformanceMode;
    private readonly DatabaseMaintenanceCoordinator _maintenance = new();
    private readonly TransactionValidationIndex _validation =
        new(new DatabaseVersion(InitialValidationFloor));

    internal int RetainedValidationCommitCount => _validation.RetainedCommitCount;

    private readonly object _orderedCommitGate = new();
    private readonly object _disposeSync = new();

    internal Func<ValueTask>? BeforeOrderedCommitAdmission
    {
        get; set;
    }

    internal Func<ValueTask>? BeforeOrderedCommitDrain
    {
        get; set;
    }

    internal Func<ValueTask>? AfterOrdinaryScopeObservation
    {
        get; set;
    }

    internal Func<ValueTask>? BeforeEntityExplanationProjection
    {
        get => _queries.BeforeEntityExplanationProjection;
        set => _queries.BeforeEntityExplanationProjection = value;
    }

    internal Func<ValueTask>? BeforeFieldQuerySnapshotCapture
    {
        get => _queries.BeforeFieldQuerySnapshotCapture;
        set => _queries.BeforeFieldQuerySnapshotCapture = value;
    }

    private readonly Queue<PendingOrderedCommit> _orderedCommits = new();
    private readonly CancellationTokenSource _orderedCommitShutdown = new();
    private readonly HashSet<Task> _orderedCommitTasks = [];
    private readonly int _maximumOrderedCommitQueue;
    private long _orderedCommitsAdmitted;
    private long _orderedCommitsRejected;
    private bool _orderedCommitRunner;
    private bool _disposed;
    private Task? _disposeTask;

    /// <summary>Returns the currently retained committed-change cursor bounds.</summary>
    public ValueTask<DatabaseChangeFeedBounds> GetChangeFeedBoundsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ChangeJournal().GetBoundsAsync(cancellationToken);
    }

    /// <summary>Atomically acquires a leased snapshot and its gap-free change-feed boundary.</summary>
    public ValueTask<SemanticChangeBootstrap> AcquireChangeBootstrapAsync(
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _snapshotLeases.AcquireBootstrapAsync(duration,
            token => ChangeJournal().GetBoundsAsync(token), cancellationToken);
    }

    /// <summary>Reads committed logical changes strictly after the supplied cursor.</summary>
    public ValueTask<DatabaseChangePage> ReadChangePageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ChangeJournal().ReadPageAfterAsync(cursor, selector, maximumCount, cancellationToken);
    }

    /// <summary>Reads committed semantic changes as decoded logical effects.</summary>
    public async ValueTask<SemanticCommittedChangePage> ReadSemanticChangePageAfterAsync(
        DatabaseChangeCursor? cursor, DatabaseChangeSelector selector, int maximumCount,
        CancellationToken cancellationToken = default)
    {
        DatabaseChangePage page = await ReadChangePageAfterAsync(cursor, selector, maximumCount,
            cancellationToken).ConfigureAwait(false);
        return new(page.Changes.Select(DecodeSemanticChange).ToArray(), page.NextCursor, page.HasMore);
    }

    /// <summary>Checks retained semantic changes for effects that may alter an entity-query result.</summary>
    public async ValueTask<SemanticEntityQueryInvalidation> CheckEntityQueryInvalidationAsync(
        EntityQueryDefinition query, DatabaseVersion observedSnapshotVersion,
        DatabaseChangeCursor? cursor = null,
        int maximumChanges = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (observedSnapshotVersion.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedSnapshotVersion));
        }

        var analyzer = new SemanticEntityQueryInvalidationAnalyzer(query, _state.Current.Catalog);
        SemanticCommittedChangePage page = await ReadSemanticChangePageAfterAsync(cursor,
            new DatabaseChangeSelector(DatabaseProfileId.Semantic), maximumChanges, cancellationToken)
            .ConfigureAwait(false);
        SemanticCommittedChangeSet? invalidating = null;
        SemanticCommittedEffectKind[] relevant = [];
        foreach (SemanticCommittedChangeSet change in page.Changes.Where(
                     item => item.Version.Value > observedSnapshotVersion.Value))
        {
            if (change.RequiresSnapshotRefresh)
            {
                invalidating = change;
                break;
            }
            relevant = change.Effects.Where(analyzer.IsRelevant).Select(item => item.Kind).Distinct().ToArray();
            if (relevant.Length == 0)
            {
                continue;
            }

            invalidating = change;
            break;
        }
        return new(invalidating is not null, observedSnapshotVersion,
            invalidating?.Version, invalidating?.TransactionId, page.NextCursor, page.HasMore,
            page.Changes.Count, relevant);
    }

    /// <summary>Streams retained and subsequently committed logical changes after a cursor.</summary>
    public IAsyncEnumerable<CommittedChangeSet> SubscribeToChangesAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return ChangeJournal().SubscribeAsync(cursor, selector, options, cancellationToken);
    }

    /// <summary>Runs a multiplexed command/event session for duplex-capable clients.</summary>
    public IAsyncEnumerable<SemanticEntityQuerySubscriptionSessionEvent> RunEntityQuerySubscriptionSessionAsync(
        IAsyncEnumerable<SemanticEntityQuerySubscriptionCommand> commands,
        SemanticEntityQuerySubscriptionSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return new SemanticEntityQuerySubscriptionSessionRunner(this).RunAsync(
            commands, options ?? new(), cancellationToken);
    }

    /// <summary>Streams resumable progress until a committed semantic effect may invalidate the query.</summary>
    public async IAsyncEnumerable<SemanticEntityQuerySubscriptionEvent> SubscribeToEntityQueryInvalidationAsync(
        EntityQueryDefinition query, DatabaseVersion observedSnapshotVersion,
        DatabaseChangeCursor? cursor = null, DatabaseChangeSubscriptionOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (observedSnapshotVersion.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedSnapshotVersion));
        }

        var analyzer = new SemanticEntityQueryInvalidationAnalyzer(query, _state.Current.Catalog);
        _telemetry.QuerySubscriptionStarted();
        try
        {
            await foreach (CommittedChangeSet committed in SubscribeToChangesAsync(cursor,
                new DatabaseChangeSelector(DatabaseProfileId.Semantic), options ?? new(), cancellationToken)
                .ConfigureAwait(false))
            {
                SemanticCommittedChangeSet change = DecodeSemanticChange(committed);
                SemanticCommittedEffectKind[] relevant = change.Version.Value > observedSnapshotVersion.Value
                    ? change.Effects.Where(analyzer.IsRelevant).Select(item => item.Kind).Distinct().ToArray()
                    : [];
                var kind = !change.RequiresSnapshotRefresh && relevant.Length == 0
                    ? SemanticEntityQuerySubscriptionEventKind.Progress
                    : SemanticEntityQuerySubscriptionEventKind.Invalidated;
                _telemetry.RecordQuerySubscriptionEvent(kind.ToString().ToLowerInvariant());
                yield return new(kind, observedSnapshotVersion, change.Version, change.TransactionId,
                    change.Cursor, relevant);
                if (kind is SemanticEntityQuerySubscriptionEventKind.Invalidated)
                {
                    yield break;
                }
            }
        }
        finally
        {
            _telemetry.QuerySubscriptionEnded();
        }
    }

    private static SemanticCommittedChangeSet DecodeSemanticChange(CommittedChangeSet change)
    {
        if (change.Payload.RequiresSnapshotRefresh)
        {
            return new(change.DatabaseIncarnation, new(change.Version.Value), change.TransactionId,
                change.Cursor, [], RequiresSnapshotRefresh: true);
        }

        if (!string.Equals(change.Payload.CodecId, SemanticCommittedEffectsCodec.CodecId,
                StringComparison.Ordinal) ||
            !SemanticCommittedEffectsCodec.SupportsVersion(change.Payload.CodecVersion))
        {
            throw new InvalidDataException($"Semantic change payload codec '{change.Payload.CodecId}' " +
                $"version {change.Payload.CodecVersion} is unsupported.");
        }

        SemanticCommittedEffects effects = SemanticCommittedEffectsCodec.Decode(change.Payload.Content, change.Payload.CodecVersion);
        return new(change.DatabaseIncarnation, new(change.Version.Value), change.TransactionId,
            change.Cursor, effects.Effects);
    }

    private SemanticDatabaseEngine(
        ISemanticCommitLog log,
        ISemanticCandidateSnapshotProvider candidates,
        IShardRouter router,
        IEntityForgeCommitObserver? commitObserver,
        EntityForgeDbTelemetry? telemetry,
        TimeProvider? timeProvider,
        IEnumerable<ISemanticGovernanceEvaluator>? governanceEvaluators,
        int maximumQueuedPublications,
        JsonValueAdmissionPolicy jsonAdmissionPolicy,
        SemanticConformanceMode conformanceMode,
        SemanticContributionRevisionLimits contributionRevisionLimits)
    {
        _log = log;
        if (log is IEntityOperationCatalogProvider operations)
        {
            _operations = new(operations.OperationCatalog, CommitOperationAsync, log as IEntityOperationCompactionAuthority);
        }

        if (log is IEntityMappingCatalogProvider mappings)
        {
            _mappingRegistry = new(mappings.MappingCatalog, CommitMappingAsync, concurrentExecution: true);
        }

        _physicalCandidates = new(candidates);
        _router = router;
        _commitObserver = commitObserver ?? new NoEntityForgeCommitObserver();
        _telemetry = telemetry ?? new EntityForgeDbTelemetry();
        _ownsTelemetry = telemetry is null;
        _timeProvider = timeProvider ?? TimeProvider.System;
        ISemanticGovernanceEvaluator[] evaluators =
            (governanceEvaluators ?? Array.Empty<ISemanticGovernanceEvaluator>()).ToArray();
        _jsonAdmissionPolicy = jsonAdmissionPolicy ?? throw new ArgumentNullException(nameof(jsonAdmissionPolicy));
        _conformanceMode = Enum.IsDefined(conformanceMode) ? conformanceMode
            : throw new ArgumentOutOfRangeException(nameof(conformanceMode));
        _state = new(_physicalCandidates, _telemetry);
        _state.IsDistributed = () => _log is ISemanticTransientSnapshotAdmission { RetainsRecentDistributedVersions: true };
        _maximumOrderedCommitQueue = maximumQueuedPublications;
        _commitGate = new SemanticCommitPreparationCoordinator(_telemetry, maximumQueuedPublications);
        _bindingCommits = new(() => _commitGate.WaitAsync(CancellationToken.None), () => _commitGate.Release(),
            CommitBindingAsync, CommitBindingsAsync,
            binding => binding.OperationId, "Semantic");
        _storageAdvisor = new(_timeProvider, _telemetry);
        _queries = new(_state, _telemetry, _timeProvider, _storageAdvisor,
            log as IDatabaseSnapshotReadAdmission);
        _transactionQueries = new(_commitGate, _log, _state, _telemetry, evaluators);
        _checkpointManager = new(_log, _telemetry);
        var mutations = new DatabaseMutationContext(_commitGate, _log, _state,
            _validation.Record, _commitObserver);
        _snapshotLeases = new(mutations, _telemetry, _timeProvider);
        _catalogMutations = new(mutations, _telemetry);
        _profileState = new(_log, candidates, _router, _dependencies, _projector,
            _sourceSnapshots, _resumableState, _validation, _state, _telemetry,
            _timeProvider, _commitObserver, _jsonAdmissionPolicy, _conformanceMode,
            contributionRevisionLimits, _commitGate);
        _resumableWrites = new(mutations, _resumableState, _dependencies, _router,
            _telemetry, _timeProvider);
    }

    private IDatabaseCommittedChangeJournal ChangeJournal() =>
        _log as IDatabaseCommittedChangeJournal ?? throw new InvalidOperationException(
            "The configured semantic commit log does not expose committed logical changes.");

    /// <summary>Opens a semantic database and completes recovery before returning it.</summary>
    internal static async ValueTask<SemanticDatabaseEngine> OpenAsync(
        ISemanticCommitLog log,
        ISemanticCandidateSnapshotProvider candidates,
        IShardRouter router,
        EntityForgeDbTelemetry? telemetry = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        IEnumerable<ISemanticGovernanceEvaluator>? governanceEvaluators = null,
        int maximumQueuedPublications = DefaultMaximumQueuedPublications,
        JsonValueAdmissionPolicy? jsonAdmissionPolicy = null,
        SemanticConformanceMode conformanceMode = SemanticConformanceMode.Validate,
        SemanticContributionRevisionLimits? contributionRevisionLimits = null)
        => await OpenCoreAsync(log, candidates, router, null, telemetry, cancellationToken, timeProvider,
            governanceEvaluators, maximumQueuedPublications, jsonAdmissionPolicy, conformanceMode,
            contributionRevisionLimits).ConfigureAwait(false);

    internal static async ValueTask<SemanticDatabaseEngine> OpenWithObserverAsync(
        ISemanticCommitLog log,
        ISemanticCandidateSnapshotProvider candidates,
        IShardRouter router,
        IEntityForgeCommitObserver commitObserver,
        EntityForgeDbTelemetry? telemetry = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        IEnumerable<ISemanticGovernanceEvaluator>? governanceEvaluators = null,
        int maximumQueuedPublications = DefaultMaximumQueuedPublications,
        JsonValueAdmissionPolicy? jsonAdmissionPolicy = null,
        SemanticConformanceMode conformanceMode = SemanticConformanceMode.Validate,
        SemanticContributionRevisionLimits? contributionRevisionLimits = null)
        => await OpenCoreAsync(log, candidates, router, commitObserver, telemetry, cancellationToken, timeProvider,
            governanceEvaluators, maximumQueuedPublications, jsonAdmissionPolicy, conformanceMode,
            contributionRevisionLimits).ConfigureAwait(false);

    private static async ValueTask<SemanticDatabaseEngine> OpenCoreAsync(
        ISemanticCommitLog log,
        ISemanticCandidateSnapshotProvider candidates,
        IShardRouter router,
        IEntityForgeCommitObserver? commitObserver,
        EntityForgeDbTelemetry? telemetry,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider,
        IEnumerable<ISemanticGovernanceEvaluator>? governanceEvaluators,
        int maximumQueuedPublications,
        JsonValueAdmissionPolicy? jsonAdmissionPolicy,
        SemanticConformanceMode conformanceMode,
        SemanticContributionRevisionLimits? contributionRevisionLimits)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(router);
        var database = new SemanticDatabaseEngine(log, candidates, router, commitObserver, telemetry,
            timeProvider, governanceEvaluators, maximumQueuedPublications,
            jsonAdmissionPolicy ?? JsonValueAdmissionPolicy.Default, conformanceMode,
            contributionRevisionLimits ?? SemanticContributionRevisionLimits.Default);
        try
        {
            await database._profileState.RecoverAsync(cancellationToken).ConfigureAwait(false);
            return database;
        }
        catch
        {
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public ValueTask<SessionCommitResult> ApplyAsync(
        SemanticSessionEnvelope session,
        CancellationToken cancellationToken = default) => ApplySessionAsync(session, null, cancellationToken);

    internal async ValueTask<SessionCommitResult> ApplySessionAsync(SemanticSessionEnvelope session,
        Action<IReadOnlyList<SemanticEntityChange>>? validateCompleteChanges, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        bool revisionManaged = session is not null && session.Start is not null &&
            _profileState.IsContributionRevisionManaged(session.Start.Source);
        if (AfterOrdinaryScopeObservation is not null)
        {
            await AfterOrdinaryScopeObservation().ConfigureAwait(false);
        }

        if (revisionManaged && session is not null && session.Start is not null)
        {
            DatabaseCommitReceipt? replay = await ResolveOrdinaryReplayAsync(
                session, isInitialBulkLoad: false, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                return Result(session, replay);
            }

            RejectRevisionManagedOrdinaryWrite(session.Start);
        }
        if (session is not null && session.Start is not null &&
            _state.Current.AppliedSessions.TryGetValue(session.Start.SessionId, out AppliedSession? applied))
        {
            return await ReplayAsync(session, applied, isInitialBulkLoad: false, cancellationToken)
                .ConfigureAwait(false);
        }

        EntityForgeWriteTransaction transaction = await BeginWriteAsync(session!, cancellationToken).ConfigureAwait(false);
        validateCompleteChanges?.Invoke(transaction.Changes);
        if (validateCompleteChanges is not null)
        {
            return await CommitConvenienceAsync(transaction, validateCompleteChanges, cancellationToken).ConfigureAwait(false);
        }
        // Apply owns preparation: expand a source replacement at admission, just as mapped
        // delivery does. Explicit transactions retain their original snapshot/conflict contract.
        if (session!.Start!.Kind is SemanticChangeSessionKind.SourceSnapshot)
        {
            return await CommitConvenienceAsync(transaction, static _ =>
{

}, cancellationToken).ConfigureAwait(false);
        }

        return await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<SessionCommitResult> BulkLoadAsync(
        SemanticSessionEnvelope session,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(session);
        bool revisionManaged = _profileState.IsContributionRevisionManaged(session.Start.Source);
        if (AfterOrdinaryScopeObservation is not null)
        {
            await AfterOrdinaryScopeObservation().ConfigureAwait(false);
        }

        if (revisionManaged)
        {
            DatabaseCommitReceipt? replay = await ResolveOrdinaryReplayAsync(
                session, isInitialBulkLoad: true, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                return Result(session, replay);
            }

            RejectRevisionManagedOrdinaryWrite(session.Start);
        }
        SemanticDatabaseState state = _state.Current;
        if (state.AppliedSessions.TryGetValue(session.Start.SessionId, out AppliedSession? applied))
        {
            return await ReplayAsync(session, applied, isInitialBulkLoad: true, cancellationToken)
                .ConfigureAwait(false);
        }

        if (state.AssertionStatistics.FieldCount != 0 ||
            state.AssertionStatistics.RelationshipCount != 0 ||
            state.AppliedSessions.Count != 0)
        {
            throw new InvalidOperationException("Semantic bulk load requires an empty database.");
        }

        EntityForgeWriteTransaction transaction = await BeginWriteCoreAsync(
            session, isInitialBulkLoad: true, cancellationToken).ConfigureAwait(false);
        return await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SessionCommitResult> ReplayAsync(
        SemanticSessionEnvelope session,
        AppliedSession applied,
        bool isInitialBulkLoad,
        CancellationToken cancellationToken)
    {
        Validate(session);
        if (applied.ContentHash != session.Close.ReplayHash)
        {
            throw new SemanticSessionValidationException(
                "Session replay content differs from the durable session.");
        }

        DatabaseCommitReceipt? receipt = await _log.ResolveReplayReceiptAsync(
            DurableDatabaseCommit.ForSemanticSession(session, isInitialBulkLoad), cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            throw new SemanticSessionValidationException(
                "The applied session has no matching ordinary transaction receipt.");
        }

        return Result(session, receipt);
    }

    internal SemanticCommitPreparationSnapshot GetPublicationFrontierSnapshot() => _commitGate.Snapshot();

    internal SemanticCommitPreparationSnapshot GetCommitCoordinatorSnapshot()
    {
        ThrowIfDisposed();
        SemanticCommitPreparationSnapshot frontier = _commitGate.Snapshot();
        lock (_orderedCommitGate)
        {
            return frontier with
            {
                WaitingPublications = _orderedCommits.Count,
                AdmittedPublications = _orderedCommitsAdmitted,
                RejectedPublications = _orderedCommitsRejected
            };
        }
    }

    /// <summary>Returns a lightweight status snapshot for diagnostics and hosting.</summary>
    public ValueTask<LocalDatabaseStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.GetStatusAsync(cancellationToken);
    }

    /// <summary>Begins an optimistic semantic write transaction at the current read version.</summary>
    public ValueTask<EntityForgeWriteTransaction> BeginWriteAsync(
        SemanticSessionEnvelope session,
        CancellationToken cancellationToken = default) =>
        BeginWriteCoreAsync(session, isInitialBulkLoad: false, cancellationToken);

    internal ValueTask<EntityForgeWriteTransaction> BeginDistributedWriteAsync(
        SemanticSessionEnvelope session, SemanticDistributedState retained, DatabaseVersion version,
        CancellationToken cancellationToken)
    {
        return BeginWriteCoreAsync(session, false, cancellationToken,
            retainedSnapshot: retained.Preparation(this, version));
    }

    private ValueTask<EntityForgeWriteTransaction> BeginWriteCoreAsync(
        SemanticSessionEnvelope session, bool isInitialBulkLoad,
        CancellationToken cancellationToken, bool allowRevisionManagedScope = false,
        DatabasePreparationSnapshot? retainedSnapshot = null)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Validate(session);
        if (!allowRevisionManagedScope && _profileState.IsContributionRevisionManaged(session.Start.Source))
        {
            throw new SemanticContributionRevisionException(session.Start.SessionId,
                "This source scope is revision-managed and cannot accept ordinary Semantic writes.");
        }

        SemanticEntityChange[] suppliedChanges = session.Pages.OrderBy(page => page.PageNumber)
            .SelectMany(page => page.Changes).ToArray();
        DatabasePreparationSnapshot snapshot = retainedSnapshot ?? _state.PreparationSnapshot;
        // Preparation is concurrent and may use metadata-only retained state. Keep its
        // expansion separate from the reconciler used by native commit planning.
        IReadOnlyList<SemanticEntityChange> changes = new SourceSnapshotReconciler().Expand(snapshot.State, session,
            suppliedChanges);
        SemanticSourcePartitionGuard.Validate(snapshot.State, session.Start, changes);
        IReadOnlyList<SemanticWriteDependency> writes = _dependencies.Capture(changes);
        // An empty contribution still commits source progress. Route that real scope dependency
        // without inventing an entity mutation for an otherwise empty participant.
        RoutedParticipantSet participants = writes.Count == 0
            ? _router.Route(new[] { session.Start.Source }) : _router.Route(writes);
        LocalShardTopology.RequireLocalParticipant(participants);
        IReadOnlyList<SemanticPredicateDependency> predicateWrites =
            SemanticPredicateDependency.WritesFrom(snapshot.State, changes);
        ValidateCatalog(snapshot.Catalog, session);
        SemanticConformanceReport conformance = EvaluateConformance(snapshot.Catalog, session);
        if (_conformanceMode is SemanticConformanceMode.Strict && !conformance.IsConformant)
        {
            throw new SemanticSessionValidationException(
                "The semantic session does not conform to the active vocabulary: " +
                string.Join("; ", conformance.Issues.Select(item => item.Message)));
        }

        return ValueTask.FromResult(new EntityForgeWriteTransaction(this, session, changes, writes,
            predicateWrites, snapshot.State.Version, snapshot.State.PropertyStorage,
            snapshot.State.Governance, participants, isInitialBulkLoad: isInitialBulkLoad));
    }

    /// <summary>Durably prepares a semantic write that can be resumed after process failure.</summary>
    public ValueTask<ResumableWriteTransaction> PrepareResumableWriteAsync(
        SemanticSessionEnvelope session,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Validate(session);
        ValidateLeaseDuration(leaseDuration);
        return _resumableWrites.PrepareAsync(session, leaseDuration,
            () => RejectRevisionManagedOrdinaryWrite(session.Start), cancellationToken);
    }

    /// <summary>Promotes an active local write transaction to durable resumable state.</summary>
    public ValueTask<ResumableWriteTransaction> PromoteToResumableAsync(
        EntityForgeWriteTransaction transaction,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(transaction);
        ValidateLeaseDuration(leaseDuration);
        return _resumableWrites.PromoteAsync(transaction, leaseDuration, () =>
        {
            RejectRevisionManagedOrdinaryWrite(transaction.Session.Start);
            transaction.DetachForResume(this);
        }, cancellationToken);
    }

    /// <summary>Resumes a durably prepared semantic write transaction.</summary>
    public async ValueTask<EntityForgeWriteTransaction> ResumeWriteAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A resumable transaction ID cannot be empty.", nameof(transactionId));
        }

        ResumedWriteState resumed = await _resumableWrites.ResumeAsync(transactionId,
            cancellationToken).ConfigureAwait(false);
        RejectRevisionManagedOrdinaryWrite(resumed.Prepared.Session.Start);
        LocalShardTopology.RequireLocalParticipant(resumed.Participants);
        return new(this, resumed.Prepared.Session, resumed.Changes, resumed.OwnershipWrites,
            resumed.PredicateWrites, resumed.Prepared.ReadVersion, resumed.PropertyStorage,
            resumed.Governance, resumed.Participants, resumed.Prepared.TransactionId,
            resumed.PredicateReads);
    }

    /// <summary>Closes and abandons a durably prepared semantic write.</summary>
    public ValueTask AbandonResumableWriteAsync(
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A resumable transaction ID cannot be empty.", nameof(transactionId));
        }

        return _resumableWrites.AbandonAsync(transactionId, cancellationToken);
    }

    internal async ValueTask<SessionCommitResult> CommitAsync(
        EntityForgeWriteTransaction write, CancellationToken cancellationToken)
        => await EnqueueCommitAsync(write, null, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<SessionCommitResult> ApplyAuthoritativeCommitAsync(
        EntityForgeWriteTransaction write, DatabaseVersion authoritativeVersion,
        CancellationToken cancellationToken = default)
    {
        if (authoritativeVersion.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authoritativeVersion),
                "An authoritative semantic commit version must be positive.");
        }

        return await EnqueueCommitAsync(write, authoritativeVersion, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<DatabaseCommitReceipt> CommitContributionRevisionAsync(
        SemanticContributionRevisionChange change,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(change);
        try
        {
            if (change.Kind is SemanticContributionRevisionChangeKind.Publish)
            {
                return await CommitContributionPublicationAsync(change, cancellationToken).ConfigureAwait(false);
            }

            if (change.Kind is SemanticContributionRevisionChangeKind.Begin)
            {
                await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await _log.CommitWithReceiptAsync(DurableDatabaseCommit.ForContributionRevision(change),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _commitGate.Release();
                }
            }
            return await _log.CommitWithReceiptAsync(DurableDatabaseCommit.ForContributionRevision(change),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseGenerationPublicationConflictException exception)
        {
            throw new SemanticContributionRevisionConflictException(change.GenerationId,
                Revision(exception.Expected), Revision(exception.Actual), exception);
        }
        catch (EntityForgeDbCommitException wrapper) when
            (wrapper.InnerException is DatabaseGenerationPublicationConflictException)
        {
            var exception = (DatabaseGenerationPublicationConflictException)wrapper.InnerException;
            throw new SemanticContributionRevisionConflictException(change.GenerationId,
                Revision(exception.Expected), Revision(exception.Actual), exception);
        }
        catch (Exception exception) when (exception is DatabaseGenerationIdentityConflictException or
            DatabaseGenerationPartConflictException or DatabaseGenerationManifestConflictException or
            DatabaseGenerationLifecycleException or KeyNotFoundException or InvalidDataException)
        {
            throw new SemanticContributionRevisionException(change.GenerationId, exception.Message, exception);
        }
        catch (EntityForgeDbCommitException exception) when (exception.InnerException is
            DatabaseGenerationIdentityConflictException or DatabaseGenerationPartConflictException or
            DatabaseGenerationManifestConflictException or DatabaseGenerationLifecycleException or
            KeyNotFoundException or InvalidDataException)
        {
            throw new SemanticContributionRevisionException(change.GenerationId,
                exception.InnerException.Message, exception.InnerException);
        }
    }

    private static SemanticContributionRevision? Revision(DatabaseGenerationIdentity? value) => value is null
        ? null : new(value.GenerationId, value.Revision, value.DefinitionHash);

    public ValueTask<SemanticContributionRevision?> GetPublishedRevisionAsync(
        SemanticContributionScope scope, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_profileState.GetPublishedContributionRevision(scope));
    }

    public ValueTask<SemanticContributionRevisionInfo?> GetRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("A contribution revision identifier is required.", nameof(generationId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_profileState.GetContributionRevision(generationId));
    }

    public ValueTask<DatabaseCommitReceipt> BeginRevisionAsync(SemanticChangeSessionStart start,
        SemanticContributionRevision? expectedPublishedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        return CommitContributionRevisionAsync(new(SemanticContributionRevisionChangeKind.Begin,
            start.SessionId, Start: start, ExpectedPublishedRevision: expectedPublishedRevision),
            cancellationToken);
    }

    public ValueTask<DatabaseCommitReceipt> AcceptRevisionPartAsync(SemanticChangeSessionPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return CommitContributionRevisionAsync(new(SemanticContributionRevisionChangeKind.AcceptPart,
            page.SessionId, Page: page), cancellationToken);
    }

    public ValueTask<DatabaseCommitReceipt> SealRevisionAsync(SemanticChangeSessionClose close,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(close);
        return CommitContributionRevisionAsync(new(SemanticContributionRevisionChangeKind.Seal,
            close.SessionId, Close: close), cancellationToken);
    }

    public ValueTask<DatabaseCommitReceipt> PublishRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default) => CommitContributionRevisionAsync(new(
            SemanticContributionRevisionChangeKind.Publish, generationId), cancellationToken);

    public ValueTask<DatabaseCommitReceipt> AbandonRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default) => CommitContributionRevisionAsync(new(
            SemanticContributionRevisionChangeKind.Abandon, generationId), cancellationToken);

    private async ValueTask<DatabaseCommitReceipt> CommitContributionPublicationAsync(
        SemanticContributionRevisionChange change, CancellationToken cancellationToken)
    {
        bool gateEntered = false;
        bool prepared = false;
        try
        {
            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            SemanticContributionRevisionInfo revision = _profileState.GetContributionRevision(change.GenerationId) ??
                throw new KeyNotFoundException(
                    $"Semantic contribution revision '{change.GenerationId}' does not exist.");
            if (revision.State is SemanticContributionRevisionState.Published or
                SemanticContributionRevisionState.Retired)
            {
                return await _log.CommitWithReceiptAsync(DurableDatabaseCommit.ForContributionRevision(change),
                    cancellationToken).ConfigureAwait(false);
            }

            if (revision.State is not SemanticContributionRevisionState.Sealed)
            {
                throw new DatabaseGenerationLifecycleException(change.GenerationId,
                    "Only a sealed generation can be published.");
            }

            SemanticSessionEnvelope session = _profileState.GetSealedContributionRevision(change.GenerationId);
            EntityForgeWriteTransaction write = await BeginWriteCoreAsync(session, isInitialBulkLoad: false,
                cancellationToken, allowRevisionManagedScope: true).ConfigureAwait(false);
            _router.Validate(write.Participants);
            LocalShardTopology.RequireLocalParticipant(write.Participants);
            await _transactionQueries.EnforceApplicableGovernanceAsync(write, cancellationToken)
                .ConfigureAwait(false);
            TransactionConflict? conflict;
            try
            {
                conflict = _validation.Validate(write.ReadVersion, write.OwnershipWrites,
                    write.PredicateReads, write.PredicateWrites);
            }
            catch (ValidationHistoryUnavailableException exception)
            {
                throw new EntityForgeValidationHistoryUnavailableException(
                    exception.ReadVersion, exception.RecoveryFloor, exception);
            }
            if (conflict is not null)
            {
                throw new EntityForgeWriteConflictException(write.ReadVersion, conflict.ConflictingVersion,
                    MapConflictKind(conflict.Kind));
            }

            _profileState.Prepare(change.GenerationId, write.OwnershipWrites, write.PredicateReads.ToArray(),
                write.PredicateWrites);
            prepared = true;
            DatabaseCommitReceipt receipt = await _log.CommitWithReceiptAsync(
                DurableDatabaseCommit.ForContributionRevision(change), cancellationToken).ConfigureAwait(false);
            _storageAdvisor.RecordWrites(write.Changes);
            long observed = session.Pages.Sum(page => page.Changes.Count);
            _telemetry.RecordSourceSnapshot(observed, write.Changes.Count - observed);
            return receipt;
        }
        catch
        {
            if (prepared)
            {
                _profileState.CancelPreparation(change.GenerationId);
            }

            throw;
        }
        finally
        {
            if (gateEntered)
            {
                _commitGate.Release();
            }
        }
    }

    internal async ValueTask<SessionCommitResult> ApplyAuthoritativeSessionAsync(
        SemanticSessionEnvelope session, DatabaseVersion authoritativeVersion,
        CancellationToken cancellationToken = default)
    {
        var replay = await _log.ResolveReplayReceiptAsync(DurableDatabaseCommit.ForSemanticSession(session,
            usesAuthoritativeVersion: true), cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            if (replay.Version != authoritativeVersion)
            {
                throw new InvalidDataException("The Semantic replay differs from its authoritative version.");
            }

            return Result(session, replay);
        }
        EntityForgeWriteTransaction write = await BeginWriteAsync(session, cancellationToken).ConfigureAwait(false);
        return await write.CommitAuthoritativeAsync(authoritativeVersion, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<DatabaseDistributedWriteLease> EnterDistributedWriteModeAsync(
        CancellationToken cancellationToken = default) =>
        (_log as IDatabaseDistributedWriteAuthority ?? throw new InvalidOperationException(
            "The configured Semantic durability service does not expose distributed-write authority."))
        .EnterDistributedWriteModeAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SemanticFieldEvidence>> QueryAsync(
        SemanticFieldEqualityQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.QueryAsync(query, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticFieldEqualityBatchResult> QueryBatchAsync(
        SemanticFieldEqualityBatchQuery query, DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.QueryBatchAsync(query, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticQueryExplanation> ExplainAsync(
        SemanticFieldEqualityQuery query,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.ExplainAsync(query, analyze, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SemanticFieldEvidence>> QueryRangeAsync(
        SemanticStringRangeQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.QueryRangeAsync(query, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SemanticEntityMembershipEvidence>> QueryConceptAsync(
        SemanticConceptMembershipQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.QueryConceptAsync(query, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<long> CountConceptMembersAsync(
        SemanticConceptMembershipQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.CountConceptMembersAsync(query, snapshot, cancellationToken);
    }

    /// <summary>Executes a representation-neutral entity query and projects an entity document.</summary>
    public ValueTask<SemanticEntityQueryResult> QueryEntitiesAsync(
        EntityQueryDefinition query,
        EntityProjectionProjectionOptions? options = null,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.QueryEntitiesAsync(query, options, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticAggregationResult> AggregateEntitiesAsync(
        SemanticAggregationQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.AggregateEntitiesAsync(query, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticAggregationExplanation> ExplainAggregationAsync(
        SemanticAggregationQuery query,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.ExplainAggregationAsync(query, analyze, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticEntityQueryExplanation> ExplainEntitiesAsync(
        EntityQueryDefinition query,
        EntityProjectionProjectionOptions? options = null,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.ExplainEntitiesAsync(query, options, analyze, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<EntityProjectionProjectionResult> ProjectEntityAsync(
        SemanticEntityProjectionQuery query,
        EntityProjectionProjectionOptions? options = null,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.ProjectEntityAsync(query, options, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticEntityProjectionExplanation> ExplainEntityAsync(
        SemanticEntityProjectionQuery query,
        bool analyze = false,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.ExplainEntityAsync(query, analyze, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticCatalogSnapshot> GetSemanticCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.GetCatalogAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticResolvedCatalog> GetResolvedSemanticCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        SemanticDatabaseState state = _state.Current;
        SemanticCatalogSnapshot vocabularyCatalog = state.Catalog;
        SemanticTermDefinition[] activeDefinitions = vocabularyCatalog.Vocabularies
            .SelectMany(item => item.Terms).GroupBy(item => item.Term)
            .Select(group => group.Last()).ToArray();
        SemanticTermDefinition[] properties = activeDefinitions
            .Where(item => item.Field is not null && !item.IsDeprecated).ToArray();
        SemanticTermDefinition[] relationships = activeDefinitions
            .Where(item => item.Relationship is not null && !item.IsDeprecated).ToArray();
        SemanticAxiom[] axioms = vocabularyCatalog.Vocabularies.SelectMany(item => item.Axioms).ToArray();
        SemanticResolvedConcept[] concepts = activeDefinitions
            .Where(item => item.Term.Role is SemanticTermRole.Concept && !item.IsDeprecated)
            .Select(concept => new SemanticResolvedConcept(
                concept,
                properties.Where(property => property.Field!.ApplicableConcepts.Count == 0 ||
                        property.Field.ApplicableConcepts.Contains(concept.Term))
                    .Select(property => new SemanticResolvedProperty(property)).ToArray(),
                relationships.Where(relationship =>
                        relationship.Relationship!.SourceConcepts.Contains(concept.Term)).ToArray(),
                axioms.Where(item => item.Kind is SemanticAxiomKind.SubConceptOf && item.Subject == concept.Term)
                    .Select(item => item.RelatedTerm!).Distinct().ToArray(),
                axioms.Where(item => item.Kind is SemanticAxiomKind.SubConceptOf && item.RelatedTerm == concept.Term)
                    .Select(item => item.Subject).Distinct().ToArray(),
                axioms.Where(item => item.Kind is SemanticAxiomKind.Equivalent &&
                        (item.Subject == concept.Term || item.RelatedTerm == concept.Term))
                    .Select(item => item.Subject == concept.Term ? item.RelatedTerm! : item.Subject)
                    .Distinct().ToArray()))
            .OrderBy(item => item.Definition.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return ValueTask.FromResult(new SemanticResolvedCatalog(vocabularyCatalog, concepts));
    }

    /// <inheritdoc />
    public ValueTask<SemanticPropertyStorage> GetPropertyStorageAsync(SemanticTerm property,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(property);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_state.Current.PropertyStorage.Resolve(property));
    }

    /// <inheritdoc/>
    public ValueTask<SemanticPropertyOptimizationReport> AnalyzePropertyStorageAsync(
        SemanticPropertyOptimizationPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        PropertyStorageCatalogState catalog = _state.Current.PropertyStorage;
        SemanticPropertyOptimizationReport report = _storageAdvisor.Analyze(catalog.Resolve, policy);
        _telemetry.RecordPropertyOptimizationObservationStatus(
            report.ObservedProperties, report.ObservationCapacityReached);
        return ValueTask.FromResult(report);
    }

    /// <inheritdoc/>
    public ValueTask ResetPropertyStorageObservationsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _storageAdvisor.Reset();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask<SemanticPropertyOptimizationResult> ApplyPropertyStorageOptimizationAsync(
        SemanticPropertyOptimizationPolicy? policy = null, int maximumChanges = 1,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (maximumChanges is <= 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChanges));
        }

        SemanticPropertyOptimizationReport analysis = await AnalyzePropertyStorageAsync(policy, cancellationToken)
            .ConfigureAwait(false);
        var applied = new List<SemanticPropertyStorage>(Math.Min(maximumChanges, analysis.Recommendations.Count));
        foreach (SemanticPropertyOptimizationRecommendation recommendation in analysis.Recommendations
                     .OrderByDescending(item => item.Confidence).Take(maximumChanges))
        {
            SemanticPropertyStorage? placement = await _catalogMutations.ApplyAutomaticPropertyStorageAsync(
                recommendation, cancellationToken).ConfigureAwait(false);
            if (placement is not null && placement.StorageKind == recommendation.RecommendedStorageKind)
            {
                applied.Add(placement);
            }
        }
        if (applied.Count > 0)
        {
            _storageAdvisor.Reset();
        }

        return new(analysis, applied);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticConformanceReport> ValidateConformanceAsync(
        SemanticSessionEnvelope session,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Validate(session);
        return ValueTask.FromResult(EvaluateConformance(_state.PreparationSnapshot.Catalog, session));
    }

    /// <inheritdoc/>
    public ValueTask<SemanticVocabulary> PublishVocabularyAsync(
        SemanticVocabulary vocabulary,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _catalogMutations.PublishVocabularyAsync(vocabulary, cancellationToken);
    }

    internal ValueTask<SemanticFieldQueryResult> QueryAsync(
        EntityForgeWriteTransaction transaction,
        SemanticFieldEqualityQuery query,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _transactionQueries.QueryAsync(transaction, query, cancellationToken);
    }

    internal ValueTask<SemanticFieldQueryResult> QueryRangeAsync(
        EntityForgeWriteTransaction transaction,
        SemanticStringRangeQuery query,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _transactionQueries.QueryRangeAsync(transaction, query, cancellationToken);
    }

    internal ValueTask<IReadOnlyList<SemanticEntityMembershipEvidence>> QueryConceptAsync(
        EntityForgeWriteTransaction transaction,
        SemanticConceptMembershipQuery query,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _transactionQueries.QueryConceptAsync(transaction, query, cancellationToken);
    }

    internal ValueTask EnforceDistributedGovernanceAsync(EntityForgeWriteTransaction retained,
        SemanticSessionEnvelope original, IReadOnlyCollection<SemanticTerm> affectedConcepts, CancellationToken token) =>
        _transactionQueries.EnforceDistributedGovernanceAsync(retained, original, affectedConcepts, token);

    internal ValueTask<SemanticGovernanceDecision> EvaluateGovernanceAsync(
        EntityForgeWriteTransaction transaction,
        Uri ruleIdentifier,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _transactionQueries.EvaluateGovernanceAsync(transaction, ruleIdentifier, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticPropertyStorage> ConfigurePropertyStorageAsync(
        SemanticTerm property,
        SemanticPropertyStorageKind storageKind,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _catalogMutations.ConfigurePropertyStorageAsync(property, storageKind, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticPropertyStorage> EnableAutomaticPropertyStorageAsync(
        SemanticTerm property, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _catalogMutations.EnableAutomaticPropertyStorageAsync(property, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticGovernanceRule> PublishGovernanceRuleAsync(
        SemanticGovernanceRuleDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _catalogMutations.PublishGovernanceRuleAsync(definition, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SemanticGovernanceRule?> GetGovernanceRuleAsync(
        Uri identifier,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.GetGovernanceRuleAsync(identifier, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SnapshotLease> AcquireSnapshotLeaseAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _snapshotLeases.AcquireAsync(duration, cancellationToken);
    }

    internal async ValueTask<ISemanticEntityReadScope> AcquireMappedReadAsync(
        Uri sourceSystem, Uri sourcePartition, TimeSpan duration, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        DateTimeOffset expires = _timeProvider.GetUtcNow().Add(duration);
        SemanticDistributedState retained = await AcquireCurrentDistributedStateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new SemanticMappedReadScope(retained, retained.Progress(sourceSystem, sourcePartition),
                _timeProvider, expires, ThrowIfDisposed);
        }
        catch
        {
            retained.Dispose();
            throw;
        }
    }

    // Current-version selection and retention must share one publication epoch.
    // Reading CurrentVersion before an await can lose that version to an unrelated commit.
    internal ValueTask<SemanticDistributedState> AcquireCurrentDistributedStateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var admission = _log as ISemanticTransientSnapshotAdmission
            ?? throw new NotSupportedException("Transient Semantic reads are unavailable.");
        return admission.CaptureCurrentStateAsync((version, retention) =>
        {
            var state = _state.CapturePreparationState(version);
            ISemanticHistoricalAccessPath physical = _physicalCandidates.CaptureCurrent(version);
            try
            {
                return new SemanticDistributedState(this, state, physical, retention);
            }
            catch (Exception failure)
            {
                try
                {
                    physical.Dispose();
                }
                catch (Exception cleanup)
                {
                    throw new SemanticRetainedReadCleanupException([failure, cleanup]);
                }
                throw;
            }
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SnapshotLease> GetSnapshotLeaseAsync(
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _snapshotLeases.GetAsync(leaseId, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<SnapshotLease> RenewSnapshotLeaseAsync(
        Guid leaseId,
        long expectedRevision,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _snapshotLeases.RenewAsync(leaseId, expectedRevision, duration, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask ReleaseSnapshotLeaseAsync(
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _snapshotLeases.ReleaseAsync(leaseId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<DatabaseVersion> GetSnapshotRetentionFloorAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.GetRetentionFloorAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(
        SemanticRelationshipTraversalQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.TraverseAsync(query, snapshot, cancellationToken);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseBatchAsync(
        SemanticRelationshipTraversalBatchQuery query,
        DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _queries.TraverseBatchAsync(query, snapshot, cancellationToken);
    }

    /// <summary>Creates a durable checkpoint and returns its semantic transaction version.</summary>
    public async ValueTask<DatabaseVersion> CreateCheckpointAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await _checkpointManager.CreateAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<DatabaseVersion> RetainOperationsAsync(DateTimeOffset retainedSince, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return (_log as IDatabaseOperationRetention ?? throw new NotSupportedException("Local retention is unavailable."))
            .RetainOperationsAsync(retainedSince, cancellationToken);
    }

    /// <summary>
    /// Rewrites semantic access paths into low reusable pages and returns the maintenance version.
    /// Readers retain their immutable snapshots while new write publications wait at the commit frontier.
    /// </summary>
    public async ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        DatabaseMaintenanceResult<DatabaseVersion> result = await _maintenance.ExecuteAsync(
            "semantic-reindex", ReindexCoreAsync, cancellationToken).ConfigureAwait(false);
        return result.PublishedVersion;
    }

    /// <inheritdoc/>
    public async ValueTask<SemanticReprojectionResult> ReprojectAsync(
        SemanticReprojectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is SemanticReprojectionKind.ReplaceablePhysicalView)
        {
            if (request.MaterializedChanges is not null)
            {
                throw new ArgumentException("A physical reprojection cannot contain logical changes.", nameof(request));
            }

            DatabaseVersion version = await ReindexAsync(cancellationToken).ConfigureAwait(false);
            return new(request.ProjectionId, request.DefinitionVersion, request.Kind, version, false);
        }

        if (request.MaterializedChanges is null)
        {
            throw new ArgumentException(
                "A materialized logical reprojection requires a provenance-bearing semantic change session.",
                nameof(request));
        }

        SessionCommitResult commit = await ApplyAsync(request.MaterializedChanges, cancellationToken)
            .ConfigureAwait(false);
        return new(request.ProjectionId, request.DefinitionVersion, request.Kind, commit.Version, true);
    }

    /// <summary>Gets the latest explicit semantic maintenance lifecycle state.</summary>
    public DatabaseMaintenanceStatus MaintenanceStatus => _maintenance.Status;

    private async ValueTask<(DatabaseVersion Version, int Attempts)> ReindexCoreAsync(Guid _,
        DatabaseMaintenanceProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            progress.Report(DatabaseMaintenancePhase.Building, 1);
            progress.Report(DatabaseMaintenancePhase.CuttingOver, 1);
            return (await _log.ReindexAsync(cancellationToken).ConfigureAwait(false), 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EntityForgeDbStorageException("The database reindex could not complete.", exception);
        }
    }

    /// <summary>Stops publication and disposes the owned commit log and telemetry resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] orderedCommitTasks;
        lock (_orderedCommitGate)
        {
            _disposed = true;
            _orderedCommitShutdown.Cancel();
            orderedCommitTasks = _orderedCommitTasks.ToArray();
        }
        // Binding publication owns accepted requests until drainage; native execution may
        // subsequently reject disposal, leaving the durable selected plan available on reopen.
        await _bindingCommits.DisposeAsync().ConfigureAwait(false);
        if (orderedCommitTasks.Length != 0)
        {
            try
            {
                await Task.WhenAll(orderedCommitTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        lock (_orderedCommitGate)
        {
            while (_orderedCommits.TryDequeue(out PendingOrderedCommit? pending))
            {
                pending.Completion.TrySetCanceled(_orderedCommitShutdown.Token);
            }
        }
        try
        {
            await _log.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new EntityForgeDbStorageException("The semantic commit log could not be closed.", exception);
        }
        finally
        {
            await _maintenance.DisposeAsync().ConfigureAwait(false);
            _physicalCandidates.Dispose();
            await _commitGate.DisposeAsync().ConfigureAwait(false);
            _orderedCommitShutdown.Dispose();
            if (_ownsTelemetry)
            {
                _telemetry.Dispose();
            }
        }
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration,
                "A resumable-write lease duration must be positive.");
        }
    }

    private void Validate(SemanticSessionEnvelope session)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(session);
            if (session.Pages.Count != session.Close.PageCount ||
                session.Pages.Sum(page => page.Changes.Count) != session.Close.ChangeCount)
            {
                throw new InvalidDataException("Semantic session counts do not match.");
            }

            if (session.Start.SessionId != session.Close.SessionId ||
                session.Pages.Any(page => page.SessionId != session.Start.SessionId))
            {
                throw new InvalidDataException("Semantic session identifiers do not match.");
            }

            var hashes = session.Close.OperationHash is null
                ? (ContentHash: _codec.ComputeContentHash(session.Pages), OperationHash: (string?)null)
                : _codec.ComputeHashes(session.Start, session.Pages);
            if (hashes.ContentHash != session.Close.ContentHash)
            {
                throw new InvalidDataException("Semantic session hash does not match.");
            }

            if (session.Close.OperationHash is not null &&
                hashes.OperationHash != session.Close.OperationHash)
            {
                throw new InvalidDataException("Semantic session operation hash does not match.");
            }

            Guid[] changeIds = session.Pages.SelectMany(page => page.Changes)
                .Select(change => change.ChangeId).ToArray();
            if (changeIds.Distinct().Count() != changeIds.Length)
            {
                throw new InvalidDataException("A semantic session cannot contain duplicate change identifiers.");
            }

            foreach (JsonSemanticValue json in session.Pages.SelectMany(page => page.Changes)
                         .SelectMany(change => change.Fields).Select(field => field.Value).OfType<JsonSemanticValue>())
            {
                _jsonAdmissionPolicy.Validate(json.CanonicalValue);
            }
        }
        catch (SemanticSessionValidationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new SemanticSessionValidationException("The semantic session is invalid.", exception);
        }
    }

    private static void ValidateCatalog(SemanticCatalogRegistry catalog, SemanticSessionEnvelope session)
    {
        try
        {
            catalog.Validate(session);
        }
        catch (Exception exception) when (exception is not SemanticSessionValidationException)
        {
            throw new SemanticSessionValidationException(
                "The semantic session conflicts with the registered semantic catalog.", exception);
        }
    }

    private SemanticConformanceReport EvaluateConformance(
        SemanticCatalogRegistry catalog,
        SemanticSessionEnvelope session)
    {
        if (_conformanceMode is SemanticConformanceMode.Open || catalog.Vocabularies.Count == 0)
        {
            return new(_conformanceMode, []);
        }

        SemanticValidationResult result = new SemanticVocabularyConformanceValidator().Validate(
            session.Pages.OrderBy(page => page.PageNumber).SelectMany(page => page.Changes),
            catalog.Vocabularies);
        return new(_conformanceMode, result.Issues);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static EntityForgeConflictKind MapConflictKind(TransactionConflictKind kind) => kind switch
    {
        TransactionConflictKind.OwnershipWrite => EntityForgeConflictKind.OwnershipWrite,
        TransactionConflictKind.ReadAfterWrite => EntityForgeConflictKind.ReadAfterWrite,
        TransactionConflictKind.WriteAfterRead => EntityForgeConflictKind.WriteAfterRead,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported transaction conflict kind.")
    };

}

internal sealed record DatabasePreparationSnapshot(
    SemanticDatabaseState State,
    SemanticCatalogRegistry Catalog);

/// <summary>Identifies the optimistic dependency that caused a semantic write conflict.</summary>
public enum EntityForgeConflictKind
{
    /// <summary>Two writes overlap the same source-owned contribution.</summary>
    OwnershipWrite,
    /// <summary>A transaction read overlaps a later committed write.</summary>
    ReadAfterWrite,
    /// <summary>A transaction write overlaps a later committed read dependency.</summary>
    WriteAfterRead
}
