using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    // Every projection uses native timestamp admission. There is no shared
    // readiness worker or qualification switch selecting another execution path.
    private readonly object _timestampAdmissionSync = new();
    private bool _timestampAdmissionClosed;

    private void RequireTimestampProjection(IDatabaseTimestampProtection protection, DurableTimestampState time, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_timestampAdmissionSync)
        {
            ObjectDisposedException.ThrowIf(_timestampAdmissionClosed, this);
        }

        EnsureReady();
        if (!TimestampMvccEnabled)
        {
            throw new NotSupportedException("Timestamp MVCC is disabled.");
        }

        var options = TimestampCostOptions!;
        if (time.Domain != options.Domain || !time.Mvcc)
        {
            throw new InvalidDataException("Timestamp projection belongs to another clock interpretation.");
        }

        lock (_timestampReadersSync)
        {
            if (protection.IsDisposed || !_timestampReaders.ContainsKey(protection))
            {
                throw new InvalidOperationException("Timestamp protection is no longer owned.");
            }
        }

        long earliest = options.Now();
        if (!options.Healthy() || time.CompareTo(_logicalFloor) < 0 ||
            options.BoundedObservation is null && time.Physical > earliest ||
            time.Physical > earliest && time.Physical - earliest > MaximumReadTimestampLead.Ticks ||
            time.Physical > DateTime.MaxValue.Ticks - options.ReservationWindow.Ticks)
        {
            throw new InvalidOperationException("Timestamp lies outside this owner's qualified protected interval.");
        }
    }

    // The commit coordinator remains held by the writer. This narrower window
    // permits only conflict-free projections while that writer awaits durability;
    // closing it joins every projection capture before publication or cleanup.
    private DatabaseTransactionFootprint[]? _appendReadFootprints;
    private DurableTimestampState?[]? _appendReadTimes;
    private TaskCompletionSource? _appendReadWindowOpened;

    private Task NextAppendReadWindow()
    {
        lock (_timestampAdmissionSync)
        {
            return (_appendReadWindowOpened ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    private async ValueTask<bool> WaitForTimestampCoordinatorAsync(Task nextWindow, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        ValueTask admission = _commitCoordinator.WaitAsync(wait.Token);
        if (admission.IsCompletedSuccessfully)
        {
            admission.GetAwaiter().GetResult();
            return true;
        }
        Task acquired = admission.AsTask();
        if (await Task.WhenAny(acquired, nextWindow).ConfigureAwait(false) == acquired)
        {
            await acquired.ConfigureAwait(false);
            return true;
        }
        // Remove this waiter before retrying. If acquisition won concurrently,
        // return its ownership exactly once; never leave an orphaned waiter.
        wait.Cancel();
        try
        {
            await acquired.ConfigureAwait(false);
            _commitCoordinator.Release();
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {

        }
        token.ThrowIfCancellationRequested();
        return false;
    }

    private bool OpenAppendReadWindow(PreparedKernelCommit prepared) => OpenAppendReadWindow([prepared]);

    private bool OpenAppendReadWindow(IReadOnlyList<PreparedKernelCommit> prepared)
    {
        if (!TimestampMvccEnabled || _profile is not IDatabaseProfilePartitionReservations profile)
        {
            return false;
        }

        var footprints = new DatabaseTransactionFootprint[prepared.Count];
        for (int i = 0; i < footprints.Length; i++)
        {
            var transaction = prepared[i].Transaction;
            if (transaction.Payload.CodecId != _profile.CodecId)
            {
                return false;
            }
            // Reuse only the reservation for byte-identical, already selected input.
            // An operation identity alone is insufficient to certify a footprint.
            footprints[i] = _profile is IDatabasePreparedAppendReadIsolation isolation
                ? isolation.DescribePreparedAppendWrites(transaction.TransactionId)
                : _partitionReservations.TryGetValue(transaction.TransactionId, out var reserved) &&
                _partitionTransactions.TryGetValue(transaction.TransactionId, out var selected) &&
                selected.Payload.Span.SequenceEqual(transaction.Payload.Content.Span)
                ? reserved : profile.DescribeTransaction(transaction.TransactionId, transaction.Payload.Content);
        }
        lock (_timestampAdmissionSync)
        {
            if (_timestampAdmissionClosed)
            {
                return false;
            }

            _appendReadFootprints = footprints;
            _appendReadTimes = prepared.Select(item => item.Transaction.Timestamp).ToArray();
            var opened = _appendReadWindowOpened;
            _appendReadWindowOpened = null;
            opened?.TrySetResult();
            return true;
        }
    }

    private void CloseAppendReadWindow()
    {
        lock (_timestampAdmissionSync)
        {
            _appendReadFootprints = null;
            _appendReadTimes = null;
        }
    }

    private bool TryCaptureDuringAppend(IDatabaseTimestampProtection protection,
        DurableTimestampState time, HashSet<string>? resources, CancellationToken token, out object? view)
    {
        view = null;
        lock (_timestampAdmissionSync)
        {
            if (_appendReadFootprints is not { } writing)
            {
                return false;
            }

            using var attempt = _telemetry.Start("db.timestamp.append_read_attempt", Profile);
            attempt?.SetTag("entityforge.append_read.outcome", "guard_rejected");
            RequireTimestampProjection(protection, time, token);
            for (int i = 0; i < writing.Length; i++)
            {
                // A strictly later version cannot affect this historical view,
                // including predicate membership and phantoms. Unknown/equal/older
                // append times still require complete nonconflicting resources.
                if (_appendReadTimes?[i] is { } appendTime && appendTime.CompareTo(time) > 0)
                {
                    continue;
                }

                if (resources is null)
                {
                    attempt?.SetTag("entityforge.append_read.outcome", "unknown_scope");
                    return false;
                }
                if (writing[i].WritesAny(resources))
                {
                    attempt?.SetTag("entityforge.append_read.outcome", "append_conflict");
                    return false;
                }
            }
            // A read cannot extend a crash-safe reservation while another append
            // is in flight. Expired ceilings use the normal durable admission path.
            long now = TimestampCostOptions!.Now();
            long ceiling = _timestampState?.ReservedReadThrough ?? 0;
            attempt?.SetTag("entityforge.append_read.remaining_ticks", ceiling - Math.Max(time.Physical, now));
            if (time.Physical >= ceiling || now >= ceiling)
            {
                attempt?.SetTag("entityforge.append_read.outcome", "expired_ceiling");
                return false;
            }
            foreach (var entry in _unresolvedTimestampPreparations.Values)
            {
                if (entry.State == DurablePartitionTransactionState.Prepared &&
                    !_committedTransactions.ContainsKey(entry.Operation) &&
                    (entry.Timestamp is null || entry.Timestamp.CompareTo(time) <= 0) &&
                    (resources is null || SnapshotFootprint(entry).WritesAny(resources)))
                {
                    attempt?.SetTag("entityforge.append_read.outcome", "pending_intent");
                    return false;
                }
            }

            var promise = new DatabaseHybridTime(Math.Max(now, time.Physical), time.Logical);
            if (promise.CompareTo(_readPromise) > 0)
            {
                _readPromise = promise;
            }

            attempt?.SetTag("entityforge.append_read.outcome", "capture_failed");
            view = TimestampProfile.CaptureTimestampView(time);
            attempt?.SetTag("entityforge.append_read.outcome", "captured");
            return true;
        }
    }

    private ValueTask CloseTimestampReadAdmissionAsync()
    {
        lock (_timestampAdmissionSync)
        {
            _timestampAdmissionClosed = true;
        }

        return ValueTask.CompletedTask;
    }
}
