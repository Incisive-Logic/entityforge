namespace EntityForge.Db.Hosting.Tests;

// Fixed memory, including for multi-day workloads. Bounds are in TimeSpan ticks
// (100 ns), exact through 128 ticks, then at most 1/64 of an octave's base wide.
internal sealed class WorkloadLatencyHistogram
{
    internal sealed record Interval(double LowerMilliseconds, double UpperMilliseconds);

    private static readonly long[] UpperTicks = CreateBounds();
    private readonly long[] _counts = new long[UpperTicks.Length];
    private long _count;

    internal int BucketCount => _counts.Length;

    internal void Add(TimeSpan elapsed)
    {
        long ticks = elapsed.Ticks;
        if (ticks < 0 || ticks > UpperTicks[^1])
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), "Latency must be between zero and one hour.");
        }
        int index = Array.BinarySearch(UpperTicks, ticks);
        if (index < 0)
        {
            index = ~index;
        }
        lock (_counts)
        {
            _counts[index] = checked(_counts[index] + 1);
            _count = checked(_count + 1);
        }
    }

    internal Interval? Percentile(double fraction)
    {
        if (!Double.IsFinite(fraction) || fraction <= 0 || fraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }
        lock (_counts)
        {
            if (_count == 0)
            {
                return null;
            }
            long rank = checked((long)Math.Ceiling(_count * fraction));
            long cumulative = 0;
            for (int index = 0; index < _counts.Length; index++)
            {
                cumulative = checked(cumulative + _counts[index]);
                if (cumulative >= rank)
                {
                    return new((index == 0 ? 0 : UpperTicks[index - 1] + 1) / (double)TimeSpan.TicksPerMillisecond,
                        UpperTicks[index] / (double)TimeSpan.TicksPerMillisecond);
                }
            }
            throw new InvalidOperationException("Histogram rank exceeds recorded count.");
        }
    }

    internal object Summary(bool includeBuckets = true)
    {
        lock (_counts)
        {
            return new
            {
                count = _count,
                p50Milliseconds = Percentile(.5),
                p95Milliseconds = Percentile(.95),
                p99Milliseconds = Percentile(.99),
                bucketUpperTicks = includeBuckets ? UpperTicks.ToArray() : null,
                bucketCounts = includeBuckets ? _counts.ToArray() : null,
                precision = "Nearest-rank percentile interval, inclusive bounds; 100ns ticks, at most 1/64 octave-base bucket width. No raw samples."
            };
        }
    }

    private static long[] CreateBounds()
    {
        long maximum = TimeSpan.FromHours(1).Ticks;
        var bounds = new SortedSet<long>(Enumerable.Range(0, 65).Select(value => (long)value));
        for (long basis = 64; basis < maximum; basis *= 2)
        {
            for (int part = 1; part <= 64; part++)
            {
                bounds.Add(Math.Min(maximum, basis + part * (basis / 64)));
            }
        }
        return bounds.ToArray();
    }
}
