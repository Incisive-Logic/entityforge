using System.Security.Cryptography;
using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    private readonly bool _retainLogicalIntents = Environment.GetEnvironmentVariable("ENTITYFORGE_PARTITION_LOGICAL_INTENTS") != "0";
    private DatabasePartitionTransactions? _partitionProtocol;

    /// <summary>Gets the explicit profile-neutral protocol for a partition-local store.</summary>
    public DatabasePartitionTransactions PartitionTransactions =>
        LazyInitializer.EnsureInitialized(ref _partitionProtocol, () => new DatabasePartitionTransactions(this));

    private readonly Dictionary<Guid, DurablePartitionTransaction> _partitionTransactions = [];
    // Evidence owns its payload. Weak keys let retention reclaim both the record
    // and its derived digest; every observation still compares the commitment.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DurablePartitionTransaction, byte[]> _partitionPayloadHashes = new();

    private byte[] PartitionPayloadHash(DurablePartitionTransaction entry) =>
        _partitionPayloadHashes.GetValue(entry, static value => SHA256.HashData(value.Payload.Span));

    private readonly Dictionary<Guid, DurablePartitionTransaction> _unresolvedTimestampPreparations = [];

    internal int UnresolvedTimestampPreparationCount => _unresolvedTimestampPreparations.Count;

    private void PublishPartitionTransaction(DurablePartitionTransaction entry)
    {
        _partitionTransactions[entry.Operation] = entry;
        if (entry.State == DurablePartitionTransactionState.Prepared && !_committedTransactions.ContainsKey(entry.Operation))
        {
            _unresolvedTimestampPreparations[entry.Operation] = entry;
        }
        else
        {
            _unresolvedTimestampPreparations.Remove(entry.Operation);
        }
    }

    private readonly Dictionary<Guid, DatabaseTransactionFootprint> _partitionReservations = [];

    internal async ValueTask<DurablePartitionTransaction?> ReadPartitionCoordinatorAsync(Guid operation, CancellationToken token)
    {
        RequirePartitionTransactions();
        if (operation == Guid.Empty)
        {
            throw new ArgumentException("Missing coordinator identity.");
        }

        await _localWriteAuthority.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                EntityOperationIds.RequireRetained(operation, _localOperationFloor);
                if (!_partitionTransactions.TryGetValue(operation, out var entry))
                {
                    return null;
                }

                if (entry.Coordinator is null)
                {
                    throw new EntityForgeDbIdempotencyException(operation);
                }

                if (_committedTransactions.TryGetValue(operation, out var committed))
                {
                    if (entry.State == DurablePartitionTransactionState.Aborted ||
                        !PartitionPayloadHash(entry).AsSpan().SequenceEqual(committed.ContentHash))
                    {
                        throw new InvalidDataException("Coordinator native commitment differs.");
                    }

                    return entry.Complete(DurablePartitionTransactionState.Committed, committed.Version, committed.Timestamp);
                }
                return entry;
            }
            finally
            {
                _commitCoordinator.Release();
            }
        }
        finally
        {
            _localWriteAuthority.Release();
        }
    }

    private async ValueTask<DatabaseCommitReceipt?> CompletePartitionTransactionIndividuallyAsync(Guid operation,
        string descriptor, bool commit, CancellationToken token = default)
    {
        RequirePartitionTransactions();
        EnsureReady();
        if (!_partitionTransactions.TryGetValue(operation, out var selected) ||
            !StringComparer.OrdinalIgnoreCase.Equals(selected.Descriptor, descriptor))
        {
            throw new InvalidDataException("No matching durable partition preparation exists.");
        }

        if (selected.State == DurablePartitionTransactionState.Aborted)
        {
            if (commit)
            {
                throw new InvalidDataException("A committed decision contradicts a durable abort.");
            }

            return null;
        }
        if (selected.State == DurablePartitionTransactionState.Committed && !commit)
        {
            throw new InvalidDataException("An abort contradicts native commitment.");
        }

        DatabaseCommitReceipt? receipt = null;
        if (commit)
        {
            // The operation identity and exact bytes were selected durably at
            // preparation. The native ledger supplies their LOCAL position.
            receipt = await CommitWithReceiptCoreAsync(operation, selected.Payload, null,
                token, partitionAdmissionOwned: true).ConfigureAwait(false);
            if (selected.State == DurablePartitionTransactionState.Committed)
            {
                return receipt;
            }
        }
        try
        {
            await _commitCoordinator.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!commit)
                {
                    await DiscardPhysicalPreparationAsync(operation).ConfigureAwait(false);
                    await AppendPartitionEvidenceAsync(selected.Complete(DurablePartitionTransactionState.Aborted),
                        CancellationToken.None).ConfigureAwait(false);
                }
                PublishPartitionTransaction(selected.Complete(commit
                    ? DurablePartitionTransactionState.Committed : DurablePartitionTransactionState.Aborted, receipt?.Version,
                    commit ? _committedTransactions[operation].Timestamp : null));
                // The native commit, or explicit WAL abort, is durable before releasing
                // the reservation. Recovery derives commitment from the native receipt.
                _partitionReservations.Remove(operation);
                _partitionCommitTimes.Remove(operation);
                (_profile as IDatabaseProfileLogicalPreparation)?.ReleaseLogicalPreparation(operation);
            }
            finally
            {
                _commitCoordinator.Release();
            }
        }
        catch (Exception failure) when (receipt is not null)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure);
        }
        return receipt;
    }

    internal async ValueTask<DatabaseCommitReceipt?> ResolvePartitionTransactionAsync(Guid operation,
        string descriptor, CancellationToken token)
    {
        RequirePartitionTransactions();
        // Join native completion and terminal publication as one owned interval.
        await _localWriteAuthority.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
            try
            {
                EnsureReady();
                EntityOperationIds.RequireRetained(operation, _localOperationFloor);
                if (!_partitionTransactions.TryGetValue(operation, out var selected))
                {
                    return null;
                }

                if (selected.Descriptor != descriptor)
                {
                    throw new EntityForgeDbIdempotencyException(operation);
                }

                if (selected.State != DurablePartitionTransactionState.Committed)
                {
                    return null;
                }

                if (!TryGetReplayReceipt(operation, PartitionPayloadHash(selected), out var receipt))
                {
                    throw new InvalidDataException("Committed partition evidence has no native receipt.");
                }

                return receipt;
            }
            finally
            {
                _commitCoordinator.Release();
            }
        }
        finally
        {
            _localWriteAuthority.Release();
        }
    }

    // Holding this handle freezes the local position without adding a durable
    // decision or allocating a version. A global cut must acquire every member
    // and reject unresolved preparations before exposing any projection.
    internal async ValueTask<IAsyncDisposable> FreezePartitionAsync(CancellationToken token = default)
    {
        RequirePartitionTransactions();
        // Profile maintenance takes its admission before native write exclusion.
        // Frozen readers must use that same order, just like MVCC snapshot pins.
        IDisposable? admission = _profile is IDatabaseProfileSnapshotPin profile
            ? await profile.EnterSnapshotPinAsync(token).ConfigureAwait(false) : null;
        bool entered = false;
        try
        {
            await EnterLocalWriteAuthorityAsync(token).ConfigureAwait(false);
            entered = true;
            EnsureReady();
            await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await ReserveTimestampReadAsync(token).ConfigureAwait(false);
            }
            finally
            {
                _commitCoordinator.Release();
            }
            var freeze = new PartitionFreeze(_localWriteAuthority, admission);
            admission = null;
            return freeze;
        }
        catch
        {
            if (entered)
            {
                _localWriteAuthority.Release();
            }

            admission?.Dispose();
            throw;
        }
    }

    private sealed class PartitionFreeze(SemaphoreSlim gate, IDisposable? admission) : IAsyncDisposable
    {
        private readonly object _disposeGate = new();
        private bool _released;
        private bool _admissionReleased;

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (!_released)
                {
                    gate.Release();
                    _released = true;
                }
                if (!_admissionReleased)
                {
                    admission?.Dispose();
                    _admissionReleased = true;
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    internal async ValueTask<IReadOnlyDictionary<Guid, string>> ReadPartitionSelectedDescriptorsAsync(
        IReadOnlyList<Guid> operations, CancellationToken token)
    {
        RequirePartitionTransactions();
        if (operations.Count is < 1 or > DatabasePartitionTransactions.MaximumSelectionObservation ||
            operations.Any(id => id == Guid.Empty) || operations.Distinct().Count() != operations.Count)
        {
            throw new ArgumentException("Invalid bounded partition selection observation.");
        }

        var owned = operations.ToArray();
        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            var result = new Dictionary<Guid, string>(owned.Length);
            foreach (var operation in owned)
            {
                if (_partitionTransactions.TryGetValue(operation, out var selected))
                {
                    result.Add(operation, selected.Descriptor);
                }
            }

            return result;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    internal async ValueTask<IReadOnlyList<DurablePartitionTransaction>> ReadPartitionTransactionsAsync(CancellationToken token = default)
    {
        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            return _partitionTransactions.Values.ToArray();
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    // A fresh local read cannot pass an unresolved participant reservation. A
    // different owner may already have exposed this transaction's committed
    // state. The returned floor also prevents a caller from substituting an old
    // cached snapshot after this check. No authority lookup is needed here.
    internal async ValueTask<DatabaseVersion> AdmitPartitionReadAsync(CancellationToken token = default)
    {
        RequirePartitionTransactions();
        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (_partitionTransactions.Values.Any(p => p.State == DurablePartitionTransactionState.Prepared))
            {
                throw new InvalidOperationException("A partition transaction must be resolved before a fresh read can complete.");
            }

            return CurrentVersion;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private async ValueTask PersistPartitionTransferCheckpointAsync(CancellationToken token)
    {
        // Protocol evidence must not invalidate concurrently retained planning/read views.
        // Normal retention remains a separate drained operation.
        try
        {
            await CreateCheckpointCoreAsync(null, 0, null, token, preserveSnapshots: true).ConfigureAwait(false);
        }
        catch
        {
            RequireRecovery("partition_evidence_checkpoint");
            throw;
        }
    }

    // Called under local-write and commit admission. Protocol records do not enter
    // the native receipt catalogue or advance the profile position. Using the next
    // native position keeps them in the WAL until a later checkpoint covers them.
    // A checkpoint at the unchanged position can leave an overlapping protocol tail;
    // replay is idempotent against its checkpoint evidence and installed expiry floor.
    private async ValueTask AppendPartitionEvidenceAsync(DurablePartitionTransaction entry, CancellationToken token)
    {
        EnsureDatabaseIncarnation();
        var envelope = PartitionEvidenceEnvelope(entry);
        try
        {
            await _commitLog.CommitAsync(envelope, token).ConfigureAwait(false);
        }
        catch
        {
            RequireRecovery("partition_evidence_wal");
            throw;
        }
    }

    private DurableTransactionEnvelope PartitionEvidenceEnvelope(DurablePartitionTransaction entry) =>
        new DurableTransactionEnvelope(_databaseIncarnation,
            new(checked(_durableVersion + 1)), entry.Operation,
            new(_profile.Profile, PartitionEvidenceCodec.Id, PartitionEvidenceCodec.Version, PartitionEvidenceCodec.Encode(entry)),
            DurableCommittedChangeKind.PartitionEvidence, null, StorageLayout, VersioningMode)
        {
            Timestamp = NextTimestamp()
        };

    private void ReplayPartitionEvidence(DurableTransactionEnvelope envelope)
    {
        RequirePartitionTransactions();
        var entry = PartitionEvidenceCodec.Decode(envelope.TransactionId, envelope.Payload.Content.Span, envelope.Payload.CodecVersion);
        if (entry.ReadVersion.Value >= envelope.Version.Value)
        {
            throw new InvalidDataException("Partition evidence precedes its selected read position.");
        }

        if (EntityOperationIds.IsBefore(entry.Operation, _localOperationFloor))
        {
            return;
        }

        if (_partitionTransactions.TryGetValue(entry.Operation, out var existing))
        {
            RequireSamePreparation(existing, entry);
            if (entry.State == DurablePartitionTransactionState.Prepared)
            {
                return;
            }

            if (existing.State == DurablePartitionTransactionState.Committed)
            {
                throw new InvalidDataException("Partition abort contradicts checkpoint commitment.");
            }
        }
        else if (_partitionTransactions.Count >= DurablePartitionTransaction.MaximumTransactions ||
            _partitionTransactions.Values.Sum(p => p.EvidenceBytes) + entry.EvidenceBytes > DurablePartitionTransaction.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Partition WAL exceeds evidence capacity.");
        }

        PublishPartitionTransaction(entry);
    }

    internal async ValueTask<DatabaseVersion> CheckpointPartitionTransferAsync(CancellationToken token)
    {
        RequirePartitionTransactions();
        await _localWriteAuthority.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await PersistPartitionTransferCheckpointAsync(token).ConfigureAwait(false);
            return CurrentVersion;
        }
        finally
        {
            _localWriteAuthority.Release();
        }
    }

    internal async ValueTask<long> RetainPartitionOperationsAsync(DateTimeOffset retainedSince, CancellationToken token = default, Func<ValueTask>? captured = null)
    {
        RequirePartitionTransactions();
        long floor = retainedSince.ToUnixTimeMilliseconds();
        if (floor <= 0 || floor > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            throw new ArgumentOutOfRangeException(nameof(retainedSince));
        }

        using IDisposable? priority = (_profile as IDatabaseProfileCheckpointPriority)?.EnterCheckpointPriority();
        await EnterLocalWriteAuthorityAsync(token).ConfigureAwait(false);
        bool writeHeld = true;
        try
        {
            // The node's drained fence excludes late preparations throughout this
            // checkpoint. Publication installs both the floor and evidence removal.
            var checkpoint = await CreateCheckpointCoreAsync(null, 0, null, token, floor, partitionRetention: true,
                captured: captured is null ? null : async () =>
                {
                    _localWriteAuthority.Release();
                    writeHeld = false;
                    await captured().ConfigureAwait(false);
                }).ConfigureAwait(false);
            return checkpoint.LocalOperationFloor;
        }
        finally
        {
            if (writeHeld)
            {
                _localWriteAuthority.Release();
            }
        }
    }

    private void RequirePartitionTransactions()
    {
        if (VersioningMode != DatabaseVersioningMode.PartitionLocal || _checkpointStore is null ||
            _profile is not ICheckpointableDatabaseProfileRuntime)
        {
            throw new NotSupportedException("Partition transactions require the checkpointed partition-local kernel.");
        }
    }

    private static void RequireSamePreparation(DurablePartitionTransaction selected, DurablePartitionTransaction proposed)
    {
        if (selected.Descriptor != proposed.Descriptor || selected.ReadVersion != proposed.ReadVersion ||
            !selected.Payload.Span.SequenceEqual(proposed.Payload.Span) ||
            (selected.Coordinator is null) != (proposed.Coordinator is null) ||
            selected.Coordinator is { } coordinator && !coordinator.Matches(proposed.Coordinator!))
        {
            throw new EntityForgeDbIdempotencyException(proposed.Operation);
        }
    }

    private void RestorePartitionTransactions(DurableCheckpointEnvelope checkpoint)
    {
        if (checkpoint.PartitionTransactions.Count != 0)
        {
            RequirePartitionTransactions();
        }

        if (checkpoint.PartitionTransactions.Count > DurablePartitionTransaction.MaximumTransactions ||
            checkpoint.PartitionTransactions.Sum(p => p.EvidenceBytes) > DurablePartitionTransaction.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Partition checkpoint exceeds evidence capacity.");
        }

        foreach (var entry in checkpoint.PartitionTransactions)
        {
            if (entry.ReadVersion.Value > checkpoint.Version.Value || entry.LocalVersion?.Value > checkpoint.Version.Value ||
                !_partitionTransactions.TryAdd(entry.Operation, entry))
            {
                throw new InvalidDataException("Invalid partition transaction recovery evidence.");
            }
        }
    }

    private void ReconcilePartitionTransactions()
    {
        foreach (var selected in _partitionTransactions.Values.ToArray())
        {
            if (_committedTransactions.TryGetValue(selected.Operation, out var committed))
            {
                if (selected.State == DurablePartitionTransactionState.Aborted ||
                    !PartitionPayloadHash(selected).AsSpan().SequenceEqual(committed.ContentHash) ||
                    committed.Version.Value <= selected.ReadVersion.Value ||
                    selected.LocalVersion is { } prior && prior != committed.Version)
                {
                    throw new InvalidDataException("Native commitment contradicts partition preparation.");
                }

                PublishPartitionTransaction(selected.Complete(DurablePartitionTransactionState.Committed, committed.Version, committed.Timestamp));
            }
            else if (selected.State == DurablePartitionTransactionState.Committed)
            {
                throw new InvalidDataException("Partition terminal evidence has no native replay receipt.");
            }
        }
        _unresolvedTimestampPreparations.Clear();
        foreach (var entry in _partitionTransactions.Values)
        {
            if (entry.State == DurablePartitionTransactionState.Prepared && !_committedTransactions.ContainsKey(entry.Operation))
            {
                _unresolvedTimestampPreparations.Add(entry.Operation, entry);
            }
        }

        _partitionReservations.Clear();
        (_profile as IDatabaseProfileLogicalPreparation)?.ClearLogicalPreparations();
        foreach (var selected in _partitionTransactions.Values.Where(p => p.State == DurablePartitionTransactionState.Prepared))
        {
            var footprint = DescribeReservation(selected.Operation, selected.Payload, selected.ReadVersion);
            if (_partitionReservations.Values.Any(footprint.ConflictsWith))
            {
                throw new InvalidDataException("Recovered partition reservations conflict.");
            }

            _partitionReservations.Add(selected.Operation, footprint);
            if (_retainLogicalIntents)
            {
                (_profile as IDatabaseProfileLogicalPreparation)?.RetainLogicalPreparation(selected.Operation, selected.Payload);
            }
        }
    }

    private DatabaseTransactionFootprint DescribeReservation(Guid operation, ReadOnlyMemory<byte> payload, DatabaseVersion readVersion)
    {
        var footprint = (_profile as IDatabaseProfilePartitionReservations)?.DescribeTransaction(operation, payload);
        if (footprint?.IsScopedPreparation != true)
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        if (footprint.ReadVersion != readVersion)
        {
            throw new InvalidDataException("Preparation read position differs from its native payload.");
        }

        return footprint;
    }

    private void RequireCompatibleReservations(Guid operation, ReadOnlyMemory<byte> payload, bool partitionAdmissionOwned)
    {
        if (_partitionReservations.Count == 0)
        {
            return;
        }

        var candidate = (_profile as IDatabaseProfilePartitionReservations)?.DescribeTransaction(operation, payload)
            ?? DatabaseTransactionFootprint.WholePartition;
        foreach (var (id, reservation) in _partitionReservations)
        {
            if (!(partitionAdmissionOwned && id == operation) && candidate.ConflictsWith(reservation))
            {
                throw new DatabasePartitionConflictException();
            }
        }
    }

    private IDisposable? EnterPreparedCompletion(Guid operation, ReadOnlyMemory<byte> payload, bool partitionAdmissionOwned)
    {
        if (!partitionAdmissionOwned || !_partitionReservations.ContainsKey(operation))
        {
            return null;
        }

        if (!_partitionTransactions.TryGetValue(operation, out var selected) ||
            selected.State != DurablePartitionTransactionState.Prepared || !selected.Payload.Span.SequenceEqual(payload.Span))
        {
            throw new EntityForgeDbIdempotencyException(operation);
        }

        return (_profile as IDatabaseProfilePartitionReservations)?.EnterPreparedCompletion(operation);
    }
}
