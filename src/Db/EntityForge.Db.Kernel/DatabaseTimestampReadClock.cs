using System.Diagnostics;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>Selects a query time in a configured, bounded timestamp domain.</summary>
public static class DatabaseTimestampReadClock
{
    private static readonly Lazy<DatabaseTimestampCostOptions?> Shared = new(() =>
        DatabaseTimestampCostOptions.FromEnvironment(DatabaseVersioningMode.PartitionLocal));

    /// <summary>Reads a fresh query timestamp. Without a supplied clock, only an explicitly enabled shared-host qualification clock is supported.</summary>
    public static DurableTimestampState Read(Guid domain, IDatabaseBoundedClock? source = null)
    {
        var clock = source is not null ? DatabaseTimestampCostOptions.FromClock(source) :
            DatabaseTimestampCostOptions.QualificationOverride.Value ?? Shared.Value;
        if (clock?.Visibility != true || clock.Domain != domain || !clock.Healthy())
        {
            throw new InvalidOperationException("No healthy timestamp clock with the selected domain is configured.");
        }

        if (clock.BoundedObservation is { } observe)
        {
            return new(domain, observe().LatestUtcTicks, 0, 0, true);
        }

        long prior = clock.Now(), now;
        long start = Stopwatch.GetTimestamp();
        do
        {
            now = clock.Now();
            if (!clock.Healthy() || Stopwatch.GetElapsedTime(start) > TimeSpan.FromMilliseconds(100))
            {
                throw new InvalidOperationException("Clock did not advance for a fresh query.");
            }
        }
        while (now <= prior);
        return new(domain, now, 0, 0, true);
    }
}
