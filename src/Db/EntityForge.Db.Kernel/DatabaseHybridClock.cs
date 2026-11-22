namespace EntityForge.Db.Kernel;

// Used by native admission; durable recovery supplies the prior allocation bound.
// A timestamp is neither a commit decision nor evidence of readable data.
internal readonly record struct DatabaseHybridTime(long PhysicalTicks, int Logical)
    : IComparable<DatabaseHybridTime>
{
    public int CompareTo(DatabaseHybridTime other)
    {
        int physical = PhysicalTicks.CompareTo(other.PhysicalTicks);
        return physical != 0 ? physical : Logical.CompareTo(other.Logical);
    }
}

// The provider must bound UTC throughout maximumSampleAge after SampledAt,
// including oscillator error. Merely reading DateTime.UtcNow or finding an NTP
// process does not supply this evidence. Samples use this process's monotonic clock.
internal readonly record struct DatabaseClockSample(
    long EarliestUtcTicks, long LatestUtcTicks, long SampledAt, bool Healthy);

internal sealed class DatabaseHybridClock
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly Func<DatabaseClockSample> _sample;
    private readonly TimeSpan _maximumSampleAge;
    private readonly long _maximumIntervalTicks;
    private readonly bool _useUpperBound;
    private DatabaseHybridTime _last;

    internal DatabaseHybridClock(TimeProvider time, Func<DatabaseClockSample> sample,
        TimeSpan maximumSampleAge, TimeSpan maximumInterval, DatabaseHybridTime recovered, bool useUpperBound = false)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(sample);
        if (maximumSampleAge <= TimeSpan.Zero || maximumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSampleAge));
        }

        Validate(recovered);
        _time = time;
        _sample = sample;
        _maximumSampleAge = maximumSampleAge;
        _maximumIntervalTicks = maximumInterval.Ticks;
        _useUpperBound = useUpperBound;
        _last = recovered;
    }

    internal DatabaseHybridTime Next(DatabaseHybridTime causalBound = default)
    {
        Validate(causalBound);
        lock (_gate)
        {
            DatabaseClockSample sample = _sample();
            long now = _time.GetTimestamp();
            TimeSpan age = _time.GetElapsedTime(sample.SampledAt, now);
            if (!sample.Healthy || age < TimeSpan.Zero || age > _maximumSampleAge ||
                sample.EarliestUtcTicks < 0 || sample.LatestUtcTicks < sample.EarliestUtcTicks ||
                sample.LatestUtcTicks - sample.EarliestUtcTicks > _maximumIntervalTicks)
            {
                throw new InvalidOperationException("No valid bounded clock observation is available.");
            }

            // Do not extrapolate a wall clock with an unqualified drift assumption.
            long upper = sample.LatestUtcTicks;
            long physical = _useUpperBound ? upper : sample.EarliestUtcTicks;
            // A newly tightened independent-clock interval may end before the
            // previous upper bound. Preserve causality; the caller's commit wait
            // must still establish that the resulting timestamp is in the past.
            if (!_useUpperBound && (_last.PhysicalTicks > upper || causalBound.PhysicalTicks > upper))
            {
                throw new InvalidOperationException("Recovered or received time exceeds the clock bound.");
            }

            physical = Math.Max(physical, Math.Max(_last.PhysicalTicks, causalBound.PhysicalTicks));
            int logical = 0;
            if (physical == _last.PhysicalTicks)
            {
                logical = checked(_last.Logical + 1);
            }

            if (physical == causalBound.PhysicalTicks)
            {
                logical = Math.Max(logical, checked(causalBound.Logical + 1));
            }

            _last = new(physical, logical);
            return _last;
        }
    }

    private static void Validate(DatabaseHybridTime time)
    {
        if (time.PhysicalTicks < 0 || time.Logical < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(time));
        }
    }
}
