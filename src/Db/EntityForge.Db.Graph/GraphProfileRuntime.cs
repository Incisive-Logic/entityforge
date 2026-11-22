using System.Collections.Immutable;
using EntityForge.Db.Mapping;
using System.Diagnostics;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphProfileRuntime : IEntityOperationBindingAdmission, IEntityOperationCompactionProfile, IDatabaseProfileDeferredCheckpoint, IDatabaseProfileSnapshotPreservingCheckpoint, IDatabaseProfileRetainedPhysicalPreparation, IDatabaseProfileChangePlanner, IDatabaseProfileValidatedChangePlanner<DatabaseProfileChangePlan>,
    IDatabaseProfileCheckpointPageReuse, IDatabaseProfileDurabilityBarrier, IDatabaseProfilePhysicalMutationPlanner, IDatabaseProfileCheckpointPublicationObserver,
    IDatabaseProfilePublicationRuntime, IDatabaseProfileOrderedPreparationRuntime, IEntityOperationAtomicCommitProfile
{
    void IEntityOperationAtomicCommitProfile.ValidateBoundPayload(EntityRecordDeliveryCodec.Metadata metadata,
        ReadOnlyMemory<byte> nativePayload)
    {
        EntityRecordDeliveryCodec.Validate(metadata, Profile, CodecVersion);
        if (metadata.NativeProfile is null)
        {
            Mappings.RequireCurrent(metadata.Binding);
        }
        ValidateAdmission(GraphTransactionCodec.Decode(metadata.OperationId, nativePayload.Span));
    }

    EntityOperationCommitted IEntityOperationAtomicCommitProfile.DescribeBoundCommitted(EntityOperationBoundCommit request, DatabaseCommitReceipt receipt) =>
        request.Describe(CodecId, CodecVersion, receipt) with
        {
            ProfileReceipt = GraphTransactionCodec.Decode(receipt.TransactionId, request.Payload.Span).QueryResult.ToArray()
        };

    EntityOperationCatalog IEntityOperationCompactionProfile.OperationCatalog => Operations;

    EntityOperationBinding? IEntityOperationCompactionProfile.ReadOperationTransition(ReadOnlyMemory<byte> payload) =>
        EntityOperationCodec.IsCommand(payload.Span) ? EntityOperationCodec.Decode(payload.Span) : null;

    EntityOperationCommitted IEntityOperationCompactionProfile.DescribeCommitted(EntityOperationBinding binding, DatabaseCommitReceipt receipt)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode(binding.Plan, Profile, CodecVersion);
        if (metadata.OperationId != binding.OperationId || metadata.DatabaseIncarnation != receipt.DatabaseIncarnation)
        {
            throw new EntityForgeDbIdempotencyException(binding.OperationId);
        }

        return EntityOperationCommitted.Create(binding.Plan, EntityRecordDeliveryCodec.PreparedIdentity(metadata, payload),
            CodecId, CodecVersion, payload, receipt,
            GraphTransactionCodec.Decode(binding.OperationId, payload).QueryResult.ToArray());
    }

    public Guid? BindingIdentity(ReadOnlyMemory<byte> payload) =>
        EntityOperationCodec.IsCommand(payload.Span) ? EntityOperationCodec.Decode(payload.Span).OperationId : null;

    public Guid? OperationIdentity(Guid transactionId, ReadOnlyMemory<byte> payload) =>
        BindingIdentity(payload) ?? (IsMetadata(payload) ? null : transactionId);

    internal EntityOperationCatalog Operations { get; } = new();

    private static bool IsMetadata(ReadOnlyMemory<byte> payload) => IsMetadataSpan(payload.Span);

    private static bool IsMetadataSpan(ReadOnlySpan<byte> payload) =>
        EntityMappingCodec.IsProfileCommandSpan(payload) || EntityOperationCodec.IsCommand(payload);

    // Only the current unleased view is retained. Explicit snapshot leases are the authority
    // for retaining historical generations; keeping a full predecessor made every commit hold
    // three complete maps while constructing the next generation.
    internal const int MaximumRetainedVersions = 1;
    internal const string ProfileCodecId = "entityforge.graph.transactions";
    internal const int ProfileCodecVersion = GraphTransactionCodec.Version;
    internal const string ProfileCheckpointCodecId = "entityforge.graph.checkpoint";
    internal const int ProfileCheckpointCodecVersion = 22;

    internal EntityMappingCatalog Mappings { get; } = new(DatabaseProfileId.PropertyGraph);

    private readonly object _stateGate = new();
    private readonly Guid _snapshotCursorScope = Guid.NewGuid();
    private ImmutableDictionary<GraphVertexId, GraphVertex> _vertices =
        ImmutableDictionary<GraphVertexId, GraphVertex>.Empty;

    private ImmutableDictionary<GraphEdgeId, GraphEdge> _edges =
        ImmutableDictionary<GraphEdgeId, GraphEdge>.Empty;

    private readonly SortedDictionary<long, GraphReadView> _history = [];

    internal Func<bool> IsDistributed { get; set; } = static () => false;

    private readonly GraphConflictTracker _lastWrites = new();
    private readonly Dictionary<long, int> _snapshotLeases = [];
    private GraphReadView _currentView = GraphReadView.Empty;
    private readonly JsonValueAdmissionPolicy _jsonAdmissionPolicy;
    private readonly GraphTransactionPlanner _planner;
    private readonly long _maximumCheckpointBytes;
    private readonly int _maximumCheckpointEntries;
    private GraphCommittedEffects _lastCommittedEffects = new(Array.Empty<GraphCommittedEffect>());
    private readonly Dictionary<Guid, GraphTransactionPlan> _preparedPlans = [];
    private readonly Dictionary<Guid, GraphTransaction> _preparedTransactions = [];
    private readonly GraphPhysicalAccessPath _physicalAccessPath;
    private readonly Dictionary<Guid, PendingGraphState> _pendingStates = [];
    private readonly HashSet<Guid> _orderedPreparations = [];
    private readonly GraphConflictTracker _orderedWrites = new();
    private readonly HashSet<Guid> _persistedPhysicalTransactions = [];
    private readonly HashSet<Guid> _physicallyStaged = [];
    private GraphQueryStatistics _queryStatistics = GraphQueryStatistics.Empty;
    private readonly Dictionary<Guid, GraphReplacementCommit> _replacementCommits = [];
    private readonly HashSet<Guid> _bulkReplacementCommits = [];
    private long _conflictRetentionFloor;

    public GraphProfileRuntime(JsonValueAdmissionPolicy jsonAdmissionPolicy,
        GraphPhysicalAccessPath physicalAccessPath, long maximumCheckpointBytes = GraphCheckpointCodec.MaximumRawByteLength,
        int maximumCheckpointEntries = GraphCheckpointCodec.MaximumEntryCount)
    {
        _jsonAdmissionPolicy = jsonAdmissionPolicy ?? throw new ArgumentNullException(nameof(jsonAdmissionPolicy));
        _physicalAccessPath = physicalAccessPath ?? throw new ArgumentNullException(nameof(physicalAccessPath));
        if (maximumCheckpointEntries < 0 || maximumCheckpointEntries > GraphCheckpointCodec.MaximumEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCheckpointEntries));
        }

        _maximumCheckpointEntries = maximumCheckpointEntries;
        _planner = new(_jsonAdmissionPolicy, maximumCheckpointEntries);
        if (maximumCheckpointBytes <= 0 || maximumCheckpointBytes > GraphCheckpointCodec.MaximumRawByteLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCheckpointBytes));
        }

        _maximumCheckpointBytes = maximumCheckpointBytes;
        _history[DatabaseVersion.Initial.Value] = GraphReadView.Empty;
    }

    private GraphTransaction DecodeTransaction(Guid transactionId, ReadOnlySpan<byte> payload) =>
        _logicalIntents.TryGet(transactionId, payload, out var retained) ? retained :
        IsMetadataSpan(payload) ? new(transactionId, []) :
            DecodeMeasured(transactionId, payload);

    private static GraphTransaction DecodeMeasured(Guid id, ReadOnlySpan<byte> payload)
    {
        long started = DatabaseProfileWorkTelemetry.Start();
        var transaction = GraphTransactionCodec.Decode(id, payload);
        DatabaseProfileWorkTelemetry.Record("graph", "decode", "mutation", transaction.Mutations.Count, started);
        return transaction;
    }

    public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

    public string CodecId => ProfileCodecId;

    public int CodecVersion => ProfileCodecVersion;

    public string CheckpointCodecId => ProfileCheckpointCodecId;

    public int CheckpointCodecVersion => ProfileCheckpointCodecVersion;

    public GraphCommittedEffects LastCommittedEffects
    {
        get
        {
            lock (_stateGate)
            {
                return _lastCommittedEffects;
            }
        }
    }

    public DatabaseVersion OldestReadableVersion
    {
        get
        {
            lock (_stateGate)
            {
                return new(_history.Keys.FirstOrDefault());
            }
        }
    }

    internal DatabaseVersion CurrentVersion
    {
        get
        {
            lock (_stateGate)
            {
                return new(_history.Keys.LastOrDefault());
            }
        }
    }

    internal GraphQueryStatistics QueryStatistics
    {
        get
        {
            lock (_stateGate)
            {
                return _queryStatistics;
            }
        }
    }

    public GraphSnapshotLease AcquireSnapshot(DatabaseVersion? requestedVersion = null)
    {
        lock (_stateGate)
        {
            if (_maintenanceAdmission)
            {
                throw new GraphSnapshotLeaseException("Graph physical maintenance is in progress; retry snapshot acquisition after it finishes.");
            }
            DatabaseVersion version = requestedVersion ?? new(_history.Keys.Last());
            _ = View(version);
            _snapshotLeases[version.Value] = _snapshotLeases.GetValueOrDefault(version.Value) + 1;
            return new(this, version, () => ReleaseSnapshot(version));
        }
    }

    internal void ValidateAdmission(GraphTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        foreach (DatabaseValue.Json json in transaction.Mutations.Select(mutation => mutation switch
                 {
                     SetVertexProperty set => set.Value,
                     SetEdgeProperty set => set.Value,
                     _ => null
                 }).OfType<DatabaseValue.Json>())
        {
            _jsonAdmissionPolicy.Validate(json.Value);
        }
    }

    public ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(CancellationToken cancellationToken = default)
        => CaptureCheckpointCore(false, cancellationToken);

    public ValueTask<ReadOnlyMemory<byte>> CaptureSnapshotPreservingCheckpointAsync(CancellationToken cancellationToken = default)
        => CaptureCheckpointCore(true, cancellationToken);

    private ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointCore(bool preserveSnapshots, CancellationToken cancellationToken)
        => ValueTask.FromResult(CaptureCheckpointEncoder(preserveSnapshots, cancellationToken)(cancellationToken));

    public ValueTask<Func<CancellationToken, ReadOnlyMemory<byte>>> CaptureCheckpointEncoderAsync(
        bool preserveSnapshots, IReadOnlySet<Guid>? expired, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(CaptureCheckpointEncoder(preserveSnapshots, cancellationToken));

    public Func<CancellationToken, ReadOnlyMemory<byte>> CaptureCheckpointEncoder(bool preserveSnapshots,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            if (!preserveSnapshots && _snapshotLeases.Count != 0)
            {
                throw new GraphSnapshotLeaseException(
                    "A checkpoint cannot advance the Graph retention floor while snapshots are leased.");
            }

            var view = _currentView;
            var statistics = _queryStatistics;
            byte[] mappings = Mappings.Capture(), operations = Operations.Capture();
            return token =>
            {
                token.ThrowIfCancellationRequested();
                var content = EntityOperationCodec.WrapCheckpoint(EntityMappingCodec.WrapCheckpoint(
                    GraphCheckpointCodec.Encode(view, statistics, token), mappings), operations);
                token.ThrowIfCancellationRequested();
                return content;
            };
        }
    }

    public ValueTask CheckpointPublishedAsync(DatabaseVersion version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            if (version.Value > CurrentVersion.Value || version.Value < _conflictRetentionFloor)
            {
                throw new InvalidOperationException("The published Graph checkpoint is outside the retained version interval.");
            }

            long floor = Math.Min(version.Value, _snapshotLeases.Keys.DefaultIfEmpty(version.Value).Min());
            _conflictRetentionFloor = floor;
            _lastWrites.RetainAfter(floor);
            foreach (long old in _history.Keys.Where(v => v < floor).ToArray())
            {
                _history.Remove(old);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        // Restore into private catalogs until the entire checkpoint is validated.
        // Cancellation/corruption must not replace live metadata with a partial restore.
        var restoredOperations = new EntityOperationCatalog();
        var restoredMappings = new EntityMappingCatalog(Profile);
        GraphCheckpoint checkpoint = GraphCheckpointCodec.Decode(
            EntityMappingCodec.UnwrapCheckpoint(EntityOperationCodec.UnwrapCheckpoint(payload, restoredOperations), restoredMappings), ValidateProperty, cancellationToken);
        GraphOpenTelemetry.Phase("checkpoint_decode", started);
        started = Stopwatch.GetTimestamp();
        ImmutableDictionary<GraphVertexId, GraphVertex> vertices = checkpoint.Vertices.ToImmutableDictionary();
        ImmutableDictionary<GraphEdgeId, GraphEdge> edges = checkpoint.Edges.ToImmutableDictionary();
        // Seed the immutable-map sizes during recovery, not on the first ordinary save.
        GraphCheckpointSize.Require(checked(GraphCheckpointSize.Graph(vertices, edges) +
            GraphCheckpointSize.Statistics(checkpoint.QueryStatistics)), _maximumCheckpointBytes);
        GraphOpenTelemetry.Phase("checkpoint_dictionary_copy", started);
        started = Stopwatch.GetTimestamp();
        var restoredView = GraphReadView.Build(vertices, edges);
        var restoredMappingBytes = restoredMappings.Capture();
        var restoredOperationBytes = restoredOperations.Capture();
        cancellationToken.ThrowIfCancellationRequested();
        if (checkpoint.QueryStatistics.Version != version)
        {
            throw new InvalidDataException("Graph checkpoint statistics do not match its database version.");
        }

        lock (_stateGate)
        {
            Mappings.Restore(restoredMappingBytes);
            Operations.Restore(restoredOperationBytes);
            _vertices = vertices;
            _edges = edges;
            _lastWrites.Clear();
            _conflictRetentionFloor = version.Value;
            _history.Clear();
            _history[version.Value] = restoredView;
            _currentView = _history[version.Value];
            _queryStatistics = checkpoint.QueryStatistics;
            TrimHistory();
        }
        GraphOpenTelemetry.Phase("checkpoint_publication", started);
        return ValueTask.CompletedTask;
    }

    public ValueTask ValidateAsync(Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) => ValidateCore(transactionId, payload, false, cancellationToken);

    public async ValueTask<DatabaseProfileChangePlan> ValidateAndPlanCommittedChangesAsync(
        DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await ValidateCore(transactionId, payload, true, cancellationToken).ConfigureAwait(false);
        return await PlanCommittedChangesAsync(version, transactionId, payload, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask ValidateCore(Guid transactionId, ReadOnlyMemory<byte> payload,
        bool retainCandidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Standalone validation and kernel admission both verify the original binding.
        var binding = Operations.Get(transactionId, operationIdentity: !IsMetadata(payload));
        if (binding?.Aborted == true)
        {
            throw new EntityOperationAbortedException(transactionId);
        }

        if (binding?.Committed is not null)
        {
            throw new EntityForgeDbIdempotencyException(transactionId);
        }

        if (binding is not null)
        {
            var (metadata, retainedPayload) = EntityRecordDeliveryCodec.Decode(binding.Plan, Profile, CodecVersion);
            if (!retainedPayload.AsSpan().SequenceEqual(payload.Span))
            {
                throw new EntityForgeDbIdempotencyException(transactionId);
            }

            if (metadata.NativeProfile is null)
            {
                Mappings.RequireCurrent(metadata.Binding);
            }
        }

        if (IsMetadata(payload))
        {
            if (EntityOperationCodec.IsCommand(payload.Span))
            {
                Operations.Validate(EntityOperationCodec.Decode(payload.Span));
            }
            else
            {
                Mappings.Validate(EntityMappingCodec.DecodeProfileCommand(payload));
            }

            return ValueTask.CompletedTask;
        }
        lock (_stateGate)
        {
            if (_replacementCommits.ContainsKey(transactionId) &&
                !_bulkReplacementCommits.Contains(transactionId))
            {
                return ValueTask.CompletedTask;
            }

            if (_bulkReplacementCommits.Contains(transactionId) && _preparedPlans.ContainsKey(transactionId))
            {
                if (_vertices.Count != 0 || _edges.Count != 0)
                {
                    throw new InvalidOperationException("An initial Graph bulk load requires an empty database.");
                }

                return ValueTask.CompletedTask;
            }
            GraphTransaction transaction = _preparedTransactions.Remove(transactionId,
                out GraphTransaction? preparedTransaction)
                ? preparedTransaction
                : DecodeTransaction(transactionId, payload.Span);
            if (transaction.IsInitialBulkLoad && (_vertices.Count != 0 || _edges.Count != 0))
            {
                throw new InvalidOperationException("An initial Graph bulk load requires an empty database.");
            }

            ValidateConflicts(transaction);
            PendingGraphState? prior = _pendingStates.Values.MaxBy(item => item.Version.Value);
            var plan = WithCheckpointCapacity(PlanLogical(transaction, payload.Span, prior?.Vertices ?? _vertices,
                prior?.Edges ?? _edges), prior?.Statistics ?? _queryStatistics);
            if (retainCandidate)
            {
                _preparedPlans[transactionId] = plan;
                _preparedTransactions[transactionId] = transaction;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<DatabaseProfileChangePlan> PlanCommittedChangesAsync(DatabaseVersion version,
        Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMetadata(payload))
        {
            return ValueTask.FromResult(DatabaseProfileChangePlan.System());
        }

        lock (_stateGate)
        {
            if (_replacementCommits.ContainsKey(transactionId) &&
                !_bulkReplacementCommits.Contains(transactionId))
            {
                return ValueTask.FromResult(DatabaseProfileChangePlan.System());
            }

            GraphTransactionPlan plan;
            if (!_preparedPlans.TryGetValue(transactionId, out plan!))
            {
                GraphTransaction transaction = DecodeTransaction(transactionId, payload.Span);
                PendingGraphState? prior = _pendingStates.Values.MaxBy(item => item.Version.Value);
                plan = WithCheckpointCapacity(PlanLogical(transaction, payload.Span, prior?.Vertices ?? _vertices,
                    prior?.Edges ?? _edges), prior?.Statistics ?? _queryStatistics);
                _preparedPlans[transactionId] = plan;
            }
            if (_bulkReplacementCommits.Contains(transactionId))
            {
                var routing = new CommittedChangeRoutingSummary(overflowedKinds:
                [
                    GraphChangeRoutingKinds.Vertex,
                    GraphChangeRoutingKinds.Edge,
                    GraphChangeRoutingKinds.Label,
                    GraphChangeRoutingKinds.Property,
                    GraphChangeRoutingKinds.EffectKind
                ]);
                return ValueTask.FromResult(DatabaseProfileChangePlan.UserData(new(
                    GraphCommittedChangeCodecs.DatasetReset,
                    GraphCommittedChangeCodecs.DatasetResetVersion, ReadOnlyMemory<byte>.Empty,
                    plan.CommittedEffects.Effects.Count, routing, CommittedChangeKind.DatasetReset)));
            }
            if (plan.CommittedEffects.Effects.Count == 0)
            {
                return ValueTask.FromResult(DatabaseProfileChangePlan.UserData(null));
            }

            byte[] content = GraphCommittedEffectsCodec.Encode(plan.CommittedEffects);
            var changes = CommittedChangePayload.BorrowOwnedContent(GraphCommittedEffectsCodec.CodecId,
                GraphCommittedEffectsCodec.CodecVersion, content,
                plan.CommittedEffects.Effects.Count,
                CommittedChangeRoutingSummary.CreateBounded(
                    GraphChangeRoutingSummaryBuilder.Build(plan.CommittedEffects)));
            return ValueTask.FromResult(DatabaseProfileChangePlan.UserData(changes));
        }
    }

    public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_orderedPreparations.Contains(transactionId))
            {
                if (EntityOperationCodec.IsCommand(payload.Span))
                {
                    Operations.Apply(EntityOperationCodec.Decode(payload.Span));
                }

                return ValueTask.CompletedTask;
            }
        }

        if (EntityOperationCodec.IsCommand(payload.Span))
        {
            Operations.Apply(EntityOperationCodec.Decode(payload.Span));
        }
        else if (EntityMappingCodec.IsProfileCommand(payload))
        {
            Mappings.Apply(EntityMappingCodec.DecodeProfileCommand(payload));
        }

        lock (_stateGate)
        {
            PendingGraphState? priorPending = _pendingStates.Count == 0 ? null :
                _pendingStates.Values.MaxBy(item => item.Version.Value);
            IReadOnlyDictionary<GraphEdgeId, GraphEdge> priorEdges = priorPending?.Edges ?? _edges;
            if (_replacementCommits.ContainsKey(transactionId) &&
                !_bulkReplacementCommits.Contains(transactionId))
            {
                ImmutableDictionary<GraphVertexId, GraphVertex> vertices = priorPending?.Vertices ?? _vertices;
                ImmutableDictionary<GraphEdgeId, GraphEdge> edges = priorPending?.Edges ?? _edges;
                _pendingStates[transactionId] = new(version, vertices, edges,
                    priorPending?.View ?? _currentView, new([]),
                    (priorPending?.Statistics ?? _queryStatistics) with
                    {
                        Version = version
                    }, []);
                return ValueTask.CompletedTask;
            }
            GraphTransaction transaction = _preparedTransactions.Remove(transactionId,
                out GraphTransaction? preparedTransaction)
                ? preparedTransaction
                : DecodeTransaction(transactionId, payload.Span);
            GraphTransactionPlan plan = _preparedPlans.Remove(transactionId, out GraphTransactionPlan? prepared)
                ? prepared : PlanLogical(transaction, payload.Span, priorPending?.Vertices ?? _vertices, priorEdges);
            GraphReadView view = GraphReadView.Build(plan.Vertices, plan.Edges);
            if (!_bulkReplacementCommits.Contains(transactionId))
            {
                view.CarryUnchangedIndexes(priorPending?.View ?? _currentView, plan.CommittedEffects.Effects);
            }

            GraphConflictKey[] conflicts = ConflictKeys(transaction.Mutations, priorEdges).ToArray();
            GraphQueryStatistics statistics = plan.Statistics is { } plannedStatistics
                ? plannedStatistics with
                {
                    Version = version
                }
                : WithCheckpointCapacity(plan, priorPending?.Statistics ?? _queryStatistics)
                    .Statistics! with
                {
                    Version = version
                };
            _pendingStates[transactionId] = new(version, plan.Vertices, plan.Edges, view,
                plan.CommittedEffects, statistics, conflicts);
        }
        return ValueTask.CompletedTask;
    }

    public bool RequiresCheckpointMaintenance => _physicalAccessPath.RequiresCheckpointMaintenance;

    public bool SupportsLocalRetentionMaintenance => true;

    public void AuthorizeCheckpointPageReuse(bool compacted) => _physicalAccessPath.AuthorizeCheckpointPageReuse();

    private bool _maintenanceAdmission;

    public IDisposable? EnterMaintenance()
    {
        lock (_stateGate)
        {
            if (_maintenanceAdmission || _snapshotLeases.Count != 0)
            {
                throw new GraphSnapshotLeaseException("Graph physical maintenance requires drained snapshots and exclusive admission.");
            }
            _maintenanceAdmission = true;
            return new MaintenanceAdmission(this);
        }
    }

    private sealed class MaintenanceAdmission(GraphProfileRuntime owner) : IDisposable
    {
        private GraphProfileRuntime? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }
            lock (current._stateGate)
            {
                current._maintenanceAdmission = false;
            }
        }
    }

    public ValueTask FlushPhysicalStateAsync(CancellationToken cancellationToken = default) =>
        _physicalAccessPath.FlushAsync(cancellationToken);

    public ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(Guid id,
        DatabaseVersion version, bool compact, CancellationToken token) =>
        _physicalAccessPath.PrepareMaintenanceAsync(id, version.Value, compact, token);

    public async ValueTask PublishMaintenanceAsync(Guid id, CancellationToken token)
    {
        await _physicalAccessPath.PersistAsync(id, token).ConfigureAwait(false);
        _physicalAccessPath.Publish(id);
    }

    public ValueTask AbandonMaintenanceAsync(Guid id)
    {
        _physicalAccessPath.Abandon(id);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteMaintenanceAsync(CancellationToken token) => _physicalAccessPath.TrimAsync(token);

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
        DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (IsMetadata(payload))
        {
            return [];
        }

        lock (_stateGate)
        {
            if (_orderedPreparations.Contains(transactionId))
            {
                return [];
            }
        }

        GraphReplacementCommit? replacement;
        lock (_stateGate)
        {
            _replacementCommits.TryGetValue(transactionId, out replacement);
        }

        if (replacement is not null)
        {
            long expected = _physicalAccessPath.GetReplacementExpectedGeneration(replacement.ReplacementId);
            try
            {
                var replacementStage = await _physicalAccessPath.PrepareReplacementAsync(transactionId,
                    replacement.ReplacementId, cancellationToken).ConfigureAwait(false);
                _physicallyStaged.Add(transactionId);
                return replacementStage.PhysicalMutations;
            }
            catch (AppendMergeReplacementStaleException)
            {
                throw new GraphReplacementStaleException(expected, _physicalAccessPath.Generation);
            }
        }
        GraphTransactionPlan plan;
        lock (_stateGate)
        {
            plan = _preparedPlans.TryGetValue(transactionId, out GraphTransactionPlan? prepared)
                ? prepared : PlanLogical(DecodeTransaction(transactionId, payload.Span), payload.Span, _vertices, _edges);
        }

        OrderedPageTreeStage? stage = _logicalEffects.TryGet(transactionId, payload.Span, out var compiled)
            ? await _physicalAccessPath.PrepareCompiledAsync(transactionId, version.Value, compiled.Indexes, cancellationToken).ConfigureAwait(false)
            : await _physicalAccessPath.PrepareAsync(transactionId, version.Value, plan.CommittedEffects, cancellationToken).ConfigureAwait(false);
        if (stage is null)
        {
            return [];
        }

        _physicallyStaged.Add(transactionId);
        return stage.PhysicalMutations;
    }

    public ValueTask PublishPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        if (!_physicallyStaged.Remove(transactionId))
        {
            return ValueTask.CompletedTask;
        }

        _persistedPhysicalTransactions.Add(transactionId);
        return _physicalAccessPath.PersistAsync(transactionId, cancellationToken);
    }

    public ValueTask PublishStagedStateAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate)
        {
            if (transactionId == Guid.Empty && _pendingStates.Count != 0)
            {
                foreach (PendingGraphState pending in _pendingStates.Values.OrderBy(item => item.Version.Value))
                {
                    Publish(pending);
                }

                _pendingStates.Clear();
            }
            else if (_pendingStates.Remove(transactionId, out PendingGraphState? pending))
            {
                Publish(pending);
            }

            _orderedPreparations.Remove(transactionId);
            if (_orderedPreparations.Count == 0)
            {
                _orderedWrites.Clear();
            }

            if (transactionId != Guid.Empty && _persistedPhysicalTransactions.Remove(transactionId))
            {
                _physicalAccessPath.Publish(transactionId);
                _replacementCommits.Remove(transactionId);
                _bulkReplacementCommits.Remove(transactionId);
            }
        }
        return ValueTask.CompletedTask;
    }

    private void Publish(PendingGraphState pending)
    {
        _vertices = pending.Vertices;
        _edges = pending.Edges;
        _currentView = pending.View;
        _history[pending.Version.Value] = pending.View;
        _lastCommittedEffects = pending.Effects;
        DatabaseProfileWorkTelemetry.Record("graph", "publish", "state-publication", 1);
        _queryStatistics = pending.Statistics;
        foreach (GraphConflictKey key in pending.ConflictKeys)
        {
            _lastWrites.Set(key, pending.Version);
        }

        TrimHistory();
    }

    public ValueTask AbandonPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _physicalAccessPath.Abandon(transactionId);
        _physicallyStaged.Remove(transactionId);
        lock (_stateGate)
        {
            _logicalHistory?.Abandon(transactionId);
            _preparedPlans.Remove(transactionId);
            _preparedTransactions.Remove(transactionId);
            if (_replacementCommits.Remove(transactionId, out GraphReplacementCommit? replacement))
            {
                _bulkReplacementCommits.Remove(transactionId);
                _physicalAccessPath.AbandonReplacement(replacement.ReplacementId);
            }
        }
        return ValueTask.CompletedTask;
    }

    internal GraphRebuildSnapshot CaptureRebuildSnapshot()
    {
        lock (_stateGate)
        {
            return new(CurrentVersion, _currentView);
        }
    }

    internal GraphLogicalSnapshotPage ReadLogicalSnapshotPage(DatabaseVersion version,
        GraphSnapshotCursor? cursor, int maximumCount)
    {
        lock (_stateGate)
        {
            GraphReadView view = View(version);
            (char phase, Guid after) = DecodeSnapshotCursor(cursor, version);
            var effects = new List<GraphCommittedEffect>(maximumCount);
            if (phase == 'v')
            {
                (Guid[] vertices, bool hasMoreVertices) = SelectNext(
                    view.Vertices.Keys.Select(item => item.Value), after, maximumCount);
                effects.AddRange(vertices.Select(id =>
                    (GraphCommittedEffect)new GraphVertexCreated(view.Vertices[new(id)])));
                if (hasMoreVertices)
                {
                    return new(new(effects), new(EncodeSnapshotCursor(version, 'v', vertices[^1])));
                }

                phase = 'e';
                after = Guid.Empty;
            }

            int remaining = maximumCount - effects.Count;
            (Guid[] edges, bool hasMoreEdges) = SelectNext(
                view.Edges.Keys.Select(item => item.Value), after, remaining);
            effects.AddRange(edges.Select(id =>
                (GraphCommittedEffect)new GraphEdgeCreated(view.Edges[new(id)])));
            GraphSnapshotCursor? next = hasMoreEdges
                ? new(EncodeSnapshotCursor(version, 'e', edges.Length == 0 ? after : edges[^1])) : null;
            return new(new(effects), next);
        }
    }

    private static (Guid[] Items, bool HasMore) SelectNext(
        IEnumerable<Guid> identifiers, Guid after, int maximumCount)
    {
        if (maximumCount == 0)
        {
            return (Array.Empty<Guid>(), identifiers.Any(identifier =>
            identifier.CompareTo(after) > 0));
        }

        var selected = new SortedSet<Guid>();
        foreach (Guid identifier in identifiers)
        {
            if (identifier.CompareTo(after) <= 0)
            {
                continue;
            }

            selected.Add(identifier);
            if (selected.Count > maximumCount + 1)
            {
                selected.Remove(selected.Max);
            }
        }
        bool hasMore = selected.Count > maximumCount;
        return (selected.Take(maximumCount).ToArray(), hasMore);
    }

    private (char Phase, Guid After) DecodeSnapshotCursor(GraphSnapshotCursor? cursor,
        DatabaseVersion version)
    {
        if (cursor is null)
        {
            return ('v', Guid.Empty);
        }

        string value = cursor.Value.Value;
        string[] parts = value.Split(':');
        if (parts.Length != 4 || parts[0].Length != 1 || parts[0][0] is not ('v' or 'e') ||
            !long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long cursorVersion) ||
            cursorVersion != version.Value || !Guid.TryParseExact(parts[2], "N", out Guid scope) ||
            scope != _snapshotCursorScope || !Guid.TryParseExact(parts[3], "N", out Guid after))
        {
            throw new ArgumentException("The Graph snapshot cursor is invalid.", nameof(cursor));
        }

        return (parts[0][0], after);
    }

    private string EncodeSnapshotCursor(DatabaseVersion version, char phase, Guid after) =>
        $"{phase}:{version.Value}:{_snapshotCursorScope:N}:{after:N}";

    internal ValueTask BuildReplacementAsync(Guid replacementId, GraphReadView snapshot,
        CancellationToken cancellationToken) =>
        _physicalAccessPath.BuildReplacementAsync(replacementId, snapshot, CurrentVersion.Value, cancellationToken);

    internal void RegisterReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_stateGate)
        {
            _replacementCommits.Add(transactionId, new(replacementId));
        }
    }

    internal void RegisterBulkReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_stateGate)
        {
            _replacementCommits.Add(transactionId, new(replacementId));
            _bulkReplacementCommits.Add(transactionId);
        }
    }

    internal GraphReadView PlanInitialBulkLoad(GraphTransaction transaction)
    {
        lock (_stateGate)
        {
            if (_vertices.Count != 0 || _edges.Count != 0)
            {
                throw new InvalidOperationException("An initial Graph bulk load requires an empty database.");
            }

            GraphTransactionPlan plan = WithCheckpointCapacity(PlanLogical(transaction, GraphTransactionCodec.Encode(transaction), _vertices, _edges),
                _queryStatistics);
            _preparedPlans.Add(transaction.TransactionId, plan);
            _preparedTransactions.Add(transaction.TransactionId, transaction);
            return GraphReadView.Build(plan.Vertices, plan.Edges);
        }
    }

    private GraphTransactionPlan WithCheckpointCapacity(GraphTransactionPlan plan, GraphQueryStatistics prior)
    {
        GraphQueryStatistics statistics = prior.Apply(prior.Version, plan.CommittedEffects);
        GraphCheckpointSize.Require(checked(GraphCheckpointSize.Graph(plan.Vertices, plan.Edges) +
            GraphCheckpointSize.Statistics(statistics)), _maximumCheckpointBytes);
        return plan with
        {
            Statistics = statistics
        };
    }

    internal void ValidateDistributedCheckpointCapacity(IEnumerable<GraphTransaction> reservations)
    {
        lock (_stateGate)
        {
            long graph = GraphCheckpointSize.Graph(_vertices, _edges);
            long statistics = GraphCheckpointSize.Statistics(_queryStatistics);
            long required = checked(graph + statistics);
            long vertices = _vertices.Count, edges = _edges.Count;
            var propertyCounts = new Dictionary<(bool Edge, Guid Id), long>();
            var labelCounts = new Dictionary<GraphVertexId, long>();
            void ReserveCount(long count)
            {
                if (count > _maximumCheckpointEntries)
                {
                    throw new InvalidOperationException("Prepared Graph work exceeds checkpoint collection capacity.");
                }
            }
            foreach (GraphTransaction transaction in reservations)
            {
                var plan = PlanLogical(transaction, GraphTransactionCodec.Encode(transaction), _vertices, _edges);
                var projected = _queryStatistics.Apply(_queryStatistics.Version, plan.CommittedEffects);
                // Never borrow space from an operation that may abort or apply later.
                // Statistics retain keys/values; summing positive growth over-reserves
                // shared additions, including additions that later hit tracking limits.
                required = checked(required + Math.Max(0, GraphCheckpointSize.Graph(plan.Vertices, plan.Edges) - graph) +
                    Math.Max(0, GraphCheckpointSize.Statistics(projected) - statistics));
                GraphCheckpointSize.Require(required, _maximumCheckpointBytes);
                vertices = checked(vertices + Math.Max(0, plan.Vertices.Count - _vertices.Count));
                edges = checked(edges + Math.Max(0, plan.Edges.Count - _edges.Count));
                ReserveCount(vertices);
                ReserveCount(edges);
                var changedVertices = new HashSet<GraphVertexId>();
                var changedEdges = new HashSet<GraphEdgeId>();
                foreach (var effect in plan.CommittedEffects.Effects)
                {
                    switch (effect)
                    {
                        case GraphVertexCreated e:
                            changedVertices.Add(e.Vertex.Id);
                            break;
                        case GraphVertexPropertyChanged e:
                            changedVertices.Add(e.Vertex);
                            break;
                        case GraphVertexLabelsChanged e:
                            changedVertices.Add(e.Vertex);
                            break;
                        case GraphEdgeCreated e:
                            changedEdges.Add(e.Edge.Id);
                            break;
                        case GraphEdgePropertyChanged e:
                            changedEdges.Add(e.Edge);
                            break;
                    }
                }

                foreach (var id in changedVertices)
                {
                    if (plan.Vertices.TryGetValue(id, out var next))
                    {
                        _vertices.TryGetValue(id, out var prior);
                        var key = (false, id.Value);
                        long properties = propertyCounts.GetValueOrDefault(key, prior?.Properties.Count ?? 0) +
                            Math.Max(0, next.Properties.Count - (prior?.Properties.Count ?? 0));
                        long labels = labelCounts.GetValueOrDefault(id, prior?.Labels.Count ?? 0) +
                            Math.Max(0, next.Labels.Count - (prior?.Labels.Count ?? 0));
                        ReserveCount(properties);
                        ReserveCount(labels);
                        propertyCounts[key] = properties;
                        labelCounts[id] = labels;
                    }
                }

                foreach (var id in changedEdges)
                {
                    if (plan.Edges.TryGetValue(id, out var next))
                    {
                        _edges.TryGetValue(id, out var prior);
                        var key = (true, id.Value);
                        long properties = propertyCounts.GetValueOrDefault(key, prior?.Properties.Count ?? 0) +
                            Math.Max(0, next.Properties.Count - (prior?.Properties.Count ?? 0));
                        ReserveCount(properties);
                        propertyCounts[key] = properties;
                    }
                }
            }
        }
    }

    internal bool IsEmpty
    {
        get
        {
            lock (_stateGate)
            {
                return _vertices.Count == 0 && _edges.Count == 0;
            }
        }
    }

    internal void AbandonReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_stateGate)
        {
            _replacementCommits.Remove(transactionId);
            _bulkReplacementCommits.Remove(transactionId);
            _preparedPlans.Remove(transactionId);
            _preparedTransactions.Remove(transactionId);
        }
        _physicalAccessPath.Abandon(transactionId);
        _physicalAccessPath.AbandonReplacement(replacementId);
    }


    public GraphVertex? GetVertex(GraphVertexId id)
    {
        lock (_stateGate)
        {
            return _timestampRead.Value is { } logical ? logical.Graph.GetVertex(id) : _currentView.GetVertex(id);
        }
    }

    internal IReadOnlyList<GraphVertexId> FindCandidates(GraphCandidatePredicate predicate)
    {
        lock (_stateGate)
        {
            if (_timestampRead.Value is not null)
            {
                return FindCandidates(predicate, CurrentVersion);
            }

            return predicate switch
            {
                GraphIdentityCandidatePredicate identity => identity.Identities
                    .Where(id => _currentView.Vertices.ContainsKey(id))
                    .Distinct().ToArray(),
                GraphPropertyCandidatePredicate property =>
                    _currentView.FindByProperty(property.Property, property.Value),
                GraphLabelCandidatePredicate label => _currentView.FindByLabel(label.Label),
                GraphNumericCandidatePredicate numeric => _currentView.FindNumericCandidates(numeric.Bounds) ?? _currentView.Vertices.Keys.ToArray(),
                GraphAllCandidatePredicate => _currentView.Vertices.Keys.ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(predicate))
            };
        }
    }

    internal IReadOnlyList<GraphVertexId> FindCandidates(GraphCandidatePredicate predicate, DatabaseVersion asOf)
    {
        lock (_stateGate)
        {
            GraphReadView view = View(asOf);
            return predicate switch
            {
                GraphIdentityCandidatePredicate identity => identity.Identities
                    .Where(view.Vertices.ContainsKey).Distinct().ToArray(),
                GraphPropertyCandidatePredicate property =>
                    view.FindByProperty(property.Property, property.Value),
                GraphLabelCandidatePredicate label => view.FindByLabel(label.Label),
                GraphNumericCandidatePredicate numeric => view.FindNumericCandidates(numeric.Bounds) ?? view.Vertices.Keys.ToArray(),
                GraphAllCandidatePredicate => view.Vertices.Keys.ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(predicate))
            };
        }
    }

    public GraphVertex? GetVertex(GraphVertexId id, DatabaseVersion asOf)
    {
        lock (_stateGate)
        {
            return View(asOf).GetVertex(id);
        }
    }

    public GraphEdge? GetEdge(GraphEdgeId id)
    {
        lock (_stateGate)
        {
            return (_timestampRead.Value?.Graph ?? _currentView).Edges.GetValueOrDefault(id);
        }
    }

    public GraphEdge? GetEdge(GraphEdgeId id, DatabaseVersion asOf)
    {
        lock (_stateGate)
        {
            return View(asOf).Edges.GetValueOrDefault(id);
        }
    }

    internal GraphReadView CaptureReadView(DatabaseVersion asOf)
    {
        lock (_stateGate)
        {
            return View(asOf);
        }
    }

    internal GraphQuerySnapshot CaptureQuerySnapshot(DatabaseVersion asOf)
    {
        lock (_stateGate)
        {
            GraphReadView view = View(asOf);
            GraphQueryStatistics statistics = _timestampRead.Value is null && _queryStatistics.Version == asOf
                ? _queryStatistics
                : view.StatisticsFor(asOf);
            return new(asOf, view, statistics);
        }
    }

    public IReadOnlyList<GraphEdge> Outgoing(GraphVertexId id, string? label)
        => Outgoing(id, label, null);

    public IReadOnlyList<GraphEdge> Outgoing(GraphVertexId id, string? label, DatabaseVersion? asOf)
    {
        lock (_stateGate)
        {
            if (_timestampRead.Value is { } logical)
            {
                return logical.Graph.Outgoing(id, label);
            }
        }

        lock (_stateGate)
        {
            return asOf is null || asOf.Value.Value == _history.Keys.LastOrDefault()
                ? _currentView.Outgoing(id, label)
                : View(asOf.Value).Outgoing(id, label);
        }
    }

    public IReadOnlyList<GraphEdge> Incoming(GraphVertexId id, string? label)
    {
        lock (_stateGate)
        {
            return _timestampRead.Value is { } logical ? logical.Graph.Incoming(id, label) : _currentView.Incoming(id, label);
        }
    }

    public IReadOnlyList<GraphEdge> Outgoing(IReadOnlyList<GraphVertexId> ids, string? label)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_stateGate)
        {
            return _timestampRead.Value is { } logical ? ids.SelectMany(id => logical.Graph.Outgoing(id, label)).DistinctBy(edge => edge.Id).ToArray() : ids.SelectMany(id => _currentView.Outgoing(id, label)).DistinctBy(edge => edge.Id).ToArray();
        }
    }

    public IReadOnlyList<GraphEdge> Incoming(IReadOnlyList<GraphVertexId> ids, string? label)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_stateGate)
        {
            return _timestampRead.Value is { } logical ? ids.SelectMany(id => logical.Graph.Incoming(id, label)).DistinctBy(edge => edge.Id).ToArray() : ids.SelectMany(id => _currentView.Incoming(id, label)).DistinctBy(edge => edge.Id).ToArray();
        }
    }

    public IReadOnlyList<GraphEdge> Incoming(GraphVertexId id, string? label, DatabaseVersion asOf)
    {
        lock (_stateGate)
        {
            if (_timestampRead.Value is { } logical)
            {
                return logical.Graph.Incoming(id, label);
            }
        }

        lock (_stateGate)
        {
            return asOf.Value == _history.Keys.LastOrDefault()
                ? _currentView.Incoming(id, label)
                : View(asOf).Incoming(id, label);
        }
    }

    private GraphReadView View(DatabaseVersion asOf)
    {
        if (_timestampRead.Value is { } logical)
        {
            return logical.Graph;
        }

        long oldest = _history.Keys.FirstOrDefault();
        if (asOf.Value < oldest)
        {
            throw new GraphSnapshotTooOldException(asOf, new(oldest));
        }

        if (asOf.Value > _history.Keys.LastOrDefault())
        {
            throw new ArgumentOutOfRangeException(nameof(asOf));
        }

        KeyValuePair<long, GraphReadView> candidate = _history.LastOrDefault(item => item.Key <= asOf.Value);
        return candidate.Value ?? GraphReadView.Empty;
    }

    private static void ValidateIdentifier(Guid value, string field)
    {
        if (value == Guid.Empty)
        {
            throw new InvalidDataException($"Graph {field} cannot be empty.");
        }
    }

    private void ValidateProperty(string name, DatabaseValue value)
    {
        ValidatePropertyName(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value is DatabaseValue.Json json)
        {
            _jsonAdmissionPolicy.Validate(json.Value);
        }
    }

    private static void ValidatePropertyName(string name)
    {
        if (String.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("A graph property name is required.");
        }
    }

    private static void ValidateLabel(string label)
    {
        if (String.IsNullOrWhiteSpace(label))
        {
            throw new InvalidDataException("A graph label is required.");
        }
    }

    private void ValidateConflicts(GraphTransaction transaction)
    {
        // Only the kernel can enter this scope, after matching the exact durable preparation.
        // Its reservation survived checkpoint/recovery and excludes every conflicting write.
        if (_partitionCompletion == transaction.TransactionId)
        {
            return;
        }

        if (transaction.ReadVersion is not DatabaseVersion readVersion)
        {
            return;
        }

        // An explicitly dependency-free participant performs no data operation.
        // Its durable coordination record does not require historical graph data.
        if (transaction.Mutations.Count == 0 && transaction.ReadPredicates is { Count: 0 })
        {
            if (readVersion.Value > _history.Keys.LastOrDefault())
            {
                throw new ArgumentOutOfRangeException(nameof(transaction));
            }
            return;
        }

        if (transaction.ReadPredicates is { Count: > 0 } dependencies && dependencies.All(p => p is GraphQueryStateRead))
        {
            if (readVersion.Value > _history.Keys.LastOrDefault())
            {
                throw new ArgumentOutOfRangeException(nameof(transaction));
            }

            foreach (var query in dependencies.Cast<GraphQueryStateRead>())
            {
                if (transaction.Mutations.Any(m => m is not SetVertexProperty value || !query.Subjects.Contains(value.Vertex)))
                {
                    throw new InvalidDataException("Query mutation leaves its validated vertex scope.");
                }
            }
            // Self-contained current-state evidence survives historical index eviction.
            var currentVertices = _pendingStates.Values.MaxBy(s => s.Version.Value)?.Vertices ?? _vertices;
            foreach (var dependency in dependencies.Cast<GraphQueryStateRead>())
            {
                if (dependency.Fingerprint(currentVertices.Values) != dependency.ExpectedFingerprint)
                {
                    throw new GraphWriteConflictException(readVersion, "query selection");
                }
            }

            return;
        }
        long oldest = _conflictRetentionFloor;
        long current = _history.Keys.LastOrDefault();
        if (readVersion.Value < oldest)
        {
            throw new GraphSnapshotTooOldException(readVersion, new(oldest));
        }

        if (readVersion.Value > current)
        {
            throw new ArgumentOutOfRangeException(nameof(transaction));
        }

        if ((transaction.ReadPredicates ?? []).Any(predicate => predicate is DatabaseRead))
        {
            // Durable operation bindings advance the version without changing graph
            // data. Compare immutable data roots so a query does not conflict with
            // its own binding, while concurrent graph writes still invalidate it.
            if (!_history.TryGetValue(readVersion.Value, out var observed))
            {
                throw new GraphWriteConflictException(readVersion, "database");
            }
            if (!ReferenceEquals(observed.Vertices, _vertices) || !ReferenceEquals(observed.Edges, _edges) ||
                _pendingStates.Values.Any(pending => !ReferenceEquals(observed.Vertices, pending.Vertices) ||
                    !ReferenceEquals(observed.Edges, pending.Edges)))
            {
                throw new GraphWriteConflictException(readVersion, "database");
            }
        }

        foreach (GraphConflictKey key in ConflictKeys(transaction.Mutations, _edges)
            .Concat(ConflictKeys(transaction.ReadPredicates ?? []))
            .Concat(LifecycleDependencies(transaction)))
        {
            if (_lastWrites.TryGetValue(key, out DatabaseVersion written) && written.Value > readVersion.Value)
            {
                throw new GraphWriteConflictException(readVersion, key.ToDiagnosticString());
            }
            else if (_orderedWrites.TryGetValue(key, out written) && written.Value > readVersion.Value)
            {
                throw new GraphWriteConflictException(readVersion, key.ToDiagnosticString());
            }
        }
    }

    private static IEnumerable<GraphConflictKey> ConflictKeys(IEnumerable<GraphMutation> mutations,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> edges)
    {
        foreach (GraphMutation mutation in mutations)
        {
            GraphConflictKey primary = mutation switch
            {
                CreateVertex value => new(GraphConflictKeyKind.Vertex, value.Vertex.Value),
                DeleteVertex value => new(GraphConflictKeyKind.Vertex, value.Vertex.Value),
                CreateEdge value => new(GraphConflictKeyKind.Edge, value.Edge.Value),
                CreateEdgeReplica value => new(GraphConflictKeyKind.Edge, value.Edge.Value),
                DeleteEdge value => new(GraphConflictKeyKind.Edge, value.Edge.Value),
                DeleteEdgeReplica value => new(GraphConflictKeyKind.Edge, value.Edge.Value),
                SetVertexProperty value => new(GraphConflictKeyKind.VertexProperty, value.Vertex.Value, value.Name),
                RemoveVertexProperty value => new(GraphConflictKeyKind.VertexProperty, value.Vertex.Value, value.Name),
                SetEdgeProperty value => new(GraphConflictKeyKind.EdgeProperty, value.Edge.Value, value.Name),
                RemoveEdgeProperty value => new(GraphConflictKeyKind.EdgeProperty, value.Edge.Value, value.Name),
                AddVertexLabel value => new(GraphConflictKeyKind.Vertex, value.Vertex.Value),
                RemoveVertexLabel value => new(GraphConflictKeyKind.Vertex, value.Vertex.Value),
                _ => throw new InvalidDataException($"Unsupported graph mutation '{mutation.GetType().Name}'.")
            };
            yield return primary;
            if (mutation is CreateVertex or DeleteVertex)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, primary.Subject);
            }
            else if (mutation is CreateEdge or CreateEdgeReplica or DeleteEdge or DeleteEdgeReplica)
            {
                yield return new(GraphConflictKeyKind.EdgeLifecycle, primary.Subject);
            }

            if (mutation is CreateEdge edge)
            {
                yield return new(GraphConflictKeyKind.OutgoingAdjacency, edge.From.Value);
                yield return new(GraphConflictKeyKind.IncomingAdjacency, edge.To.Value);
                yield return new(GraphConflictKeyKind.LabelledOutgoingAdjacency, edge.From.Value, edge.Label);
                yield return new(GraphConflictKeyKind.LabelledIncomingAdjacency, edge.To.Value, edge.Label);
            }
            else if (mutation is CreateEdgeReplica replica)
            {
                if (replica.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency))
                {
                    yield return new(GraphConflictKeyKind.OutgoingAdjacency, replica.From.Value);
                    yield return new(GraphConflictKeyKind.LabelledOutgoingAdjacency, replica.From.Value, replica.Label);
                }
                if (replica.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency))
                {
                    yield return new(GraphConflictKeyKind.IncomingAdjacency, replica.To.Value);
                    yield return new(GraphConflictKeyKind.LabelledIncomingAdjacency, replica.To.Value, replica.Label);
                }
            }
            else if (mutation is DeleteEdge deletion && edges.TryGetValue(deletion.Edge, out GraphEdge? deleted))
            {
                yield return new(GraphConflictKeyKind.OutgoingAdjacency, deleted.From.Value);
                yield return new(GraphConflictKeyKind.IncomingAdjacency, deleted.To.Value);
                yield return new(GraphConflictKeyKind.LabelledOutgoingAdjacency, deleted.From.Value, deleted.Label);
                yield return new(GraphConflictKeyKind.LabelledIncomingAdjacency, deleted.To.Value, deleted.Label);
            }
            else if (mutation is DeleteEdgeReplica replicaDelete)
            {
                if (replicaDelete.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency))
                {
                    yield return new(GraphConflictKeyKind.OutgoingAdjacency, replicaDelete.From.Value);
                    yield return new(GraphConflictKeyKind.LabelledOutgoingAdjacency, replicaDelete.From.Value,
                        replicaDelete.Label);
                }
                if (replicaDelete.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency))
                {
                    yield return new(GraphConflictKeyKind.IncomingAdjacency, replicaDelete.To.Value);
                    yield return new(GraphConflictKeyKind.LabelledIncomingAdjacency, replicaDelete.To.Value,
                        replicaDelete.Label);
                }
            }
        }
    }

    internal static IEnumerable<GraphMutation> SnapshotMutations(GraphReadView view)
    {
        foreach (GraphVertex vertex in view.Vertices.Values)
        {
            yield return new CreateVertex(vertex.Id, vertex.Labels);
            foreach (var property in vertex.Properties)
            {
                yield return new SetVertexProperty(vertex.Id, property.Key, property.Value);
            }
        }

        foreach (GraphEdge edge in view.Edges.Values)
        {
            yield return edge.StorageRole is GraphEdgeStorageRole.Complete
                ? new CreateEdge(edge.Id, edge.From, edge.To, edge.Label)
                : new CreateEdgeReplica(edge.Id, edge.From, edge.To, edge.Label, edge.StorageRole);
            foreach (var property in edge.Properties)
            {
                yield return new SetEdgeProperty(edge.Id, property.Key, property.Value);
            }
        }
    }

    private static IEnumerable<GraphConflictKey> ConflictKeys(IEnumerable<GraphReadPredicate> predicates)
    {
        foreach (GraphReadPredicate predicate in predicates)
        {
            if (predicate is DatabaseRead)
            {
                continue;
            }

            yield return predicate switch
            {
                VertexRead value => new(GraphConflictKeyKind.Vertex, value.Vertex.Value),
                WholeVertexRead value => new(GraphConflictKeyKind.WholeVertex, value.Vertex.Value),
                WholeEdgeRead value => new(GraphConflictKeyKind.WholeEdge, value.Edge.Value),
                OutgoingAdjacencyRead value => value.Label is null
                    ? new(GraphConflictKeyKind.OutgoingAdjacency, value.Vertex.Value)
                    : new(GraphConflictKeyKind.LabelledOutgoingAdjacency, value.Vertex.Value, value.Label),
                IncomingAdjacencyRead value => value.Label is null
                    ? new(GraphConflictKeyKind.IncomingAdjacency, value.Vertex.Value)
                    : new(GraphConflictKeyKind.LabelledIncomingAdjacency, value.Vertex.Value, value.Label),
                EdgePropertyRead value => new(GraphConflictKeyKind.EdgeProperty, value.Edge.Value, value.Property),
                VertexPropertyRead value => new(GraphConflictKeyKind.VertexProperty, value.Vertex.Value, value.Property),
                _ => throw new InvalidDataException($"Unsupported graph read predicate '{predicate.GetType().Name}'.")
            };
        }
    }

    private void TrimHistory()
    {
        long leaseFloor = _snapshotLeases.Keys.DefaultIfEmpty(long.MaxValue).Min();
        int capacity = IsDistributed() ? DistributedSnapshotAcquisition.MaximumVersions : MaximumRetainedVersions;
        while (_history.Count > capacity && _history.Keys.First() < leaseFloor)
        {
            _history.Remove(_history.Keys.First());
        }
    }

    private void ReleaseSnapshot(DatabaseVersion version)
    {
        lock (_stateGate)
        {
            int remaining = _snapshotLeases[version.Value] - 1;
            if (remaining == 0)
            {
                _snapshotLeases.Remove(version.Value);
            }
            else
            {
                _snapshotLeases[version.Value] = remaining;
            }

            TrimHistory();
        }
    }

    private static IEnumerable<GraphConflictKey> LifecycleDependencies(GraphTransaction transaction)
    {
        foreach (GraphMutation mutation in transaction.Mutations)
        {
            if (mutation is CreateEdge create)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, create.From.Value);
                yield return new(GraphConflictKeyKind.VertexLifecycle, create.To.Value);
            }
            else if (mutation is CreateEdgeReplica replica)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, replica.From.Value);
                yield return new(GraphConflictKeyKind.VertexLifecycle, replica.To.Value);
            }
            else if (mutation is DeleteVertex delete)
            {
                yield return new(GraphConflictKeyKind.OutgoingAdjacency, delete.Vertex.Value);
                yield return new(GraphConflictKeyKind.IncomingAdjacency, delete.Vertex.Value);
            }
            else if (mutation is AddVertexLabel add)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, add.Vertex.Value);
            }
            else if (mutation is RemoveVertexLabel label)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, label.Vertex.Value);
            }
            else if (mutation is SetVertexProperty set)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, set.Vertex.Value);
            }
            else if (mutation is RemoveVertexProperty remove)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, remove.Vertex.Value);
            }
            else if (mutation is SetEdgeProperty setEdge)
            {
                yield return new(GraphConflictKeyKind.EdgeLifecycle, setEdge.Edge.Value);
            }
            else if (mutation is RemoveEdgeProperty removeEdge)
            {
                yield return new(GraphConflictKeyKind.EdgeLifecycle, removeEdge.Edge.Value);
            }
        }
        foreach (GraphReadPredicate predicate in transaction.ReadPredicates ?? [])
        {
            if (predicate is VertexPropertyRead property)
            {
                yield return new(GraphConflictKeyKind.VertexLifecycle, property.Vertex.Value);
            }
            else if (predicate is EdgePropertyRead edgeProperty)
            {
                yield return new(GraphConflictKeyKind.EdgeLifecycle, edgeProperty.Edge.Value);
            }
        }
    }

    private enum GraphConflictKeyKind : byte
    {
        Vertex,
        WholeVertex,
        WholeEdge,
        Edge,
        VertexLifecycle,
        EdgeLifecycle,
        VertexProperty,
        EdgeProperty,
        OutgoingAdjacency,
        IncomingAdjacency,
        LabelledOutgoingAdjacency,
        LabelledIncomingAdjacency
    }

    private sealed record PendingGraphState(DatabaseVersion Version,
        ImmutableDictionary<GraphVertexId, GraphVertex> Vertices,
        ImmutableDictionary<GraphEdgeId, GraphEdge> Edges,
        GraphReadView View, GraphCommittedEffects Effects, GraphQueryStatistics Statistics,
        IReadOnlyList<GraphConflictKey> ConflictKeys);

    private readonly record struct GraphConflictKey(GraphConflictKeyKind Kind, Guid Subject, string? Qualifier = null)
    {
        public string ToDiagnosticString() => Kind switch
        {
            GraphConflictKeyKind.Vertex => $"vertex:{Subject:N}",
            GraphConflictKeyKind.WholeVertex => $"vertex:{Subject:N}:contents",
            GraphConflictKeyKind.Edge => $"edge:{Subject:N}",
            GraphConflictKeyKind.VertexLifecycle => $"vertex:{Subject:N}:lifecycle",
            GraphConflictKeyKind.EdgeLifecycle => $"edge:{Subject:N}:lifecycle",
            GraphConflictKeyKind.WholeEdge => $"edge:{Subject:N}:whole",
            GraphConflictKeyKind.VertexProperty => $"vertex:{Subject:N}:property:{Qualifier}",
            GraphConflictKeyKind.EdgeProperty => $"edge:{Subject:N}:property:{Qualifier}",
            GraphConflictKeyKind.OutgoingAdjacency => $"adjacency:out:{Subject:N}",
            GraphConflictKeyKind.IncomingAdjacency => $"adjacency:in:{Subject:N}",
            GraphConflictKeyKind.LabelledOutgoingAdjacency => $"adjacency:out:{Subject:N}:label:{Qualifier}",
            GraphConflictKeyKind.LabelledIncomingAdjacency => $"adjacency:in:{Subject:N}:label:{Qualifier}",
            _ => throw new InvalidOperationException($"Unsupported Graph conflict-key kind '{Kind}'.")
        };
    }

    private sealed class GraphConflictTracker
    {
        private readonly PartitionedVersionMap<Guid> _vertices = new();
        private readonly PartitionedVersionMap<Guid> _wholeVertices = new();
        private readonly PartitionedVersionMap<Guid> _wholeEdges = new();
        private readonly PartitionedVersionMap<Guid> _edges = new();
        private readonly PartitionedVersionMap<Guid> _vertexLifecycles = new();
        private readonly PartitionedVersionMap<Guid> _edgeLifecycles = new();
        private readonly PartitionedVersionMap<(Guid Subject, string Qualifier)> _vertexProperties = new();
        private readonly PartitionedVersionMap<(Guid Subject, string Qualifier)> _edgeProperties = new();
        private readonly PartitionedVersionMap<Guid> _outgoing = new();
        private readonly PartitionedVersionMap<Guid> _incoming = new();
        private readonly PartitionedVersionMap<(Guid Subject, string Qualifier)> _labelledOutgoing = new();
        private readonly PartitionedVersionMap<(Guid Subject, string Qualifier)> _labelledIncoming = new();

        public void Set(GraphConflictKey key, DatabaseVersion version)
        {
            // Maintain an aggregate read watermark without broadening the writes used for conflict validation.
            if (key.Kind is GraphConflictKeyKind.Vertex or GraphConflictKeyKind.VertexProperty or GraphConflictKeyKind.VertexLifecycle)
            {
                _wholeVertices[key.Subject] = version;
            }

            if (key.Kind is GraphConflictKeyKind.Edge or GraphConflictKeyKind.EdgeProperty or GraphConflictKeyKind.EdgeLifecycle)
            {
                _wholeEdges[key.Subject] = version;
            }

            switch (key.Kind)
            {
                case GraphConflictKeyKind.Vertex:
                    _vertices[key.Subject] = version;
                    break;
                case GraphConflictKeyKind.Edge:
                    _edges[key.Subject] = version;
                    break;
                case GraphConflictKeyKind.VertexLifecycle:
                    _vertexLifecycles[key.Subject] = version;
                    break;
                case GraphConflictKeyKind.EdgeLifecycle:
                    _edgeLifecycles[key.Subject] = version;
                    break;
                case GraphConflictKeyKind.VertexProperty:
                    _vertexProperties[Qualified(key)] = version;
                    break;
                case GraphConflictKeyKind.EdgeProperty:
                    _edgeProperties[Qualified(key)] = version;
                    break;
                case GraphConflictKeyKind.OutgoingAdjacency:
                    _outgoing[key.Subject] = version;
                    break;
                case GraphConflictKeyKind.IncomingAdjacency:
                    _incoming[key.Subject] = version;
                    break;
                case GraphConflictKeyKind.LabelledOutgoingAdjacency:
                    _labelledOutgoing[Qualified(key)] = version;
                    break;
                case GraphConflictKeyKind.LabelledIncomingAdjacency:
                    _labelledIncoming[Qualified(key)] = version;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Graph conflict-key kind '{key.Kind}'.");
            }
        }

        public bool TryGetValue(GraphConflictKey key, out DatabaseVersion version) => key.Kind switch
        {
            GraphConflictKeyKind.Vertex => _vertices.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.WholeVertex => _wholeVertices.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.WholeEdge => _wholeEdges.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.Edge => _edges.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.VertexLifecycle => _vertexLifecycles.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.EdgeLifecycle => _edgeLifecycles.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.VertexProperty => _vertexProperties.TryGetValue(Qualified(key), out version),
            GraphConflictKeyKind.EdgeProperty => _edgeProperties.TryGetValue(Qualified(key), out version),
            GraphConflictKeyKind.OutgoingAdjacency => _outgoing.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.IncomingAdjacency => _incoming.TryGetValue(key.Subject, out version),
            GraphConflictKeyKind.LabelledOutgoingAdjacency => _labelledOutgoing.TryGetValue(Qualified(key), out version),
            GraphConflictKeyKind.LabelledIncomingAdjacency => _labelledIncoming.TryGetValue(Qualified(key), out version),
            _ => throw new InvalidOperationException($"Unsupported Graph conflict-key kind '{key.Kind}'.")
        };

        public void RetainAfter(long floor)
        {
            _vertices.RetainAfter(floor);
            _wholeVertices.RetainAfter(floor);
            _wholeEdges.RetainAfter(floor);
            _edges.RetainAfter(floor);
            _vertexLifecycles.RetainAfter(floor);
            _edgeLifecycles.RetainAfter(floor);
            _vertexProperties.RetainAfter(floor);
            _edgeProperties.RetainAfter(floor);
            _outgoing.RetainAfter(floor);
            _incoming.RetainAfter(floor);
            _labelledOutgoing.RetainAfter(floor);
            _labelledIncoming.RetainAfter(floor);
        }

        public void Clear()
        {
            _vertices.Clear();
            _wholeVertices.Clear();
            _wholeEdges.Clear();
            _edges.Clear();
            _vertexLifecycles.Clear();
            _edgeLifecycles.Clear();
            _vertexProperties.Clear();
            _edgeProperties.Clear();
            _outgoing.Clear();
            _incoming.Clear();
            _labelledOutgoing.Clear();
            _labelledIncoming.Clear();
        }

        private static (Guid Subject, string Qualifier) Qualified(GraphConflictKey key) =>
            (key.Subject, key.Qualifier ?? throw new InvalidOperationException(
                $"Graph conflict-key kind '{key.Kind}' requires a qualifier."));
    }

    private sealed class PartitionedVersionMap<TKey> where TKey : notnull
    {
        // Bounded partitions avoid a single large Dictionary resize temporarily retaining both
        // the old and new entry arrays inside a constrained-memory commit publication window.
        private const int PartitionCount = 32;
        private readonly Dictionary<TKey, DatabaseVersion>[] _partitions =
            Enumerable.Range(0, PartitionCount).Select(_ => new Dictionary<TKey, DatabaseVersion>()).ToArray();

        public DatabaseVersion this[TKey key]
        {
            set => Partition(key)[key] = value;
        }

        public bool TryGetValue(TKey key, out DatabaseVersion version) =>
            Partition(key).TryGetValue(key, out version);

        public void RetainAfter(long floor)
        {
            foreach (var partition in _partitions)
            {
                foreach (var key in partition.Where(p => p.Value.Value <= floor).Select(p => p.Key).ToArray())
                {
                    partition.Remove(key);
                }
            }
        }

        public void Clear()
        {
            foreach (Dictionary<TKey, DatabaseVersion> partition in _partitions)
            {
                partition.Clear();
            }
        }

        private Dictionary<TKey, DatabaseVersion> Partition(TKey key)
        {
            int hash = EqualityComparer<TKey>.Default.GetHashCode(key) & Int32.MaxValue;
            return _partitions[hash % PartitionCount];
        }
    }
}
