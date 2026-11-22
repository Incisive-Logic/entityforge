using System.Diagnostics;
using System.Diagnostics.Metrics;
using EntityForge.Db;

namespace EntityForge.Db.Storage.Local;

/// <summary>Emits local-storage tracing and metrics for WAL, recovery and checkpoint operations.</summary>
/// <remarks>Share one instance where appropriate and dispose it after its consumers finish. Disposal releases
/// the meter and activity source; it does not flush storage or dispose activities returned to callers.</remarks>
public sealed class DatabaseStorageTelemetry : IDisposable
{
    /// <summary>The activity-source name used to subscribe to local-storage tracing.</summary>
    public const string ActivitySourceName = "EntityForge.Db.Storage.Local";

    /// <summary>The meter name used to subscribe to local-storage measurements.</summary>
    public const string MeterName = "EntityForge.Db.Storage.Local";
    private readonly ActivitySource _activities = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _walAppends;
    private readonly Counter<long> _walBytes;
    private readonly Counter<long> _recoveryRecords;
    private readonly Counter<long> _recoveryBytes;
    private readonly Histogram<double> _durableFlushDuration;
    private readonly Histogram<long> _checkpointWriteBytes;
    private readonly Histogram<double> _checkpointPhaseDuration;
    private readonly Histogram<long> _checkpointEntries;
    private readonly Histogram<long> _checkpointRestoreBytes;
    private readonly Histogram<long> _imageReadBytes;
    private readonly Histogram<long> _imageBytes;
    private readonly Histogram<double> _imageDuration;

    /// <summary>Creates the meter, activity source and storage instruments.</summary>
    public DatabaseStorageTelemetry()
    {
        _walAppends = _meter.CreateCounter<long>("entityforge.db.storage.wal.appends");
        _walBytes = _meter.CreateCounter<long>("entityforge.db.storage.wal.bytes");
        _recoveryRecords = _meter.CreateCounter<long>("entityforge.db.storage.recovery.records");
        _recoveryBytes = _meter.CreateCounter<long>("entityforge.db.storage.recovery.bytes");
        _durableFlushDuration = _meter.CreateHistogram<double>("entityforge.db.storage.flush.duration", "ms");
        _checkpointPhaseDuration = _meter.CreateHistogram<double>("entityforge.db.storage.checkpoint.phase.duration", "ms");
        _checkpointEntries = _meter.CreateHistogram<long>("entityforge.db.storage.checkpoint.entries", "entries");
        _checkpointWriteBytes = _meter.CreateHistogram<long>("entityforge.db.storage.checkpoint.write.bytes", "By");
        _checkpointRestoreBytes = _meter.CreateHistogram<long>("entityforge.db.storage.checkpoint.restore.bytes", "By");
        _imageReadBytes = _meter.CreateHistogram<long>("entityforge.db.storage.checkpoint.image.read.bytes", "By");
        _imageBytes = _meter.CreateHistogram<long>("entityforge.db.storage.checkpoint.image.bytes", "By");
        _imageDuration = _meter.CreateHistogram<double>("entityforge.db.storage.checkpoint.image.duration", "ms");
    }

    /// <summary>Starts a storage activity when a tracing listener requests one.</summary>
    /// <param name="operation">The activity name identifying the storage operation.</param>
    /// <returns>A caller-owned activity to dispose when the operation ends, or null when none is created.</returns>
    public Activity? Start(string operation) => _activities.StartActivity(operation);

    /// <summary>Records one WAL append, its byte count and durable-flush duration.</summary>
    /// <param name="profile">The database profile associated with the append.</param>
    /// <param name="bytes">The number of appended bytes.</param>
    /// <param name="flushDuration">The measured flush duration, recorded in milliseconds.</param>
    public void Appended(DatabaseProfileId profile, long bytes, TimeSpan flushDuration)
    {
        TagList tags = Tags(profile);
        _walAppends.Add(1, tags);
        _walBytes.Add(bytes, tags);
        _durableFlushDuration.Record(flushDuration.TotalMilliseconds, tags);
    }

    /// <summary>Records one recovered WAL record and its byte count.</summary>
    /// <param name="profile">The database profile being recovered.</param>
    /// <param name="bytes">The number of bytes attributed to the recovered record.</param>
    public void Recovered(DatabaseProfileId profile, long bytes)
    {
        TagList tags = Tags(profile);
        _recoveryRecords.Add(1, tags);
        _recoveryBytes.Add(bytes, tags);
    }

    internal void CheckpointPhase(DatabaseProfileId profile, string phase, long started) =>
        _checkpointPhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new TagList { { "entityforge.db.profile", profile.ToString() }, { "entityforge.db.phase", phase } });

    internal void CheckpointEntries(DatabaseProfileId profile, string kind, int count) =>
        _checkpointEntries.Record(count,
            new TagList { { "entityforge.db.profile", profile.ToString() }, { "entityforge.db.phase", kind } });

    /// <summary>Records the size of a published checkpoint.</summary>
    /// <param name="profile">The checkpoint database profile.</param>
    /// <param name="bytes">The checkpoint file size in bytes.</param>
    public void CheckpointWritten(DatabaseProfileId profile, long bytes) => _checkpointWriteBytes.Record(bytes, Tags(profile));

    /// <summary>Records the size of a successfully loaded checkpoint.</summary>
    /// <param name="profile">The checkpoint database profile.</param>
    /// <param name="bytes">The loaded checkpoint file size in bytes.</param>
    public void CheckpointRestored(DatabaseProfileId profile, long bytes) => _checkpointRestoreBytes.Record(bytes, Tags(profile));

    /// <summary>Records physical-image size, bytes read and capture duration.</summary>
    /// <param name="profile">The database profile whose image was captured.</param>
    /// <param name="bytes">The total physical-image size in bytes.</param>
    /// <param name="readBytes">Bytes actually read while computing the image commitment.</param>
    /// <param name="duration">The capture duration, recorded in milliseconds.</param>
    public void PhysicalImageCaptured(DatabaseProfileId profile, long bytes, long readBytes, TimeSpan duration)
    {
        var tags = Tags(profile);
        _imageBytes.Record(bytes, tags);
        _imageReadBytes.Record(readBytes, tags);
        _imageDuration.Record(duration.TotalMilliseconds, tags);
    }

    private static TagList Tags(DatabaseProfileId profile) => new() { { "entityforge.db.profile", profile.ToString() } };

    /// <summary>Disposes this instance's activity source and meter.</summary>
    /// <remarks>Finish instrumented operations and dispose their activities before disposing this owner.</remarks>
    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}
