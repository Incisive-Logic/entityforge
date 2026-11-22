namespace EntityForge.Db.Kernel;

/// <summary>A current UTC interval supplied by a monitored clock service.</summary>
/// <param name="EarliestUtcTicks">Inclusive earliest UTC estimate, in DateTime ticks since its epoch.</param>
/// <param name="LatestUtcTicks">Inclusive latest UTC estimate, in the same units and epoch.</param>
public readonly record struct DatabaseClockInterval(long EarliestUtcTicks, long LatestUtcTicks);

/// <summary>
/// Supplies a conservative bound containing current UTC. Implementations must reject
/// observations when synchronisation, uncertainty or freshness cannot be established.
/// The domain identifies the durable time interpretation, not a process or boot.
/// Reads must be thread-safe local observations; clock-service polling belongs
/// in the monitor, outside database admission.
/// </summary>
public interface IDatabaseBoundedClock
{
    /// <summary>Gets the stable identifier of the durable clock interpretation shared by compatible observations.</summary>
    /// <remarks>This identifier must survive process restarts; it is not a boot identifier.</remarks>
    Guid Domain
    {
        get;
    }

    /// <summary>Reads a conservative current UTC interval from local monitoring evidence.</summary>
    /// <returns>A healthy, fresh interval containing current UTC under the monitor's uncertainty model.</returns>
    /// <remarks>Implementations must fail when a usable bound cannot be established, rather than
    /// substitute an unverified wall-clock value. Calls must be thread-safe and must not poll remote services.</remarks>
    DatabaseClockInterval Read();
}

/// <summary>
/// A trusted monitor's UTC bound at a local monotonic timestamp. The monitor must
/// account for source error, measurement latency and oscillator error. A detected
/// clock step or lost synchronisation must invalidate the observation.
/// </summary>
/// <param name="EarliestUtcTicks">Inclusive earliest UTC bound at the observation timestamp, in DateTime ticks.</param>
/// <param name="LatestUtcTicks">Inclusive latest UTC bound at that timestamp, in DateTime ticks.</param>
/// <param name="MonotonicTimestamp">Timestamp from the same TimeProvider used by the consuming clock adapter.</param>
/// <param name="Synchronized">Whether the monitor can currently substantiate the recorded UTC bound.</param>
public sealed record DatabaseClockObservation(long EarliestUtcTicks, long LatestUtcTicks,
    long MonotonicTimestamp, bool Synchronized);

/// <summary>
/// Common adapter for chrony/NTP or PTP monitoring. This type consumes measured
/// evidence; it does not infer synchronisation from the presence of a daemon.
/// </summary>
public sealed class MonitoredDatabaseClock : IDatabaseBoundedClock
{
    private readonly TimeProvider _time;
    private readonly Func<DatabaseClockObservation> _observe;
    private readonly TimeSpan _maximumAge;
    private readonly TimeSpan _maximumUncertainty;
    private readonly long _maximumDriftPartsPerMillion;
    private const long PartsPerMillion = 1_000_000;

    /// <inheritdoc/>
    public Guid Domain
    {
        get;
    }

    /// <summary>Creates an adapter that ages monitored UTC bounds using a local monotonic clock.</summary>
    /// <param name="domain">Nonempty stable clock-domain identifier.</param>
    /// <param name="time">Provider whose timestamps and frequency are used by the monitor.</param>
    /// <param name="observe">Thread-safe local callback returning the latest monitoring evidence.</param>
    /// <param name="maximumAge">Positive maximum observation age, including timestamp-resolution uncertainty.</param>
    /// <param name="maximumUncertainty">Nonnegative maximum width of the resulting UTC interval.</param>
    /// <param name="maximumDriftPartsPerMillion">Conservative oscillator-drift bound, from zero through 999,999.</param>
    /// <exception cref="ArgumentException">The clock-domain identifier is empty.</exception>
    /// <exception cref="ArgumentNullException">The time provider or observation callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An age, uncertainty or drift limit is outside its allowed range.</exception>
    /// <remarks>The callback supplies evidence; this adapter does not establish synchronization itself.
    /// A width too small for the provider's timestamp resolution causes reads to fail even with zero configured drift.</remarks>
    public MonitoredDatabaseClock(Guid domain, TimeProvider time,
        Func<DatabaseClockObservation> observe, TimeSpan maximumAge,
        TimeSpan maximumUncertainty, long maximumDriftPartsPerMillion)
    {
        if (domain == Guid.Empty)
        {
            throw new ArgumentException("A stable clock domain is required.", nameof(domain));
        }

        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(observe);
        if (maximumAge <= TimeSpan.Zero || maximumUncertainty < TimeSpan.Zero ||
            maximumDriftPartsPerMillion is < 0 or >= PartsPerMillion)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        Domain = domain;
        _time = time;
        _observe = observe;
        _maximumAge = maximumAge;
        _maximumUncertainty = maximumUncertainty;
        _maximumDriftPartsPerMillion = maximumDriftPartsPerMillion;
    }

    /// <summary>Advances the latest monitored bound to the current timestamp, conservatively widening for resolution and drift.</summary>
    /// <returns>The resulting inclusive UTC interval.</returns>
    /// <exception cref="InvalidOperationException">Monitoring evidence is absent, unsynchronized, malformed, stale,
    /// outside the DateTime range, or wider than the admitted uncertainty.</exception>
    /// <exception cref="OverflowException">Timestamp or bound arithmetic cannot be represented.</exception>
    /// <remarks>Observation-callback exceptions propagate. No wall-clock fallback or remote polling occurs.</remarks>
    public DatabaseClockInterval Read()
    {
        var sample = _observe() ?? throw new InvalidOperationException("Clock monitoring returned no observation.");
        long elapsed = checked(_time.GetTimestamp() - sample.MonotonicTimestamp);
        long frequency = _time.TimestampFrequency;
        if (!sample.Synchronized || elapsed < 0 || frequency <= 0 ||
            sample.EarliestUtcTicks < DateTime.MinValue.Ticks ||
            sample.LatestUtcTicks < sample.EarliestUtcTicks || sample.LatestUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidOperationException("Clock observation is unhealthy, stale or malformed.");
        }
        // Counter quantisation and conversion rounding are uncertainty too. In
        // particular, a zero-drift provider must not round the upper age down.
        decimal ticks = (decimal)elapsed * TimeSpan.TicksPerSecond / frequency;
        long resolution = checked((long)decimal.Ceiling((decimal)TimeSpan.TicksPerSecond / frequency));
        long lowerAge = checked((long)decimal.Floor(ticks) - resolution);
        long upperAge = checked((long)decimal.Ceiling(ticks) + resolution);
        if (upperAge > _maximumAge.Ticks)
        {
            throw new InvalidOperationException("Clock observation is stale at the monotonic counter's resolution.");
        }
        // Ceiling, rather than truncation, keeps even sub-tick drift conservative.
        long drift = checked((long)decimal.Ceiling((decimal)upperAge * _maximumDriftPartsPerMillion / PartsPerMillion));
        long earliest = checked(sample.EarliestUtcTicks + lowerAge - drift);
        long latest = checked(sample.LatestUtcTicks + upperAge + drift);
        if (earliest < DateTime.MinValue.Ticks || latest > DateTime.MaxValue.Ticks ||
            latest - earliest > _maximumUncertainty.Ticks)
        {
            throw new InvalidOperationException("Clock uncertainty exceeds the admitted bound.");
        }

        return new(earliest, latest);
    }
}
