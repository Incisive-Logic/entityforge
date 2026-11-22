using EntityForge.Db.Distributed.Sharding;
using System.Diagnostics;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Describes whether a supervised placement lease may still admit operations.</summary>
public enum ShardPlacementLeaseState
{
    /// <summary>A valid lease is held and admission remains subject to its remaining lifetime.</summary>
    Active,
    /// <summary>A transient failure prevents authority confirmation; new admission is withdrawn.</summary>
    AuthorityUncertain,
    /// <summary>Authority was definitively lost or its certified lifetime expired.</summary>
    AuthorityLost,
    /// <summary>Supervision has stopped.</summary>
    Stopped
}

/// <summary>
/// RenewalInterval defines the nominal renewal window; renewal starts earlier to reserve
/// time for authority I/O and an explicitly configured operation lifetime. Renewal response
/// time never extends the lease deadline or reduces the required operation lifetime.
/// </summary>
/// <param name="LeaseLifetime">The positive lease lifetime requested from the authority.</param>
/// <param name="RenewalInterval">The nominal renewal interval, positive and shorter than the lease lifetime.</param>
/// <param name="RetryInterval">The retry interval after an uncertain renewal, positive and shorter than the lease lifetime.</param>
/// <param name="MaximumOperationDuration">The time that must remain for an admitted operation; defaults to LeaseLifetime minus RenewalInterval.</param>
public sealed record ShardPlacementLeaseSupervisorOptions(TimeSpan LeaseLifetime, TimeSpan RenewalInterval,
    TimeSpan RetryInterval, TimeSpan? MaximumOperationDuration = null)
{
    /// <summary>Gets a thirty-second lease lifetime, ten-second renewal interval and one-second retry interval.</summary>
    public static ShardPlacementLeaseSupervisorOptions Default
    {
        get;
    }
    = new(TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1));

    internal void Validate()
    {
        if (LeaseLifetime <= TimeSpan.Zero || RenewalInterval <= TimeSpan.Zero || RetryInterval <= TimeSpan.Zero ||
            RenewalInterval >= LeaseLifetime || RetryInterval >= LeaseLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseLifetime),
                "Lease lifetime must be positive and exceed both renewal and retry intervals.");
        }

        TimeSpan operation = MaximumOperationDuration ?? LeaseLifetime - RenewalInterval;
        if (operation <= TimeSpan.Zero || operation >= LeaseLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOperationDuration));
        }
    }
}
