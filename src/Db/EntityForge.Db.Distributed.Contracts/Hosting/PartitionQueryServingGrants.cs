using System.Collections.Immutable;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Authority-enforced read grants. They delay reassignment, not write completion.</summary>
/// <remarks>Every controller must use the grant-aware placement protocol. Existing grants
/// survive liveness loss, release and home shutdown until expiry. A healthy bounded UTC
/// clock is mandatory; unhealthy/expired evidence fails closed without a protocol fallback.</remarks>
/// <param name="Lifetime">The positive, integral-second serving-grant lifetime, at most one minute.</param>
/// <param name="RenewalMargin">The positive interval reserved for renewal before the grant expires; must be less than Lifetime.</param>
public sealed record PartitionQueryServingOptions(TimeSpan Lifetime, TimeSpan RenewalMargin)
{
    /// <summary>Gets a ten-second serving grant with a one-second renewal margin.</summary>
    public static PartitionQueryServingOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));

    internal void Validate()
    {
        if (Lifetime <= RenewalMargin || RenewalMargin <= TimeSpan.Zero || Lifetime > TimeSpan.FromMinutes(1) ||
            Lifetime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Lifetime), "Serving grants require an integral, bounded lifetime greater than their renewal margin.");
        }
    }
}
