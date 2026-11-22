using System.Diagnostics;
using System.Diagnostics.Metrics;
using EntityForge.Db;

namespace EntityForge.Db.Kernel;

/// <summary>Records low-cardinality kernel activities, durations and operation counters.</summary>
public sealed class DatabaseKernelTelemetry : IDisposable
{
    /// <summary>The ActivitySource name used for kernel tracing.</summary>
    public const string ActivitySourceName = "EntityForge.Db.Kernel";
    /// <summary>The Meter name used for kernel metrics.</summary>
    public const string MeterName = "EntityForge.Db.Kernel";
    private readonly ActivitySource _activities = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _commits;
    private readonly Counter<long> _recoveryRecords;
    private readonly Histogram<double> _commitDuration;
    private readonly Histogram<double> _commitPhaseDuration;
    private readonly Histogram<long> _commitPhaseAllocatedBytes;
    private readonly Histogram<long> _physicalMutationPages;
    private readonly Histogram<long> _boundRecordBytes;

    internal bool MeasureCommitPhases => _commitPhaseDuration.Enabled;

    private readonly Histogram<long> _physicalMutationBytes;
    private readonly Histogram<long> _checkpointBytes;
    private readonly Histogram<double> _checkpointPhaseDuration;
    private readonly Histogram<long> _checkpointPhaseAllocatedBytes;
    private readonly Histogram<double> _recoveryPhaseDuration;
    private readonly Counter<long> _faults;
    private readonly Counter<long> _changeFeedReads;
    private readonly Counter<long> _changeFeedChanges;
    private readonly Histogram<long> _partitionCohortSize;
    private readonly IReadOnlyDictionary<string, Counter<long>> _partitionCohortStops;

    /// <summary>Creates kernel telemetry instruments.</summary>
    public DatabaseKernelTelemetry()
    {
        _commits = _meter.CreateCounter<long>("entityforge.db.kernel.commits");
        _recoveryRecords = _meter.CreateCounter<long>("entityforge.db.kernel.recovery.records");
        _commitDuration = _meter.CreateHistogram<double>("entityforge.db.kernel.commit.duration", "ms");
        _commitPhaseDuration = _meter.CreateHistogram<double>("entityforge.db.kernel.commit.phase.duration", "ms");
        _commitPhaseAllocatedBytes = _meter.CreateHistogram<long>(
            "entityforge.db.kernel.commit.phase.allocated_bytes", "By");
        _boundRecordBytes = _meter.CreateHistogram<long>("entityforge.db.kernel.bound_record.bytes", "By");
        _physicalMutationPages = _meter.CreateHistogram<long>("entityforge.db.kernel.commit.physical.pages");
        _physicalMutationBytes = _meter.CreateHistogram<long>("entityforge.db.kernel.commit.physical.bytes", "By");
        _checkpointBytes = _meter.CreateHistogram<long>("entityforge.db.kernel.checkpoint.bytes", "By");
        _checkpointPhaseDuration = _meter.CreateHistogram<double>(
            "entityforge.db.kernel.checkpoint.phase.duration", "ms");
        _checkpointPhaseAllocatedBytes = _meter.CreateHistogram<long>(
            "entityforge.db.kernel.checkpoint.phase.allocated_bytes", "By");
        _recoveryPhaseDuration = _meter.CreateHistogram<double>(
            "entityforge.db.kernel.recovery.phase.duration", "ms");
        _faults = _meter.CreateCounter<long>("entityforge.db.kernel.faults");
        _changeFeedReads = _meter.CreateCounter<long>("entityforge.db.kernel.change_feed.reads");
        _changeFeedChanges = _meter.CreateCounter<long>("entityforge.db.kernel.change_feed.changes");
        _partitionCohortSize = _meter.CreateHistogram<long>("entityforge.db.kernel.partition.cohort_size", "commands");
        _partitionCohortStops = new[] { "empty", "phase", "identity-or-log", "limit" }.ToDictionary(reason => reason,
            reason => _meter.CreateCounter<long>("entityforge.db.kernel.partition.cohort_stop." + reason));
    }

