using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Storage;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    public bool RequiresCheckpointMaintenance => _checkpointCompaction is not null || _pageSpace.RequiresCheckpointMaintenance;

    public void AbandonCheckpointMaintenance(Guid transactionId)
    {
        // Preparation releases its own failed transaction. Kernel cleanup must also
        // be safe when preparation failed before a directory transaction was staged.
        if (_stagedMutations.ContainsKey(transactionId))
        {
            Abandon(transactionId);
        }
    }

    public bool RequiresCheckpointCompaction => _checkpointCompaction is not null || _retentionMayBeNeeded && _pageSpace.HasRetainedGrowthDebt;

    public void AuthorizeCheckpointPageReuse(bool compacted)
    {
        _pageSpace.AuthorizeCheckpointPageReuse(privateWorkspacesPaused: _checkpointCompaction is not null);
        if (compacted || !_retentionMayBeNeeded)
        {
            _pageSpace.RecordCompactedOwnership();
        }
    }

    public bool NeedsMaintenance => _ranges.GetMaintenanceStatus().NeedsMaintenance ||
                                    _shared.GetMaintenanceStatus().NeedsMaintenance ||
                                    _concepts.GetMaintenanceStatus().NeedsMaintenance ||
                                    _relationships.NeedsMaintenance || _fields.NeedsMaintenance;

    public void RecordMaintenanceStatus()
    {
        _telemetry.MaintenanceStatus(SemanticProjectionRole.StringRange,
            _ranges.GetMaintenanceStatus());
        _telemetry.MaintenanceStatus(SemanticProjectionRole.SharedSemanticExact,
            _shared.GetMaintenanceStatus());
        _telemetry.MaintenanceStatus(SemanticProjectionRole.ConceptMembership,
            _concepts.GetMaintenanceStatus());
        _relationships.RecordMaintenanceStatus(_telemetry);
        _fields.RecordMaintenanceStatus(_telemetry);
    }

    public ValueTask<bool> ShouldVacuumAsync(CancellationToken cancellationToken) =>
        _pageSpace.ShouldVacuumAsync(cancellationToken);

    public async ValueTask BuildOrderedReplacementAsync(Guid replacementId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken)
    {
        PhysicalPageWorkspace workspace = _pageSpace.BeginReplacementWorkspace(replacementId);
        try
        {
            await _ranges.BuildReplacementAsync(replacementId, retentionFloor.Value, workspace, false, cancellationToken)
                .ConfigureAwait(false);
            lock (_replacementGate)
            {
                _appendReplacements.Add(replacementId,
                (PhysicalMaintenanceTarget.Ordered, workspace));
            }
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public async ValueTask<DatabaseMaintenanceEvidence> CaptureMaintenanceEvidenceAsync(
        CancellationToken cancellationToken)
    {
        PhysicalPageOccupancySnapshot occupancy = await _pageSpace.AnalyzeOccupancyAsync(cancellationToken)
            .ConfigureAwait(false);
        AppendMergeMaintenanceStatus[] statuses = [_shared.GetMaintenanceStatus(),
            _concepts.GetMaintenanceStatus(),
            .. _relationships.MaintenanceStatuses,
            .. _fields.MaintenanceStatuses];
        // Read amplification is paid per selected access path, not across every independent path.
        // Summing paths made a small healthy database look like one severely amplified index.
        int runs = statuses.Length == 0 ? 0 : statuses.Max(item => item.RunCount);
        int retired = statuses.Sum(item => item.RetiredPageCount);
        double fragmentation = occupancy.AccessPathPageCount == 0 ? 0 :
            Math.Clamp((occupancy.UnusedBytes +
                        occupancy.UnownedInteriorPageCount * (long)occupancy.PageSize) /
                       (double)(occupancy.AccessPathPageCount * (long)occupancy.PageSize), 0, 1);
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        bool memoryPressure = memory.TotalAvailableMemoryBytes > 0 &&
                              memory.HeapSizeBytes >= memory.TotalAvailableMemoryBytes * .70;
        long estimatedWorkspaceBytes = checked(occupancy.AccessPathPageCount * (long)occupancy.PageSize);
        long availableMemoryBytes = memory.TotalAvailableMemoryBytes <= 0 ? long.MaxValue :
            Math.Max(0, memory.TotalAvailableMemoryBytes - memory.HeapSizeBytes);
        return new(fragmentation, runs, retired, Math.Max(1, runs), 0, memoryPressure, false,
            estimatedWorkspaceBytes, availableMemoryBytes, occupancy.UnownedInteriorPageCount);
    }

    public async ValueTask<OrderedPageTreeStage> PrepareOrderedReplacementAsync(Guid transactionId,
        Guid replacementId, CancellationToken cancellationToken)
    {
        (PhysicalMaintenanceTarget Target, PhysicalPageWorkspace Workspace) replacement;
        lock (_replacementGate)
        {
            replacement = _appendReplacements.GetValueOrDefault(replacementId);
        }

        if (replacement.Workspace is null || replacement.Target is not PhysicalMaintenanceTarget.Ordered)
        {
            throw new AppendMergeException("The Semantic ordered replacement is unavailable.");
        }

        long expected = _ranges.GetReplacementExpectedGeneration(replacementId);
        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            physical.OpenReplacementAccessPath(AccessPathId, AccessMethod, expected, replacement.Workspace);
            AppendMergeStage stage = _ranges.PrepareReplacement(transactionId, replacementId);
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _stagedRanges.Add(transactionId);
            _stagedMutations.Add(transactionId, []);
            lock (_replacementGate)
            {
                _appendReplacements.Remove(replacementId);
            }

            return new(transactionId, new(stage.Generation), directory.PhysicalMutations);
        }
        catch (PhysicalPageSpaceException) when (_ranges.Generation != expected)
        {
            _ranges.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            _ranges.AbandonReplacement(replacementId);
            lock (_replacementGate)
            {
                _appendReplacements.Remove(replacementId);
            }

            throw new OrderedPageTreeReplacementStaleException(expected, _ranges.Generation);
        }
        catch
        {
            _ranges.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            _ranges.AbandonReplacement(replacementId);
            lock (_replacementGate)
            {
                _appendReplacements.Remove(replacementId);
            }

            throw;
        }
    }

    public void AbandonOrderedReplacement(Guid replacementId)
    {
        AbandonAppendReplacement(replacementId);
    }

    public async ValueTask BuildAppendReplacementAsync(PhysicalMaintenanceTarget target,
        Guid replacementId, DatabaseVersion retentionFloor, CancellationToken cancellationToken,
        Func<SemanticAccessKey, byte[], bool>? retain = null, bool pruneEvidence = false)
    {
        PhysicalPageWorkspace workspace = _pageSpace.BeginReplacementWorkspace(replacementId);
        try
        {
            using var privateWrites = _checkpointCompaction?.EnterWorkspace(workspace);
            if (target is PhysicalMaintenanceTarget.Ordered)
            {
                await _ranges.BuildReplacementAsync(replacementId, retentionFloor.Value, workspace, false, cancellationToken);
            }
            else if (target is PhysicalMaintenanceTarget.Shared)
            {
                await _shared.BuildReplacementAsync(replacementId, retentionFloor.Value, workspace, false, cancellationToken, retain);
            }
            else if (target is PhysicalMaintenanceTarget.Concept)
            {
                await _concepts.BuildReplacementAsync(replacementId, retentionFloor.Value, workspace, false, cancellationToken);
            }
            else if (target is PhysicalMaintenanceTarget.OutgoingRelationship or
                     PhysicalMaintenanceTarget.IncomingRelationship)
            {
                await _relationships.BuildReplacementAsync(target, replacementId, retentionFloor.Value, workspace, cancellationToken);
            }
            else
            {
                await _fields.BuildReplacementAsync(target, replacementId, retentionFloor.Value, workspace, cancellationToken,
                    pruneEvidence ? (_, bytes) => _rows.DecodeField(bytes).EndVersion is not { } end || end.Value > retentionFloor.Value : null);
            }

            lock (_replacementGate)
            {
                _appendReplacements.Add(replacementId, (target, workspace));
            }
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public async ValueTask CatchUpReplacementAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        DatabaseVersion retentionFloor, CancellationToken token)
    {
        PhysicalPageWorkspace workspace;
        lock (_replacementGate)
        {
            workspace = _appendReplacements[replacementId].Workspace;
        }

        using var privateWrites = _checkpointCompaction?.EnterWorkspace(workspace);
        await CatchUpReplacementCoreAsync(target, replacementId, retentionFloor, token).ConfigureAwait(false);
    }

    private ValueTask CatchUpReplacementCoreAsync(PhysicalMaintenanceTarget target, Guid replacementId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken)
    {
        if (target is PhysicalMaintenanceTarget.Ordered)
        {
            return _ranges.CatchUpReplacementAsync(replacementId, retentionFloor.Value, false,
                cancellationToken);
        }

        if (target is PhysicalMaintenanceTarget.Shared)
        {
            return _shared.CatchUpReplacementAsync(replacementId, retentionFloor.Value, false,
                cancellationToken);
        }

        if (target is PhysicalMaintenanceTarget.Concept)
        {
            return _concepts.CatchUpReplacementAsync(replacementId, retentionFloor.Value, false,
                cancellationToken);
        }

        if (target is PhysicalMaintenanceTarget.OutgoingRelationship or
            PhysicalMaintenanceTarget.IncomingRelationship)
        {
            return _relationships.CatchUpReplacementAsync(target, replacementId,
                retentionFloor.Value, cancellationToken);
        }

        return _fields.CatchUpReplacementAsync(target, replacementId,
            retentionFloor.Value, cancellationToken);
    }

    public async ValueTask<OrderedPageTreeStage> PrepareAppendReplacementAsync(Guid transactionId,
        Guid replacementId, CancellationToken cancellationToken)
    {
        (PhysicalMaintenanceTarget target, PhysicalPageWorkspace workspace) replacement;
        lock (_replacementGate)
        {
            replacement = _appendReplacements.GetValueOrDefault(replacementId);
        }

        (PhysicalMaintenanceTarget target, PhysicalPageWorkspace workspace) = replacement;
        if (workspace is null)
        {
            throw new AppendMergeException("The Semantic append/merge replacement is unavailable.");
        }

        (PhysicalAccessPathId id, PhysicalAccessMethodKind method, long generation) = target switch
        {
            PhysicalMaintenanceTarget.Ordered =>
                (AccessPathId, AccessMethod, _ranges.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.Shared =>
                (SharedAccessPathId, SharedAccessMethod, _shared.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.Concept =>
                (ConceptAccessPathId, ConceptAccessMethod, _concepts.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.OutgoingRelationship or PhysicalMaintenanceTarget.IncomingRelationship =>
                _relationships.ReplacementMetadata(target, replacementId),
            _ => _fields.ReplacementMetadata(target, replacementId)
        };
        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            physical.OpenReplacementAccessPath(id, method, generation, workspace);
            AppendMergeStage stage;
            if (target is PhysicalMaintenanceTarget.Ordered)
            {
                stage = _ranges.PrepareReplacement(transactionId, replacementId);
                _stagedRanges.Add(transactionId);
            }
            else if (target is PhysicalMaintenanceTarget.Shared)
            {
                stage = _shared.PrepareReplacement(transactionId, replacementId);
                _stagedShared.Add(transactionId);
            }
            else if (target is PhysicalMaintenanceTarget.Concept)
            {
                stage = _concepts.PrepareReplacement(transactionId, replacementId);
                _stagedConcepts.Add(transactionId);
            }
            else if (target is PhysicalMaintenanceTarget.OutgoingRelationship or
                     PhysicalMaintenanceTarget.IncomingRelationship)
            {
                stage = _relationships.PrepareReplacement(transactionId, target, replacementId);
            }
            else
            {
                stage = _fields.PrepareReplacement(transactionId, target, replacementId);
            }

            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _stagedMutations.Add(transactionId, []);
            lock (_replacementGate)
            {
                _appendReplacements.Remove(replacementId);
            }

            return new(transactionId, new(stage.Generation), directory.PhysicalMutations);
        }
        catch (PhysicalPageSpaceException)
        {
            if (_stagedRanges.Remove(transactionId))
            {
                _ranges.Abandon(transactionId);
            }

            if (_stagedShared.Remove(transactionId))
            {
                _shared.Abandon(transactionId);
            }

            if (_stagedConcepts.Remove(transactionId))
            {
                _concepts.Abandon(transactionId);
            }

            _relationships.Abandon(transactionId);
            _fields.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            AbandonAppendReplacement(replacementId);
            long actual = _pageSpace.CaptureDirectory().Entries
                .Single(entry => entry.AccessPathId == id).Generation;
            if (actual != generation)
            {
                throw new AppendMergeReplacementStaleException(generation, actual);
            }

            throw;
        }
        catch
        {
            if (_stagedRanges.Remove(transactionId))
            {
                _ranges.Abandon(transactionId);
            }

            if (_stagedShared.Remove(transactionId))
            {
                _shared.Abandon(transactionId);
            }

            if (_stagedConcepts.Remove(transactionId))
            {
                _concepts.Abandon(transactionId);
            }

            _relationships.Abandon(transactionId);
            _fields.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            AbandonAppendReplacement(replacementId);
            throw;
        }
    }

    public void AbandonAppendReplacement(Guid replacementId)
    {
        (PhysicalMaintenanceTarget Target, PhysicalPageWorkspace Workspace) replacement;
        lock (_replacementGate)
        {
            if (!_appendReplacements.Remove(replacementId, out replacement))
            {
                return;
            }
        }

        if (replacement.Target is PhysicalMaintenanceTarget.Ordered)
        {
            _ranges.AbandonReplacement(replacementId);
        }
        else if (replacement.Target is PhysicalMaintenanceTarget.Shared)
        {
            _shared.AbandonReplacement(replacementId);
        }
        else if (replacement.Target is PhysicalMaintenanceTarget.Concept)
        {
            _concepts.AbandonReplacement(replacementId);
        }
        else if (replacement.Target is PhysicalMaintenanceTarget.OutgoingRelationship or
                 PhysicalMaintenanceTarget.IncomingRelationship)
        {
            _relationships.AbandonReplacement(replacement.Target, replacementId);
        }
        else
        {
            _fields.AbandonReplacement(replacement.Target, replacementId);
        }

        replacement.Workspace.Dispose();
    }

    public async ValueTask<OrderedPageTreeStage> PrepareReplacementBatchAsync(Guid transactionId,
        IReadOnlyList<(PhysicalMaintenanceTarget Target, Guid ReplacementId)> replacements,
        CancellationToken cancellationToken)
    {
        if (replacements.Count == 0)
        {
            throw new ArgumentException("A replacement batch must contain at least one target.", nameof(replacements));
        }

        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        var expectedGenerations = new Dictionary<PhysicalAccessPathId, long>(replacements.Count);
        try
        {
            foreach ((PhysicalMaintenanceTarget target, Guid replacementId) in replacements)
            {
                (PhysicalMaintenanceTarget storedTarget, PhysicalPageWorkspace appendWorkspace) appendReplacement;
                lock (_replacementGate)
                {
                    appendReplacement = _appendReplacements.GetValueOrDefault(replacementId);
                }

                (PhysicalMaintenanceTarget storedTarget, PhysicalPageWorkspace appendWorkspace) = appendReplacement;
                if (appendWorkspace is null || storedTarget != target)
                {
                    throw new AppendMergeException("The Semantic append/merge replacement is unavailable.");
                }

                (PhysicalAccessPathId id, PhysicalAccessMethodKind method, long generation) =
                    ReplacementMetadata(target, replacementId);
                expectedGenerations.Add(id, generation);
                physical.OpenReplacementAccessPath(id, method, generation, appendWorkspace);
                PrepareAppendReplacementStage(transactionId, target, replacementId);
            }

            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(physical, cancellationToken)
                .ConfigureAwait(false);
            _stagedMutations.Add(transactionId, []);
            foreach ((PhysicalMaintenanceTarget target, Guid replacementId) in replacements)
            {
                lock (_replacementGate)
                {
                    _appendReplacements.Remove(replacementId);
                }
            }
            return new(transactionId, new(_ranges.Generation), directory.PhysicalMutations);
        }
        catch (PhysicalPageSpaceException)
        {
            PhysicalPageSpaceDirectory published = _pageSpace.CaptureDirectory();
            KeyValuePair<PhysicalAccessPathId, long>? stale = expectedGenerations.Where(expected =>
                    published.Entries.Single(entry => entry.AccessPathId == expected.Key).Generation != expected.Value)
                .Select(expected => (KeyValuePair<PhysicalAccessPathId, long>?)expected).FirstOrDefault();
            Abandon(transactionId);
            foreach ((PhysicalMaintenanceTarget target, Guid replacementId) in replacements)
            {
                AbandonReplacement(target, replacementId);
            }

            if (stale is { } mismatch)
            {
                long actual = published.Entries.Single(entry => entry.AccessPathId == mismatch.Key).Generation;
                throw new AppendMergeReplacementStaleException(mismatch.Value, actual);
            }
            throw;
        }
        catch
        {
            Abandon(transactionId);
            foreach ((PhysicalMaintenanceTarget target, Guid replacementId) in replacements)
            {
                AbandonReplacement(target, replacementId);
            }

            throw;
        }
    }

    public void AbandonReplacement(PhysicalMaintenanceTarget target, Guid replacementId)
    {
        AbandonAppendReplacement(replacementId);
    }

    private (PhysicalAccessPathId Id, PhysicalAccessMethodKind Method, long Generation) ReplacementMetadata(
        PhysicalMaintenanceTarget target, Guid replacementId) => target switch
        {
            PhysicalMaintenanceTarget.Ordered =>
                (AccessPathId, AccessMethod, _ranges.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.Shared =>
                (SharedAccessPathId, SharedAccessMethod, _shared.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.Concept =>
                (ConceptAccessPathId, ConceptAccessMethod, _concepts.GetReplacementExpectedGeneration(replacementId)),
            PhysicalMaintenanceTarget.OutgoingRelationship or PhysicalMaintenanceTarget.IncomingRelationship =>
                _relationships.ReplacementMetadata(target, replacementId),
            _ => _fields.ReplacementMetadata(target, replacementId)
        };

    private void PrepareAppendReplacementStage(Guid transactionId, PhysicalMaintenanceTarget target,
        Guid replacementId)
    {
        if (target is PhysicalMaintenanceTarget.Ordered)
        {
            _ranges.PrepareReplacement(transactionId, replacementId);
            _stagedRanges.Add(transactionId);
        }
        else if (target is PhysicalMaintenanceTarget.Shared)
        {
            _shared.PrepareReplacement(transactionId, replacementId);
            _stagedShared.Add(transactionId);
        }
        else if (target is PhysicalMaintenanceTarget.Concept)
        {
            _concepts.PrepareReplacement(transactionId, replacementId);
            _stagedConcepts.Add(transactionId);
        }
        else if (target is PhysicalMaintenanceTarget.OutgoingRelationship or
                 PhysicalMaintenanceTarget.IncomingRelationship)
        {
            _relationships.PrepareReplacement(transactionId, target, replacementId);
        }
        else
        {
            _fields.PrepareReplacement(transactionId, target, replacementId);
        }
    }

    public ValueTask FlushReplacementPagesAsync(CancellationToken cancellationToken) =>
        _pages.FlushAsync(cancellationToken);

    public IReadOnlyList<PhysicalMaintenanceTarget> GetVacuumTargets()
    {
        var targets = new Dictionary<PhysicalAccessPathId, PhysicalMaintenanceTarget>
        {
            [AccessPathId] = PhysicalMaintenanceTarget.Ordered,
            [SharedAccessPathId] = PhysicalMaintenanceTarget.Shared,
            [ConceptAccessPathId] = PhysicalMaintenanceTarget.Concept,
            [SemanticDirectionalRelationshipAccessPaths.OutgoingId] =
                PhysicalMaintenanceTarget.OutgoingRelationship,
            [SemanticDirectionalRelationshipAccessPaths.IncomingId] =
                PhysicalMaintenanceTarget.IncomingRelationship,
            [SemanticFieldAccessPaths.EvidenceId] = PhysicalMaintenanceTarget.FieldEvidence,
            [SemanticFieldAccessPaths.EqualityId] = PhysicalMaintenanceTarget.FieldEquality,
            [SemanticFieldAccessPaths.SubjectId] = PhysicalMaintenanceTarget.SubjectField
        };
        return _pageSpace.CaptureDirectory().Entries
            .Where(entry => targets.ContainsKey(entry.AccessPathId))
            .OrderByDescending(entry => entry.Extents.Select(extent => extent.EndExclusive).DefaultIfEmpty(-1).Max())
            .Select(entry => targets[entry.AccessPathId])
            .ToArray();
    }

    public async ValueTask<OrderedPageTreeStage?> PrepareMaintenanceAsync(Guid transactionId,
        DatabaseVersion retentionFloor, CancellationToken cancellationToken) => await PrepareMaintenanceCoreAsync(transactionId,
        vacuumTarget: null, retentionFloor, cancellationToken).ConfigureAwait(false);

    public ValueTask<OrderedPageTreeStage?> PrepareCheckpointMaintenanceAsync(Guid id, DatabaseVersion version, bool compact, CancellationToken token) =>
        compact && _checkpointCompaction is not null
            ? PrepareIncrementalCompactionPublicationAsync(id, token)
            : PrepareMaintenanceCoreAsync(id, null, version, token, checkpoint: true, compact, version);

    public async ValueTask<OrderedPageTreeStage?> PrepareVacuumAsync(Guid transactionId,
        PhysicalMaintenanceTarget target, DatabaseVersion retentionFloor, CancellationToken cancellationToken) =>
        await PrepareMaintenanceCoreAsync(transactionId, target, retentionFloor, cancellationToken).ConfigureAwait(false);

    private async ValueTask<OrderedPageTreeStage?> PrepareMaintenanceCoreAsync(Guid transactionId,
        PhysicalMaintenanceTarget? vacuumTarget, DatabaseVersion retentionFloor, CancellationToken cancellationToken, bool checkpoint = false, bool compact = false,
        DatabaseVersion? pruneThrough = null)
    {
        bool forceVacuum = vacuumTarget is not null;
        bool reclaimOnly = checkpoint && !compact;
        Func<SemanticAccessKey, byte[], bool>? retain = compact && _retentionMayBeNeeded && pruneThrough is { } floor
            ? await CaptureRetentionFilterAsync(floor, cancellationToken).ConfigureAwait(false) : null;
        PhysicalPageSpaceTransaction physical = forceVacuum || checkpoint
            ? _pageSpace.BeginVacuumTransaction(transactionId)
            : _pageSpace.BeginTransaction(transactionId);
        var accessMutations = new List<DatabasePhysicalPageMutation>();
        try
        {
            AppendMergeMaintenanceStatus rangeStatus = _ranges.GetMaintenanceStatus();
            _telemetry.MaintenanceStatus(SemanticProjectionRole.StringRange, rangeStatus);
            if (compact && rangeStatus.RunCount > 0 || rangeStatus.NeedsMaintenance && !forceVacuum ||
                vacuumTarget is PhysicalMaintenanceTarget.Ordered)
            {
                long started = Stopwatch.GetTimestamp();
                PhysicalAccessPathAllocationScope allocation = physical.OpenAccessPath(
                    AccessPathId, AccessMethod, _ranges.Generation);
                AppendMergeMaintenanceStage? maintenance = forceVacuum || compact
                    ? await _ranges.PrepareVacuumAsync(transactionId, retentionFloor.Value, allocation, cancellationToken)
                        .ConfigureAwait(false)
                    : await _ranges.PrepareMaintenanceAsync(transactionId, retentionFloor.Value, allocation, cancellationToken, reclaimOnly)
                        .ConfigureAwait(false);
                if (maintenance is not null)
                {
                    _telemetry.Maintenance(SemanticProjectionRole.StringRange,
                        maintenance, Stopwatch.GetElapsedTime(started));
                    accessMutations.AddRange(maintenance.Stage.PhysicalMutations);
                    _stagedRanges.Add(transactionId);
                }
                else
                {
                    physical.CancelAccessPath(allocation);
                }
            }
            AppendMergeMaintenanceStatus sharedStatus = _shared.GetMaintenanceStatus();
            _telemetry.MaintenanceStatus(SemanticProjectionRole.SharedSemanticExact, sharedStatus);
            if (compact && sharedStatus.RunCount > 0 || sharedStatus.NeedsMaintenance && !forceVacuum ||
                vacuumTarget is PhysicalMaintenanceTarget.Shared)
            {
                long started = Stopwatch.GetTimestamp();
                PhysicalAccessPathAllocationScope allocation = physical.OpenAccessPath(
                    SharedAccessPathId, SharedAccessMethod, _shared.Generation);
                AppendMergeMaintenanceStage? maintenance = forceVacuum || compact
                    ? await _shared.PrepareVacuumAsync(transactionId, retentionFloor.Value, allocation,
                        cancellationToken, retain).ConfigureAwait(false)
                    : await _shared.PrepareMaintenanceAsync(transactionId, retentionFloor.Value, allocation,
                        cancellationToken, reclaimOnly).ConfigureAwait(false);
                if (maintenance is not null)
                {
                    _telemetry.Maintenance(SemanticProjectionRole.SharedSemanticExact, maintenance,
                        Stopwatch.GetElapsedTime(started));
                    accessMutations.AddRange(maintenance.Stage.PhysicalMutations);
                    _stagedShared.Add(transactionId);
                }
                else
                {
                    physical.CancelAccessPath(allocation);
                }
            }
            AppendMergeMaintenanceStatus conceptStatus = _concepts.GetMaintenanceStatus();
            _telemetry.MaintenanceStatus(SemanticProjectionRole.ConceptMembership, conceptStatus);
            if (compact && conceptStatus.RunCount > 0 || conceptStatus.NeedsMaintenance && !forceVacuum ||
                vacuumTarget is PhysicalMaintenanceTarget.Concept)
            {
                long started = Stopwatch.GetTimestamp();
                PhysicalAccessPathAllocationScope allocation = physical.OpenAccessPath(
                    ConceptAccessPathId, ConceptAccessMethod, _concepts.Generation);
                AppendMergeMaintenanceStage? maintenance = forceVacuum || compact
                    ? await _concepts.PrepareVacuumAsync(transactionId, retentionFloor.Value, allocation,
                        cancellationToken).ConfigureAwait(false)
                    : await _concepts.PrepareMaintenanceAsync(transactionId, retentionFloor.Value, allocation,
                        cancellationToken, reclaimOnly).ConfigureAwait(false);
                if (maintenance is not null)
                {
                    _telemetry.Maintenance(SemanticProjectionRole.ConceptMembership, maintenance,
                        Stopwatch.GetElapsedTime(started));
                    accessMutations.AddRange(maintenance.Stage.PhysicalMutations);
                    _stagedConcepts.Add(transactionId);
                }
                else
                {
                    physical.CancelAccessPath(allocation);
                }
            }
            accessMutations.AddRange(forceVacuum
                ? await _relationships.PrepareVacuumAsync(transactionId, vacuumTarget!.Value,
                    physical, _telemetry, cancellationToken, retentionFloorVersion: retentionFloor.Value).ConfigureAwait(false)
                : await _relationships.PrepareMaintenanceAsync(transactionId, physical, _telemetry,
                    cancellationToken, compact, retentionFloorVersion: retentionFloor.Value, reclaimOnly: reclaimOnly).ConfigureAwait(false));
            accessMutations.AddRange(forceVacuum
                ? await _fields.PrepareVacuumAsync(transactionId, vacuumTarget!.Value,
                    physical, _telemetry, cancellationToken, retentionFloorVersion: retentionFloor.Value).ConfigureAwait(false)
                : await _fields.PrepareMaintenanceAsync(transactionId, physical, _telemetry,
                    cancellationToken, compact, pruneThrough, retentionFloor.Value, reclaimOnly).ConfigureAwait(false));
            if (accessMutations.Count == 0)
            {
                _pageSpace.Abandon(transactionId);
                return null;
            }
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _stagedMutations.Add(transactionId, accessMutations);
            // Layout-only maintenance preserves membership. Pruning must rebuild from its new root.
            if (retain is null)
            {
                CarryAtomAdmission(transactionId);
            }
            // Maintenance preserves the sequence record even when it prunes atoms:
            // ordinals are never renumbered. Carry only a cache validated for this
            // exact source generation; Publish installs it, Abandon discards it.
            if (_publishedAtomSequence is { } sequence && sequence.Generation == _shared.Generation)
            {
                _stagedUnchangedAtomSequences.Add(transactionId, sequence);
            }

            return new(transactionId, new(_ranges.Generation),
                accessMutations.Concat(directory.PhysicalMutations).ToArray());
        }
        catch
        {
            if (_stagedRanges.Remove(transactionId))
            {
                _ranges.Abandon(transactionId);
            }

            if (_stagedShared.Remove(transactionId))
            {
                _shared.Abandon(transactionId);
            }

            if (_stagedConcepts.Remove(transactionId))
            {
                _concepts.Abandon(transactionId);
            }

            _relationships.Abandon(transactionId);
            _fields.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            throw;
        }
    }
}
