using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Kernel;
using EntityForge.Db;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal interface ISemanticCommitDecoder
{
    DurableDatabaseCommit Decode(DatabaseVersion version, ReadOnlySpan<byte> payload);
}

internal sealed partial class SemanticDurabilityRuntime : IEntityOperationBindingAdmission, IEntityOperationCompactionProfile, IDatabaseProfileDeferredCheckpoint,
    IDatabaseProfileRetainedPhysicalPreparation, IDatabaseProfileChangePlanner, IDatabaseProfilePublicationRuntime, IDatabaseProfileOperationRetention,
    IDatabaseProfileCheckpointPublicationObserver, IDatabaseProfileCheckpointPageReuse, IDatabaseProfileCheckpointPriority,
    IDatabaseProfileIncrementalCheckpointCompaction, IDatabaseProfilePhysicalMutationPlanner, IDatabaseProfileDurabilityBarrier,
    IDatabaseProfileOrderedPreparationRuntime, IDatabaseProfileGroupedPhysicalMutationPlanner, IDatabaseProfileCheckpointPreparation
{
    EntityOperationCatalog IEntityOperationCompactionProfile.OperationCatalog => Operations;

    EntityOperationBinding? IEntityOperationCompactionProfile.ReadOperationTransition(ReadOnlyMemory<byte> payload) =>
        !payload.IsEmpty && payload.Span[0] == (byte)DurableDatabaseCommitKind.ApplicationOperation
            ? _decoder.Decode(DatabaseVersion.Initial, payload.Span).ApplicationOperation : null;

    EntityOperationCommitted IEntityOperationCompactionProfile.DescribeCommitted(EntityOperationBinding binding, DatabaseCommitReceipt receipt)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan);
        if (metadata.OperationId != binding.OperationId || metadata.DatabaseIncarnation != receipt.DatabaseIncarnation)
        {
            throw new EntityForgeDbIdempotencyException(binding.OperationId);
        }

        var session = SemanticEntityDeliveryCodec.Session(metadata, payload);
        var native = new SemanticCommitCodec().Encode(DurableDatabaseCommit.ForSemanticTransaction([new(session, LocalOwnerCoverage: metadata.LocalOwnerCoverage)]));
        var result = new SessionCommitResult(session.Start.SessionId, receipt.Version, SessionCommitOutcome.Applied,
            receipt.DatabaseIncarnation, receipt.TransactionId, receipt.ChangeCursor, receipt.EffectCount ?? throw new InvalidDataException("A native commit requires its effect count."),
            new SemanticSourceCommitProgress(session.Start.Source.SourceSystem, session.Start.Source.SourcePartition,
                session.Start.Source.Position, receipt.Version));
        return EntityOperationCommitted.Create(binding.Plan, SemanticEntityDeliveryCodec.PreparedIdentity(metadata, payload),
            CodecId, CodecVersion, native, receipt, SemanticSessionReceiptCodec.Encode(result));
    }

    internal static readonly AsyncLocal<Action?> BeforePhysicalAbandon = new();

    public Guid? BindingIdentity(ReadOnlyMemory<byte> payload) =>
        !payload.IsEmpty && payload.Span[0] == (byte)DurableDatabaseCommitKind.ApplicationOperation
            ? _decoder.Decode(DatabaseVersion.Initial, payload.Span).ApplicationOperation!.OperationId : null;

    private readonly ISemanticCommitDecoder _decoder;

    public Guid? OperationIdentity(Guid transactionId, ReadOnlyMemory<byte> payload) =>
        BindingIdentity(payload) ?? (!payload.IsEmpty && payload.Span[0] == (byte)DurableDatabaseCommitKind.SemanticTransaction
            ? transactionId : null);

    internal EntityOperationCatalog Operations { get; } = new();

    internal EntityMappingCatalog Mappings { get; } = new(DatabaseProfileId.Semantic);

    private readonly SemanticProfileCheckpointStore _checkpointCodec;
    private ISemanticProfileStateParticipant? _participant;
    private bool _recovering;
    private Guid? _groupPublicationFinal;

    public bool SupportsAtomicGroupPublication => true;

    private readonly SemanticPhysicalProjectionStore _physicalWriter;
    private readonly SemanticPhysicalQueryStore _physicalReader;
    private readonly Dictionary<Guid, SemanticCommittedEffects> _plannedEffects = [];
    private readonly Dictionary<Guid, SemanticSourceMetadataChanges> _plannedSourceMetadata = [];
    private readonly Dictionary<Guid, PropertyStorageCatalogCheckpoint> _plannedPropertyStorage = [];
    private readonly Dictionary<Guid, DurableDatabaseCommit> _plannedCommits = [];
    private readonly Dictionary<Guid, (PhysicalMaintenanceKind Kind, PhysicalMaintenanceTarget Target)>
        _plannedMaintenance = [];

    private readonly HashSet<Guid> _physicallyStaged = [];
    private readonly Dictionary<Guid, Guid> _orderedReplacementCommits = [];
    private readonly Dictionary<Guid, Guid> _appendReplacementCommits = [];
    private readonly Dictionary<Guid, IReadOnlyList<(PhysicalMaintenanceTarget Target, Guid ReplacementId)>>
        _batchReplacementCommits = [];

    private readonly HashSet<Guid> _persistedPhysicalTransactions = [];

    public SemanticDurabilityRuntime(
        ISemanticCommitDecoder decoder,
        SemanticProfileCheckpointStore checkpointCodec,
        SemanticPhysicalAccessPath physicalAccessPath)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _checkpointCodec = checkpointCodec ?? throw new ArgumentNullException(nameof(checkpointCodec));
        ArgumentNullException.ThrowIfNull(physicalAccessPath);
        _physicalWriter = new(physicalAccessPath);
        _physicalReader = new(physicalAccessPath);
    }

    public DatabaseProfileId Profile => DatabaseProfileId.Semantic;

    public string CodecId => SemanticCommitCodec.CodecId;

    public int CodecVersion => SemanticCommitCodec.CodecVersion;

    public string CheckpointCodecId => SemanticProfileCheckpointStore.SemanticCheckpointCodecId;

    public int CheckpointCodecVersion => SemanticProfileCheckpointStore.SemanticCheckpointCodecVersion;

    public bool CanReadCheckpointVersion(int codecVersion) => SemanticProfileCheckpointStore.CanReadCodecVersion(codecVersion);

    public void Bind(ISemanticProfileStateParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (_participant is not null)
        {
            throw new InvalidOperationException("The Semantic profile participant is already bound.");
        }

        _participant = participant;
    }

    public void BeginRecovery() => _recovering = true;

    internal ValueTask<IDisposable> EnterMaintenancePublicationAsync(CancellationToken cancellationToken) =>
        Participant().EnterMaintenancePublicationAsync(cancellationToken);

    public void EndRecovery() => _recovering = false;

    public async ValueTask<DatabaseProfileChangePlan> PlanCommittedChangesAsync(
        DatabaseVersion version,
        Guid transactionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        DurableDatabaseCommit commit = DecodeLogicalCommit(transactionId, version, payload);
        foreach (var partitionRead in commit.Contributions.Select(c => c.Read).OfType<SemanticPartitionReadEvidence>())
        {
            foreach (var manifest in partitionRead.Mappings ?? throw new InvalidDataException("Missing selected mapping dependencies."))
            {
                Mappings.RequireCurrent(manifest);
            }
        }

        var binding = Operations.Get(transactionId, operationIdentity: commit.Kind == DurableDatabaseCommitKind.SemanticTransaction);
        if (commit.Contributions.Any(c => c.LocalOwnerCoverage is not null) &&
            (binding is null || commit.Contributions.Count != 1))
        {
            throw new InvalidDataException("Local owner coverage requires its exact mapped operation binding.");
        }

        if (binding?.Aborted == true)
        {
            throw new EntityOperationAbortedException(transactionId);
        }

        if (binding?.Committed is not null)
        {
            throw new EntityForgeDbIdempotencyException(transactionId);
        }

        if (binding is not null && (commit.Kind != DurableDatabaseCommitKind.SemanticTransaction ||
            commit.IsInitialBulkLoad || commit.UsesAuthoritativeVersion ||
            !SemanticEntityDeliveryCodec.MatchesSession(binding.Plan, commit.Session!)))
        {
            throw new EntityForgeDbIdempotencyException(transactionId);
        }

        if (binding is not null && commit.Kind == DurableDatabaseCommitKind.SemanticTransaction &&
            !Equals(EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan).Metadata.LocalOwnerCoverage,
                commit.Contributions.Single().LocalOwnerCoverage))
        {
            throw new EntityForgeDbIdempotencyException(transactionId);
        }

        if (commit.Kind is DurableDatabaseCommitKind.MappingRegistration)
        {
            Mappings.Validate(commit.MappingRegistration!);
        }

        if (commit.Kind is DurableDatabaseCommitKind.ApplicationOperation)
        {
            Operations.Validate(commit.ApplicationOperation!);
        }

        if (commit.Kind is DurableDatabaseCommitKind.PhysicalMaintenance)
        {
            _plannedMaintenance.Add(transactionId,
                (commit.PhysicalMaintenanceKind!.Value, commit.PhysicalMaintenanceTarget));
            _plannedCommits.Add(transactionId, commit);
            return DatabaseProfileChangePlan.System();
        }
        if (commit.Kind is DurableDatabaseCommitKind.ContributionRevision &&
            commit.ContributionRevision!.Kind is not SemanticContributionRevisionChangeKind.Publish)
        {
            _ = await Participant().PlanCommittedChangesAsync(commit, cancellationToken).ConfigureAwait(false);
            _plannedCommits.Add(transactionId, commit);
            return DatabaseProfileChangePlan.System();
        }
        if (commit.Kind is not (DurableDatabaseCommitKind.SemanticTransaction or
            DurableDatabaseCommitKind.GovernanceRule or
            DurableDatabaseCommitKind.VocabularyPublication or
            DurableDatabaseCommitKind.ContributionRevision))
        {
            _plannedCommits.Add(transactionId, commit);
            return DatabaseProfileChangePlan.System();
        }
        SemanticCommittedChangePlan changes = await Participant().PlanCommittedChangesAsync(commit, cancellationToken)
            .ConfigureAwait(false);
        if (binding is not null)
        {
            try
            {
                var (metadata, _) = EntityRecordDeliveryCodec.Decode<SemanticEntityDeliveryCodec.Metadata>(binding.Plan);
                if (metadata.ChangePageLengths is null)
                {
                    foreach (var manifest in metadata.Manifests)
                    {
                        Mappings.RequireCurrent(manifest);
                    }

                    SemanticEntityDeliveryCodec.ValidateCompleteChanges(commit.Session!,
                        new(metadata.Entities, metadata.Depth, metadata.Bytes),
                        changes.CompleteChanges ?? throw new InvalidOperationException("Bound Semantic execution requires the complete expanded write."));
                }
            }
            catch (Exception primary)
            {
                // Planning has cached an unpublished candidate. Admission failure must release it.
                try
                {
                    await Participant().AbandonPreparedStatesAsync([transactionId], CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    EntityFailureDiagnostics.Attach(primary, cleanupFailure);
                }
                throw;
            }
        }

        _plannedCommits.Add(transactionId, commit);
        if (changes.Effects is not null)
        {
            _plannedEffects[transactionId] = changes.Effects;
        }

        if (changes.SourceMetadata.Memberships.Count != 0 || changes.SourceMetadata.Progress.Count != 0)
        {
            _plannedSourceMetadata[transactionId] = changes.SourceMetadata;
        }

        if (changes.PropertyStorage is not null)
        {
            _plannedPropertyStorage[transactionId] = changes.PropertyStorage;
        }

        return DatabaseProfileChangePlan.UserData(changes.Payload);
    }

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PreparePhysicalMutationsAsync(
        DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (_plannedMaintenance.TryGetValue(transactionId,
                out (PhysicalMaintenanceKind Kind, PhysicalMaintenanceTarget Target) maintenance))
        {
            OrderedPageTreeStage? maintenanceStage =
                _batchReplacementCommits.TryGetValue(transactionId, out var batch)
                ? await _physicalWriter.PrepareReplacementBatchAsync(transactionId, batch, cancellationToken)
                    .ConfigureAwait(false)
                : _orderedReplacementCommits.TryGetValue(transactionId, out Guid replacementId)
                ? await _physicalWriter.PrepareOrderedReplacementAsync(transactionId, replacementId,
                    cancellationToken).ConfigureAwait(false)
                : _appendReplacementCommits.TryGetValue(transactionId, out replacementId)
                ? await _physicalWriter.PrepareAppendReplacementAsync(transactionId, replacementId,
                    cancellationToken).ConfigureAwait(false)
                : maintenance.Kind is PhysicalMaintenanceKind.Reindex
                ? await _physicalWriter.PrepareVacuumAsync(transactionId, maintenance.Target,
                    GetPhysicalHistoryRetentionFloor(), cancellationToken)
                    .ConfigureAwait(false)
                : await _physicalWriter.PrepareMaintenanceAsync(transactionId, GetPhysicalHistoryRetentionFloor(), cancellationToken)
                    .ConfigureAwait(false);
            if (maintenanceStage is null)
            {
                return Array.Empty<DatabasePhysicalPageMutation>();
            }

            _physicallyStaged.Add(transactionId);
            return maintenanceStage.PhysicalMutations;
        }
        if (_plannedCommits.TryGetValue(transactionId, out DurableDatabaseCommit? plannedCommit) &&
            plannedCommit.Kind is DurableDatabaseCommitKind.PropertyStorage)
        {
            OrderedPageTreeStage? placementStage = await _physicalWriter.PreparePropertyStorageChangeAsync(
                transactionId, version.Value, plannedCommit.PropertyStorage!, cancellationToken)
                .ConfigureAwait(false);
            if (placementStage is null)
            {
                return Array.Empty<DatabasePhysicalPageMutation>();
            }

            _physicallyStaged.Add(transactionId);
            return placementStage.PhysicalMutations;
        }
        if (!_plannedEffects.TryGetValue(transactionId, out SemanticCommittedEffects? effects) &&
            !_plannedSourceMetadata.ContainsKey(transactionId))
        {
            return Array.Empty<DatabasePhysicalPageMutation>();
        }

        PropertyStorageCatalogCheckpoint storage = _plannedPropertyStorage.GetValueOrDefault(transactionId) ??
            new(default, []);
        OrderedPageTreeStage? stage = await _physicalWriter.PrepareAsync(transactionId, version.Value, effects ?? new([]), storage,
            cancellationToken, sourceMetadata: _plannedSourceMetadata.GetValueOrDefault(transactionId)).ConfigureAwait(false);
        if (stage is null)
        {
            return Array.Empty<DatabasePhysicalPageMutation>();
        }

        _physicallyStaged.Add(transactionId);
        return stage.PhysicalMutations;
    }

    public async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>>>
        PreparePhysicalMutationGroupAsync(IReadOnlyList<DatabaseProfilePhysicalPreparation> preparations,
            CancellationToken cancellationToken = default)
    {
        if (preparations.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>>();
        }

        DatabaseProfilePhysicalPreparation final = preparations[^1];
        _groupPublicationFinal = final.TransactionId;
        SemanticCommittedEffect[] effects = preparations.SelectMany(preparation =>
            _plannedEffects.GetValueOrDefault(preparation.TransactionId)?.Effects ?? []).ToArray();
        Dictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>> result = preparations.ToDictionary(
            preparation => preparation.TransactionId,
            _ => (IReadOnlyList<DatabasePhysicalPageMutation>)Array.Empty<DatabasePhysicalPageMutation>());
        // Binding-only groups advance durable metadata, not physical evidence. Preserve
        // their final logical publication boundary without building empty per-member plans.
        if (effects.Length == 0 && !preparations.Any(item => _plannedSourceMetadata.ContainsKey(item.TransactionId)))
        {
            return result;
        }

        PropertyStorageCatalogCheckpoint storage = _plannedPropertyStorage.GetValueOrDefault(
            preparations[0].TransactionId) ?? new(default, []);
        OrderedPageTreeStage? stage = await _physicalWriter.PrepareAsync(final.TransactionId,
            final.Version.Value, new SemanticCommittedEffects(effects), storage, cancellationToken,
            preparations.Select(item => (item.Version.Value,
                _plannedEffects.GetValueOrDefault(item.TransactionId) ?? new SemanticCommittedEffects([]))).ToArray(),
            sourceMetadataGroup: preparations.Select(item => (item.Version.Value,
                _plannedSourceMetadata.GetValueOrDefault(item.TransactionId) ?? SemanticSourceMetadataChanges.Empty)).ToArray()).ConfigureAwait(false);
        if (stage is not null)
        {
            _physicallyStaged.Add(final.TransactionId);
            result[final.TransactionId] = stage.PhysicalMutations;
        }
        return result;
    }

    public async ValueTask PublishPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        if (_physicallyStaged.Remove(transactionId))
        {
            await _physicalWriter.PersistAsync(transactionId, cancellationToken).ConfigureAwait(false);
            _persistedPhysicalTransactions.Add(transactionId);
        }
        else
        {
            _plannedEffects.Remove(transactionId);
            _plannedSourceMetadata.Remove(transactionId);
            _plannedPropertyStorage.Remove(transactionId);
            _plannedMaintenance.Remove(transactionId);
        }
    }

    public ValueTask AbandonPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default) =>
        AbandonPhysicalMutationsCoreAsync(transactionId, false, cancellationToken);

    public ValueTask AbandonUnsubmittedPhysicalMutationsAsync(Guid transactionId,
        CancellationToken cancellationToken = default) =>
        AbandonPhysicalMutationsCoreAsync(transactionId,
            !_plannedMaintenance.ContainsKey(transactionId) &&
            (!_plannedCommits.TryGetValue(transactionId, out var commit) ||
                commit.Kind != DurableDatabaseCommitKind.PropertyStorage), cancellationToken);

    private async ValueTask AbandonPhysicalMutationsCoreAsync(Guid transactionId,
        bool reclaimUnsubmittedTail, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_timestampGate)
        {
            _timestampHistory?.Abandon(transactionId);
        }

        _plannedEffects.Remove(transactionId);
        _plannedSourceMetadata.Remove(transactionId);
        _plannedPropertyStorage.Remove(transactionId);
        _plannedCommits.Remove(transactionId);
        _plannedMaintenance.Remove(transactionId);
        Exception? failure = null;
        void Cleanup(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                if (failure is null)
                {
                    failure = exception;
                }
                else
                {
                    EntityFailureDiagnostics.Attach(failure, exception);
                }
            }
        }
        Cleanup(() => BeforePhysicalAbandon.Value?.Invoke());
        if (_orderedReplacementCommits.Remove(transactionId, out Guid replacementId))
        {
            Cleanup(() => _physicalWriter.AbandonOrderedReplacement(replacementId));
        }

        if (_appendReplacementCommits.Remove(transactionId, out replacementId))
        {
            Cleanup(() => _physicalWriter.AbandonAppendReplacement(replacementId));
        }

        if (_batchReplacementCommits.Remove(transactionId, out var batch))
        {
            foreach ((PhysicalMaintenanceTarget target, Guid batchReplacementId) in batch)
            {
                Cleanup(() => _physicalWriter.AbandonReplacement(target, batchReplacementId));
            }
        }

        if (_physicallyStaged.Remove(transactionId))
        {
            Cleanup(() => _physicalWriter.Abandon(transactionId, reclaimUnsubmittedTail));
        }

        try
        {
            await Participant().AbandonPreparedStatesAsync([transactionId], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (failure is null)
            {
                failure = exception;
            }
            else
            {
                EntityFailureDiagnostics.Attach(failure, exception);
            }
        }
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public ISemanticCandidateSnapshot CaptureCandidateSnapshot(DatabaseVersion? maximumVersion = null) =>
        _physicalReader.CaptureSnapshot(
            _persistedPhysicalTransactions.Count == 1 ? _persistedPhysicalTransactions.Single() : null,
            maximumVersion);

    public DatabaseVersion GetSnapshotRetentionFloor() => Participant().GetSnapshotRetentionFloor();

    public DatabaseVersion GetPhysicalHistoryRetentionFloor() =>
        Participant().GetPhysicalHistoryRetentionFloor();

    public DatabaseVersion GetCurrentVersion() => Participant().GetCurrentVersion();

    public bool HasActiveSnapshotRetention() => Participant().HasActiveSnapshotRetention();

    public async ValueTask ApplyAsync(DatabaseVersion version, Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        DurableDatabaseCommit commit = _plannedCommits.Remove(transactionId, out DurableDatabaseCommit? planned)
            ? planned : DecodeLogicalCommit(transactionId, version, payload);
        var timestampBefore = _timestampHistory is null ? null : TimestampState.TimestampMetadata;
        var timestampEffects = _plannedEffects.GetValueOrDefault(transactionId) ?? new SemanticCommittedEffects([]);
        if (commit.Kind is DurableDatabaseCommitKind.MappingRegistration)
        {
            Mappings.Apply(commit.MappingRegistration!);
        }

        if (commit.Kind is DurableDatabaseCommitKind.ApplicationOperation)
        {
            Operations.Apply(commit.ApplicationOperation!);
        }

        if (_participant is not null)
        {
            await _participant.ApplyAsync(commit, _recovering, cancellationToken).ConfigureAwait(false);
        }

        if (timestampBefore is not null)
        {
            lock (_timestampGate)
            {
                _timestampPublications.Add(transactionId, new(
                new(timestampBefore, TimestampState.TimestampMetadata, timestampEffects, TimestampState.TimestampSession, TimestampState.TimestampSessions), commit.MappingRegistration));
            }
        }
    }

    public async ValueTask PublishStagedStateAsync(Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        if (transactionId != Guid.Empty && _persistedPhysicalTransactions.Remove(transactionId))
        {
            _physicalWriter.Publish(transactionId);
            _plannedEffects.Remove(transactionId);
            _plannedSourceMetadata.Remove(transactionId);
            _plannedPropertyStorage.Remove(transactionId);
            _plannedMaintenance.Remove(transactionId);
            _orderedReplacementCommits.Remove(transactionId);
            _appendReplacementCommits.Remove(transactionId);
            _batchReplacementCommits.Remove(transactionId);
        }
        if (_groupPublicationFinal is not null && _groupPublicationFinal != transactionId)
        {
            return;
        }

        _groupPublicationFinal = null;
        await Participant().PublishStagedStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask StagePreparedStateAsync(DatabaseVersion version, Guid transactionId,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        DurableDatabaseCommit commit = _plannedCommits[transactionId];
        return Participant().StagePreparedStateAsync(commit, cancellationToken);
    }

    public ValueTask AbandonPreparedStatesAsync(IReadOnlyList<Guid> transactionIds,
        CancellationToken cancellationToken = default)
    {
        _groupPublicationFinal = null;
        var preparationIds = new List<Guid>(transactionIds.Count);
        foreach (Guid transactionId in transactionIds)
        {
            _plannedEffects.Remove(transactionId);
            _plannedSourceMetadata.Remove(transactionId);
            _plannedPropertyStorage.Remove(transactionId);
            if (_plannedCommits.Remove(transactionId, out DurableDatabaseCommit? commit) &&
                commit.Kind is DurableDatabaseCommitKind.SemanticTransaction)
            {
                preparationIds.Add(commit.TransactionId);
            }
        }
        return Participant().AbandonPreparedStatesAsync(preparationIds, cancellationToken);
    }

    public ValueTask BeginPreparedPublicationAsync(CancellationToken cancellationToken = default) =>
        Participant().BeginPreparedPublicationAsync(cancellationToken);

    internal ValueTask BuildOrderedReplacementAsync(Guid replacementId, DatabaseVersion retentionFloor,
        CancellationToken cancellationToken) =>
        _physicalWriter.BuildOrderedReplacementAsync(replacementId, retentionFloor, cancellationToken);

    internal void RegisterOrderedReplacement(Guid transactionId, Guid replacementId) =>
        _orderedReplacementCommits.Add(transactionId, replacementId);

    internal void AbandonOrderedReplacement(Guid transactionId, Guid replacementId)
    {
        _orderedReplacementCommits.Remove(transactionId);
        _physicalWriter.AbandonOrderedReplacement(replacementId);
    }

    internal ValueTask BuildAppendReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken) =>
        _physicalWriter.BuildAppendReplacementAsync(target, replacementId, retentionFloor, cancellationToken);

    internal ValueTask CatchUpReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken) =>
        _physicalWriter.CatchUpReplacementAsync(target, replacementId, retentionFloor, cancellationToken);

    internal void RegisterAppendReplacement(Guid transactionId, Guid replacementId) =>
        _appendReplacementCommits.Add(transactionId, replacementId);

    internal void AbandonAppendReplacement(Guid transactionId, Guid replacementId)
    {
        _appendReplacementCommits.Remove(transactionId);
        _physicalWriter.AbandonAppendReplacement(replacementId);
    }

    internal ValueTask FlushReplacementPagesAsync(CancellationToken cancellationToken) =>
        _physicalWriter.FlushReplacementPagesAsync(cancellationToken);

    internal void RegisterReplacementBatch(Guid transactionId,
        IReadOnlyList<(PhysicalMaintenanceTarget Target, Guid ReplacementId)> replacements) =>
        _batchReplacementCommits.Add(transactionId, replacements);

    public ValueTask FlushPhysicalStateAsync(CancellationToken cancellationToken = default) =>
        _physicalWriter.FlushReplacementPagesAsync(cancellationToken);

    public async ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(
        CancellationToken cancellationToken = default)
    {
        var encode = await CaptureCheckpointEncoderAsync(false, null, cancellationToken).ConfigureAwait(false);
        return encode(cancellationToken);
    }

    public async ValueTask<Func<CancellationToken, ReadOnlyMemory<byte>>> CaptureCheckpointEncoderAsync(
        bool preserveSnapshots, IReadOnlySet<Guid>? expired, CancellationToken cancellationToken = default)
    {
        // Capture the compact physical reference and all logical metadata under kernel
        // admission. The encoder below must never consult subsequently published state.
        SemanticCheckpoint checkpoint = await CaptureKeyedCheckpointAsync(cancellationToken).ConfigureAwait(false);
        checkpoint = checkpoint with
        {
            MappingCatalog = Mappings.Capture(),
            AppliedSessions = !preserveSnapshots && expired is not null
                ? checkpoint.AppliedSessions.Where(item => !expired.Contains(item.SessionId)).ToArray()
                : checkpoint.AppliedSessions
        };
        byte[] operations = Operations.Capture();
        cancellationToken.ThrowIfCancellationRequested();
        return token =>
        {
            token.ThrowIfCancellationRequested();
            var content = EntityOperationCodec.WrapCheckpoint(_checkpointCodec.EncodeCurrentPayload(checkpoint, token), operations);
            token.ThrowIfCancellationRequested();
            return content;
        };
    }

    public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        RestoreCheckpointAsync(version, CheckpointCodecVersion, payload, cancellationToken);

    public async ValueTask RestoreCheckpointAsync(DatabaseVersion version, int codecVersion,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var restoredOperations = new EntityOperationCatalog();
        var restoredMappings = new EntityMappingCatalog(Profile);
        SemanticCheckpoint checkpoint = _checkpointCodec.DecodeVersionedPayload(
            EntityOperationCodec.UnwrapCheckpoint(payload, restoredOperations), codecVersion, cancellationToken);
        if (checkpoint.Version != version)
        {
            throw new InvalidDataException("Checkpoint metadata version differs from kernel envelope.");
        }

        bool sourceMetadataComplete = checkpoint.SourceMetadataReference is not null;
        if (checkpoint.SourceMetadataReference is { } reference)
        {
            _physicalWriter.ValidateSourceMetadataReference(reference);
            var metadata = await _physicalWriter.ReadSourceMetadataAsync(reference.Version.Value, cancellationToken).ConfigureAwait(false);
            if (metadata.Memberships.Count != reference.MembershipCount || metadata.Progress.Count != reference.ProgressCount ||
                metadata.Progress.Any(item => item.Version.Value > version.Value))
            {
                throw new InvalidDataException("Compact checkpoint source metadata is incomplete or outside its ceiling.");
            }

            checkpoint = checkpoint with
            {
                SourceMemberships = metadata.Memberships,
                SourceProgress = metadata.Progress,
                SourceMetadataReference = null
            };
        }
        restoredMappings.Restore(checkpoint.MappingCatalog);
        var mappingBytes = restoredMappings.Capture();
        var operationBytes = restoredOperations.Capture();
        cancellationToken.ThrowIfCancellationRequested();
        await Participant().RestoreCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        Mappings.Restore(mappingBytes);
        Operations.Restore(operationBytes);
        _sourceMetadataComplete = sourceMetadataComplete;
    }

    public async ValueTask<ReadOnlyMemory<byte>> CaptureRetentionCheckpointAsync(IReadOnlySet<Guid> expired,
        CancellationToken cancellationToken)
    {
        var encode = await CaptureCheckpointEncoderAsync(false, expired, cancellationToken).ConfigureAwait(false);
        return encode(cancellationToken);
    }

    public void RetentionCheckpointPublished(IReadOnlySet<Guid> expired) => Participant().RetainAppliedSessions(expired);

    internal Func<IDisposable>? MaintenanceAdmission
    {
        get; set;
    }

    internal Func<IDisposable?>? PrivateCompactionAdmission
    {
        get; set;
    }

    internal Func<IDisposable>? CheckpointPriority
    {
        get; set;
    }

    public IDisposable EnterCheckpointPriority() => (CheckpointPriority ??
        throw new InvalidOperationException("Semantic checkpoint priority is not bound."))();

    public bool RequiresCheckpointMaintenance => _physicalWriter.RequiresCheckpointMaintenance;

    public bool RequiresCheckpointCompaction => _physicalWriter.RequiresCheckpointCompaction;

    public bool CanAdvancePrivateCheckpointCompaction => _physicalWriter.CanAdvancePrivateCheckpointCompaction;

    public IDisposable? TryEnterPrivateCheckpointCompaction() => PrivateCompactionAdmission?.Invoke();

    public ValueTask<bool> AdvanceCheckpointCompactionAsync(DatabaseVersion version,
        Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> journal, CancellationToken token) =>
        _physicalWriter.AdvanceCheckpointCompactionAsync(version, journal, token);

    public ValueTask StopCheckpointCompactionAsync() => _physicalWriter.StopCheckpointCompactionAsync();

    public void AuthorizeCheckpointPageReuse(bool compacted) => _physicalWriter.AuthorizeCheckpointPageReuse(compacted);

    public IDisposable? EnterMaintenance() => (MaintenanceAdmission ?? throw new InvalidOperationException("Semantic maintenance admission is not bound."))();

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(Guid id,
        DatabaseVersion version, bool compact, CancellationToken token) =>
        (await _physicalWriter.PrepareCheckpointMaintenanceAsync(id, version, compact, token).ConfigureAwait(false))?.PhysicalMutations ?? [];

    public async ValueTask PublishMaintenanceAsync(Guid id, CancellationToken token)
    {
        await _physicalWriter.PersistAsync(id, token).ConfigureAwait(false);
        _physicalWriter.Publish(id);
        _physicalWriter.ClearMaintenanceCaches();
        Participant().RetainAppliedSessions(new HashSet<Guid>());
    }

    public ValueTask AbandonMaintenanceAsync(Guid id)
    {
        _physicalWriter.AbandonCheckpointMaintenance(id);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteMaintenanceAsync(CancellationToken token) => _physicalWriter.TrimAsync(token);

    public ValueTask CheckpointPublishedAsync(DatabaseVersion version, CancellationToken cancellationToken = default)
    {
        Participant().AdvanceCheckpointRecoveryFloor(version);
        return ValueTask.CompletedTask;
    }

    private ISemanticProfileStateParticipant Participant() => _participant ??
        throw new InvalidOperationException("The Semantic profile participant is not bound.");

}
