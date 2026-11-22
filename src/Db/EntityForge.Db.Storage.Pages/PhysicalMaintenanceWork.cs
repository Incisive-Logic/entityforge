namespace EntityForge.Db.Storage.Pages;

/// <summary>Cooperatively advances one private maintenance operation in bounded I/O slices.
/// A slice ends only between operations, never partway through a durable write batch.</summary>
public sealed class PhysicalMaintenanceWork : IAsyncDisposable
{
    /// <summary>Maximum number of pages in one indivisible reservation or write batch.</summary>
    public const int MaximumBatchPages = 256;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _advance = new(1, 1);
    private TaskCompletionSource _paused = NewSignal();
    private TaskCompletionSource? _resume;
    private Task? _work;
    private int _remaining;
    private bool _closed;
    private Exception? _observedFailure;
    private readonly TimeProvider _time;
    private TimeSpan? _sliceDuration;
    private long _sliceStarted;
    private bool _reservedInSlice;

    /// <summary>Creates a maintenance controller using the supplied clock or the system clock.</summary>
    public PhysicalMaintenanceWork(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>Gets the token cancelled when the operation is stopped or disposed.</summary>
    public CancellationToken Cancellation => _stop.Token;

    /// <summary>Gets whether the operation finished successfully. Unstarted, cancelled and failed work returns false.</summary>
    public bool IsCompleted => _work?.IsCompletedSuccessfully == true;

    /// <summary>Starts private work. Its first I/O reservation waits for a slice.</summary>
    public void Start(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_work is not null)
            {
                throw new InvalidOperationException("Maintenance work is already started.");
            }

            _work = RunAsync(work);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> work) =>
        await work(_stop.Token).ConfigureAwait(false);

    /// <summary>Reserves complete pages before reading or committing a write batch.
    /// Callers must keep each reservation no larger than the slice budget.</summary>
    public async ValueTask ReserveAsync(int pages)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pages);
        if (pages > MaximumBatchPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pages));
        }

        while (true)
        {
            Task resume;
            lock (_gate)
            {
                _stop.Token.ThrowIfCancellationRequested();
                bool elapsed = _reservedInSlice && _sliceDuration is { } duration &&
                    _time.GetElapsedTime(_sliceStarted) >= duration;
                if (_remaining >= pages && !elapsed)
                {
                    _remaining -= pages;
                    _reservedInSlice = true;
                    return;
                }
                _paused.TrySetResult();
                resume = (_resume ??= NewSignal()).Task;
            }
            await resume.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the current slice at a phase boundary before beginning more work.</summary>
    public ValueTask YieldAsync()
    {
        lock (_gate)
        {
            _remaining = 0;
        }

        return ReserveAsync(1);
    }

    /// <summary>Returns after the operation completes or exhausts this slice.</summary>
    /// <param name="pageBudget">Page reservations available in this slice; at least <see cref="MaximumBatchPages"/>.</param>
    /// <param name="token">Cancels waiting for admission. Once admitted, cancellation stops and drains the operation.</param>
    /// <param name="maximumDuration">Optional positive duration checked between reservations after the first reservation;
    /// it does not interrupt an in-flight batch or impose a hard elapsed-time deadline.</param>
    /// <returns>True on successful completion; false when work pauses for another slice. Work failures propagate.</returns>
    public async ValueTask<bool> AdvanceAsync(int pageBudget, CancellationToken token = default,
        TimeSpan? maximumDuration = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageBudget);
        if (pageBudget < MaximumBatchPages)
        {
            throw new ArgumentOutOfRangeException(nameof(pageBudget));
        }

        if (maximumDuration is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDuration));
        }

        await _advance.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Task work, paused;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                work = _work ?? throw new InvalidOperationException("Maintenance work has not started.");
                _remaining = pageBudget;
                _sliceDuration = maximumDuration;
                _sliceStarted = _time.GetTimestamp();
                _reservedInSlice = false;
                paused = (_paused = NewSignal()).Task;
                var resume = _resume;
                _resume = null;
                resume?.TrySetResult();
            }
            try
            {
                await Task.WhenAny(work, paused).WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _stop.Cancel();
                try
                {
                    await work.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {

                }
                catch (Exception failure)
                {
                    _observedFailure = failure;
                    throw;
                }
                throw;
            }
            if (work.IsCompleted)
            {
                try
                {
                    await work.ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    _observedFailure = failure;
                    throw;
                }
                return true;
            }
            return false;
        }
        finally
        {
            _advance.Release();
        }
    }

    /// <summary>Stops and drains the operation. Cancellation and failures already reported by
    /// <see cref="AdvanceAsync"/> are suppressed; previously unobserved failures propagate.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? work;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            work = _work;
        }
        _stop.Cancel();
        if (work is not null)
        {
            try
            {
                await work.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {

            }
            // Advance already reported this exact failure to the kernel, which
            // invalidated admission. Disposal must drain, not report it a second
            // time as a new shutdown failure. Unobserved failures still propagate.
            catch (Exception failure) when (ReferenceEquals(failure, _observedFailure))
            {

            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
