using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Kernel;

internal sealed class DatabaseCommitQueueTelemetry
{
    private static readonly ConcurrentDictionary<string, DatabaseCommitQueueTelemetry> Profiles = new();

    internal static DatabaseCommitQueueTelemetry For(string profile) => Profiles.GetOrAdd(profile, p => new(p));

    internal readonly ActivitySource Activities;
    internal readonly string ActivityName;
    internal readonly Histogram<double> CapacityWait, QueueWait, Scheduling, Admission, Busy;
    internal readonly Histogram<long> GroupSize;

    private DatabaseCommitQueueTelemetry(string profile)
    {
        Activities = new($"EntityForge.Db.{profile}.CommitQueue");
        var meter = new Meter($"EntityForge.Db.{profile}.CommitQueue");
        string prefix = $"entityforge.db.{profile.ToLowerInvariant()}.commit.";
        ActivityName = $"db.{profile.ToLowerInvariant()}.commit.queue";
        CapacityWait = meter.CreateHistogram<double>(prefix + "capacity_wait", "ms");
        QueueWait = meter.CreateHistogram<double>(prefix + "queue_wait", "ms");
        Scheduling = meter.CreateHistogram<double>(prefix + "scheduling", "ms");
        Admission = meter.CreateHistogram<double>(prefix + "admission", "ms");
        Busy = meter.CreateHistogram<double>(prefix + "busy", "ms");
        GroupSize = meter.CreateHistogram<long>(prefix + "selected_group_size", "requests");
    }
}
