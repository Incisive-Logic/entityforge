using EntityForge.Db.Mapping;
using System.Diagnostics;
using System.Security.Cryptography;

using EntityForge.Db.Durability;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    internal async ValueTask PrepareCheckpointChangesAsync(CancellationToken token)
    {
        if (_changeStore is IDatabaseCommittedChangeCheckpointPreparation preparation)
        {
            await preparation.PrepareCheckpointAsync(token).ConfigureAwait(false);
        }

        if (_checkpointStore is IDatabaseCheckpointImagePreparation imagePreparation)
        {
            await imagePreparation.PrepareImageAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>Persists a checkpoint of the current profile and kernel recovery state.</summary>
    /// <param name="cancellationToken">Cancels cancellable checkpoint work; publication or reclamation already completed is not rolled back.</param>
    /// <returns>The durable checkpoint envelope, including any refreshed profile state following page maintenance.</returns>
    /// <remarks>
    /// The composition must support profile checkpointing and the kernel must be ready. Timestamp protections
    /// and prepared partition transactions can require a different, snapshot-preserving checkpoint path.
    /// This operation can perform eligible page maintenance and apply the configured commit-log retention policy.
    /// A checkpoint does not copy the database's storage files into a backup. Use the storage backup facilities
    /// or coordinated checkpoint-image capture for a recoverable copy; do not copy live files independently.
    /// Cancellation or a later failure does not imply that no checkpoint was persisted. A failure requiring
    /// recovery leaves the kernel unavailable for ordinary operations. Dispose and reopen the database;
    /// recovery cannot be repeated on the existing kernel instance.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Checkpointing is unavailable, the kernel is not ready, or active protections prevent this checkpoint mode.</exception>
    /// <exception cref="EntityForgeDbRecoveryRequiredException">The database must be reopened for recovery before checkpointing can continue.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested during cancellable work.</exception>
    public ValueTask<DurableCheckpointEnvelope> CreateCheckpointAsync(CancellationToken cancellationToken = default) =>
        CreateCheckpointCoreAsync(null, 0, null, cancellationToken);

    /// <summary>Creates a checkpoint and captures its storage image while checkpoint publication and commits are excluded.</summary>
    /// <typeparam name="T">The capture operation's result type.</typeparam>
    /// <param name="capture">Copies or verifies the checkpoint's storage files and returns the capture result.</param>
    /// <param name="cancellationToken">Cancels checkpoint creation or image capture.</param>
    /// <returns>The result returned by <paramref name="capture"/>.</returns>
    /// <remarks>
    /// Intended for storage composition and backup services. The callback must not perform commits,
    /// checkpoints, or other operations requiring the kernel's commit or publication gates, because
    /// those gates remain held until it finishes. Existing read snapshots are preserved. A failed
    /// capture does not roll back the checkpoint; the caller owns cleanup of any incomplete copy.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The capture callback is null.</exception>
    /// <exception cref="InvalidOperationException">This composition does not support checkpointing.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested.</exception>
    public async ValueTask<T> CaptureCheckpointImageAsync<T>(
        Func<CancellationToken, ValueTask<T>> capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        T result = default!;
        await CreateCheckpointCoreAsync(null, 0, null, cancellationToken, preserveSnapshots: true,
            captureImage: async () => result = await capture(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);
        return result;
    }

    internal ValueTask<DurableCheckpointEnvelope> RetainOperationsAsync(Guid leaseId, long floor, IReadOnlySet<Guid> expired,
        CancellationToken cancellationToken) => CreateCheckpointCoreAsync(leaseId, floor, expired, cancellationToken);

    internal static readonly AsyncLocal<Func<CancellationToken, ValueTask>?> BeforeDeferredCheckpointEncoding = new();

    private async ValueTask<DurableCheckpointEnvelope> CreateCheckpointCoreAsync(Guid? leaseId, long floor,
        IReadOnlySet<Guid>? expired, CancellationToken cancellationToken, long? localFloor = null, bool partitionRetention = false, bool preserveSnapshots = false, Func<ValueTask>? captured = null, Func<ValueTask>? captureImage = null)
    {
        if (_checkpointStore is null || _profile is not ICheckpointableDatabaseProfileRuntime profile)
        {
            throw new InvalidOperationException("This database composition does not provide profile checkpointing.");
        }

        long publicationWaitStarted = Stopwatch.GetTimestamp();
        long publicationWaitAllocated = GC.GetTotalAllocatedBytes();
        await _checkpointPublication.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool coordinatorHeld = false;
        bool checkpointAttempted = false;
        IDisposable? maintenanceAdmission = null;
        try
        {
            _telemetry.CheckpointPhase(_profile.Profile, "publication_gate_wait", publicationWaitStarted, publicationWaitAllocated);
            long coordinatorWaitStarted = Stopwatch.GetTimestamp();
            long coordinatorWaitAllocated = GC.GetTotalAllocatedBytes();
            await _commitCoordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            coordinatorHeld = true;
            _telemetry.CheckpointPhase(_profile.Profile, "coordinator_gate_wait", coordinatorWaitStarted, coordinatorWaitAllocated);
            EnsureReady();
            // Explicit local retention holds local write authority; distributed
            // reclamation holds its drained partition fence. Both can rewrite pages.
            // Ordinary local checkpoints may reclaim already retired pages without
            // expiring operation history or forcing a full live-index rewrite.
            bool localPageReuse = VersioningMode == DatabaseVersioningMode.LocalOrAuthoritative &&
                !IsDistributedWriteMode && !partitionRetention &&
                leaseId is null && localFloor is null && !preserveSnapshots &&
                _profile is IDatabaseProfileCheckpointPageReuse
                {
                    SupportsLocalRetentionMaintenance: true, RequiresCheckpointMaintenance: true
                };
            bool maintainPages = localPageReuse || localFloor is not null && !partitionRetention &&
                _profile is IDatabaseProfileCheckpointMaintenance { SupportsLocalRetentionMaintenance: true } ||
                leaseId is not null || partitionRetention &&
                _profile is IDatabaseProfileCheckpointMaintenance { RequiresCheckpointMaintenance: true };
            bool canDetach = captured is not null && partitionRetention && !preserveSnapshots && leaseId is null &&
                _historyRetention is CheckpointDatabaseHistoryRetentionPolicy &&
                _profile is IDatabaseProfileConcurrentCheckpoint &&
                _checkpointStore is IConcurrentDatabaseCheckpointStore { SupportsConcurrentPublication: true } &&
                _commitLog is IConcurrentCheckpointCommitLog;
            bool concurrent = !maintainPages && canDetach;
            await DiscardPhysicalPreparationAsync().ConfigureAwait(false);
            if (TimestampProtectionCount != 0 && (localFloor is not null || leaseId is not null || !preserveSnapshots))
            {
                throw new InvalidOperationException("Timestamp reader protections must drain before reclamation or an unpinned checkpoint.");
            }

            if (!preserveSnapshots && _partitionReservations.Count != 0)
            {
                throw new InvalidOperationException("Prepared partition transactions require a snapshot-preserving protocol checkpoint.");
            }

            long floorStarted = Stopwatch.GetTimestamp();
            long floorAllocatedBefore = GC.GetTotalAllocatedBytes();
            var (retainedCatalog, retainedCatalogBytes, retainedExpired) =
                PrepareCheckpointRetention(leaseId, floor, localFloor, partitionRetention, expired);
            expired = retainedExpired;
            maintenanceAdmission = maintainPages && _profile is IDatabaseProfileCheckpointMaintenance maintenanceProfile
                ? maintenanceProfile.EnterMaintenance() : null;
            if (localFloor is not null || leaseId is not null)
            {
                _telemetry.CheckpointPhase(_profile.Profile, "floor_prepare", floorStarted, floorAllocatedBefore);
            }

            DatabaseVersion version = CurrentVersion;
            // Protocol records do not advance this version. Capture their independent
            // durable prefix under the same exclusion as the checkpoint state.
            long? checkpointPosition = VersioningMode == DatabaseVersioningMode.PartitionLocal &&
                _commitLog is ICheckpointPositionDatabaseCommitLog positioned ? positioned.CaptureCheckpointPosition() : null;
            var (changeBounds, content, encode) = await CaptureCheckpointProfileAsync(
                profile, version, concurrent, preserveSnapshots, expired, retainedCatalog, cancellationToken)
                .ConfigureAwait(false);
            long phaseStarted;
            long allocatedBefore;
            phaseStarted = Stopwatch.GetTimestamp();
            allocatedBefore = GC.GetTotalAllocatedBytes();
            var checkpoint = new DurableCheckpointEnvelope(
                _databaseIncarnation,
                version,
                new DurableProfilePayload(profile.Profile, profile.CheckpointCodecId,
                    profile.CheckpointCodecVersion, content),
                _committedTransactions.Where(item => expired is null || !expired.Contains(item.Key)).Select(item => new DurableCommittedTransaction(
                    item.Key, item.Value.Version, item.Value.ContentHash,
                    item.Value.ChangeKind, item.Value.EffectCount, item.Value.ApplicationOperationId)
                {
                    Timestamp = item.Value.Timestamp
                }).ToArray(),
                changeBounds.Current, leaseId is null ? _authoritativeOperationFloor : floor, localFloor ?? _localOperationFloor, StorageLayout, VersioningMode)
            {
                Timestamp = _timestampState,
                PartitionTransactions = _partitionTransactions.Values.Where(p => !partitionRetention ||
                !EntityOperationIds.IsBefore(p.Operation, localFloor!.Value)).ToArray()
            };
            _telemetry.CheckpointPhase(_profile.Profile, "envelope", phaseStarted, allocatedBefore);

            IDatabaseCheckpointPublication? publication = null;
            if (concurrent)
            {
                checkpointPosition = await ((IConcurrentCheckpointCommitLog)_commitLog)
                    .BeginCheckpointEpochAsync(cancellationToken).ConfigureAwait(false);
                publication = await ((IConcurrentDatabaseCheckpointStore)_checkpointStore)
                    .PreparePublicationAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                // Install the admission floor and prune ONLY the captured old identities while
                // still excluded. Never restore a captured catalogue over later commits.
                checkpointAttempted = true;
                PublishCheckpointRetention(profile, leaseId, floor, localFloor, partitionRetention, retainedCatalogBytes, expired);
                _commitCoordinator.Release();
                coordinatorHeld = false;
                await captured!().ConfigureAwait(false);
            }

            phaseStarted = Stopwatch.GetTimestamp();
            allocatedBefore = GC.GetTotalAllocatedBytes();
            checkpointAttempted = true;
            try
            {
                if (encode is not null)
                {
                    if (BeforeDeferredCheckpointEncoding.Value is { } beforeEncoding)
                    {
                        await beforeEncoding(cancellationToken).ConfigureAwait(false);
                    }

                    long encodingStarted = Stopwatch.GetTimestamp();
                    long encodingAllocated = GC.GetTotalAllocatedBytes();
                    content = encode(cancellationToken);
                    if (retainedCatalog is not null)
                    {
                        content = EntityOperationCodec.ReplaceCheckpointCatalog(content, retainedCatalog);
                    }

                    checkpoint = checkpoint.WithProfileState(new DurableProfilePayload(profile.Profile,
                        profile.CheckpointCodecId, profile.CheckpointCodecVersion, content));
                    _telemetry.CheckpointPhase(_profile.Profile, "detached_profile_encoding", encodingStarted, encodingAllocated);
                    await ((IDatabaseDeferredCheckpointPublication)publication!).PublishAsync(checkpoint.ProfileState, cancellationToken).ConfigureAwait(false);
                }
                else if (publication is null)
                {
                    await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await publication.PublishAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                if (leaseId is not null)
                {
                    _state = DatabaseKernelState.RecoveryRequired;
                }

                throw;
            }
            _telemetry.CheckpointPhase(_profile.Profile, "persistence", phaseStarted, allocatedBefore);
            if (!coordinatorHeld)
            {
                await _commitCoordinator.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                coordinatorHeld = true;
                EnsureReady();
            }
            long publicationStarted = Stopwatch.GetTimestamp();
            long publicationAllocated = GC.GetTotalAllocatedBytes();
            if (!concurrent)
            {
                PublishCheckpointRetention(profile, leaseId, floor, localFloor, partitionRetention, retainedCatalogBytes, expired);
            }

            _telemetry.CheckpointPhase(_profile.Profile, "retention_publication", publicationStarted, publicationAllocated);
            _telemetry.CheckpointPhase(_profile.Profile, "save", phaseStarted, allocatedBefore);
            _telemetry.Checkpoint(_profile.Profile, content.Length);

            if (!preserveSnapshots && _profile is IDatabaseProfileCheckpointPublicationObserver publicationObserver)
            {
                await publicationObserver.CheckpointPublishedAsync(version, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (maintainPages && version.Value > 0 && _profile is IDatabaseProfileCheckpointMaintenance maintenance)
            {
                // Partition space reclamation needs safe page reuse, not two full live-index
                // rewrites on every retention tick. Keep explicit legacy vacuum behavior.
                var reuse = partitionRetention || localPageReuse ? maintenance as IDatabaseProfileCheckpointPageReuse : null;
                long maintenanceStarted = Stopwatch.GetTimestamp();
                long maintenanceAllocated = GC.GetTotalAllocatedBytes();
                await MaintainCheckpointPagesAsync(maintenance, version, cancellationToken, compact: reuse is null).ConfigureAwait(false);
                _telemetry.CheckpointPhase(_profile.Profile, "page_maintenance", maintenanceStarted, maintenanceAllocated);
                bool compacted = reuse is null;
                if (reuse?.RequiresCheckpointCompaction == true)
                {
                    var incremental = maintenance as IDatabaseProfileIncrementalCheckpointCompaction;
                    bool ready = incremental is null || await AdvanceCheckpointCompactionAsync(
                        incremental, version, cancellationToken).ConfigureAwait(false);
                    if (ready)
                    {
                        await MaintainCheckpointPagesAsync(maintenance, version, cancellationToken,
                            compactionPasses: incremental is null ? 2 : 1).ConfigureAwait(false);
                        compacted = true;
                    }
                }
                // Maintenance can change page bytes and advance redo without changing logical state.
                // Rebind the durable checkpoint before backup/archive may authorize reclamation.
                try
                {
                    // Compact profile references must describe the replacement roots,
                    // rather than the roots captured before the physical rewrite.
                    var refreshed = await CaptureCheckpointProfileAsync(profile, version, false,
                        preserveSnapshots, expired, retainedCatalog, cancellationToken).ConfigureAwait(false);
                    checkpoint = checkpoint.WithProfileState(new DurableProfilePayload(profile.Profile,
                        profile.CheckpointCodecId, profile.CheckpointCodecVersion, refreshed.Content));
                    await _checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                    reuse?.AuthorizeCheckpointPageReuse(compacted);
                    if (_commitLog is ICheckpointPositionDatabaseCommitLog maintainedLog)
                    {
                        checkpointPosition = maintainedLog.CaptureCheckpointPosition();
                    }
                }
                catch
                {
                    _state = DatabaseKernelState.RecoveryRequired;
                    throw;
                }
            }

            if (maintainPages && canDetach)
            {
                // The rewritten roots and their recovery binding are now durable. Rotate
                // full-page protection before admitting any post-checkpoint mutations.
                checkpointPosition = await ((IConcurrentCheckpointCommitLog)_commitLog)
                    .BeginCheckpointEpochAsync(CancellationToken.None).ConfigureAwait(false);
                maintenanceAdmission?.Dispose();
                maintenanceAdmission = null;
                _commitCoordinator.Release();
                coordinatorHeld = false;
                await captured!().ConfigureAwait(false);
            }
            else if (concurrent)
            {
                _commitCoordinator.Release();
                coordinatorHeld = false;
            }
            await ReclaimCheckpointHistoryAsync(checkpoint, version, checkpointPosition, preserveSnapshots).ConfigureAwait(false);

            if (captureImage is not null)
            {
                // Keep both gates until the checkpoint, pages and change journal
                // have been copied together. Later commits may mutate page bytes
                // without replacing the checkpoint file.
                await captureImage().ConfigureAwait(false);
            }

            StartPrivateCheckpointCompaction(version);

            return checkpoint;
        }
        catch
        {
            if (!coordinatorHeld)
            {
                await _commitCoordinator.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                coordinatorHeld = true;
            }
            if (localFloor is not null && checkpointAttempted)
            {
                _state = DatabaseKernelState.RecoveryRequired;
                if (_profile is IEntityOperationCompactionProfile failedOperations)
                {
                    failedOperations.OperationCatalog.RetentionFailed();
                }
            }
            throw;
        }
        finally
        {
            try
            {
                maintenanceAdmission?.Dispose();
            }
            finally
            {
                if (coordinatorHeld)
                {
                    _commitCoordinator.Release();
                }

                _checkpointPublication.Release();
            }
        }
    }

    private async ValueTask<bool> AdvanceCheckpointCompactionAsync(
        IDatabaseProfileIncrementalCheckpointCompaction incremental, DatabaseVersion version, CancellationToken token,
        DatabaseVersion? coveringCheckpoint = null)
    {
        if (_commitLog is not IPhysicalDatabaseCommitLog log)
        {
            throw new InvalidOperationException("Incremental maintenance requires physical redo durability.");
        }

        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetTotalAllocatedBytes();
        try
        {
            return await incremental.AdvanceCheckpointCompactionAsync(version, async (mutations, cancellation) =>
            {
                var envelope = new DurableTransactionEnvelope(_databaseIncarnation, version, Guid.NewGuid(),
                    new(_profile.Profile, _profile.CodecId, _profile.CodecVersion,
                        coveringCheckpoint is { } covering ? PrivateCompactionRecord.Encode(covering.Value) : ReadOnlyMemory<byte>.Empty),
                    DurableCommittedChangeKind.CheckpointMaintenance, null, StorageLayout, VersioningMode);
                await log.CommitWithPhysicalMutationsAsync(envelope, mutations, cancellation).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch
        {
            // A slice may already have durable batches, even if a later read or
            // flush fails. Stop admission until physical redo recovery completes.
            RequireRecovery("checkpoint_compaction_slice");
            throw;
        }
        finally
        {
            _telemetry.CheckpointPhase(_profile.Profile, "compaction_slice", started, allocated);
        }
    }

    private (EntityOperationCatalog? Catalog, byte[]? CatalogBytes, IReadOnlySet<Guid>? Expired)
        PrepareCheckpointRetention(Guid? leaseId, long floor, long? localFloor,
            bool partitionRetention, IReadOnlySet<Guid>? expired)
    {
        EntityOperationCatalog? retainedCatalog = null;
        byte[]? retainedCatalogBytes = null;
        if (localFloor is { } cutoff)
        {
            if (IsDistributedWriteMode || cutoff < _localOperationFloor)
            {
                throw new InvalidOperationException("Local retention requires local ownership and a monotonic cutoff.");
            }

            lock (_transferRetentionSync)
            {
                if (_transferRetentions.Count != 0)
                {
                    throw new InvalidOperationException("Transfer history must drain before retention.");
                }
            }

            if (_profile is IEntityOperationCompactionProfile operationProfile)
            {
                RestoreCommittedOperationProjections();
                retainedCatalog = operationProfile.OperationCatalog.RetainedCopy(cutoff);
                retainedCatalogBytes = retainedCatalog.Capture();
            }
            // Some role profiles own only common partition transaction evidence,
            // so they have no independent mapped-operation catalogue to compact.
            else if (!partitionRetention)
            {
                throw new NotSupportedException("The profile does not support local operation retention.");
            }

            expired = _committedTransactions.Where(entry => entry.Value.ApplicationOperationId is { } id &&
                EntityOperationIds.IsBefore(id, cutoff)).Select(entry => entry.Key).ToHashSet();
            if (partitionRetention)
            {
                RequirePartitionTransactions();
                if (_partitionTransactions.Values.Any(p => p.State == DurablePartitionTransactionState.Prepared))
                {
                    throw new InvalidOperationException("Unresolved partition preparation prevents retention.");
                }

                expired = expired.Concat(_partitionTransactions.Keys.Where(id => EntityOperationIds.IsBefore(id, cutoff))).ToHashSet();
            }
        }
        if (leaseId is not null)
        {
            if (_distributedWriteLeaseId != leaseId || Volatile.Read(ref _distributedWriteMode) == 0 || floor < _authoritativeOperationFloor)
            {
                throw new InvalidOperationException("Retention requires the current distributed enrollment and a monotonic horizon.");
            }

            if (expired is null || expired.Any(id => !EntityOperationIds.IsBefore(id, floor)))
            {
                throw new InvalidOperationException("Only expired distributed identities may be reclaimed.");
            }

            lock (_transferRetentionSync)
            {
                if (_transferRetentions.Count != 0)
                {
                    throw new InvalidOperationException("Transfer history must drain before retention.");
                }
            }
        }
        return (retainedCatalog, retainedCatalogBytes, expired);
    }

    // Called only with commit coordination held, before detached encoding or after
    // ordinary publication. Never restores an old catalog over subsequent commits.
    private void PublishCheckpointRetention(ICheckpointableDatabaseProfileRuntime profile,
        Guid? leaseId, long floor, long? localFloor, bool partitionRetention,
        byte[]? retainedCatalogBytes, IReadOnlySet<Guid>? expired)
    {
        if (localFloor is { } publishedFloor)
        {
            _localOperationFloor = publishedFloor;
            _snapshotCertificate = null; // Do not retain reclaimed preparation payloads through the derived cache.
            if (_profile is IEntityOperationCompactionProfile retainedOperations)
            {
                retainedOperations.OperationCatalog.PublishRetention(retainedCatalogBytes!, publishedFloor);
            }

            if (partitionRetention)
            {
                foreach (var id in _partitionTransactions.Keys.Where(id => EntityOperationIds.IsBefore(id, publishedFloor)).ToArray())
                {
                    _partitionTransactions.Remove(id);
                    _unresolvedTimestampPreparations.Remove(id);
                }
            }
        }
        if (leaseId is not null || localFloor is not null)
        {
            if (leaseId is not null)
            {
                _authoritativeOperationFloor = floor;
            }

            foreach (var id in expired!)
            {
                _committedTransactions.Remove(id);
            }

            try
            {
                if (profile is IDatabaseProfileOperationRetention retainedProfile)
                {
                    retainedProfile.RetentionCheckpointPublished(expired!);
                }
            }
            catch
            {
                _state = DatabaseKernelState.RecoveryRequired;
                throw;
            }
        }
    }

    private async ValueTask<(DatabaseChangeFeedBounds Bounds, ReadOnlyMemory<byte> Content,
        Func<CancellationToken, ReadOnlyMemory<byte>>? Encode)> CaptureCheckpointProfileAsync(
        ICheckpointableDatabaseProfileRuntime profile, DatabaseVersion version, bool concurrent,
        bool preserveSnapshots, IReadOnlySet<Guid>? expired, EntityOperationCatalog? retainedCatalog,
        CancellationToken cancellationToken)
    {
        long phaseStarted = Stopwatch.GetTimestamp();
        long allocatedBefore = GC.GetTotalAllocatedBytes();
        if (_profile is IDatabaseProfileCheckpointPreparation preparation)
        {
            await PrepareCheckpointMetadataAsync(preparation, version, cancellationToken).ConfigureAwait(false);
        }

        if (!concurrent && _profile is IDatabaseProfileDurabilityBarrier durabilityBarrier)
        {
            await durabilityBarrier.FlushPhysicalStateAsync(cancellationToken).ConfigureAwait(false);
        }

        _telemetry.CheckpointPhase(_profile.Profile, "physical_flush", phaseStarted, allocatedBefore);
        phaseStarted = Stopwatch.GetTimestamp();
        allocatedBefore = GC.GetTotalAllocatedBytes();
        if (_changeStore is IDatabaseCommittedChangeDurabilityBarrier changeBarrier)
        {
            await changeBarrier.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _telemetry.CheckpointPhase(_profile.Profile, "change_flush", phaseStarted, allocatedBefore);
        phaseStarted = Stopwatch.GetTimestamp();
        allocatedBefore = GC.GetTotalAllocatedBytes();
        DatabaseChangeFeedBounds changeBounds = await _changeStore.GetBoundsAsync(cancellationToken)
            .ConfigureAwait(false);
        _telemetry.CheckpointPhase(_profile.Profile, "change_bounds", phaseStarted, allocatedBefore);
        phaseStarted = Stopwatch.GetTimestamp();
        allocatedBefore = GC.GetTotalAllocatedBytes();
        Func<CancellationToken, ReadOnlyMemory<byte>>? encode = concurrent &&
            _checkpointStore is IConcurrentDatabaseCheckpointStore { SupportsDeferredProfileContent: true } &&
            profile is IDatabaseProfileDeferredCheckpoint deferred
            ? await deferred.CaptureCheckpointEncoderAsync(preserveSnapshots,
                preserveSnapshots ? null : expired, cancellationToken).ConfigureAwait(false) : null;
        ReadOnlyMemory<byte> content = encode is not null ? ReadOnlyMemory<byte>.Empty :
            preserveSnapshots && profile is IDatabaseProfileSnapshotPreservingCheckpoint preserving
            ? await preserving.CaptureSnapshotPreservingCheckpointAsync(cancellationToken).ConfigureAwait(false)
            : expired is not null && profile is IDatabaseProfileOperationRetention retention
            ? await retention.CaptureRetentionCheckpointAsync(expired, cancellationToken).ConfigureAwait(false)
            : await profile.CaptureCheckpointAsync(cancellationToken).ConfigureAwait(false);
        if (encode is null && retainedCatalog is not null)
        {
            content = EntityOperationCodec.ReplaceCheckpointCatalog(content, retainedCatalog);
        }

        _telemetry.CheckpointPhase(_profile.Profile, "profile_capture", phaseStarted, allocatedBefore);
        return (changeBounds, content, encode);
    }

    private async ValueTask ReclaimCheckpointHistoryAsync(DurableCheckpointEnvelope checkpoint,
        DatabaseVersion version, long? checkpointPosition, bool preserveSnapshots)
    {
        if (!preserveSnapshots && _commitLog is IReclaimableDatabaseCommitLog reclaimable)
        {
            long phaseStarted = Stopwatch.GetTimestamp();
            long allocatedBefore = GC.GetTotalAllocatedBytes();
            DatabaseHistoryDisposition disposition = await _historyRetention.DecideAsync(
                new(_databaseIncarnation, _profile.Profile, version, checkpoint),
                CancellationToken.None).ConfigureAwait(false);
            if (disposition is DatabaseHistoryDisposition.Reclaim or
                DatabaseHistoryDisposition.ArchiveVerifiedAndReclaim)
            {
                var boundary = TransferReclamationBoundary(version);
                if (boundary == version && checkpointPosition is { } position &&
                    reclaimable is ICheckpointPositionDatabaseCommitLog positionedLog)
                {
                    await positionedLog.ReclaimCheckpointAsync(version, position, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await reclaimable.ReclaimThroughAsync(boundary, CancellationToken.None).ConfigureAwait(false);
                }
            }
            _telemetry.CheckpointPhase(_profile.Profile, "retention", phaseStarted, allocatedBefore);
        }

    }
}
