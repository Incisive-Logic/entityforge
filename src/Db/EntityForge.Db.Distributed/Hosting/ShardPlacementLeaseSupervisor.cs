using EntityForge.Db.Distributed.Sharding;
using System.Diagnostics;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>
/// Maintains one authoritative placement lease. A stale/authentication result withdraws authority immediately;
/// transient authority failures are tolerated only until the last successfully renewed lease must have expired.
/// </summary>
public sealed class ShardPlacementLeaseSupervisor : IAsyncDisposable
{
    private readonly IShardPlacementAuthority _authority;
    private readonly ShardPlacementLeaseSupervisorOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _authorityLost = new();
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private int _activeOperations;
    private TaskCompletionSource? _drained;
    private readonly object _disposeSync = new();
    private ShardPlacementLease _lease;
    private ShardPlacementLeaseState _state = ShardPlacementLeaseState.Active;
    private readonly Task _run;
    private long _expiresAtTimestamp;
    private long _renewalGeneration;
    private TaskCompletionSource _renewalChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _admissionClosed;
    private Task? _disposeTask;
    private ShardPlacementRenewalUncertainException? _uncertainRenewal;

    private ShardPlacementLeaseSupervisor(IShardPlacementAuthority authority, ShardPlacementLease lease,
        ShardPlacementLeaseSupervisorOptions options, long expiresAtTimestamp)
    {
        _authority = authority;
        _lease = lease;
        _options = options;
        _expiresAtTimestamp = expiresAtTimestamp;
        _run = RunAsync();
    }

    internal ValueTask SealServingEpochAsync(ShardPlacementClaim claim, Guid transfer, string digest, CancellationToken token) =>
        _authority is DurableServingEpochAuthority epoch ? epoch.SealAsync(claim, transfer, digest, token) : ValueTask.CompletedTask;

    internal bool HasDurableServingEpoch => _authority is DurableServingEpochAuthority;

    internal bool UsesAuthority(IShardPlacementAuthority authority) => ReferenceEquals(_authority, authority);

    /// <summary>Gets a cancellation token signalled when serving authority is lost.</summary>
    public CancellationToken AuthorityLost => _authorityLost.Token;

    /// <summary>Gets the task representing the supervisor's renewal loop.</summary>
    public Task Completion => _run;

    /// <summary>Gets the current supervised authority lease.</summary>
    public ShardPlacementLease CurrentLease
    {
        get
        {
            lock (_sync)
            {
                return _lease;
            }
        }
    }

    /// <summary>Gets the current lease supervision state.</summary>
    public ShardPlacementLeaseState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets whether enough certified authority lifetime remains to admit an operation.</summary>
    public bool CanAdmitOperation
    {
        get
        {
            if (Volatile.Read(ref _admissionClosed) != 0)
            {
                return false;
            }

            TimeSpan required = _options.MaximumOperationDuration ??
                _options.LeaseLifetime - _options.RenewalInterval;
            lock (_sync)
            {
                return _state is ShardPlacementLeaseState.Active &&
                Remaining(_expiresAtTimestamp) >= required;
            }
        }
    }

    internal bool HasAuthorityNow
    {
        get
        {
            if (Volatile.Read(ref _admissionClosed) != 0)
            {
                return false;
            }

            lock (_sync)
            {
                return _state is ShardPlacementLeaseState.Active &&
                Remaining(_expiresAtTimestamp) > TimeSpan.Zero;
            }
        }
    }

