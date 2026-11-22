using System.Diagnostics;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Mapping;
using EntityForge.Db;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticProfileCommitLog : ISemanticCommitLog, ISemanticCandidateSnapshotProvider,
    IDatabaseCommittedChangeJournal, IDatabaseBackgroundMaintenanceReadAdmission,
    IDatabaseSnapshotReadAdmission, IDatabaseDistributedWriteAuthority, IDatabaseTransferRuntime,
    IEntityOperationCompactionAuthority, ISemanticTransientSnapshotAdmission, IEntityMappingCatalogProvider, IEntityOperationCatalogProvider, IDatabaseOperationRetention
{
    ValueTask<EntityOperationBinding?> IEntityOperationCompactionAuthority.ReadOperationAsync(Guid id, CancellationToken token) =>
        ((IEntityOperationCompactionAuthority)_kernel).ReadOperationAsync(id, token);

    ValueTask IEntityOperationCompactionAuthority.CompactCommittedAsync(Guid id, CancellationToken token) =>
        ((IEntityOperationCompactionAuthority)_kernel).CompactCommittedAsync(id, token);

    ValueTask<EntityOperationBinding?> IEntityOperationCompactionAuthority.PrepareCommittedCompactionAsync(Guid id, CancellationToken token) =>
        ((IEntityOperationCompactionAuthority)_kernel).PrepareCommittedCompactionAsync(id, token);

    ValueTask IEntityOperationCompactionAuthority.VerifyReadableAsync(EntityOperationCommitted terminal, CancellationToken token) =>
        ((IEntityOperationCompactionAuthority)_kernel).VerifyReadableAsync(terminal, token);

    ValueTask IEntityOperationCompactionAuthority.CompleteCommittedCompactionAsync(EntityOperationBinding compact, CancellationToken token) =>
        ((IEntityOperationCompactionAuthority)_kernel).CompleteCommittedCompactionAsync(compact, token);

    private const int StructuralEvidenceSamplingCommitInterval = 256;
    public const string SemanticPayloadCodecId = SemanticCommitCodec.CodecId;
    public const int SemanticPayloadCodecVersion = SemanticCommitCodec.CodecVersion;
    private readonly SemanticCommitCodec _codec = new();
    private readonly SemanticDurabilityRuntime _runtime;

    public EntityOperationCatalog OperationCatalog => _runtime.Operations;

    public EntityMappingCatalog MappingCatalog => _runtime.Mappings;

    private readonly DatabaseKernel _kernel;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly SemanticProjectionTelemetry _projectionTelemetry;
    private readonly SemanticPhysicalAccessPath _physicalAccessPath;
    private readonly DatabaseBackgroundMaintenanceCoordinator _maintenance;
    private readonly DatabaseMaintenancePolicy _maintenancePolicy = new();
    private readonly AsyncWriterPriorityGate _reindexAdmission = new();
    private readonly SemaphoreSlim _reindexExecution = new(1, 1);
    private int _transientSnapshots;
    private readonly bool _archiveBeforeReindex;
    private int _commitsSinceStructuralEvidence;
    private string? _catchUpTarget;

    internal Action? ReindexWriterTurnstileAcquired
    {
        get; set;
    }

    internal Func<CancellationToken, ValueTask>? BeforeBackgroundMaintenanceEvidence
    {
        get; set;
    }

    private SemanticProfileCommitLog(
        IDatabaseCommitLog kernelLog,
        IDatabaseCommittedChangeStore changeStore,
        SemanticProfileCheckpointStore checkpointStore,
        SemanticPhysicalAccessPath physicalAccessPath, IResizablePhysicalPageStore pages,
        SemanticProjectionTelemetry projectionTelemetry, IDatabaseHistoryRetentionPolicy? historyRetention,
        Guid? durableDatabaseIncarnation, DatabaseVersioningMode versioningMode, IDatabaseBoundedClock? timestampClock)
    {
        ArgumentNullException.ThrowIfNull(kernelLog);
        ArgumentNullException.ThrowIfNull(changeStore);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        _runtime = new(_codec, checkpointStore, physicalAccessPath);
        _runtime.SnapshotAdmission = EnterPartitionSnapshotAsync;
        _runtime.MaintenanceAdmission = () => _reindexAdmission.TryEnterWrite() ??
            throw new InvalidOperationException("Semantic retention maintenance requires drained readers.");
        _runtime.PrivateCompactionAdmission = () => _reindexAdmission.TryEnterWrite();
        _pages = pages;
        _projectionTelemetry = projectionTelemetry;
        _physicalAccessPath = physicalAccessPath;
        _kernel = new DatabaseKernel(kernelLog, changeStore, _runtime, checkpointStore: checkpointStore.KernelStore,
            historyRetention: historyRetention, durableDatabaseIncarnation: durableDatabaseIncarnation, versioningMode: versioningMode, timestampClock: timestampClock);
        _archiveBeforeReindex = historyRetention is IArchiveVerifiedDatabaseHistoryRetentionPolicy;
        _maintenance = new DatabaseBackgroundMaintenanceCoordinator(() => !_kernel.IsDistributedWriteMode && _physicalAccessPath.NeedsMaintenance,
            ExecuteMaintenanceAsync);
        _runtime.CheckpointPriority = _maintenance.EnterForegroundRead;
    }

    internal DatabaseMaintenanceStatus MaintenanceStatus => _maintenance.Status;

    public DatabaseVersion CurrentVersion => _kernel.CurrentVersion;

    public Guid DatabaseIncarnation => _kernel.DatabaseIncarnation;

    internal DatabaseKernel PartitionKernel => _kernel;

    public DatabaseProfileId Profile => _kernel.Profile;

    public ValueTask<DatabaseTransferRetentionLease> RetainTransferHistoryAsync(
        CancellationToken cancellationToken = default) =>
        _kernel.RetainTransferHistoryAsync(cancellationToken);

    public IAsyncEnumerable<DatabaseTransferCommit> ExportTransferCommitsAsync(
        DatabaseVersion afterExclusive, DatabaseVersion throughInclusive,
        CancellationToken cancellationToken = default) =>
        _kernel.ExportTransferCommitsAsync(afterExclusive, throughInclusive, cancellationToken);

    public ValueTask<DatabaseCommitReceipt> ApplyTransferCommitAsync(DatabaseTransferCommit commit,
        CancellationToken cancellationToken = default) =>
        _kernel.ApplyTransferCommitAsync(commit, cancellationToken);

    bool ISemanticTransientSnapshotAdmission.RetainsRecentDistributedVersions => _kernel.IsDistributedWriteMode;

    public async ValueTask<SemanticDistributedState> CaptureCurrentStateAsync(
        Func<DatabaseVersion, IDisposable, SemanticDistributedState> capture, CancellationToken cancellationToken)
    {
        using IDisposable foreground = _maintenance.EnterForegroundRead();
        await _reindexExecution.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IDisposable admission = await _reindexAdmission.EnterReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _transientSnapshots);
            using var retention = new TransientSnapshotAdmission(this, admission);
            // Select and capture within the same completed publication epoch, after maintenance
            // admission. A concurrent publication disposes the unreturned capture before retry.
            return await _kernel.ReadPublishedAsync(version =>
            {
                IDisposable reference = retention.Retain();
                try
                {
                    return capture(version, reference);
                }
                catch (Exception failure)
                {
                    if (failure is not IEntityPreparationCleanupFailure)
                    {
                        reference.Dispose();
                    }

                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexExecution.Release();
        }
    }

    public async ValueTask<DatabaseDistributedWriteLease> EnterDistributedWriteModeAsync(
        CancellationToken cancellationToken = default)
    {
        // Serialize enrollment with maintenance admission; a pre-enrollment job finishes first,
        // and a later job observes distributed ownership before attempting any local commit.
        await _reindexExecution.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _kernel.EnterDistributedWriteModeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexExecution.Release();
        }
    }

    public IDisposable EnterForegroundRead() => _maintenance.EnterForegroundRead();

    public async ValueTask<IDisposable> EnterSnapshotReadAsync(
        CancellationToken cancellationToken = default)
    {
        IDisposable foreground = _maintenance.EnterForegroundRead();
        try
        {
            IDisposable reindex = await _reindexAdmission.EnterReadAsync(cancellationToken)
                .ConfigureAwait(false);
            return new CompositeReadAdmission(reindex, foreground);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreground.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            foreground.Dispose();
            throw;
        }
    }

    internal async ValueTask<IDisposable?> EnterPartitionSnapshotAsync(CancellationToken token)
    {
        // Request cancellation before waiting for the execution gate held by a background
        // rebuild. Once enrolled, _transientSnapshots protects the retained view instead.
        using IDisposable foreground = _maintenance.EnterForegroundRead();
        await _reindexExecution.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var admission = await _reindexAdmission.EnterReadAsync(token).ConfigureAwait(false);
            Interlocked.Increment(ref _transientSnapshots);
            return new TransientSnapshotAdmission(this, admission);
        }
        finally
        {
            _reindexExecution.Release();
        }
    }

    public async ValueTask<IDisposable> RetainTransientSnapshotAsync(DatabaseVersion version,
        CancellationToken cancellationToken = default)
    {
        using IDisposable foreground = _maintenance.EnterForegroundRead();
        await _reindexExecution.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IDisposable admission = await _reindexAdmission.EnterReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _kernel.ReadPublishedAsync(current =>
                {
                    DatabaseVersion floor = _kernel.IsDistributedWriteMode
                        ? _runtime.GetPhysicalHistoryRetentionFloor() : _runtime.GetSnapshotRetentionFloor();
                    // Native planning may stage the next metadata version before publication.
                    // Its default no-lease floor cannot invalidate the published current view.
                    if (floor.Value > current.Value)
                    {
                        floor = current;
                    }

                    if (version.Value < floor.Value)
                    {
                        throw new EntityForgeSnapshotUnavailableException(version, floor);
                    }

                    if (version.Value > current.Value)
                    {
                        throw new InvalidOperationException(
                            $"Semantic snapshot {version.Value} is outside the retained range {floor.Value}..{current.Value}.");
                    }

                    return true;
                }, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _transientSnapshots);
                return new TransientSnapshotAdmission(this, admission);
            }
            catch
            {
                admission.Dispose();
                throw;
            }
        }
        finally
        {
            _reindexExecution.Release();
        }
    }

    public static async ValueTask<SemanticProfileCommitLog> OpenAsync(
        IDatabaseCommitLog kernelLog, IDatabaseCommittedChangeStore changeStore,
        SemanticProfileCheckpointStore checkpointStore, IResizablePhysicalPageStore pages,
        CancellationToken cancellationToken = default,
        IDatabaseHistoryRetentionPolicy? historyRetention = null,
        Guid? durableDatabaseIncarnation = null, DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative,
        IDatabaseBoundedClock? timestampClock = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        long started = Stopwatch.GetTimestamp();
        if (kernelLog is GroupedRedoDatabaseCommitLog redo)
        {
            await redo.RecoverPagesAsync(pages, cancellationToken).ConfigureAwait(false);
        }

        DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-startup", "all", "native_page_redo", 0, started);
        started = Stopwatch.GetTimestamp();
        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages, cancellationToken)
            .ConfigureAwait(false);
        DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-startup", "all", "page_space_open", 0, started);
        var projectionTelemetry = new SemanticProjectionTelemetry();
        try
        {
            started = Stopwatch.GetTimestamp();
            SemanticPhysicalAccessPath accessPath = await SemanticPhysicalAccessPath.OpenAsync(
                pages, pageSpace, projectionTelemetry, cancellationToken).ConfigureAwait(false);
            DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-startup", "all", "physical_access_paths_open", 0, started);
            return new(kernelLog, changeStore, checkpointStore, accessPath, pages, projectionTelemetry,
                historyRetention, durableDatabaseIncarnation, versioningMode, timestampClock);
        }
        catch
        {
            projectionTelemetry.Dispose();
            throw;
        }
    }

    public async ValueTask<DatabaseVersion> CommitAsync(
        DurableDatabaseCommit commit,
        CancellationToken cancellationToken = default) =>
        (await CommitWithReceiptAsync(commit, cancellationToken).ConfigureAwait(false)).Version;

    public async ValueTask<DatabaseCommitReceipt> CommitWithReceiptAsync(
        DurableDatabaseCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Kind is not DurableDatabaseCommitKind.PhysicalMaintenance)
        {
            _maintenance.Preempt();
        }

        byte[] payload = _codec.Encode(commit);
        string? catchUpTarget = Volatile.Read(ref _catchUpTarget);
        long admissionStarted = Stopwatch.GetTimestamp();
        using IDisposable admission = await _reindexAdmission.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        if (catchUpTarget is not null)
        {
            DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-reindex", catchUpTarget,
                "writer-admission-wait", 0, admissionStarted);
        }

        using IDisposable io = PhysicalPageWorkload.EnterForegroundWrite();
        DatabaseCommitReceipt receipt = await _kernel.CommitWithReceiptAsync(
            SemanticCommitCodec.TransactionId(commit, payload), payload, cancellationToken).ConfigureAwait(false);
        if (commit.Kind is not DurableDatabaseCommitKind.PhysicalMaintenance)
        {
            SignalMaintenanceAfterForegroundCommits(1);
        }

        return receipt;
    }

    public async ValueTask RecoverAsync(CancellationToken cancellationToken = default)
    {
        _runtime.BeginRecovery();
        try
        {
            await _kernel.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runtime.EndRecovery();
        }
        _maintenance.Signal();
    }

    internal ValueTask WaitForMaintenanceAsync(CancellationToken cancellationToken = default) =>
        _maintenance.WaitForIdleAsync(cancellationToken);

    internal void RequestMaintenanceEvaluation() => _maintenance.Signal(forceEvaluation: true);

    private async ValueTask<bool> ExecuteMaintenanceAsync(CancellationToken cancellationToken)
    {
        long waitStarted = Stopwatch.GetTimestamp();
        await _reindexExecution.WaitAsync(cancellationToken).ConfigureAwait(false);
        DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-reindex", "all",
            "background-execution-wait", 0, waitStarted);
        try
        {
            if (_kernel.IsDistributedWriteMode || Volatile.Read(ref _transientSnapshots) != 0)
            {
                return false;
            }

            return await ExecuteMaintenanceCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexExecution.Release();
        }
    }

    private async ValueTask<bool> ExecuteMaintenanceCoreAsync(CancellationToken cancellationToken)
    {
        if (BeforeBackgroundMaintenanceEvidence is { } beforeEvidence)
        {
            await beforeEvidence(cancellationToken).ConfigureAwait(false);
        }

        long evidenceStarted = Stopwatch.GetTimestamp();
        var evidence = await _physicalAccessPath.CaptureMaintenanceEvidenceAsync(cancellationToken).ConfigureAwait(false);
        DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-reindex", "all",
            "evidence_prepare", 0, evidenceStarted);
        long policyStarted = Stopwatch.GetTimestamp();
        DatabaseMaintenanceDecision decision = _maintenancePolicy.Evaluate(evidence);
        DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-reindex", "all",
            $"policy-{decision.Action.ToString().ToLowerInvariant()}-{decision.Reason}", 0,
            policyStarted);
        if (decision.Action is DatabaseMaintenanceAction.Defer or DatabaseMaintenanceAction.None)
        {
            return false;
        }

        if (decision.Action is DatabaseMaintenanceAction.Reindex)
        {
            try
            {
                await ReindexWithRetentionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OrderedPageTreeReplacementStaleException)
            {
                return false;
            }
            catch (AppendMergeReplacementStaleException)
            {
                return false;
            }
            // A heavy job is one bounded scheduling unit. Residual retirement debt is reconsidered
            // on the next foreground signal instead of self-chaining an unbounded worker loop.
            return false;
        }
        DurableDatabaseCommit commit = DurableDatabaseCommit.ForPhysicalMaintenance(Guid.NewGuid());
        byte[] payload = _codec.Encode(commit);
        using IDisposable publication = await _runtime.EnterMaintenancePublicationAsync(cancellationToken)
            .ConfigureAwait(false);
        await _kernel.CommitAsync(SemanticCommitCodec.TransactionId(commit, payload), payload,
            cancellationToken).ConfigureAwait(false);
        _physicalAccessPath.RecordMaintenanceStatus();
        return _physicalAccessPath.NeedsMaintenance;
    }

    public void BindProfile(ISemanticProfileStateParticipant participant) => _runtime.Bind(participant);

    public ISemanticCandidateSnapshot CaptureCandidateSnapshot(DatabaseVersion? maximumVersion = null) =>
        _runtime.CaptureCandidateSnapshot(maximumVersion);

    public ValueTask<DatabaseVersion> CreateCheckpointAsync(CancellationToken cancellationToken = default) =>
        CreateCheckpointCoreAsync(null, cancellationToken);

    public ValueTask<DatabaseVersion> RetainOperationsAsync(DateTimeOffset retainedSince, CancellationToken cancellationToken = default) =>
        CreateCheckpointCoreAsync(retainedSince, cancellationToken);

    private async ValueTask<DatabaseVersion> CreateCheckpointCoreAsync(DateTimeOffset? retainedSince,
        CancellationToken cancellationToken)
    {
        long waitStarted = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        _maintenance.Preempt();
        await _maintenance.WaitForIdleAsync(cancellationToken).ConfigureAwait(false);
        DatabaseMaintenanceCoordinator.RecordPhase("semantic-reindex", "all",
            "checkpoint-preemption-wait", 0, waitStarted, allocatedBefore);
        await _reindexExecution.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A checkpoint may proceed with exclusive admission immediately available (also
            // needed by source transfer), but must never queue behind a retained view.
            using IDisposable admission = _kernel.IsDistributedWriteMode || Volatile.Read(ref _transientSnapshots) != 0
                ? _reindexAdmission.TryEnterWrite() ?? throw new InvalidOperationException("Checkpoint deferred while transient views are retained.")
                : await _reindexAdmission.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
            return retainedSince is { } cutoff
                ? await _kernel.RetainOperationsAsync(cutoff, cancellationToken).ConfigureAwait(false)
                : await CreateKernelCheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexExecution.Release();
        }
    }

    public async ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default)
    {
        _maintenance.Preempt();
        return await ReindexCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DatabaseVersion> ReindexCoreAsync(CancellationToken cancellationToken)
    {
        long waitStarted = Stopwatch.GetTimestamp();
        await _reindexExecution.WaitAsync(cancellationToken).ConfigureAwait(false);
        DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-reindex", "all",
            "execution-wait", 0, waitStarted);
        try
        {
            RequireLocalMaintenance();
            return await ReindexWithRetentionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexExecution.Release();
        }
    }

    // The execution gate also serializes enrollment. Reject before entering the writer-priority
    // gate: retained delivery may need another reader admission while its original lease is live.
    private void RequireLocalMaintenance()
    {
        if (_kernel.IsDistributedWriteMode)
        {
            throw new InvalidOperationException("Explicit Semantic maintenance requires distributed unenrollment.");
        }

        if (Volatile.Read(ref _transientSnapshots) != 0)
        {
            throw new InvalidOperationException("Explicit Semantic maintenance is deferred while transient views are retained.");
        }
    }

    private async ValueTask<DatabaseVersion> ReindexWithRetentionAsync(
        CancellationToken cancellationToken)
    {
        if (!_archiveBeforeReindex)
        {
            return await ReindexTargetsAsync(cancellationToken).ConfigureAwait(false);
        }

        using (await _reindexAdmission.EnterWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            await CreateKernelCheckpointAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReindexTargetsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DatabaseVersion> ReindexTargetsAsync(CancellationToken cancellationToken)
    {
        PhysicalMaintenanceTarget[] targets = _physicalAccessPath.GetVacuumTargets().ToArray();
        var replacements = new Dictionary<PhysicalMaintenanceTarget, Guid>(targets.Length);
        DatabaseVersion reindexVersion = default;
        try
        {
            await BuildReplacementBatchAsync(targets, replacements, 1, cancellationToken).ConfigureAwait(false);
            // Foreground commits acquire semantic admission before physical read admission.
            // Preserve that order: never wait for a validating commit while holding the writer turnstile.
            using IDisposable publication = await _runtime.EnterMaintenancePublicationAsync(cancellationToken)
                .ConfigureAwait(false);
            const string batchTarget = "batch";
            Volatile.Write(ref _catchUpTarget, batchTarget);
            IDisposable catchUpAdmission;
            try
            {
                DatabaseMaintenancePhaseMeasurement admission =
                    DatabaseMaintenanceCoordinator.BeginPhase();
                catchUpAdmission = await _reindexAdmission.EnterWriteAsync(cancellationToken,
                        ReindexWriterTurnstileAcquired)
                    .ConfigureAwait(false);
                admission.Complete("semantic-reindex", batchTarget,
                    DatabaseMaintenanceCoordinator.WriterExclusionWaitPhase, 2);
            }
            catch
            {
                Volatile.Write(ref _catchUpTarget, null);
                throw;
            }
            long catchUpStarted = Stopwatch.GetTimestamp();
            try
            {
                using (catchUpAdmission)
                {
                    await CatchUpReplacementBatchAsync(targets, replacements, cancellationToken)
                        .ConfigureAwait(false);
                    var batch = targets.Select(target => (target, replacements[target])).ToArray();
                    replacements.Clear();
                    reindexVersion = await AdoptReplacementBatchAsync(batch, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsStaleReplacement(exception))
            {
                throw new EntityForgeDbStorageException(
                    "Semantic reindex remained stale inside the delta catch-up window.", exception);
            }
            finally
            {
                DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-reindex", batchTarget,
                    "catch-up-window", 2, catchUpStarted);
                Volatile.Write(ref _catchUpTarget, null);
            }

            long trimStarted = Stopwatch.GetTimestamp();
            long trimAllocated = GC.GetTotalAllocatedBytes();
            bool trimmed = await _physicalAccessPath.TryTrimUnownedTailAsync(cancellationToken).ConfigureAwait(false);
            DatabaseMaintenanceCoordinator.RecordPhase("semantic-reindex", "all",
                trimmed ? "trim" : "trim-deferred", 1, trimStarted, trimAllocated);
            _physicalAccessPath.RecordMaintenanceStatus();
            return reindexVersion;
        }
        finally
        {
            foreach ((PhysicalMaintenanceTarget target, Guid replacementId) in replacements)
            {
                DatabaseMaintenancePhaseMeasurement cleanup =
                    DatabaseMaintenanceCoordinator.BeginPhase();
                AbandonReplacement(target, replacementId);
                cleanup.Complete("semantic-reindex", TargetName(target),
                    DatabaseMaintenanceCoordinator.CleanupPhase, 1);
            }
        }
    }

    private async ValueTask CatchUpReplacementBatchAsync(
        IReadOnlyList<PhysicalMaintenanceTarget> targets,
        Dictionary<PhysicalMaintenanceTarget, Guid> replacements,
        CancellationToken cancellationToken)
    {
        DatabaseVersion retentionFloor = _runtime.GetPhysicalHistoryRetentionFloor();
        foreach (PhysicalMaintenanceTarget target in targets)
        {
            long phaseStarted = Stopwatch.GetTimestamp();
            long allocatedBefore = GC.GetTotalAllocatedBytes();
            try
            {
                await _runtime.CatchUpReplacementAsync(target, replacements[target], retentionFloor,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AppendMergeReplacementStaleException)
            {
                // Write-time compaction may replace source runs during the optimistic build.
                // Publication and physical writer admission are held here: rebuild only this
                // target against the now-stable view instead of applying an incomplete delta.
                AbandonReplacement(target, replacements[target]);
                replacements.Remove(target);
                replacements.Add(target, await BuildReplacementAsync(target, 2, cancellationToken)
                    .ConfigureAwait(false));
            }
            DatabaseMaintenanceCoordinator.RecordPhase("semantic-reindex", TargetName(target),
                DatabaseMaintenanceCoordinator.CatchUpPhase, 2, phaseStarted, allocatedBefore);
        }
        await FlushReplacementBatchAsync("batch", 2, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Guid> BuildReplacementAsync(PhysicalMaintenanceTarget target, int attempt,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        using IDisposable io = PhysicalPageWorkload.EnterMaintenance(TargetName(target));
        Guid replacementId = Guid.NewGuid();
        long phaseStarted = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        DatabaseVersion retentionFloor = _runtime.GetPhysicalHistoryRetentionFloor();
        if (target is PhysicalMaintenanceTarget.Ordered)
        {
            await _runtime.BuildOrderedReplacementAsync(replacementId, retentionFloor, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _runtime.BuildAppendReplacementAsync(target, replacementId, retentionFloor, cancellationToken)
                .ConfigureAwait(false);
        }

        DatabaseMaintenanceCoordinator.RecordPhase("semantic-reindex", TargetName(target),
            DatabaseMaintenanceCoordinator.BuildPhase, attempt, phaseStarted, allocatedBefore);
        return replacementId;
    }

    private async ValueTask BuildReplacementBatchAsync(IReadOnlyList<PhysicalMaintenanceTarget> targets,
        Dictionary<PhysicalMaintenanceTarget, Guid> replacements, int attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (PhysicalMaintenanceTarget target in targets)
            {
                Guid replacementId = await BuildReplacementAsync(target, attempt, cancellationToken)
                    .ConfigureAwait(false);
                replacements.Add(target, replacementId);
                await FlushReplacementBatchAsync(TargetName(target), attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            KeyValuePair<PhysicalMaintenanceTarget, Guid>[] built = replacements.ToArray();
            replacements.Clear();
            foreach ((PhysicalMaintenanceTarget target, Guid replacementId) in built)
            {
                DatabaseMaintenancePhaseMeasurement cleanup =
                    DatabaseMaintenanceCoordinator.BeginPhase();
                AbandonReplacement(target, replacementId);
                cleanup.Complete("semantic-reindex", TargetName(target),
                    DatabaseMaintenanceCoordinator.CleanupPhase, attempt);
            }
            throw;
        }
    }

    private async ValueTask FlushReplacementBatchAsync(string target, int attempt,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        using IDisposable io = PhysicalPageWorkload.EnterMaintenance(target);
        long phaseStarted = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        await _runtime.FlushReplacementPagesAsync(cancellationToken).ConfigureAwait(false);
        DatabaseMaintenanceCoordinator.RecordPhase("semantic-reindex", target,
            DatabaseMaintenanceCoordinator.FlushPhase, attempt, phaseStarted, allocatedBefore);
    }

    private async ValueTask<DatabaseVersion> AdoptReplacementBatchAsync(
        IReadOnlyList<(PhysicalMaintenanceTarget Target, Guid ReplacementId)> replacements,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        using IDisposable io = PhysicalPageWorkload.EnterMaintenance("batch");
        Guid batchId = Guid.NewGuid();
        DurableDatabaseCommit commit = DurableDatabaseCommit.ForPhysicalMaintenance(
            batchId, PhysicalMaintenanceKind.Reindex, PhysicalMaintenanceTarget.None);
        byte[] payload = _codec.Encode(commit);
        Guid transactionId = SemanticCommitCodec.TransactionId(commit, payload);
        _runtime.RegisterReplacementBatch(transactionId, replacements);
        long phaseStarted = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        DatabaseVersion version = await _kernel.CommitAsync(transactionId, payload, cancellationToken)
            .ConfigureAwait(false);
        DatabaseMaintenanceCoordinator.RecordPhase("semantic-reindex", "batch",
            DatabaseMaintenanceCoordinator.CutoverPhase, 2, phaseStarted, allocatedBefore);
        return new(version.Value);
    }

    public async ValueTask<DatabaseVersion> ApplyAuthoritativeCommitAsync(
        DurableDatabaseCommit commit,
        DatabaseVersion authoritativeVersion,
        CancellationToken cancellationToken = default) =>
        (await ApplyAuthoritativeCommitWithReceiptAsync(commit, authoritativeVersion, cancellationToken)
            .ConfigureAwait(false)).Version;

    public async ValueTask<DatabaseCommitReceipt> ApplyAuthoritativeCommitWithReceiptAsync(
        DurableDatabaseCommit commit,
        DatabaseVersion authoritativeVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (authoritativeVersion.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authoritativeVersion),
                "An authoritative semantic commit version must be positive.");
        }

        if (commit.Kind is not DurableDatabaseCommitKind.SemanticTransaction)
        {
            throw new ArgumentException("Only semantic session commits may use an authoritative version.",
                nameof(commit));
        }

        _maintenance.Preempt();
        byte[] payload = _codec.Encode(commit);
        string? catchUpTarget = Volatile.Read(ref _catchUpTarget);
        long admissionStarted = Stopwatch.GetTimestamp();
        using IDisposable admission = await _reindexAdmission.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        if (catchUpTarget is not null)
        {
            DatabaseMaintenanceCoordinator.RecordDurationPhase("semantic-reindex", catchUpTarget,
                "writer-admission-wait", 0, admissionStarted);
        }

        using IDisposable io = PhysicalPageWorkload.EnterForegroundWrite();
        DatabaseCommitReceipt receipt = await _kernel.ApplyAuthoritativeCommitAsync(
            SemanticCommitCodec.TransactionId(commit, payload), payload,
            new DatabaseVersion(authoritativeVersion.Value), cancellationToken).ConfigureAwait(false);
        SignalMaintenanceAfterForegroundCommits(1);
        return receipt;
    }

    public ValueTask<DatabaseCommitReceipt?> ResolveReplayReceiptAsync(
        DurableDatabaseCommit commit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        byte[] payload = _codec.Encode(commit);
        return _kernel.ResolveReplayReceiptAsync(
            SemanticCommitCodec.TransactionId(commit, payload), payload, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<DatabaseVersion>> CommitGroupAsync(
        IReadOnlyList<DurableDatabaseCommit> commits, CancellationToken cancellationToken = default) =>
        (await CommitGroupWithReceiptsAsync(commits, cancellationToken).ConfigureAwait(false))
        .Select(receipt => receipt.Version).ToArray();

    public async ValueTask<IReadOnlyList<DatabaseCommitReceipt>> CommitGroupWithReceiptsAsync(
        IReadOnlyList<DurableDatabaseCommit> commits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commits);
        if (commits.Count == 0)
        {
            throw new ArgumentException("A Semantic commit group cannot be empty.",
            nameof(commits));
        }

        _maintenance.Preempt();
        byte[][] payloads = commits.Select(_codec.Encode).ToArray();
        using IDisposable admission = await _reindexAdmission.EnterReadAsync(cancellationToken)
            .ConfigureAwait(false);
        using IDisposable io = PhysicalPageWorkload.EnterForegroundWrite();
        IReadOnlyList<DatabaseCommitReceipt> receipts = await _kernel.CommitGroupWithReceiptsAsync(
            commits.Select((commit, index) => new DatabaseCommitRequest(
                SemanticCommitCodec.TransactionId(commit, payloads[index]), payloads[index])).ToArray(),
            cancellationToken).ConfigureAwait(false);
        SignalMaintenanceAfterForegroundCommits(commits.Count);
        return receipts;
    }

    private void SignalMaintenanceAfterForegroundCommits(int commitCount)
    {
        int sampledCommits = Interlocked.Add(ref _commitsSinceStructuralEvidence, commitCount);
        if (sampledCommits < StructuralEvidenceSamplingCommitInterval)
        {
            _maintenance.Signal();
            return;
        }

        Interlocked.Exchange(ref _commitsSinceStructuralEvidence, 0);
        _maintenance.Signal(forceEvaluation: true);
    }

    private void AbandonReplacement(PhysicalMaintenanceTarget target, Guid replacementId)
    {
        if (target is PhysicalMaintenanceTarget.Ordered)
        {
            _runtime.AbandonOrderedReplacement(Guid.Empty, replacementId);
        }
        else
        {
            _runtime.AbandonAppendReplacement(Guid.Empty, replacementId);
        }
    }

    private static bool IsStaleReplacement(Exception exception) =>
        exception is OrderedPageTreeReplacementStaleException or AppendMergeReplacementStaleException;

    private async ValueTask<DatabaseVersion> CreateKernelCheckpointAsync(CancellationToken cancellationToken)
    {
        DatabaseVersion version = new((await _kernel.CreateCheckpointAsync(cancellationToken)
            .ConfigureAwait(false)).Version.Value);
        return version;
    }

    private sealed class TransientSnapshotAdmission(SemanticProfileCommitLog owner, IDisposable admission) : IDisposable
    {
        private int _references = 1;
        private int _disposed;

        internal IDisposable Retain()
        {
            Interlocked.Increment(ref _references);
            return new Reference(this);
        }

        private void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0)
            {
                return;
            }

            admission.Dispose();
            Interlocked.Decrement(ref owner._transientSnapshots);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release();
            }
        }

        private sealed class Reference(TransientSnapshotAdmission owner) : IDisposable
        {
            private TransientSnapshotAdmission? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }

    private sealed class CompositeReadAdmission(IDisposable reindex, IDisposable foreground) : IDisposable
    {
        private IDisposable? _reindex = reindex;
        private IDisposable? _foreground = foreground;

        public void Dispose()
        {
            Interlocked.Exchange(ref _reindex, null)?.Dispose();
            Interlocked.Exchange(ref _foreground, null)?.Dispose();
        }
    }


    private async ValueTask<DatabaseVersion> CommitMaintenanceAsync(
        PhysicalMaintenanceKind kind, PhysicalMaintenanceTarget target,
        CancellationToken cancellationToken)
    {
        using IDisposable io = PhysicalPageWorkload.EnterMaintenance(TargetName(target));
        DurableDatabaseCommit commit = DurableDatabaseCommit.ForPhysicalMaintenance(Guid.NewGuid(), kind, target);
        byte[] payload = _codec.Encode(commit);
        using IDisposable publication = await _runtime.EnterMaintenancePublicationAsync(cancellationToken)
            .ConfigureAwait(false);
        DatabaseVersion version = await _kernel.CommitAsync(SemanticCommitCodec.TransactionId(commit, payload),
            payload, cancellationToken).ConfigureAwait(false);
        return new(version.Value);
    }

    private static string TargetName(PhysicalMaintenanceTarget target) =>
        target.ToString().ToLowerInvariant();

    public ValueTask<DatabaseChangeFeedBounds> GetBoundsAsync(CancellationToken cancellationToken = default) =>
        _kernel.GetBoundsAsync(cancellationToken);

    public ValueTask<DatabaseChangePage> ReadPageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken = default) =>
        _kernel.ReadPageAfterAsync(cursor, selector, maximumCount, cancellationToken);

    public IAsyncEnumerable<CommittedChangeSet> SubscribeAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default) =>
        _kernel.SubscribeAsync(cursor, selector, options, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _maintenance.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _kernel.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await _pages.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _projectionTelemetry.Dispose();
            // Retain the managed admission primitives so leases admitted before closure can
            // release safely after the kernel and maintenance coordinator have drained.
        }
    }
}
