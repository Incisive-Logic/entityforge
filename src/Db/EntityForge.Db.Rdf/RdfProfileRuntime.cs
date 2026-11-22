using System.Collections.Concurrent;
using System.Collections.Immutable;
using EntityForge.Db.Kernel;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;

using EntityForge.Db.Mapping;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfProfileRuntime : IEntityOperationBindingAdmission, IEntityOperationCompactionProfile, IDatabaseProfileDeferredCheckpoint, IDatabaseProfileSnapshotPreservingCheckpoint,
    IDatabaseProfileCheckpointPageReuse,
    IDatabaseProfileRetainedPhysicalPreparation, IDatabaseProfileChangePlanner, IDatabaseProfileGroupedPhysicalMutationPlanner, IDatabaseProfileOrderedPreparationRuntime,
    IDatabaseProfilePublicationRuntime, IDatabaseProfileDurabilityBarrier, IEntityOperationAtomicCommitProfile
{
    void IEntityOperationAtomicCommitProfile.ValidateBoundPayload(EntityRecordDeliveryCodec.Metadata metadata,
        ReadOnlyMemory<byte> nativePayload)
    {
        if (metadata.NativeProfile is not null)
        {
            throw new EntityForgeDbIdempotencyException(metadata.OperationId);
        }

        Mappings.RequireCurrent(metadata.Binding);
    }

    EntityOperationCommitted IEntityOperationAtomicCommitProfile.DescribeBoundCommitted(
        EntityOperationBoundCommit request, DatabaseCommitReceipt receipt) =>
        request.Describe(CodecId, CodecVersion, receipt);

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
            CodecId, CodecVersion, payload, receipt);
    }

    public Guid? BindingIdentity(ReadOnlyMemory<byte> payload) =>
        EntityOperationCodec.IsCommand(payload.Span) ? EntityOperationCodec.Decode(payload.Span).OperationId : null;

    public Guid? OperationIdentity(Guid transactionId, ReadOnlyMemory<byte> payload) =>
        BindingIdentity(payload) ?? (IsMetadata(payload) ? null : transactionId);

    internal EntityOperationCatalog Operations { get; } = new();

    private static bool IsMetadata(ReadOnlyMemory<byte> payload) => IsMetadataSpan(payload.Span);

    private static bool IsMetadataSpan(ReadOnlySpan<byte> payload) =>
        EntityMappingCodec.IsProfileCommandSpan(payload) || EntityOperationCodec.IsCommand(payload);

    internal const string TransactionCodecId = "entityforge.rdf.transactions";
    internal const int TransactionCodecVersion = RdfTransactionCodec.Version;
    internal const string CheckpointCodecIdValue = "entityforge.rdf.checkpoint";
    internal const int CheckpointCodecVersionValue = 5;

    internal EntityMappingCatalog Mappings { get; } = new(DatabaseProfileId.Rdf);

    internal const string CommittedTransactionCodecId = "entityforge.rdf.transaction";
    internal const int CommittedTransactionCodecVersion = 1;
    internal const string DatasetResetCodecId = "entityforge.rdf.dataset-reset";
    internal const int DatasetResetCodecVersion = 1;
    private const int MaximumSelectivePhysicalCandidates = 256;
    private readonly object _gate = new();
    private RdfReadView _view = RdfReadView.Empty;
    private readonly SortedDictionary<long, RdfReadView> _history = [];

    internal Func<bool> IsDistributed { get; set; } = static () => false;

    private readonly Dictionary<long, int> _leases = [];
    private DatabaseVersion _version = DatabaseVersion.Initial;
    private readonly RdfPhysicalAccessPath _physical;
    private readonly HashSet<Guid> _stagedPhysical = [];
    private readonly HashSet<Guid> _persistedPhysical = [];
    private readonly Dictionary<Guid, Guid> _replacementCommits = [];
    private readonly HashSet<Guid> _bulkReplacementCommits = [];
    private ImmutableDictionary<Guid, RdfCommandReplay> _commandReplays = ImmutableDictionary<Guid, RdfCommandReplay>.Empty;
    private readonly Dictionary<Guid, RdfTransaction> _preparedTransactions = [];
    private readonly Dictionary<Guid, PendingRdfState> _pendingStates = [];

    public RdfProfileRuntime(RdfPhysicalAccessPath physical)
    {
        _physical = physical;
        _history[DatabaseVersion.Initial.Value] = _view;
    }

    public DatabaseProfileId Profile => DatabaseProfileId.Rdf;

    public string CodecId => TransactionCodecId;

    public int CodecVersion => TransactionCodecVersion;

    public string CheckpointCodecId => CheckpointCodecIdValue;

    public int CheckpointCodecVersion => CheckpointCodecVersionValue;

    internal DatabaseVersion CurrentVersion
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    internal DatabaseVersion OldestReadableVersion
    {
        get
        {
            lock (_gate)
            {
                return new(_history.Keys.First());
            }
        }
    }

    internal static byte[] Encode(RdfTransaction transaction) =>
        RdfTransactionCodec.Encode(transaction);

    private RdfTransaction Decode(Guid transactionId, ReadOnlySpan<byte> payload)
    {
        if (_logicalIntents.TryGet(transactionId, payload, out var retained))
        {
            return retained;
        }

        if (IsMetadataSpan(payload))
        {
            return new(transactionId, []);
        }

        return DecodeMeasured(transactionId, payload);
    }

    private static RdfTransaction DecodeMeasured(Guid id, ReadOnlySpan<byte> payload)
    {
        long started = DatabaseProfileWorkTelemetry.Start();
        var transaction = RdfTransactionCodec.DecodeTransaction(id, payload);
        DatabaseProfileWorkTelemetry.Record("rdf", "decode", "quad-mutation", transaction.Mutations.Count, started);
        return transaction;
    }

    internal static RdfTransaction DecodeTransaction(Guid transactionId, ReadOnlySpan<byte> payload) =>
        IsMetadataSpan(payload) ? new(transactionId, []) : RdfTransactionCodec.DecodeTransaction(transactionId, payload);

    public ValueTask ValidateAsync(Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This check runs under kernel commit admission, including direct native writes.
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

        lock (_gate)
        {
            if (_replacementCommits.ContainsKey(transactionId) &&
                         !_bulkReplacementCommits.Contains(transactionId))
            {
                return ValueTask.CompletedTask;
            }
        }

        RdfTransaction transaction = Decode(transactionId, payload.Span);
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
        }

        if (transaction.Mutations is null)
        {
            throw new InvalidDataException("RDF mutations are required.");
        }

        lock (_gate)
        {
            if (transaction.IsInitialBulkLoad && _view.Quads.Count != 0)
            {
                throw new InvalidOperationException("An initial RDF bulk load requires an empty database.");
            }
        }

        ValidateConflicts(transaction);
        foreach (RdfMutation mutation in transaction.Mutations)
        {
            _ = (mutation ?? throw new InvalidDataException("An RDF mutation cannot be null.")).Quad.Validate();
        }

        lock (_gate)
        {
            _preparedTransactions[transactionId] = transaction;
        }

        return ValueTask.CompletedTask;
    }

    private void ValidateConflicts(RdfTransaction transaction)
    {
        if (transaction.ReadVersion is not DatabaseVersion readVersion)
        {
            return;
        }

        lock (_gate)
        {
            if (readVersion.Value > _version.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(transaction), "An RDF read version cannot be in the future.");
            }

            var currentView = _pendingStates.Values.MaxBy(state => state.Version.Value)?.View ?? _view;
            foreach (RdfReadPredicate predicate in transaction.ReadPredicates ?? [])
            {
                if (predicate is RdfQueryStateRead query)
                {
                    if (transaction.Mutations.Any(m => !query.Subjects.Contains(m.Quad.Subject)))
                    {
                        throw new InvalidDataException("Query mutation leaves its validated subject scope.");
                    }

                    if (query.Fingerprint(currentView.QueryEvidence(query)) != query.ExpectedFingerprint)
                    {
                        throw new RdfWriteConflictException(readVersion, query.Predicate);
                    }

                    continue;
                }
                if (predicate is not RdfSubjectStateRead subjectRead)
                {
                    throw new InvalidDataException($"Unsupported RDF read predicate '{predicate.GetType().Name}'.");
                }

                var pattern = new RdfQuadPattern(subjectRead.Subject, Graph: subjectRead.Graph,
                    MatchDefaultGraphOnly: subjectRead.MatchDefaultGraphOnly);
                string current = RdfSubjectStateRead.Capture(subjectRead.Subject, subjectRead.Graph,
                    subjectRead.MatchDefaultGraphOnly,
                    currentView.Candidates(pattern).Where(quad => Matches(quad, pattern))).ExpectedFingerprint;
                if (!StringComparer.Ordinal.Equals(current, subjectRead.ExpectedFingerprint))
                {
                    throw new RdfWriteConflictException(readVersion, subjectRead.Subject);
                }
            }
        }
    }

    public ValueTask<DatabaseProfileChangePlan> PlanCommittedChangesAsync(DatabaseVersion version,
        Guid transactionId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsMetadata(payload))
        {
            return ValueTask.FromResult(DatabaseProfileChangePlan.System());
        }

        lock (_gate)
        {
            if (_replacementCommits.ContainsKey(transactionId) &&
                         !_bulkReplacementCommits.Contains(transactionId))
            {
                return ValueTask.FromResult(DatabaseProfileChangePlan.System());
            }
        }

        RdfTransaction transaction = PreparedTransaction(transactionId, payload);
        if (transaction.IsInitialBulkLoad)
        {
            var routing = new CommittedChangeRoutingSummary(overflowedKinds:
                ["rdf.subject", "rdf.predicate", "rdf.graph"]);
            return ValueTask.FromResult(DatabaseProfileChangePlan.UserData(new(
                DatasetResetCodecId, DatasetResetCodecVersion, ReadOnlyMemory<byte>.Empty,
                transaction.Mutations.Count, routing, CommittedChangeKind.DatasetReset)));
        }
        if (transaction.Mutations.Count == 0)
        {
            return ValueTask.FromResult(DatabaseProfileChangePlan.UserData(null));
        }

        IEnumerable<CommittedChangeRoutingKey> keys = transaction.Mutations.SelectMany(mutation =>
            RoutingKeys(mutation.Quad));
        return ValueTask.FromResult(DatabaseProfileChangePlan.UserData(new(
            CommittedTransactionCodecId, CommittedTransactionCodecVersion,
            payload, transaction.Mutations.Count,
            CommittedChangeRoutingSummary.CreateBounded(keys))));
    }

    public ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
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

        RdfTransaction transaction = PreparedTransaction(transactionId, payload);
        lock (_gate)
        {
            try
            {
                PendingRdfState? prior = _pendingStates.Count == 0 ? null :
                    _pendingStates.Values.MaxBy(item => item.Version.Value);
                RdfReadView priorView = prior?.View ?? _view;
                var replays = prior?.CommandReplays ?? _commandReplays;
                if (_replacementCommits.ContainsKey(transactionId) &&
                    !_bulkReplacementCommits.Contains(transactionId))
                {
                    _pendingStates[transactionId] = new(version, priorView, replays);
                    return ValueTask.CompletedTask;
                }
                if (transaction.Mutations.Count == 0)
                {
                    // Metadata advances the logical boundary but does not change the dataset.
                    // Keep its immutable indexes and caches, including for retained readers.
                    if (transaction.SourceCommandHash is not null)
                    {
                        replays = replays.SetItem(transactionId, new(transaction.SourceCommandHash, 0, version));
                    }

                    _pendingStates[transactionId] = new(version, priorView, replays);
                    return ValueTask.CompletedTask;
                }
                long started = DatabaseProfileWorkTelemetry.Start();
                bool reused = _logicalEffects.TryGet(transactionId, payload.Span, out var retainedEffects);
                RdfReadView next = reused ? retainedEffects!.Apply(priorView) : priorView.Apply(transaction.Mutations);
                DatabaseProfileWorkTelemetry.Record("rdf", reused ? "reuse" : "compile", "quad-mutation", transaction.Mutations.Count, started);
                if (transaction.SourceCommandHash is not null)
                {
                    replays = replays.SetItem(transactionId, new(transaction.SourceCommandHash,
                        transaction.Mutations.Count, version));
                }

                _pendingStates[transactionId] = new(version, next, replays);
            }
            finally
            {
                _preparedTransactions.Remove(transactionId);
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(
        CancellationToken cancellationToken = default)
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
        lock (_gate)
        {
            if (!preserveSnapshots && _leases.Count != 0)
            {
                throw new RdfSnapshotRetentionException();
            }

            var quads = _view.Quads;
            var replays = _commandReplays;
            byte[] mappings = Mappings.Capture(), operations = Operations.Capture();
            return token => EntityOperationCodec.WrapCheckpoint(EntityMappingCodec.WrapCheckpoint(
                RdfCheckpointCodec.Encode(quads, replays, token), mappings), operations);
        }
    }

    public bool RequiresCheckpointMaintenance => _physical.RequiresCheckpointMaintenance;

    public bool SupportsLocalRetentionMaintenance => true;

    public void AuthorizeCheckpointPageReuse(bool compacted) => _physical.AuthorizeCheckpointPageReuse();

    private bool _maintenanceAdmission;

    public IDisposable? EnterMaintenance()
    {
        lock (_gate)
        {
            if (_maintenanceAdmission || _leases.Count != 0)
            {
                throw new RdfSnapshotRetentionException();
            }
            _maintenanceAdmission = true;
            return new MaintenanceAdmission(this);
        }
    }

    private sealed class MaintenanceAdmission(RdfProfileRuntime owner) : IDisposable
    {
        private RdfProfileRuntime? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }
            lock (current._gate)
            {
                current._maintenanceAdmission = false;
            }
        }
    }

    public ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(Guid id,
        DatabaseVersion version, bool compact, CancellationToken token)
    {
        lock (_gate)
        {
            if (_leases.Count != 0)
            {
                throw new InvalidOperationException("RDF maintenance requires drained snapshots.");
            }
        }

        return _physical.PrepareMaintenanceAsync(id, version.Value, compact, token);
    }

    public async ValueTask PublishMaintenanceAsync(Guid id, CancellationToken token)
    {
        await _physical.PersistAsync(id, token).ConfigureAwait(false);
        _physical.Publish(id);
    }

    public ValueTask AbandonMaintenanceAsync(Guid id)
    {
        _physical.Abandon(id);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteMaintenanceAsync(CancellationToken token) => _physical.TrimAsync(token);

    public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restoredOperations = new EntityOperationCatalog();
        var restoredMappings = new EntityMappingCatalog(Profile);
        var checkpoint = RdfCheckpointCodec.Decode(
            EntityMappingCodec.UnwrapCheckpoint(EntityOperationCodec.UnwrapCheckpoint(payload, restoredOperations), restoredMappings),
            cancellationToken);
        if (checkpoint.CommandReplays.Values.Any(replay => replay.Version.Value > version.Value))
        {
            throw new InvalidDataException("RDF checkpoint command replay exceeds the checkpoint version.");
        }
        RdfReadView restored = RdfReadView.Build(checkpoint.Quads);
        var restoredReplays = checkpoint.CommandReplays.ToImmutableDictionary();
        var mappingBytes = restoredMappings.Capture();
        var operationBytes = restoredOperations.Capture();
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Mappings.Restore(mappingBytes);
            Operations.Restore(operationBytes);
            _view = restored;
            _version = version;
            _commandReplays = restoredReplays;
            _history.Clear();
            _history[version.Value] = _view;
        }
        return ValueTask.CompletedTask;
    }

    internal bool TryGetCommandReplay(Guid transactionId, string commandHash, out RdfSparqlUpdateCommit commit)
    {
        lock (_gate)
        {
            if (!_commandReplays.TryGetValue(transactionId, out RdfCommandReplay? replay))
            {
                commit = default!;
                return false;
            }
            if (!String.Equals(replay.CommandHash, commandHash, StringComparison.Ordinal))
            {
                throw new EntityForgeDbIdempotencyException(transactionId);
            }

            commit = new(replay.Version, replay.MutationCount);
            return true;
        }
    }

    internal sealed record RdfCommandReplay(string CommandHash, int MutationCount, DatabaseVersion Version);

    internal IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        RdfReadView view = CurrentReadView();
        return Ordered(FindUnordered(view, pattern));
    }

    internal IReadOnlyList<RdfQuad> FindBounded(RdfQuadPattern pattern, DatabaseVersion version, int maximumResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (maximumResults < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        var values = new List<RdfQuad>();
        foreach (var quad in ReadViewCaptured(version).Candidates(pattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Matches(quad, pattern))
            {
                continue;
            }

            if (values.Count == maximumResults)
            {
                throw new InvalidOperationException("The complete RDF query exceeds its retained result bound.");
            }

            values.Add(quad);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Ordered(values);
    }

    internal IReadOnlyList<RdfQuad> FindUnordered(RdfQuadPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return FindUnordered(CurrentReadView(), pattern).ToArray();
    }

    internal IReadOnlyList<RdfQuad> FindUnordered(RdfQuadPattern pattern, DatabaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return FindUnordered(ReadViewCaptured(version), pattern).ToArray();
    }

    internal int EstimateCandidates(RdfQuadPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return CurrentReadView().Candidates(pattern).Count;
    }


    internal int EstimateCandidates(RdfQuadPattern pattern, DatabaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return ReadViewCaptured(version).Candidates(pattern).Count;
    }

    internal IEnumerable<RdfQuad> FindInMemoryUnordered(RdfQuadPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return FindUnordered(CurrentReadView(), pattern);
    }

    internal IReadOnlySet<RdfQuad> FindInMemoryCandidates(RdfTerm? subject, RdfIri? predicate,
        RdfTerm? obj, RdfTerm? graph, bool matchDefaultGraphOnly)
    {
        return CurrentReadView().Candidates(subject, predicate, obj, graph, matchDefaultGraphOnly);
    }

    internal IReadOnlySet<RdfQuad> FindInMemoryCandidates(RdfTerm? subject, RdfIri? predicate,
        RdfTerm? obj, RdfTerm? graph, bool matchDefaultGraphOnly, DatabaseVersion version)
    {
        return ReadViewCaptured(version).Candidates(subject, predicate, obj, graph, matchDefaultGraphOnly);
    }

    internal IReadOnlyList<RdfQuad> FindInMemoryByPredicateOrderedSubject(RdfIri predicate, bool descending)
    {
        return CurrentReadView().ByPredicateOrderedSubject(predicate, descending);
    }

    internal IReadOnlyList<RdfQuad> FindInMemoryByPredicateOrderedSubject(RdfIri predicate, bool descending,
        DatabaseVersion version)
    {
        return ReadViewCaptured(version).ByPredicateOrderedSubject(predicate, descending);
    }

    internal IReadOnlyList<RdfQuad> FindInMemoryByPatternOrderedSubject(
        RdfQuadPattern pattern, bool descending)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return CurrentReadView().ByPatternOrderedSubject(pattern, descending);
    }


    internal IReadOnlyList<RdfQuad> FindInMemoryByPatternOrderedSubject(
        RdfQuadPattern pattern, bool descending, DatabaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return ReadViewCaptured(version).ByPatternOrderedSubject(pattern, descending);
    }

    internal int CountInMemory(RdfIri predicate, RdfTerm obj, RdfTerm? graph)
    {
        return CurrentReadView().Count(predicate, obj, graph);
    }


    internal int CountInMemory(RdfIri predicate, RdfTerm obj, RdfTerm? graph, DatabaseVersion version)
    {
        return ReadViewCaptured(version).Count(predicate, obj, graph);
    }

    internal IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate)
    {
        return CurrentReadView().CountDefaultGraphByObject(predicate);
    }


    internal IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate,
        DatabaseVersion version)
    {
        return ReadViewCaptured(version).CountDefaultGraphByObject(predicate);
    }

    private RdfReadView CurrentReadView()
    {
        lock (_gate)
        {
            return _timestampRead.Value?.Data ?? _view;
        }
    }

    private RdfReadView ReadViewCaptured(DatabaseVersion version)
    {
        lock (_gate)
        {
            return ReadView(version);
        }
    }

    private RdfReadView ReadView(DatabaseVersion version) =>
        _timestampRead.Value?.Data ?? (_history.TryGetValue(version.Value, out RdfReadView? view)
            ? view
            : throw new RdfSnapshotTooOldException(version));

    private static IEnumerable<RdfQuad> FindUnordered(RdfReadView view, RdfQuadPattern pattern) =>
        view.Candidates(pattern).Where(quad => Matches(quad, pattern));

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
        DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (IsMetadata(payload))
        {
            return [];
        }

        Guid replacementId;
        bool replacement;
        lock (_gate)
        {
            replacement = _replacementCommits.TryGetValue(transactionId, out replacementId);
        }

        if (replacement)
        {
            long expected = _physical.GetReplacementExpectedGeneration(replacementId);
            try
            {
                OrderedPageTreeStage replacementStage = await _physical.PrepareReplacementAsync(
                    transactionId, replacementId, cancellationToken).ConfigureAwait(false);
                _stagedPhysical.Add(transactionId);
                return replacementStage.PhysicalMutations;
            }
            catch (AppendMergeReplacementStaleException)
            {
                throw new RdfReplacementStaleException(expected, _physical.Generation);
            }
        }
        OrderedPageTreeStage? stage = _logicalEffects.TryGet(transactionId, payload.Span, out var compiled)
            ? await _physical.PrepareCompiledAsync(transactionId, version.Value, compiled.Indexes, cancellationToken).ConfigureAwait(false)
            : await _physical.PrepareAsync(transactionId, version.Value, PreparedTransaction(transactionId, payload), cancellationToken).ConfigureAwait(false);
        if (stage is null)
        {
            return [];
        }

        _stagedPhysical.Add(transactionId);
        return stage.PhysicalMutations;
    }

    public ValueTask PublishPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        if (!_stagedPhysical.Remove(transactionId))
        {
            return ValueTask.CompletedTask;
        }

        _persistedPhysical.Add(transactionId);
        return _physical.PersistAsync(transactionId, cancellationToken);
    }

    public ValueTask PublishStagedStateAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (transactionId == Guid.Empty && _pendingStates.Count != 0)
            {
                foreach (PendingRdfState pending in _pendingStates.Values.OrderBy(item => item.Version.Value))
                {
                    Publish(pending);
                }

                _pendingStates.Clear();
            }
            else if (_pendingStates.Remove(transactionId, out PendingRdfState? pending))
            {
                Publish(pending);
            }

            _orderedPreparations.Remove(transactionId);
            if (transactionId != Guid.Empty && _persistedPhysical.Remove(transactionId))
            {
                _physical.Publish(transactionId);
                _replacementCommits.Remove(transactionId);
                _bulkReplacementCommits.Remove(transactionId);
            }
        }
        return ValueTask.CompletedTask;
    }

    private void Publish(PendingRdfState pending)
    {
        _view = pending.View;
        _version = pending.Version;
        DatabaseProfileWorkTelemetry.Record("rdf", "publish", "state-publication", 1);
        _commandReplays = pending.CommandReplays;
        _history[pending.Version.Value] = pending.View;
        TrimHistory();
    }

    public ValueTask FlushPhysicalStateAsync(CancellationToken cancellationToken = default) =>
        _physical.FlushAsync(cancellationToken);

    public ValueTask AbandonPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _physical.Abandon(transactionId);
        _stagedPhysical.Remove(transactionId);
        lock (_gate)
        {
            _timestampHistory?.Abandon(transactionId);
            _preparedTransactions.Remove(transactionId);
            _pendingStates.Remove(transactionId);
            if (_replacementCommits.Remove(transactionId, out Guid replacementId))
            {
                _bulkReplacementCommits.Remove(transactionId);
                _physical.AbandonReplacement(replacementId);
            }
        }
        return ValueTask.CompletedTask;
    }

    private sealed record PendingRdfState(DatabaseVersion Version, RdfReadView View,
        ImmutableDictionary<Guid, RdfCommandReplay> CommandReplays);

    private RdfTransaction PreparedTransaction(Guid transactionId, ReadOnlyMemory<byte> payload)
    {
        lock (_gate)
        {
            if (_preparedTransactions.TryGetValue(transactionId, out RdfTransaction? transaction))
            {
                return transaction;
            }
        }

        return Decode(transactionId, payload.Span);
    }

    internal RdfQuad[] CaptureRebuildSnapshot()
    {
        lock (_gate)
        {
            return _view.Quads.ToArray();
        }
    }

    internal RdfPhysicalSnapshot CapturePhysicalSnapshot() => _physical.CaptureSnapshot();

    // Reuse an existing immutable primary index without building or copying a result set.
    internal IReadOnlySet<RdfQuad>? ScanIndexedCandidates(RdfQuadPattern pattern, DatabaseVersion version)
    {
        var view = ReadViewCaptured(version);
        var empty = ImmutableHashSet<RdfQuad>.Empty;
        // Match physical access-family precedence so changing the source does not
        // change which candidates are charged against the request's work budget.
        return RdfPhysicalAccessPath.SelectedFamily(pattern) switch
        {
            RdfPhysicalAccessFamily.Subject => view.BySubject.GetValueOrDefault(pattern.Subject!, empty),
            RdfPhysicalAccessFamily.Object => view.ByObject.GetValueOrDefault(pattern.Object!, empty),
            RdfPhysicalAccessFamily.Predicate => view.ByPredicate.GetValueOrDefault(pattern.Predicate!, empty),
            RdfPhysicalAccessFamily.Graph => view.ByGraph.GetValueOrDefault(pattern.Graph!, empty),
            RdfPhysicalAccessFamily.DefaultGraph => view.DefaultGraph,
            _ => null
        };
    }

    internal IAsyncEnumerable<(IReadOnlyList<RdfQuad> Quads, bool Complete)> ReadPhysicalScanPagesAsync(
        RdfPhysicalSnapshot snapshot, RdfQuadPattern pattern, int pageSize, int maximumExaminedQuads, CancellationToken cancellation,
        Action? chargeCandidate = null) =>
        _physical.ReadScanPagesAsync(snapshot, pattern, pageSize, maximumExaminedQuads, cancellation, chargeCandidate);

    internal ValueTask<RdfPhysicalQueryPage> ReadPhysicalPageAsync(RdfPhysicalSnapshot snapshot,
        RdfQuadPattern pattern, int pageSize, AppendMergeCursor<string, byte[]>? cursor,
        CancellationToken cancellationToken) =>
        _physical.ReadPageAsync(snapshot, pattern, pageSize, cursor, cancellationToken);

    internal ValueTask BuildReplacementAsync(Guid replacementId, IEnumerable<RdfQuad> quads,
        CancellationToken cancellationToken) => _physical.BuildReplacementAsync(replacementId, quads,
            Math.Max(1, CurrentVersion.Value), cancellationToken);

    internal void RegisterReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_gate)
        {
            _replacementCommits.Add(transactionId, replacementId);
        }
    }

    internal void RegisterBulkReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_gate)
        {
            _replacementCommits.Add(transactionId, replacementId);
            _bulkReplacementCommits.Add(transactionId);
        }
    }

    internal void AbandonReplacement(Guid transactionId, Guid replacementId)
    {
        lock (_gate)
        {
            _replacementCommits.Remove(transactionId);
            _bulkReplacementCommits.Remove(transactionId);
        }
        _physical.Abandon(transactionId);
        _physical.AbandonReplacement(replacementId);
    }

    internal RdfSnapshotLease AcquireSnapshot(DatabaseVersion version)
    {
        lock (_gate)
        {
            if (_maintenanceAdmission)
            {
                throw new InvalidOperationException("RDF physical maintenance is in progress; retry snapshot acquisition after it finishes.");
            }
            if (!_history.ContainsKey(version.Value))
            {
                throw new RdfSnapshotTooOldException(version);
            }

            _leases[version.Value] = _leases.GetValueOrDefault(version.Value) + 1;
            return new(this, version, () => Release(version));
        }
    }

    internal IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern, DatabaseVersion version)
    {
        RdfReadView view = ReadViewCaptured(version);
        return Ordered(FindUnordered(view, pattern));
    }

    internal IReadOnlyList<RdfQuad> FindBounded(RdfQuadPattern pattern, DatabaseVersion version,
        int maximumCount)
    {
        RdfReadView view = ReadViewCaptured(version);
        return FindUnordered(view, pattern).Take(maximumCount).ToArray();
    }

    internal IReadOnlyList<RdfTerm> ReadSparqlGraphNames(DatabaseVersion version, int maximumNames, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumNames);
        cancellation.ThrowIfCancellationRequested();
        var names = new List<RdfTerm>();
        if (maximumNames == 0)
        {
            return names;
        }
        foreach (var entry in ReadViewCaptured(version).ByGraph)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.Value.Count != 0)
            {
                names.Add(entry.Key);
                if (names.Count == maximumNames)
                {
                    break;
                }
            }
        }
        return names;
    }

    internal IReadOnlyList<RdfQuad> ReadSparqlPrefix(RdfQuadPattern pattern, DatabaseVersion version,
        int maximumCount, bool namedOnly, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        cancellation.ThrowIfCancellationRequested();
        if (maximumCount == 0)
        {
            return [];
        }
        var values = new List<RdfQuad>();
        foreach (var quad in ReadViewCaptured(version).Candidates(pattern))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Matches(quad, pattern) || namedOnly && quad.Graph is null)
            {
                continue;
            }
            values.Add(quad);
            if (values.Count == maximumCount)
            {
                break;
            }
        }
        return Ordered(values);
    }

    private void Release(DatabaseVersion version)
    {
        lock (_gate)
        {
            if (_leases[version.Value] == 1)
            {
                _leases.Remove(version.Value);
            }
            else
            {
                _leases[version.Value]--;
            }

            TrimHistory();
        }
    }

    private void TrimHistory()
    {
        int capacity = IsDistributed() ? DistributedSnapshotAcquisition.MaximumVersions : 1;
        foreach (long version in _history.Keys.Reverse().Skip(capacity).Where(value => !_leases.ContainsKey(value)).ToArray())
        {
            _history.Remove(version);
        }
    }

    private static IEnumerable<CommittedChangeRoutingKey> RoutingKeys(RdfQuad quad)
    {
        yield return new("rdf.subject", quad.Subject.ToString()!);
        yield return new("rdf.predicate", quad.Predicate.Value);
        if (quad.Graph is not null)
        {
            yield return new("rdf.graph", quad.Graph.ToString()!);
        }
    }

    private static bool Matches(RdfQuad quad, RdfQuadPattern pattern) =>
        (pattern.Subject is null || Equals(pattern.Subject, quad.Subject)) &&
        (pattern.Predicate is null || Equals(pattern.Predicate, quad.Predicate)) &&
        (pattern.Object is null || Equals(pattern.Object, quad.Object)) &&
        (pattern.Graph is null || Equals(pattern.Graph, quad.Graph)) &&
        (!pattern.MatchDefaultGraphOnly || quad.Graph is null);

    private static IReadOnlyList<RdfQuad> Ordered(IEnumerable<RdfQuad> quads) => quads
        .OrderBy(quad => quad.Subject, RdfTermComparer.Instance)
        .ThenBy(quad => quad.Predicate.Value, StringComparer.Ordinal)
        .ThenBy(quad => quad.Object, RdfTermComparer.Instance)
        .ThenBy(quad => quad.Graph, RdfTermComparer.Instance).ToArray();

    private sealed class RdfTermComparer : IComparer<RdfTerm?>
    {
        public static RdfTermComparer Instance { get; } = new();

        public int Compare(RdfTerm? left, RdfTerm? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int kind = Kind(left).CompareTo(Kind(right));
            if (kind != 0)
            {
                return kind;
            }

            return (left, right) switch
            {
                (RdfBlankNode a, RdfBlankNode b) => StringComparer.Ordinal.Compare(a.Identifier, b.Identifier),
                (RdfIri a, RdfIri b) => StringComparer.Ordinal.Compare(a.Value, b.Value),
                (RdfLiteral a, RdfLiteral b) => CompareLiteral(a, b),
                (RdfTripleTerm a, RdfTripleTerm b) => CompareTriple(a, b),
                _ => throw new ArgumentOutOfRangeException(nameof(left))
            };
        }

        private int CompareTriple(RdfTripleTerm left, RdfTripleTerm right)
        {
            int subject = Compare(left.Subject, right.Subject);
            if (subject != 0)
            {
                return subject;
            }

            int predicate = StringComparer.Ordinal.Compare(left.Predicate.Value, right.Predicate.Value);
            return predicate != 0 ? predicate : Compare(left.Object, right.Object);
        }

        private static int CompareLiteral(RdfLiteral left, RdfLiteral right)
        {
            int lexical = StringComparer.Ordinal.Compare(left.LexicalForm, right.LexicalForm);
            if (lexical != 0)
            {
                return lexical;
            }

            int datatype = StringComparer.Ordinal.Compare(left.Datatype, right.Datatype);
            return datatype != 0 ? datatype : StringComparer.Ordinal.Compare(left.Language, right.Language);
        }

        private static int Kind(RdfTerm term) => term switch
        {
            RdfBlankNode => 0,
            RdfIri => 1,
            RdfLiteral => 2,
            RdfTripleTerm => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(term))
        };
    }

    private sealed partial record RdfReadView(
        ImmutableHashSet<RdfQuad> Quads,
        ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>> BySubject,
        ImmutableDictionary<RdfIri, ImmutableHashSet<RdfQuad>> ByPredicate,
        ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>> ByObject,
        ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>> ByGraph,
        ImmutableHashSet<RdfQuad> DefaultGraph)
    {
        private const int MaximumOrderedPatternCacheEntries = 256;
        private const int MaximumOrderedPredicateCacheEntries = 32;
        private const int MaximumOrderedCacheQuads = 1_000_000;
        private const int MaximumExactCountCacheEntries = 4_096;
        private const int MaximumGroupedCountCacheEntries = 32;
        private const int MaximumPredicatePartitions = 32;
        private const int MaximumPredicatePartitionEntries = 1_000_000;
        private const int PredicatePartitionAdmissionLookups = 8;
        private readonly ConcurrentDictionary<(RdfIri Predicate, bool Descending), IReadOnlyList<RdfQuad>>
            _orderedPredicateSubjects = new();

        private readonly ConcurrentDictionary<RdfOrderedSubjectPatternKey, IReadOnlyList<RdfQuad>>
            _orderedPatternSubjects = new();

        private readonly ConcurrentDictionary<RdfPredicateObjectGraphKey, int>
            _predicateObjectGraphCounts = new();

        private readonly ConcurrentDictionary<RdfIri, IReadOnlyDictionary<RdfTerm, int>>
            _defaultGraphPredicateObjectCounts = new();

        private readonly ConcurrentDictionary<RdfIri, int> _predicatePartitionLookups = new();
        private readonly ConcurrentDictionary<RdfIri, Lazy<RdfPredicatePartitions>>
            _predicatePartitions = new();

        private readonly object _orderedCacheGate = new();
        private int _orderedCachedQuads;
        private readonly object _predicatePartitionGate = new();
        private int _predicatePartitionEntries;

        public static RdfReadView Empty
        {
            get;
        }
        = new(ImmutableHashSet<RdfQuad>.Empty,
            ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>>.Empty,
            ImmutableDictionary<RdfIri, ImmutableHashSet<RdfQuad>>.Empty,
            ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>>.Empty,
            ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>>.Empty, ImmutableHashSet<RdfQuad>.Empty);

        public static RdfReadView Build(IEnumerable<RdfQuad> source) =>
            Empty.Apply(source.Select(quad => new AssertRdfQuad(quad)));

        public IReadOnlySet<RdfQuad> Candidates(RdfQuadPattern pattern)
            => Candidates(pattern.Subject, pattern.Predicate, pattern.Object, pattern.Graph,
                pattern.MatchDefaultGraphOnly);

        public IReadOnlyList<RdfQuad> ByPredicateOrderedSubject(RdfIri predicate, bool descending)
        {
            var key = (predicate, descending);
            if (_orderedPredicateSubjects.TryGetValue(key, out IReadOnlyList<RdfQuad>? cached))
            {
                return cached;
            }

            IEnumerable<RdfQuad> values = Get(ByPredicate, predicate);
            RdfQuad[] ordered = (descending
                ? values.OrderByDescending(quad => quad.Subject, RdfSparql.RdfTermOrderComparer.Instance)
                : values.OrderBy(quad => quad.Subject, RdfSparql.RdfTermOrderComparer.Instance)).ToArray();
            lock (_orderedCacheGate)
            {
                if (_orderedPredicateSubjects.TryGetValue(key, out cached))
                {
                    return cached;
                }

                if (_orderedPredicateSubjects.Count >= MaximumOrderedPredicateCacheEntries ||
                    ordered.Length > MaximumOrderedCacheQuads - _orderedCachedQuads)
                {
                    return ordered;
                }

                _orderedPredicateSubjects[key] = ordered;
                _orderedCachedQuads += ordered.Length;
                return ordered;
            }
        }

        public IReadOnlyList<RdfQuad> ByPatternOrderedSubject(RdfQuadPattern pattern, bool descending)
        {
            var key = new RdfOrderedSubjectPatternKey(pattern.Subject, pattern.Predicate, pattern.Object,
                pattern.Graph, pattern.MatchDefaultGraphOnly, descending);
            if (_orderedPatternSubjects.TryGetValue(key, out IReadOnlyList<RdfQuad>? cached))
            {
                return cached;
            }

            IEnumerable<RdfQuad> values = Candidates(pattern).Where(quad => Matches(quad, pattern));
            RdfQuad[] ordered = (descending
                ? values.OrderByDescending(quad => quad.Subject, RdfSparql.RdfTermOrderComparer.Instance)
                : values.OrderBy(quad => quad.Subject, RdfSparql.RdfTermOrderComparer.Instance)).ToArray();
            lock (_orderedCacheGate)
            {
                if (_orderedPatternSubjects.TryGetValue(key, out cached))
                {
                    return cached;
                }

                if (_orderedPatternSubjects.Count >= MaximumOrderedPatternCacheEntries ||
                    ordered.Length > MaximumOrderedCacheQuads - _orderedCachedQuads)
                {
                    return ordered;
                }

                _orderedPatternSubjects[key] = ordered;
                _orderedCachedQuads += ordered.Length;
                return ordered;
            }
        }

        public int Count(RdfIri predicate, RdfTerm obj, RdfTerm? graph)
        {
            var key = new RdfPredicateObjectGraphKey(predicate, obj, graph);
            if (_predicateObjectGraphCounts.TryGetValue(key, out int cached))
            {
                return cached;
            }

            int count = 0;
            foreach (RdfQuad quad in Get(ByObject, key.Object))
            {
                if (quad.Predicate == key.Predicate && Equals(quad.Graph, key.Graph))
                {
                    count++;
                }
            }

            if (_predicateObjectGraphCounts.Count >= MaximumExactCountCacheEntries)
            {
                _predicateObjectGraphCounts.Clear();
            }

            return _predicateObjectGraphCounts.GetOrAdd(key, count);
        }

        public IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate)
        {
            if (_defaultGraphPredicateObjectCounts.TryGetValue(predicate, out var cached))
            {
                return cached;
            }

            IReadOnlyDictionary<RdfTerm, int> counts = BuildDefaultGraphObjectCounts(predicate);
            return _defaultGraphPredicateObjectCounts.Count < MaximumGroupedCountCacheEntries
                ? _defaultGraphPredicateObjectCounts.GetOrAdd(predicate, counts)
                : counts;
        }

        private IReadOnlyDictionary<RdfTerm, int> BuildDefaultGraphObjectCounts(RdfIri predicate)
        {
            var counts = new Dictionary<RdfTerm, int>();
            foreach (RdfQuad quad in Get(ByPredicate, predicate))
            {
                if (quad.Graph is null)
                {
                    counts[quad.Object] = counts.GetValueOrDefault(quad.Object) + 1;
                }
            }

            return counts;
        }

        public IReadOnlySet<RdfQuad> Candidates(RdfTerm? subject, RdfIri? predicate,
            RdfTerm? obj, RdfTerm? graph, bool matchDefaultGraphOnly)
        {
            IReadOnlySet<RdfQuad> selected = Quads;
            int smallest = Int32.MaxValue;
            if (subject is not null)
            {
                Select(Get(BySubject, subject), ref selected, ref smallest);
            }

            if (predicate is not null)
            {
                Select(Get(ByPredicate, predicate), ref selected, ref smallest);
            }

            if (obj is not null)
            {
                Select(Get(ByObject, obj), ref selected, ref smallest);
            }

            if (graph is not null)
            {
                Select(Get(ByGraph, graph), ref selected, ref smallest);
            }

            if (matchDefaultGraphOnly)
            {
                Select(DefaultGraph, ref selected, ref smallest);
            }
            // A selective existing bucket must not pay to partition a larger predicate.
            // Completed partitions remain reusable; admission and memory limits still apply.
            if (predicate is not null && (subject is not null || obj is not null) &&
                TryPredicatePartitions(predicate, Get(ByPredicate, predicate).Count <= smallest,
                    out RdfPredicatePartitions? partitions) &&
                partitions is not null)
            {
                if (subject is not null)
                {
                    Select(Get(partitions.BySubject, subject), ref selected, ref smallest);
                }

                if (obj is not null)
                {
                    Select(Get(partitions.ByObject, obj), ref selected, ref smallest);
                }
            }
            return selected;
        }

        private bool TryPredicatePartitions(RdfIri predicate, bool allowBuild, out RdfPredicatePartitions? partitions)
        {
            if (_predicatePartitions.TryGetValue(predicate, out Lazy<RdfPredicatePartitions>? existing) &&
                (allowBuild || existing.IsValueCreated))
            {
                partitions = existing.Value;
                return partitions.Available;
            }
            if (!allowBuild)
            {
                partitions = null;
                return false;
            }
            int observations = _predicatePartitionLookups.AddOrUpdate(predicate, 1,
                static (_, count) => count + 1);
            if (observations < PredicatePartitionAdmissionLookups)
            {
                partitions = null;
                return false;
            }
            Lazy<RdfPredicatePartitions> admitted;
            lock (_predicatePartitionGate)
            {
                if (!_predicatePartitions.TryGetValue(predicate, out admitted!))
                {
                    if (_predicatePartitions.Count >= MaximumPredicatePartitions)
                    {
                        partitions = null;
                        return false;
                    }
                    admitted = new(() => BuildPredicatePartitions(predicate),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    _predicatePartitions.AddOrUpdate(predicate, admitted, static (_, current) => current);
                }
            }
            partitions = admitted.Value;
            return partitions.Available;
        }

        private RdfPredicatePartitions BuildPredicatePartitions(RdfIri predicate)
        {
            IReadOnlySet<RdfQuad> source = Get(ByPredicate, predicate);
            int retainedEntries = checked(source.Count * 2);
            lock (_predicatePartitionGate)
            {
                if (retainedEntries > MaximumPredicatePartitionEntries - _predicatePartitionEntries)
                {
                    return RdfPredicatePartitions.Unavailable;
                }

                _predicatePartitionEntries += retainedEntries;
            }
            var subjects = new Dictionary<RdfTerm, HashSet<RdfQuad>>();
            var objects = new Dictionary<RdfTerm, HashSet<RdfQuad>>();
            foreach (RdfQuad quad in source)
            {
                Add(subjects, quad.Subject, quad);
                Add(objects, quad.Object, quad);
            }
            PredicatePartitionBuilt.Value?.Invoke();
            return new(Freeze(subjects), Freeze(objects), true);
        }

        private static void Select(IReadOnlySet<RdfQuad> candidate,
            ref IReadOnlySet<RdfQuad> selected, ref int smallest)
        {
            if (candidate.Count >= smallest)
            {
                return;
            }

            selected = candidate;
            smallest = candidate.Count;
        }

        private static void Add<TKey>(Dictionary<TKey, HashSet<RdfQuad>> index, TKey key, RdfQuad quad)
            where TKey : notnull
        {
            if (!index.TryGetValue(key, out HashSet<RdfQuad>? values))
            {
                index[key] = values = [];
            }

            values.Add(quad);
        }

        private static ImmutableDictionary<TKey, ImmutableHashSet<RdfQuad>> Freeze<TKey>(
            Dictionary<TKey, HashSet<RdfQuad>> index) where TKey : notnull =>
            index.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutableHashSet());

        private static IReadOnlySet<RdfQuad> Get<TKey, TSet>(
            IReadOnlyDictionary<TKey, TSet> index, TKey key) where TKey : notnull where TSet : IReadOnlySet<RdfQuad> =>
            index.TryGetValue(key, out TSet? values) ? values : EmptySet;

        private static IReadOnlySet<RdfQuad> EmptySet { get; } = new HashSet<RdfQuad>();
    }

    internal static readonly AsyncLocal<Action?> PredicatePartitionBuilt = new();

    private readonly record struct RdfPredicateObjectGraphKey(RdfIri Predicate, RdfTerm Object, RdfTerm? Graph);

    private sealed record RdfPredicatePartitions(
        ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>> BySubject,
        ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>> ByObject,
        bool Available)
    {
        public static RdfPredicatePartitions Unavailable
        {
            get;
        }
        = new(
            ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>>.Empty,
            ImmutableDictionary<RdfTerm, ImmutableHashSet<RdfQuad>>.Empty, false);
    }

    private readonly record struct RdfOrderedSubjectPatternKey(RdfTerm? Subject, RdfIri? Predicate,
        RdfTerm? Object, RdfTerm? Graph, bool MatchDefaultGraphOnly, bool Descending);
}