    /// <summary>Admits an operation against the current lease and verifies authority before returning.</summary>
    /// <remarks>Admitted callbacks may overlap. Their native synchronization owns execution conflicts;
    /// supervisor shutdown cancels and drains every admitted lifetime before releasing placement.</remarks>
    public async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using LeaseOperation operationLease = await AcquireOperationAsync(cancellationToken).ConfigureAwait(false);
        T result = await operation(operationLease.Token).ConfigureAwait(false);
        await operationLease.VerifyAuthorityAsync().ConfigureAwait(false);
        return result;
    }

    // Only the built-in native fence uses this entrance. Its callback must validate authority
    // inside the fence before touching state; the post-operation authority check is unchanged.
    internal async ValueTask<T> ExecuteFencedAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var admitted = await AcquireOperationAsync(cancellationToken, validateAuthority: false).ConfigureAwait(false);
        T result = await operation(admitted.Token).ConfigureAwait(false);
        await admitted.VerifyAuthorityAsync().ConfigureAwait(false);
        return result;
    }

    // The result is explicitly pending: its coordinator must validate placement
    // atomically in publication or post-application acknowledgement.
    internal async ValueTask<T> ExecutePendingAuthorityCompletionAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        await using var admitted = await AcquireOperationAsync(cancellationToken, validateAuthority: false).ConfigureAwait(false);
        T result = await operation(admitted.Token).ConfigureAwait(false);
        admitted.VerifyLocalAuthority();
        return result;
    }

    internal async ValueTask<LeaseOperation> AcquireOperationAsync(CancellationToken cancellationToken = default,
        bool enforceOperationDeadline = true, bool validateAuthority = true)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        TimeSpan required = _options.MaximumOperationDuration ?? _options.LeaseLifetime - _options.RenewalInterval;
        long? inadequateRenewalGeneration = null;
        while (true)
        {
            if (Volatile.Read(ref _admissionClosed) != 0)
            {
                throw new StaleShardPlacementException(CurrentLease.Placement.Claim.Shard);
            }

            try
            {
                await DistributedReadTelemetry.WaitAsync(_operations, "participant.supervisor.wait", wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested &&
                                                       !cancellationToken.IsCancellationRequested)
            {
                throw new StaleShardPlacementException(CurrentLease.Placement.Claim.Shard);
            }
            bool held = true;
            LeaseOperation? registered = null;
            var admission = DistributedReadTelemetry.Measure("participant.supervisor.admission-held");
            void ReleaseAdmission()
            {
                admission.Dispose();
                _operations.Release();
                held = false;
            }
            try
            {
                // Observe before checking admission. Renewal can complete during validation;
                // waiting on this signal cannot miss that state transition.
                Task renewalChanged;
                lock (_sync)
                {
                    renewalChanged = _renewalChanged.Task;
                }

                if (Volatile.Read(ref _admissionClosed) != 0 ||
                    State is ShardPlacementLeaseState.AuthorityLost or ShardPlacementLeaseState.Stopped)
                {
                    throw new StaleShardPlacementException(CurrentLease.Placement.Claim.Shard);
                }

                if (!CanAdmitOperation)
                {
                    long generation = Volatile.Read(ref _renewalGeneration);
                    if (inadequateRenewalGeneration is { } observed && generation != observed)
                    {
                        throw new StaleShardPlacementException(CurrentLease.Placement.Claim.Shard);
                    }

                    inadequateRenewalGeneration ??= generation;
                    ReleaseAdmission();
                    await WaitForRenewalAsync(renewalChanged, wait.Token, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                ShardPlacementLease current = CurrentLease;
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token,
                    _authorityLost.Token);
                // Own validation before releasing admission. Shutdown joins this lifetime
                // even if an authority ignores cancellation or its response is delayed.
                lock (_sync)
                {
                    if (_activeOperations++ == 0)
                    {
                        _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }

                registered = new(this, current.Placement.Claim, lifetime);
                ReleaseAdmission();
                if (validateAuthority)
                {
                    using var validation = DistributedReadTelemetry.Measure("participant.supervisor.admission-validation");
                    if (!await _authority.ValidateAsync(current.Placement.Claim, lifetime.Token).ConfigureAwait(false))
                    {
                        throw new StaleShardPlacementException(current.Placement.Claim.Shard);
                    }
                }
                if (Volatile.Read(ref _admissionClosed) != 0)
                {
                    throw new StaleShardPlacementException(current.Placement.Claim.Shard);
                }

                lifetime.Token.ThrowIfCancellationRequested();
                bool admissible;
                lock (_sync)
                {
                    admissible = current.Placement.Claim == _lease.Placement.Claim && CanAdmitOperation;
                }

                if (!admissible)
                {
                    long generation = Volatile.Read(ref _renewalGeneration);
                    if (inadequateRenewalGeneration is { } observed && generation != observed)
                    {
                        throw new StaleShardPlacementException(current.Placement.Claim.Shard);
                    }

                    inadequateRenewalGeneration ??= generation;
                    await registered.DisposeAsync().ConfigureAwait(false);
                    registered = null;
                    await WaitForRenewalAsync(renewalChanged, wait.Token, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (enforceOperationDeadline)
                {
                    lifetime.CancelAfter(required);
                }

                return registered;
            }
            catch
            {
                if (held)
                {
                    ReleaseAdmission();
                }

                if (registered is not null)
                {
                    await registered.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }

        }
    }

    private async Task WaitForRenewalAsync(Task changed, CancellationToken wait, CancellationToken caller)
    {
        using var measured = DistributedReadTelemetry.Measure("participant.supervisor.renewal-wait");
        try
        {
            await changed.WaitAsync(wait).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested && !caller.IsCancellationRequested)
        {
            throw new StaleShardPlacementException(CurrentLease.Placement.Claim.Shard);
        }
    }

    // Caller owns _sync. Signals notify only; awakened callers repeat every admission check.
    private void NotifyRenewalChanged()
    {
        var changed = _renewalChanged;
        _renewalChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    internal sealed class LeaseOperation(ShardPlacementLeaseSupervisor owner,
        ShardPlacementClaim claim, CancellationTokenSource lifetime) : IAsyncDisposable
    {
        private int _disposed;

        public CancellationToken Token => lifetime.Token;

        internal void VerifyLocalAuthority()
        {
            if (lifetime.IsCancellationRequested || !owner.HasAuthorityNow || claim != owner.CurrentLease.Placement.Claim)
            {
                throw new StaleShardPlacementException(claim.Shard);
            }
        }

        public async ValueTask VerifyAuthorityAsync()
        {
            if (lifetime.IsCancellationRequested || !owner.HasAuthorityNow ||
                !await owner._authority.ValidateAsync(claim, CancellationToken.None).ConfigureAwait(false))
            {
                throw new StaleShardPlacementException(claim.Shard);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                lifetime.Dispose();
                lock (owner._sync)
                {
                    if (--owner._activeOperations == 0)
                    {
                        owner._drained!.TrySetResult();
                    }
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    internal async ValueTask ValidateOperationAsync(ShardPlacementClaim claim,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasAuthorityNow || claim != CurrentLease.Placement.Claim ||
            !await _authority.ValidateAsync(claim, cancellationToken).ConfigureAwait(false))
        {
            throw new StaleShardPlacementException(claim.Shard);
        }
    }

    /// <summary>Starts supervision of an existing placement lease with validated renewal and operation-duration bounds.</summary>
    public static async ValueTask<ShardPlacementLeaseSupervisor> StartAsync(IShardPlacementAuthority authority,
        ShardPlacementLease lease, ShardPlacementLeaseSupervisorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(lease);
        ShardPlacementLeaseSupervisorOptions resolved = options ?? ShardPlacementLeaseSupervisorOptions.Default;
        resolved.Validate();
        // Establish a complete, locally measured lease window instead of guessing the age of the supplied lease.
        long renewalStarted = Stopwatch.GetTimestamp();
        ShardPlacementLease renewed;
        try
        {
            renewed = await authority.RenewAsync(lease, resolved.LeaseLifetime, cancellationToken).ConfigureAwait(false);
        }
        catch (ShardPlacementRenewalUncertainException uncertain) when (uncertain.BelongsTo(authority))
        {
            // No supervised lifetime has been returned. Either discharge the exact
            // attempt or return its identity with the startup/cleanup failure.
            try
            {
                await uncertain.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Lease startup and renewal cleanup remain incomplete.", uncertain, cleanup);
            }
            throw;
        }
        return new(authority, renewed, resolved, Add(renewalStarted, resolved.LeaseLifetime));
    }

    // Reserve a quarter of the admission window for renewal I/O. This changes when
    // renewal starts, never the lease deadline or the required operation lifetime.
    private const int RenewalHeadroomDivisor = 4;

    private TimeSpan NextRenewalDelay()
    {
        TimeSpan required = _options.MaximumOperationDuration ?? _options.LeaseLifetime - _options.RenewalInterval;
        long window = Math.Min(_options.RenewalInterval.Ticks, (_options.LeaseLifetime - required).Ticks);
        TimeSpan interval = TimeSpan.FromTicks(Math.Max(1, window - window / RenewalHeadroomDivisor));
        long renewedAt;
        lock (_sync)
        {
            renewedAt = _expiresAtTimestamp - (long)(_options.LeaseLifetime.TotalSeconds * Stopwatch.Frequency);
        }

        TimeSpan remaining = Remaining(Add(renewedAt, interval));
        // A slow renewal must not turn the worker into a tight retry loop.
        TimeSpan minimum = TimeSpan.FromTicks(Math.Max(1, interval.Ticks / RenewalHeadroomDivisor));
        return remaining > minimum ? remaining : minimum;
    }

    private async Task RunAsync()
    {
        TimeSpan delay = NextRenewalDelay();
        try
        {
            while (true)
            {
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                try
                {
                    long renewalStarted = Stopwatch.GetTimestamp();
                    // Shutdown stops admission and future renewals, but must drain an
                    // admitted lease replacement. Cancelling its reply can lose the new
                    // lease ID and make shutdown release the superseded ID instead.
                    using var renewalDeadline = new CancellationTokenSource(_options.LeaseLifetime);
                    ShardPlacementLease renewed = await _authority.RenewAsync(CurrentLease,
                        _options.LeaseLifetime, renewalDeadline.Token).ConfigureAwait(false);
                    lock (_sync)
                    {
                        _lease = renewed;
                        _state = ShardPlacementLeaseState.Active;
                        _expiresAtTimestamp = Add(renewalStarted, _options.LeaseLifetime);
                        Interlocked.Increment(ref _renewalGeneration);
                        NotifyRenewalChanged();
                    }
                    delay = NextRenewalDelay();
                }
                catch (ShardPlacementRenewalUncertainException uncertain)
                {
                    _uncertainRenewal = uncertain;
                    // Stop admission and further renewals. Retain this attempt until
                    // explicit cleanup succeeds; never adopt an observed lease blindly.
                    LoseAuthority();
                    break;
                }
                catch (Exception exception) when (IsDefinitiveAuthorityLoss(exception))
                {
                    LoseAuthority();
                    break;
                }
                catch
                {
                    lock (_sync)
                    {
                        _state = ShardPlacementLeaseState.AuthorityUncertain;
                        NotifyRenewalChanged();
                    }
                    TimeSpan remaining = Remaining(_expiresAtTimestamp);
                    if (remaining <= TimeSpan.Zero)
                    {
                        LoseAuthority();
                        break;
                    }
                    delay = remaining < _options.RetryInterval ? remaining : _options.RetryInterval;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {

        }
        finally
        {
            lock (_sync)
            {
                if (_state is not ShardPlacementLeaseState.AuthorityLost)
                {
                    _state = ShardPlacementLeaseState.Stopped;
                }

                NotifyRenewalChanged();
            }
        }
    }

    private void LoseAuthority()
    {
        lock (_sync)
        {
            _state = ShardPlacementLeaseState.AuthorityLost;
            NotifyRenewalChanged();
        }
        _authorityLost.Cancel();
    }

    private static bool IsDefinitiveAuthorityLoss(Exception exception) =>
        exception is StaleShardPlacementException or ShardPlacementAuthorityAuthenticationException;

    private static long Add(long timestamp, TimeSpan duration) => checked(timestamp +
        (long)(duration.TotalSeconds * Stopwatch.Frequency));

    private static TimeSpan Remaining(long deadline) => TimeSpan.FromSeconds(
        Math.Max(0, deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);

    /// <summary>Stops renewal and releases supervisor-owned resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
            {
                _disposeTask = DisposeCoreAsync();
            }

            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _admissionClosed, 1);
        _stop.Cancel();
        var failures = new List<Exception>();
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                Task drainage;
                lock (_sync)
                {
                    drainage = _activeOperations == 0 ? Task.CompletedTask : _drained!.Task;
                }

                await drainage.ConfigureAwait(false);
                if (_uncertainRenewal is { } uncertain)
                {
                    try
                    {
                        if (!uncertain.BelongsTo(_authority) || uncertain.Previous != CurrentLease)
                        {
                            throw new InvalidOperationException("Uncertain renewal does not identify this supervisor's attempt.");
                        }

                        await uncertain.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                        _uncertainRenewal = null;
                    }
                    catch (Exception cleanup)
                    {
                        failures.Add(new AggregateException("The exact renewal leases still require cleanup.", uncertain, cleanup));
                    }
                }
                else if (State is not ShardPlacementLeaseState.AuthorityLost)
                {
                    try
                    {
                        await _authority.ReleaseAsync(CurrentLease, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (StaleShardPlacementException)
                    {

                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }
            }
            finally
            {
                _operations.Release();
                // Queued admission continuations may still observe closure; keep the managed gate
                // available so they can unwind rather than racing semaphore disposal.
                if (failures.Count == 0)
                {
                    _stop.Dispose();
                    _authorityLost.Dispose();
                }
            }
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("Shard lease supervisor cleanup failed.", failures);
        }
    }
}
