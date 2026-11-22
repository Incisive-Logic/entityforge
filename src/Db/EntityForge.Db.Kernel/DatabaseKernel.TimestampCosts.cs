using System.Diagnostics;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

// Timestamp visibility remains opt-in while profile and deployment qualification
// is incomplete. The shared-host provider is retained for matched experiments.
internal sealed record DatabaseTimestampCostOptions(Guid Domain, Func<long> Now,
    Func<bool> Healthy, TimeSpan ReservationWindow)
{
    internal bool Visibility
    {
        get; init;
    }

    internal IDatabaseBoundedClock? BoundedClock
    {
        get; init;
    }

    internal static DatabaseTimestampCostOptions FromClock(IDatabaseBoundedClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Guid domain = clock.Domain;
        if (domain == Guid.Empty)
        {
            throw new ArgumentException("A stable clock domain is required.", nameof(clock));
        }

        DatabaseClockInterval Read()
        {
            var interval = clock.Read();
            if (clock.Domain != domain || interval.EarliestUtcTicks < 0 ||
                interval.LatestUtcTicks < interval.EarliestUtcTicks || interval.LatestUtcTicks > DateTime.MaxValue.Ticks)
            {
                throw new InvalidOperationException("Clock interpretation changed or returned invalid bounds.");
            }

            return interval;
        }
        return new(domain, () => Read().EarliestUtcTicks, () =>
        {
            Read();
            return true;
        },
            TimeSpan.FromMilliseconds(500))
        {
            Visibility = true,
            BoundedClock = clock,
            BoundedObservation = Read
        };
    }

    internal Func<DatabaseClockInterval>? BoundedObservation
    {
        get; private init;
    }

    internal static readonly AsyncLocal<DatabaseTimestampCostOptions?> QualificationOverride = new();

    internal static DatabaseTimestampCostOptions? FromEnvironment(DatabaseVersioningMode mode)
    {
        bool visibility = Environment.GetEnvironmentVariable("ENTITYFORGE_TIMESTAMP_MVCC") == "1";
        if (!visibility && Environment.GetEnvironmentVariable("ENTITYFORGE_TIMESTAMP_COST") != "1" ||
            mode != DatabaseVersioningMode.PartitionLocal)
        {
            return null;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new NotSupportedException("Clock-cost qualification requires a shared Linux boot clock.");
        }

        Guid domain = Guid.Parse(File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim());
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_QUALIFICATION_CLOCK_INTERVAL_MS") is { Length: > 0 } text)
        {
            if (!visibility || !double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double milliseconds) ||
                !double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 100)
            {
                throw new InvalidOperationException("Invalid qualification-only clock interval.");
            }

            return FromClock(new QualificationIntervalClock(domain, TimeSpan.FromMilliseconds(milliseconds).Ticks));
        }
        return new(domain, () => Stopwatch.GetElapsedTime(0).Ticks, () => true, TimeSpan.FromMilliseconds(500))
        {
            Visibility = visibility
        };
    }

    // Synthetic common-host uncertainty for costing only; never independent-clock evidence.
    private sealed class QualificationIntervalClock(Guid domain, long width) : IDatabaseBoundedClock
    {
        public Guid Domain => domain;

        public DatabaseClockInterval Read()
        {
            long now = DateTime.UtcNow.Ticks;
            return new(now - width / 2, now + width - width / 2);
        }
    }
}

public sealed partial class DatabaseKernel
{
    private const string TimestampEvidenceCodec = "entityforge.kernel.timestamp-cost.v1";
    private static readonly TimeSpan ClockSampleValidity = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumRecoveryClockWait = TimeSpan.FromSeconds(2);

    internal DatabaseTimestampCostOptions? TimestampCostOptions
    {
        get; set;
    }

    private DatabaseHybridClock? _hybridClock;
    private DurableTimestampState? _timestampState;
    private DatabaseHybridTime _readPromise;
    private int _timestampProtectionCount;

    internal int TimestampProtectionCount => Volatile.Read(ref _timestampProtectionCount);

    internal DurableTimestampState? TimestampCostState => _timestampState;

    internal long TimestampReservationForces
    {
        get; private set;
    }

    private IDisposable ProtectTimestampSnapshot(IDisposable pin)
    {
        if (TimestampCostOptions is null)
        {
            return pin;
        }

        Interlocked.Increment(ref _timestampProtectionCount);
        return new TimestampProtectedPin(this, pin);
    }

    private sealed class TimestampProtectedPin(DatabaseKernel kernel, IDisposable pin) : IDisposable
    {
        private readonly object _gate = new();
        private bool _released;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_released)
                {
                    return;
                }

