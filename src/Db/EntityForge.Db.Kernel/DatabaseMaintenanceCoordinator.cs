using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace EntityForge.Db.Kernel;

/// <summary>Couples an immutable profile view to its exact version and committed-change boundary.</summary>
/// <param name="Version">The exact database version associated with the immutable snapshot.</param>
/// <param name="ChangeCursor">The committed-change boundary captured with the snapshot, if available.</param>
/// <param name="Snapshot">The immutable profile view on which maintenance will operate.</param>
public sealed record DatabaseMaintenanceSnapshot<TSnapshot>(
    DatabaseVersion Version,
    DatabaseChangeCursor? ChangeCursor,
    TSnapshot Snapshot);

/// <summary>Allows foreground reads to suspend opportunistic background maintenance.</summary>
public interface IDatabaseBackgroundMaintenanceReadAdmission
{
    /// <summary>Acquires a foreground-read lease; maintenance resumes after the final lease is released.</summary>
    IDisposable EnterForegroundRead();
}

/// <summary>Protects read-view selection and physical snapshot use from explicit structural replacement.</summary>
public interface IDatabaseSnapshotReadAdmission
{
    /// <summary>Acquires a disposable read scope that protects snapshot selection and use from structural replacement.</summary>
    ValueTask<IDisposable> EnterSnapshotReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reports bounded progress from a physical maintenance implementation.</summary>
public sealed class DatabaseMaintenanceProgress
{
    private readonly Action<DatabaseMaintenancePhase, int, long, long> _report;

    internal DatabaseMaintenanceProgress(Action<DatabaseMaintenancePhase, int, long, long> report) =>
        _report = report;

    /// <summary>Publishes the current phase and optional bounded work counters.</summary>
    public void Report(DatabaseMaintenancePhase phase, int attempt = 0,
        long completedWork = 0, long totalWork = 0) =>
        _report(phase, attempt, completedWork, totalWork);
}

/// <summary>Serializes bounded instance-owned maintenance and exposes low-cardinality lifecycle telemetry.</summary>
public sealed class DatabaseMaintenanceCoordinator : IAsyncDisposable
{
    /// <summary>The Meter name used for maintenance lifecycle and phase measurements.</summary>
    public const string MeterName = "EntityForge.Db.Maintenance";
    /// <summary>The telemetry phase identifier for capturing the maintenance snapshot.</summary>
    public const string SnapshotPhase = "snapshot";
    /// <summary>The telemetry phase identifier for building replacement state.</summary>
    public const string BuildPhase = "build";
    /// <summary>The telemetry phase identifier for catching replacement state up to current writes.</summary>
    public const string CatchUpPhase = "catch-up";
    /// <summary>The telemetry phase identifier for publishing replacement roots.</summary>
    public const string CutoverPhase = "cutover";
    /// <summary>The telemetry phase identifier for waiting to exclude writers.</summary>
    public const string WriterExclusionWaitPhase = "writer-exclusion-wait";
    /// <summary>The telemetry phase identifier for discarding obsolete or abandoned replacement resources.</summary>
    public const string CleanupPhase = "cleanup";
    /// <summary>The telemetry phase identifier for flushing replacement storage.</summary>
    public const string FlushPhase = "replacement-flush";
    internal const string PhaseDurationInstrument = "entityforge.db.maintenance.phase.duration";
    internal const string PhaseAllocatedBytesInstrument = "entityforge.db.maintenance.phase.allocated_bytes";
    private const double MaximumMaintenanceMemoryLoad = 0.70;
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Jobs = Meter.CreateCounter<long>("entityforge.db.maintenance.jobs");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("entityforge.db.maintenance.duration", "ms");

    private static readonly Histogram<double> PhaseDuration =
        Meter.CreateHistogram<double>(PhaseDurationInstrument, "ms");

    private static readonly Histogram<long> PhaseAllocatedBytes =
        Meter.CreateHistogram<long>(PhaseAllocatedBytesInstrument, "By");

    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly Func<bool> _hasCapacity;
    private readonly object _gate = new();
    private DatabaseMaintenanceStatus _status = Idle();
    private int _disposed;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    /// <summary>Creates a coordinator with an optional resource-admission predicate.</summary>
    public DatabaseMaintenanceCoordinator(Func<bool>? hasCapacity = null) =>
        _hasCapacity = hasCapacity ?? HasMemoryCapacity;