    /// <summary>Starts a profile-tagged activity for a kernel operation, or returns null when no listener requests it.</summary>
    public Activity? Start(string operation, DatabaseProfileId profile) =>
        _activities.StartActivity(operation)?.AddTag("entityforge.db.profile", profile.ToString());

    /// <summary>Records a successful commit and its elapsed duration for a profile.</summary>
    public void Commit(DatabaseProfileId profile, TimeSpan elapsed)
    {
        TagList tags = Tags(profile);
        _commits.Add(1, tags);
        _commitDuration.Record(elapsed.TotalMilliseconds, tags);
    }

    /// <summary>Records one measured phase within a database commit.</summary>
    public void CommitPhase(DatabaseProfileId profile, string phase, TimeSpan elapsed, long allocatedBytes)
    {
        var tags = new TagList
        {
            { "entityforge.db.profile", profile.ToString() },
            { "entityforge.db.phase", phase }
        };
        _commitPhaseDuration.Record(elapsed.TotalMilliseconds, tags);
        _commitPhaseAllocatedBytes.Record(allocatedBytes, tags);
    }

    internal void BoundRecordBytes(DatabaseProfileId profile, string member, long bytes) =>
        _boundRecordBytes.Record(bytes, new TagList { { "entityforge.db.profile", profile.ToString() }, { "entityforge.db.phase", member } });

    internal void PartitionCohort(DatabaseProfileId profile, string phase, int count) =>
        _partitionCohortSize.Record(count, new TagList { { "entityforge.db.profile", profile.ToString() }, { "entityforge.db.phase", phase } });

    internal void PartitionCohortStop(DatabaseProfileId profile, string reason) =>
        _partitionCohortStops[reason].Add(1, Tags(profile));

    /// <summary>Records physical page and byte amplification for one commit.</summary>
    public void PhysicalCommit(DatabaseProfileId profile, int pages, long bytes)
    {
        TagList tags = Tags(profile);
        _physicalMutationPages.Record(pages, tags);
        _physicalMutationBytes.Record(bytes, tags);
    }

    /// <summary>Records a completed profile recovery.</summary>
    public void Recovered(DatabaseProfileId profile) => _recoveryRecords.Add(1, Tags(profile));

    /// <summary>Records elapsed time since the supplied Stopwatch timestamp for a named recovery phase.</summary>
    public void RecoveryPhase(DatabaseProfileId profile, string phase, long started)
    {
        var tags = new TagList
        {
            { "entityforge.db.profile", profile.ToString() },
            { "entityforge.db.phase", phase }
        };
        _recoveryPhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }

    /// <summary>Records a profile checkpoint and its encoded size in bytes.</summary>
    public void Checkpoint(DatabaseProfileId profile, long bytes) => _checkpointBytes.Record(bytes, Tags(profile));

    /// <summary>Records checkpoint phase duration and allocation growth from the supplied counters.</summary>
    public void CheckpointPhase(DatabaseProfileId profile, string phase, long started, long allocatedBefore)
    {
        var tags = new TagList
        {
            { "entityforge.db.profile", profile.ToString() },
            { "entityforge.db.phase", phase }
        };
        _checkpointPhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        _checkpointPhaseAllocatedBytes.Record(GC.GetTotalAllocatedBytes() - allocatedBefore, tags);
    }

    /// <summary>Records a failure at a named kernel stage.</summary>
    public void Fault(DatabaseProfileId profile, string stage) => _faults.Add(1,
        new TagList { { "entityforge.db.profile", profile.ToString() }, { "entityforge.db.stage", stage } });

    /// <summary>Records a change-feed read and the number of changes returned.</summary>
    public void ChangeFeedRead(DatabaseProfileId profile, int changes)
    {
        TagList tags = Tags(profile);
        _changeFeedReads.Add(1, tags);
        _changeFeedChanges.Add(changes, tags);
    }

    private static TagList Tags(DatabaseProfileId profile) => new() { { "entityforge.db.profile", profile.ToString() } };

    /// <summary>Disposes the telemetry instruments owned by this instance.</summary>
    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}
