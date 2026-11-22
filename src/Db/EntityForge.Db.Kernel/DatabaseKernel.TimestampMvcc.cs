using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    // Bound how far a reader can advance future write admission. This is an
    // admission limit, not a delay; oversized clock intervals fail closed.
    private static readonly TimeSpan MaximumReadTimestampLead = TimeSpan.FromSeconds(1);

    internal bool TimestampMvccEnabled => TimestampCostOptions?.Visibility == true;

    private readonly Dictionary<Guid, DurableTimestampState> _partitionCommitTimes = [];
    private readonly Dictionary<IDatabaseTimestampProtection, DurableTimestampState> _timestampReaders = [];
    private readonly object _timestampReadersSync = new();
    private DurableTimestampState? _logicalFloor;

    private IDatabaseTimestampProfile TimestampProfile => _profile as IDatabaseTimestampProfile ??
        throw new NotSupportedException("This profile has not implemented timestamp MVCC.");

    private async ValueTask InitializeTimestampVisibilityAsync(CancellationToken token)
    {
        if (!TimestampMvccEnabled)
        {
            return;
        }

        if (_partitionTransactions.Values.Any(entry => entry.Timestamp is null || !entry.Timestamp.Mvcc || entry.Timestamp.Domain != TimestampCostOptions!.Domain))
        {
            throw new InvalidDataException("Recovered preparations lack this visibility clock association.");
        }

        _logicalFloor = NextTimestamp()!;
        await TimestampProfile.InitializeTimestampHistoryAsync(_logicalFloor, token).ConfigureAwait(false);
    }

    private DurableTimestampState? CommitTimestamp(Guid operation, bool prepared)
    {
        if (TimestampMvccEnabled && prepared && _partitionTransactions.ContainsKey(operation))
        {
            if (!_partitionCommitTimes.TryGetValue(operation, out var selected))
            {
                throw new InvalidOperationException("Prepared completion requires its exact common commit timestamp.");
            }

            RestoreTimestamp(selected);
            return new(selected.Domain, selected.Physical, selected.Logical, _timestampState!.ReservedReadThrough, true);
        }
        return NextTimestamp();
    }

    internal async ValueTask<DurableTimestampState> ChooseTimestampAsync(IReadOnlyList<DurableTimestampState> votes, CancellationToken token)
    {
        if (!TimestampMvccEnabled || votes.Count is < 1 or > 64)
        {
            throw new NotSupportedException("Timestamp agreement is unavailable.");
        }

        var bound = votes.Aggregate((a, b) => a.CompareTo(b) >= 0 ? a : b);
        if (bound.Domain != TimestampCostOptions!.Domain || !bound.Mvcc)
        {
            throw new InvalidDataException("Vote clock domain differs.");
        }
        // HLC advancement is immediate. Durability/publication can overlap the
        // uncertainty interval; successful completion still enforces expiry.
        await _commitCoordinator.WaitAsync(token);
        try
        {
            EnsureReady();
            var causal = TimestampCausalBound();
            var voted = new DatabaseHybridTime(bound.Physical, bound.Logical);
            var time = _hybridClock!.Next(voted.CompareTo(causal) > 0 ? voted : causal);
            var chosen = new DurableTimestampState(bound.Domain, time.PhysicalTicks, time.Logical, _timestampState!.ReservedReadThrough, true);
            RestoreTimestamp(chosen);
            return chosen;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private void RequireCommitTime(Guid operation, DurableTimestampState? time)
    {
        if (!TimestampMvccEnabled)
        {
            if (time is not null)
            {
                throw new NotSupportedException("Timestamp visibility is not enabled.");
            }

            return;
        }
        if (!_partitionTransactions.TryGetValue(operation, out var selected))
        {
            throw new InvalidDataException("No timestamp preparation.");
        }

        if (_committedTransactions.TryGetValue(operation, out var committed))
        {
            if (time is null || committed.Timestamp?.CompareTo(time) != 0)
            {
                throw new InvalidDataException("Replay changed commit timestamp.");
            }

            return;
        }
        if (time is null || selected.Timestamp is null || time.CompareTo(selected.Timestamp) <= 0)
        {
            throw new InvalidDataException("Commit timestamp does not exceed its durable vote.");
        }

        _partitionCommitTimes[operation] = time;
    }

    internal async ValueTask<DurableTimestampState?> ReadTimestampVoteAsync(Guid operation, CancellationToken token)
    {
        await _commitCoordinator.WaitAsync(token);
        try
        {
            EnsureReady();
            return _partitionTransactions.GetValueOrDefault(operation)?.Timestamp;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }

    private DurableTimestampState? RegisterTimestampGeneration(IDatabaseTimestampProtection snapshot)
    {
        if (!TimestampMvccEnabled)
        {
            return null;
        }

        DurableTimestampState floor;
        lock (_timestampReadersSync)
        {
            _timestampReaders.Add(snapshot, floor = NextTimestamp()!);
        }

        return floor;
    }

    internal void ReleaseTimestampGeneration(IDatabaseTimestampProtection snapshot)
    {
        if (!TimestampMvccEnabled)
        {
            return;
        }

        lock (_timestampReadersSync)
        {
            _timestampReaders.Remove(snapshot);
        }
    }

    private void TrimLogicalHistory()
    {
        if (!TimestampMvccEnabled)
        {
            return;
        }

        DurableTimestampState floor;
        lock (_timestampReadersSync)
        {
            foreach (var reader in _timestampReaders.Keys.Where(r => r.IsDisposed).ToArray())
            {
                _timestampReaders.Remove(reader);
            }

            floor = _timestampReaders.Count == 0 ? NextTimestamp()! : _timestampReaders.Values.Min()!;
        }
        if (floor.CompareTo(_logicalFloor) <= 0)
        {
            return;
        }
        // Pending lower timestamps may arrive later. They update the retained base;
        // relevant-read admission below still joins their terminal application.
        TimestampProfile.TrimTimestampVersions(floor);
        _logicalFloor = floor;
    }

    internal ValueTask<object> CaptureTimestampProjectionAsync(IDatabaseTimestampProtection protection,
        DurableTimestampState time, HashSet<string>? resources, CancellationToken token) =>
        CaptureAdmittedTimestampProjectionAsync(protection, time, resources, token);

    private async ValueTask<object> CaptureAdmittedTimestampProjectionAsync(IDatabaseTimestampProtection protection,
        DurableTimestampState time, HashSet<string>? resources, CancellationToken token)
    {
        if (!TimestampMvccEnabled)
        {
            throw new NotSupportedException("Timestamp MVCC is disabled.");
        }

        if (time.Domain != TimestampCostOptions!.Domain || !time.Mvcc)
        {
            throw new InvalidDataException("Timestamp projection belongs to another clock interpretation.");
        }
        while (true)
        {
            if (TryCaptureDuringAppend(protection, time, resources, token, out var captured))
            {
                return captured!;
            }

            Task nextWindow = NextAppendReadWindow();
            // Close the registration race: an append may have opened after the
            // first attempt but before we registered for the next notification.
            if (TryCaptureDuringAppend(protection, time, resources, token, out captured))
            {
                return captured!;
            }

            Guid[] pending;
            using (_telemetry.Start("db.timestamp.admission_wait", Profile))
            {
                if (!await WaitForTimestampCoordinatorAsync(nextWindow, token))
                {
                    continue;
                }
            }

            try
            {
                using var service = _telemetry.Start("db.timestamp.admission_service", Profile);
                RequireTimestampProjection(protection, time, token);
                // Establish safe time by durable admission, not wall-clock expiry.
                // Future local writes and preparation votes must exceed this
                // promise. Existing lower votes remain obligations and are joined
                // below; they must never be pushed or discarded by a reader.
                // Commit publication still waits for clock expiry under the same
                // gate, so a returned view cannot expose a future committed value
                // to a later independent reader whose clock has not caught up.
                if (new DatabaseHybridTime(time.Physical, time.Logical).CompareTo(_readPromise) > 0)
                {
                    _readPromise = new(time.Physical, time.Logical);
                }

                using (_telemetry.Start("db.timestamp.safe_read_admission", Profile))
                {
                    await ReserveTimestampReadAsync(token);
                }

                using var inspection = _telemetry.Start("db.timestamp.intent_inspection", Profile);
                inspection?.SetTag("entityforge.retained", _partitionTransactions.Count);
                inspection?.SetTag("entityforge.unresolved", _unresolvedTimestampPreparations.Count);
                pending = _unresolvedTimestampPreparations.Values.Where(entry => entry.State == DurablePartitionTransactionState.Prepared &&
                    !_committedTransactions.ContainsKey(entry.Operation) &&
                    (entry.Timestamp is null || entry.Timestamp.CompareTo(time) <= 0) &&
                    (resources is null || SnapshotFootprint(entry).WritesAny(resources)))
                    .Select(entry => entry.Operation).ToArray();
                if (pending.Length == 0)
                {
                    return TimestampProfile.CaptureTimestampView(time);
                }
            }
            finally
            {
                _commitCoordinator.Release();
            }
            using var trace = _telemetry.Start("db.timestamp.intent_wait", Profile);
            await WaitForSnapshotProgressAsync(pending, token);
        }
    }

    internal IDisposable EnterTimestampProjection(object view) => TimestampProfile.EnterTimestampView(view);

    internal void ValidateTimestampMappings(IReadOnlyList<EntityMappingManifest> manifests) => TimestampProfile.ValidateTimestampMappings(manifests);
}