    /// <summary>Gets the latest lifecycle state.</summary>
    public DatabaseMaintenanceStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>Runs one admitted maintenance operation exclusively for the owning database instance.</summary>
    public async ValueTask<DatabaseMaintenanceResult<TVersion>> ExecuteAsync<TVersion>(string operation,
        Func<Guid, DatabaseMaintenanceProgress, CancellationToken, ValueTask<(TVersion Version, int Attempts)>> execute,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(execute);
        Guid jobId = Guid.NewGuid();
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            await _exclusive.WaitAsync(admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(DatabaseMaintenanceCoordinator));
        }
        long started = Stopwatch.GetTimestamp();
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            Update(jobId, operation, DatabaseMaintenancePhase.WaitingForAdmission, 0, 0, 0, null);
            if (!_hasCapacity())
            {
                throw new DatabaseMaintenanceAdmissionException(operation);
            }

            var progress = new DatabaseMaintenanceProgress((phase, attempt, completed, total) =>
                Update(jobId, operation, phase, attempt, completed, total, null));
            (TVersion version, int attempts) = await execute(jobId, progress, cancellationToken)
                .ConfigureAwait(false);
            TimeSpan duration = Stopwatch.GetElapsedTime(started);
            Update(jobId, operation, DatabaseMaintenancePhase.Completed, attempts, 1, 1, null);
            Record(operation, "completed", duration);
            return new(jobId, operation, version, attempts, duration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TimeSpan duration = Stopwatch.GetElapsedTime(started);
            Update(jobId, operation, DatabaseMaintenancePhase.Cancelled, 0, 0, 0, null);
            Record(operation, "cancelled", duration);
            throw;
        }
        catch (Exception exception)
        {
            TimeSpan duration = Stopwatch.GetElapsedTime(started);
            Update(jobId, operation, DatabaseMaintenancePhase.Failed, 0, 0, 0,
                exception.GetType().Name);
            Record(operation, "failed", duration);
            throw;
        }
        finally
        {
            _exclusive.Release();
        }
    }

    /// <summary>Cancels maintenance and waits for exclusive maintenance activity to finish before releasing resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        _shutdown.Cancel();
        await _exclusive.WaitAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private void Update(Guid jobId, string operation, DatabaseMaintenancePhase phase,
        int attempt, long completed, long total, string? failure)
    {
        lock (_gate)
        {
            _status = new(jobId, operation, phase, attempt, completed, total,
            failure, DateTimeOffset.UtcNow);
        }
    }

    private static void Record(string operation, string outcome, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "entityforge.db.maintenance.operation", operation },
            { "entityforge.db.outcome", outcome }
        };
        Jobs.Add(1, tags);
        Duration.Record(duration.TotalMilliseconds, tags);
    }

    /// <summary>Records one measured maintenance phase for a profile implementation.</summary>
    public static void RecordPhase(string operation, string target, string phase, int attempt,
        long started, long allocatedBefore)
    {
        var tags = new TagList
        {
            { "entityforge.db.maintenance.operation", operation },
            { "entityforge.db.maintenance.target", target },
            { "entityforge.db.maintenance.phase", phase },
            { "entityforge.db.maintenance.attempt", attempt }
        };
        PhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        PhaseAllocatedBytes.Record(Math.Max(0, GC.GetTotalAllocatedBytes() - allocatedBefore), tags);
    }

    /// <summary>Records one measured maintenance phase when allocation attribution is not required.</summary>
    public static void RecordDurationPhase(string operation, string target, string phase, int attempt,
        long started)
    {
        var tags = new TagList
        {
            { "entityforge.db.maintenance.operation", operation },
            { "entityforge.db.maintenance.target", target },
            { "entityforge.db.maintenance.phase", phase },
            { "entityforge.db.maintenance.attempt", attempt }
        };
        PhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }

    /// <summary>Starts a consistently attributed maintenance phase measurement.</summary>
    public static DatabaseMaintenancePhaseMeasurement BeginPhase() => new(
        Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes());

    private static bool HasMemoryCapacity()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long liveBytes = GC.GetTotalMemory(forceFullCollection: true);
        return memory.TotalAvailableMemoryBytes <= 0 ||
               liveBytes < memory.TotalAvailableMemoryBytes * MaximumMaintenanceMemoryLoad;
    }

    private static DatabaseMaintenanceStatus Idle() => new(null, null,
        DatabaseMaintenancePhase.Idle, 0, 0, 0, null, DateTimeOffset.UtcNow);
}

/// <summary>Completes one consistently attributed maintenance phase measurement.</summary>
/// <param name="Started">The Stopwatch timestamp captured at phase entry.</param>
/// <param name="AllocatedBefore">The allocation counter captured at phase entry, in bytes.</param>
public readonly record struct DatabaseMaintenancePhaseMeasurement(long Started, long AllocatedBefore)
{
    /// <summary>Records the elapsed duration and allocations under the supplied maintenance phase.</summary>
    public void Complete(string operation, string target, string phase, int attempt) =>
        DatabaseMaintenanceCoordinator.RecordPhase(operation, target, phase, attempt, Started, AllocatedBefore);
}

