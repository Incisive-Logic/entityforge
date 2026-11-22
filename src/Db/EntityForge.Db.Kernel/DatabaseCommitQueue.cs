using EntityForge.Db.Mapping;
using System.Diagnostics;

namespace EntityForge.Db.Kernel;

/// <summary>Shares flushes only among already waiting requests; never delays a lone writer.</summary>
/// <remarks>Requests must remain immutable until completion. Group callbacks return receipts in input
/// order and may request individual fallback only by throwing <see cref="DatabaseCommitGroupPreparationException"/>
/// after abandoning all speculative state without submitting durable work. The queue owns admission
/// around both callbacks; callbacks must not acquire that same semaphore.</remarks>
public sealed class DatabaseCommitQueue<T> : IAsyncDisposable
{
    private readonly Func<ValueTask> _enterAdmission;
    private readonly Action _releaseAdmission;
    private readonly Func<T, ValueTask<DatabaseCommitReceipt>> commit;
    private readonly Func<IReadOnlyList<T>, ValueTask<IReadOnlyList<DatabaseCommitReceipt>>> commitGroup;
    private readonly Func<T, Guid> identity;
    private readonly DatabaseCommitQueueTelemetry _telemetry;

    /// <summary>Creates a queue whose batch is selected after acquiring publication admission.</summary>
    public DatabaseCommitQueue(SemaphoreSlim admission,
        Func<T, ValueTask<DatabaseCommitReceipt>> commit,
        Func<IReadOnlyList<T>, ValueTask<IReadOnlyList<DatabaseCommitReceipt>>> commitGroup,
        Func<T, Guid> identity, string profile)
        : this(() => new ValueTask(admission.WaitAsync()), () => admission.Release(),
            commit, commitGroup, identity, profile)
    {
    }

    /// <summary>Creates a queue with caller-owned publication admission around selection and execution.</summary>
    /// <remarks>A successful entry must acquire admission; a failed entry must leave no admission owned.
    /// Release is called exactly once after a successful entry and must not throw.</remarks>
    public DatabaseCommitQueue(Func<ValueTask> enterAdmission, Action releaseAdmission,
        Func<T, ValueTask<DatabaseCommitReceipt>> commit,
        Func<IReadOnlyList<T>, ValueTask<IReadOnlyList<DatabaseCommitReceipt>>> commitGroup,
        Func<T, Guid> identity, string profile)
    {
        ArgumentNullException.ThrowIfNull(enterAdmission);
        ArgumentNullException.ThrowIfNull(releaseAdmission);
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(commitGroup);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        _enterAdmission = enterAdmission;
        _releaseAdmission = releaseAdmission;
        this.commit = commit;
        this.commitGroup = commitGroup;
        this.identity = identity;
        _telemetry = DatabaseCommitQueueTelemetry.For(profile);
    }

    internal const int MaximumGroupSize = 32;
    private const int MaximumOutstanding = 256;
    private readonly SemaphoreSlim _capacity = new(MaximumOutstanding, MaximumOutstanding);
    private readonly object _gate = new();
    private readonly Queue<Request> _waiting = new();
    private Task _drain = Task.CompletedTask;
    private bool _running;
    private bool _closed;

    /// <summary>Queues a frozen request, observing cancellation before execution ownership passes to the writer.</summary>
    public async ValueTask<DatabaseCommitReceipt> CommitAsync(T value,
        CancellationToken cancellationToken)
    {
        using Activity? activity = _telemetry.Activities.StartActivity(_telemetry.ActivityName);
        long capacityStart = Stopwatch.GetTimestamp();
        await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        _telemetry.CapacityWait.Record(Stopwatch.GetElapsedTime(capacityStart).TotalMilliseconds);
        var request = new Request(value, cancellationToken, activity);
        lock (_gate)
        {
            if (_closed)
            {
                _capacity.Release();
                throw new ObjectDisposedException(nameof(DatabaseCommitQueue<T>));
            }
            _waiting.Enqueue(request);
            if (!_running)
            {
                _running = true;
                long scheduled = Stopwatch.GetTimestamp();
                _drain = Task.Run(() => DrainAsync(scheduled));
            }
        }
        return await request.Completion.Task.ConfigureAwait(false);
    }