                pin.Dispose(); // A failed release must not claim protection was discharged.
                _released = true;
                Interlocked.Decrement(ref kernel._timestampProtectionCount);
            }
        }
    }

    private void RestoreTimestamp(DurableTimestampState? state)
    {
        if (state is null)
        {
            return;
        }

        var options = TimestampCostOptions ?? throw new EntityForgeDbFormatException("Timestamp experiment must be enabled for this store.");
        if (state.Domain != options.Domain || state.Mvcc != options.Visibility)
        {
            throw new EntityForgeDbFormatException("Timestamp boot domain or visibility mode differs; implicit rebasing is forbidden.");
        }

        if (_timestampState is null)
        {
            _timestampState = state;
        }
        else
        {
            var maximum = new DatabaseHybridTime(state.Physical, state.Logical).CompareTo(
                new(_timestampState.Physical, _timestampState.Logical)) > 0 ? state : _timestampState;
            _timestampState = new(options.Domain, maximum.Physical, maximum.Logical,
                Math.Max(state.ReservedReadThrough, _timestampState.ReservedReadThrough), options.Visibility);
        }
    }

    private async ValueTask InitializeTimestampClockAsync(CancellationToken token)
    {
        if (TimestampCostOptions is not { } options)
        {
            return;
        }

        if (options.Domain == Guid.Empty || options.ReservationWindow <= TimeSpan.Zero ||
            options.ReservationWindow > TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("Invalid clock experiment options.");
        }

        long recovered = Math.Max(_timestampState?.Physical ?? 0, _timestampState?.ReservedReadThrough ?? 0);
        long started = Stopwatch.GetTimestamp();
        while (options.Now() < recovered)
        {
            if (!options.Healthy() || Stopwatch.GetElapsedTime(started) > MaximumRecoveryClockWait)
            {
                throw new InvalidOperationException("Recovered timestamp exceeds the qualified clock; admission refused.");
            }

            await Task.Delay(1, token).ConfigureAwait(false);
        }
        var lower = new DatabaseHybridTime(recovered, _timestampState?.Logical ?? 0);
        _timestampState ??= new(options.Domain, lower.PhysicalTicks, lower.Logical, 0, options.Visibility);
        _readPromise = lower;
        _hybridClock = new(TimeProvider.System, () =>
        {
            long sampledAt = Stopwatch.GetTimestamp();
            if (options.BoundedObservation is { } observe)
            {
                var interval = observe();
                return new(interval.EarliestUtcTicks, interval.LatestUtcTicks, sampledAt, true);
            }
            long now = options.Now();
            return new(now, checked(now + ClockSampleValidity.Ticks), sampledAt, options.Healthy());
        }, ClockSampleValidity, options.BoundedClock is null ? ClockSampleValidity : TimeSpan.MaxValue, lower,
            useUpperBound: options.BoundedClock is not null);
    }

    // Called only under kernel commit admission. Every record in the experiment
    // carries its native transaction identity and clock in the same WAL envelope.
    private DurableTimestampState? NextTimestamp()
    {
        if (TimestampCostOptions is not { } options)
        {
            return null;
        }

        var bound = TimestampCausalBound();
        var time = (_hybridClock ?? throw new InvalidOperationException("Clock is not recovered.")).Next(bound);
        return _timestampState = new(options.Domain, time.PhysicalTicks, time.Logical,
            _timestampState?.ReservedReadThrough ?? 0, options.Visibility);
    }

    private DatabaseHybridTime TimestampCausalBound()
    {
        var committed = new DatabaseHybridTime(_timestampState?.Physical ?? 0, _timestampState?.Logical ?? 0);
        return committed.CompareTo(_readPromise) > 0 ? committed : _readPromise;
    }

    // An independently clocked owner must not acknowledge a timestamp in its
    // uncertainty interval as having occurred before a subsequent fresh read.
    // Failure here is after durability and is handled as committed-incomplete.
    private async ValueTask WaitForTimestampClockAsync(DurableTimestampState time, CancellationToken token, string phase = "commit_completion")
    {
        if (TimestampCostOptions?.BoundedObservation is not { } observe)
        {
            return;
        }

        using var trace = _telemetry.Start("db.timestamp.clock_wait", Profile);
        trace?.SetTag("entityforge.phase", phase);
        long started = Stopwatch.GetTimestamp();
        var bound = observe();
        trace?.SetTag("entityforge.clock.wait_required", bound.EarliestUtcTicks <= time.Physical);
        trace?.SetTag("entityforge.clock.interval_ticks", bound.LatestUtcTicks - bound.EarliestUtcTicks);
        while (bound.EarliestUtcTicks <= time.Physical)
        {
            if (Stopwatch.GetElapsedTime(started) >= MaximumRecoveryClockWait)
            {
                throw new InvalidOperationException("Clock cannot establish completion of the committed timestamp.");
            }

            await Task.Delay(1, token).ConfigureAwait(false);
            bound = observe();
        }
    }

    // Persist a crash-safe ceiling for read promises. Visibility, when enabled,
    // additionally requires logical history and relevant-intent admission.
    internal async ValueTask ReserveTimestampReadAsync(CancellationToken token)
    {
        if (TimestampCostOptions is not { } options)
        {
            return;
        }

        EnsureReady();
        DurableTimestampState stamp = NextTimestamp()!;
        // Recovery restores a physical ceiling, not every issued logical tick.
        // A promise at the ceiling therefore needs a new strictly higher ceiling.
        if (stamp.Physical >= stamp.ReservedReadThrough)
        {
            using var trace = _telemetry.Start("db.timestamp.read_reservation", Profile);
            var reserved = new DurableTimestampState(stamp.Domain, stamp.Physical, stamp.Logical,
                checked(stamp.Physical + options.ReservationWindow.Ticks), options.Visibility);
            var record = new DurableTransactionEnvelope(_databaseIncarnation, new(checked(_durableVersion + 1)),
                Guid.NewGuid(), new(_profile.Profile, TimestampEvidenceCodec, 1, ReadOnlyMemory<byte>.Empty),
                DurableCommittedChangeKind.TimestampEvidence, null, StorageLayout, VersioningMode)
            {
                Timestamp = reserved
            };
            try
            {
                await _commitLog.CommitAsync(record, token).ConfigureAwait(false);
            }
            catch
            {
                RequireRecovery("timestamp_read_reservation");
                throw;
            }
            _timestampState = reserved;
            TimestampReservationForces++;
        }
        _readPromise = new(stamp.Physical, stamp.Logical);
    }
}