/// <summary>Indicates that a maintenance operation exceeded the current resource-admission budget.</summary>
public sealed class DatabaseMaintenanceAdmissionException(string operation) : EntityForgeDbException(
    $"Maintenance operation '{operation}' was deferred because the instance resource budget is exhausted.");

/// <summary>
/// Coalesces opportunistic maintenance signals into bounded background passes while exposing the
/// same lifecycle and telemetry contract as explicitly requested maintenance.
/// </summary>
public sealed class DatabaseBackgroundMaintenanceCoordinator :
    IDatabaseBackgroundMaintenanceReadAdmission, IAsyncDisposable
{
    private const int SignalCapacity = 1;
    private const int MaximumPassesPerSignal = 8;
    private const double MaximumBackgroundMaintenanceMemoryLoad = 0.45;
    private static readonly TimeSpan ForegroundReadQuietPeriod = TimeSpan.FromMilliseconds(250);
    private readonly Channel<long> _signals = Channel.CreateBounded<long>(new BoundedChannelOptions(SignalCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    private readonly Func<bool> _needsMaintenance;
    private readonly Func<CancellationToken, ValueTask<bool>> _execute;
    private readonly DatabaseMaintenanceCoordinator _lifecycle;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private readonly object _sync = new();
    private TaskCompletionSource _idle = Completed();
    private CancellationTokenSource? _activePass;
    private int _forceEvaluationRequested;
    private long _preemptionGeneration;
    private int _foregroundReaders;
    private bool _deferredSignal;
    private bool _deferredForceEvaluation;
    private bool _disposed;
    private CancellationTokenSource? _readQuietPeriod;

    /// <summary>Creates an instance-owned bounded background maintenance worker.</summary>
    public DatabaseBackgroundMaintenanceCoordinator(Func<bool> needsMaintenance,
        Func<CancellationToken, ValueTask<bool>> execute, Func<bool>? hasCapacity = null)
    {
        _needsMaintenance = needsMaintenance ?? throw new ArgumentNullException(nameof(needsMaintenance));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _lifecycle = new(hasCapacity ?? HasBackgroundMemoryCapacity);
        _worker = RunAsync();
    }

    /// <summary>Gets the latest background maintenance lifecycle state.</summary>
    public DatabaseMaintenanceStatus Status => _lifecycle.Status;

    /// <summary>Requests maintenance, coalescing duplicate outstanding requests.</summary>
    public void Signal(bool forceEvaluation = false)
    {
        if (!forceEvaluation && !_needsMaintenance())
        {
            return;
        }

        lock (_sync)
        {
            if (_foregroundReaders > 0)
            {
                _deferredSignal = true;
                _deferredForceEvaluation |= forceEvaluation;
                return;
            }
            CancelReadQuietPeriod();
            _deferredSignal = false;
            _deferredForceEvaluation = false;
            if (forceEvaluation)
            {
                Interlocked.Exchange(ref _forceEvaluationRequested, 1);
            }

            if (_idle.Task.IsCompleted)
            {
                _idle = NewCompletion();
            }

            _signals.Writer.TryWrite(_preemptionGeneration);
        }
    }

    /// <inheritdoc />
    public IDisposable EnterForegroundRead()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _foregroundReaders++;
            if (_foregroundReaders == 1)
            {
                CancelReadQuietPeriod();
                _preemptionGeneration++;
                _deferredSignal |= _needsMaintenance() || _activePass is not null;
                while (_signals.Reader.TryRead(out long _))
                {
                    _deferredSignal = true;
                }

                _activePass?.Cancel();
            }
        }
        return new ForegroundReadLease(this);
    }

    /// <summary>Cancels opportunistic work so foreground activity can take priority.</summary>
    public void Preempt()
    {
        lock (_sync)
        {
            CancelReadQuietPeriod();
            _deferredSignal = false;
            _deferredForceEvaluation = false;
            _preemptionGeneration++;
            Interlocked.Exchange(ref _forceEvaluationRequested, 0);
            while (_signals.Reader.TryRead(out long _))
            {
            }
            _activePass?.Cancel();
            if (_activePass is null)
            {
                _idle.TrySetResult();
            }
        }
    }

    private void ExitForegroundRead()
    {
        lock (_sync)
        {
            if (--_foregroundReaders != 0)
            {
                return;
            }

            if (_disposed)
            {
                return;
            }

            if (!_deferredSignal)
            {
                return;
            }

            if (_idle.Task.IsCompleted)
            {
                _idle = NewCompletion();
            }

            var quietPeriod = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            _readQuietPeriod = quietPeriod;
            _ = ResumeAfterReadQuietPeriodAsync(quietPeriod);
        }
    }

    private async Task ResumeAfterReadQuietPeriodAsync(CancellationTokenSource quietPeriod)
    {
        try
        {
            await Task.Delay(ForegroundReadQuietPeriod, quietPeriod.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (quietPeriod.IsCancellationRequested)
        {
            return;
        }
        lock (_sync)
        {
            if (!ReferenceEquals(_readQuietPeriod, quietPeriod) || _disposed || _foregroundReaders != 0)
            {
                return;
            }

            _readQuietPeriod = null;
            bool forceEvaluation = _deferredForceEvaluation;
            _deferredSignal = false;
            _deferredForceEvaluation = false;
            if (forceEvaluation)
            {
                Interlocked.Exchange(ref _forceEvaluationRequested, 1);
            }

            _signals.Writer.TryWrite(_preemptionGeneration);
        }
        quietPeriod.Dispose();
    }

    private void CancelReadQuietPeriod()
    {
        CancellationTokenSource? quietPeriod = _readQuietPeriod;
        _readQuietPeriod = null;
        if (quietPeriod is null)
        {
            return;
        }

        quietPeriod.Cancel();
        quietPeriod.Dispose();
    }

    /// <summary>Waits until all currently signalled maintenance becomes idle.</summary>
    public ValueTask WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_sync)
        {
            idle = _idle.Task;
        }

        return new(idle.WaitAsync(cancellationToken));
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (long generation in _signals.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                bool stale;
                lock (_sync)
                {
                    stale = generation != _preemptionGeneration;
                    if (!stale)
                    {
                        _activePass = pass;
                    }
                    else if (!_signals.Reader.TryPeek(out long _))
                    {
                        _idle.TrySetResult();
                    }
                }
                if (stale)
                {
                    continue;
                }

                bool forceEvaluation = Interlocked.Exchange(ref _forceEvaluationRequested, 0) != 0;
                try
                {
                    await _lifecycle.ExecuteAsync("background-maintenance",
                        (operationId, progress, cancellationToken) => ExecutePassesAsync(
                            operationId, progress, forceEvaluation, cancellationToken),
                        pass.Token).ConfigureAwait(false);
                }
                catch (DatabaseMaintenanceAdmissionException)
                {

                }
                // A prepared partition transaction can reject opportunistic work
                // before durability. Defer to a later signal; never discard its reservation.
                catch (DatabasePartitionConflictException)
                {

                }
                catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
                {

                }
                catch (Exception exception) when (IsResourcePressure(exception))
                {

                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var failure = new EntityForgeDbStorageException("Database background maintenance failed.", exception);
                    lock (_sync)
                    {
                        _idle.TrySetException(failure);
                    }

                    throw failure;
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activePass, pass))
                        {
                            _activePass = null;
                        }

                        if (!_signals.Reader.TryPeek(out long _))
                        {
                            _idle.TrySetResult();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {

        }
    }

    private async ValueTask<(bool Version, int Attempts)> ExecutePassesAsync(Guid _,
        DatabaseMaintenanceProgress progress, bool forceEvaluation, CancellationToken cancellationToken)
    {
        int pass = 0;
        for (; pass < MaximumPassesPerSignal && (forceEvaluation || _needsMaintenance()); pass++)
        {
            forceEvaluation = false;
            progress.Report(DatabaseMaintenancePhase.Building, pass + 1);
            if (!await _execute(cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
        return (true, pass);
    }

    /// <summary>Stops accepting maintenance signals, cancels current maintenance and waits for the worker to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            CancelReadQuietPeriod();
        }
        _signals.Writer.TryComplete();
        Preempt();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _stopping.Cancel();
            _stopping.Dispose();
            await _lifecycle.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ForegroundReadLease(DatabaseBackgroundMaintenanceCoordinator owner) : IDisposable
    {
        private DatabaseBackgroundMaintenanceCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitForegroundRead();
    }

    private static TaskCompletionSource Completed()
    {
        TaskCompletionSource completion = NewCompletion();
        completion.SetResult();
        return completion;
    }

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsResourcePressure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OutOfMemoryException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasBackgroundMemoryCapacity()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        return memory.TotalAvailableMemoryBytes <= 0 ||
               memory.HeapSizeBytes < memory.TotalAvailableMemoryBytes * MaximumBackgroundMaintenanceMemoryLoad;
    }
}