    private async Task DrainAsync(long scheduled)
    {
        while (true)
        {
            _telemetry.Scheduling.Record(Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds);
            var batch = new List<Request>(MaximumGroupSize);
            long admissionStarted = Stopwatch.GetTimestamp();
            try
            {
                await _enterAdmission().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                // Custom bounded admission can reject before a permit is owned. Drain the
                // selected queue obligation without releasing an unowned permit or stranding callers.
                lock (_gate)
                {
                    while (_waiting.TryDequeue(out Request? rejected))
                    {
                        rejected.Completion.TrySetException(failure);
                        _capacity.Release();
                    }
                    _running = false;
                }
                return;
            }
            _telemetry.Admission.Record(Stopwatch.GetElapsedTime(admissionStarted).TotalMilliseconds);
            long busyStarted = Stopwatch.GetTimestamp();
            try
            {
                lock (_gate)
                {
                    while (batch.Count < MaximumGroupSize && _waiting.TryDequeue(out Request? request))
                    {
                        batch.Add(request);
                    }
                }

                foreach (Request request in batch)
                {
                    double elapsed = Stopwatch.GetElapsedTime(request.QueuedAt).TotalMilliseconds;
                    _telemetry.QueueWait.Record(elapsed);
                    // Each request keeps its own trace even though one worker commits the group.
                    request.Trace?.SetTag("entityforge.db.queue_wait_ms", elapsed);
                }
                Request[] active = batch.Where(request => !request.Token.IsCancellationRequested).ToArray();
                _telemetry.GroupSize.Record(active.Length);
                foreach (Request request in batch.Except(active))
                {
                    request.Completion.TrySetCanceled(request.Token);
                }

                Guid[] identities = active.Length > 1 ? active.Select(request => identity(request.Value)).ToArray() : [];
                if (active.Length > 1 && identities.Distinct().Count() == active.Length)
                {
                    await CommitGroupAsync(active, identities).ConfigureAwait(false);
                }
                else
                {
                    foreach (Request request in active)
                    {
                        await CommitSingleAsync(request).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception failure)
            {
                // An unexpected callback/protocol failure must not strand accepted work or retry
                // a commit whose durable outcome is unknown. Close this queue and fail its owners.
                lock (_gate)
                {
                    _closed = true;
                    _running = false;
                    foreach (Request request in batch)
                    {
                        request.Completion.TrySetException(failure);
                    }
                    while (_waiting.TryDequeue(out Request? request))
                    {
                        request.Completion.TrySetException(failure);
                        _capacity.Release();
                    }
                }
                return;
            }
            finally
            {
                _telemetry.Busy.Record(Stopwatch.GetElapsedTime(busyStarted).TotalMilliseconds);
                _releaseAdmission();
                foreach (Request request in batch)
                {
                    _capacity.Release();
                }
            }
            lock (_gate)
            {
                if (_waiting.Count == 0)
                {
                    _running = false;
                    return;
                }
            }
            // Let already-runnable completion/request continuations run before selecting the next group.
            // This adds no timer or lone-writer collection window.
            scheduled = Stopwatch.GetTimestamp();
            await Task.Yield();
        }
    }

    // Receipt-count failures deliberately escape to the drain loop: the queue must close
    // rather than retry a group whose durable outcome cannot be attributed to its callers.
    private async ValueTask CommitGroupAsync(Request[] active, Guid[] identities)
    {
        try
        {
            var receipts = await commitGroup(active.Select(request => request.Value).ToArray())
                .ConfigureAwait(false);
            ValidateReceiptCount(receipts.Count, active.Length);
            for (int index = 0; index < active.Length; index++)
            {
                active[index].Completion.TrySetResult(receipts[index]);
            }
        }
        catch (EntityOperationGroupDurableException failure)
        {
            ValidateReceiptCount(failure.Receipts.Count, active.Length, failure);
            for (int index = 0; index < active.Length; index++)
            {
                var receipt = failure.Receipts[index];
                active[index].Completion.TrySetException(
                    new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure));
            }
        }
        catch (EntityOperationGroupCompletionException failure)
        {
            ValidateReceiptCount(failure.Receipts.Count, active.Length, failure);
            for (int index = 0; index < active.Length; index++)
            {
                var receipt = failure.Receipts[index];
                if (failure.Failures.TryGetValue(identities[index], out var cause))
                {
                    active[index].Completion.TrySetException(
                        new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, cause));
                }
                else
                {
                    active[index].Completion.TrySetResult(receipt);
                }
            }
        }
        catch (DatabaseCommitGroupPreparationException)
        {
            // No log submission occurred and all staged state was released. Give each
            // caller its own admission result; a stale request must not poison its peers.
            foreach (Request request in active)
            {
                await CommitSingleAsync(request).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is not ReceiptCountException)
        {
            foreach (Request request in active)
            {
                request.Completion.TrySetException(new EntityForgeDbCommitException(
                    "The grouped commit attempt failed.", failure));
            }
        }
    }

    private static void ValidateReceiptCount(int actual, int expected, Exception? cause = null)
    {
        if (actual != expected)
        {
            throw new ReceiptCountException(cause);
        }
    }

    private sealed class ReceiptCountException(Exception? cause) : InvalidOperationException(
        "The grouped commit returned a receipt count that does not match its requests; its durable outcome must be reconciled.", cause);

    private async ValueTask CommitSingleAsync(Request request)
    {
        try
        {
            request.Token.ThrowIfCancellationRequested();
            // Ownership passes to the writer here. Cancellation cannot abandon a submitted commit.
            request.Completion.TrySetResult(await commit(request.Value).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            request.Completion.TrySetCanceled(request.Token);
        }
        catch (Exception failure)
        {
            request.Completion.TrySetException(failure);
        }
    }

    /// <summary>Closes admission and drains already accepted requests.</summary>
    public async ValueTask DisposeAsync()
    {
        Task drain;
        lock (_gate)
        {
            _closed = true;
            drain = _drain;
        }
        await drain.ConfigureAwait(false);
    }

    private sealed record Request(T Value, CancellationToken Token, Activity? Trace)
    {
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();

        public TaskCompletionSource<DatabaseCommitReceipt> Completion
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
