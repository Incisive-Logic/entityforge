using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Owns the meter and instruments used to observe physical-page and access-path work.</summary>
/// <remarks>Durations use milliseconds and byte measurements use bytes. Instruments report observed work,
/// not application throughput or durability guarantees. Subscribe through a metrics listener or exporter.</remarks>
public sealed class PhysicalPageTelemetry : IDisposable
{
    /// <summary>Name used to subscribe to this storage component's meters.</summary>
    public const string MeterName = "EntityForge.Db.Storage.Pages";
    private const string IoDirectionTag = "entityforge.db.io";

    /// <summary>Tag identifying foreground-read, foreground-write or maintenance page activity.</summary>
    public const string WorkloadTag = "entityforge.db.workload";

    /// <summary>Tag identifying the maintenance target when a maintenance scope supplies one.</summary>
    public const string MaintenanceTargetTag = "entityforge.db.maintenance.target";

    /// <summary>Tag distinguishing storage layers, such as device and buffer, to avoid combining unlike work.</summary>
    public const string StorageLayerTag = "entityforge.db.storage.layer";
    private const string ReadDirection = "read";
    private const string WriteDirection = "write";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _reads;
    private readonly Counter<long> _writes;
    private readonly Counter<long> _bytes;
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _evictions;
    private readonly Counter<long> _corruption;
    private readonly Counter<long> _flushes;
    private readonly Histogram<long> _pins;
    private readonly Histogram<long> _dirtyPages;
    private readonly Histogram<double> _readDuration;
    private readonly Histogram<double> _latchWaitDuration;
    private readonly Histogram<double> _writeDuration;
    private readonly Histogram<double> _flushDuration;
    private readonly Counter<long> _evictionWritebackPages;
    private readonly Histogram<double> _evictionWritebackDuration;
    private readonly Histogram<double> _accessSeekDuration;
    private readonly Histogram<double> _accessMutationDuration;
    private readonly Histogram<long> _accessEncodedBytes;
    private readonly Histogram<long> _accessRedoBytes;
    private readonly Histogram<long> _accessTouchedPages;
    private readonly Counter<long> _accessSplits;
    private readonly Counter<long> _accessMerges;

    private readonly UpDownCounter<long> _loadBytes;
    private readonly Histogram<double> _loadAdmissionWait;
    private readonly string _storageLayer;

    /// <summary>Creates a meter instance and its storage instruments.</summary>
    /// <param name="storageLayer">Nonblank, stable layer label applied to page-work measurements.</param>
    /// <remarks>Use a bounded set of labels; do not include database paths, queries or per-request identifiers.</remarks>
    /// <exception cref="ArgumentException">The layer is null, empty or whitespace.</exception>
    public PhysicalPageTelemetry(string storageLayer = "device")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLayer);
        _storageLayer = storageLayer;
        _loadBytes = _meter.CreateUpDownCounter<long>("entityforge.db.pages.load.inflight.bytes", "By");
        _loadAdmissionWait = _meter.CreateHistogram<double>("entityforge.db.pages.load.admission.duration", "ms");
        _reads = _meter.CreateCounter<long>("entityforge.db.pages.reads");
        _writes = _meter.CreateCounter<long>("entityforge.db.pages.writes");
        _bytes = _meter.CreateCounter<long>("entityforge.db.pages.bytes", "By");
        _hits = _meter.CreateCounter<long>("entityforge.db.pages.cache.hits");
        _misses = _meter.CreateCounter<long>("entityforge.db.pages.cache.misses");
        _evictions = _meter.CreateCounter<long>("entityforge.db.pages.cache.evictions");
        _corruption = _meter.CreateCounter<long>("entityforge.db.pages.corruption");
        _flushes = _meter.CreateCounter<long>("entityforge.db.pages.flushes");
        _pins = _meter.CreateHistogram<long>("entityforge.db.pages.pins");
        _dirtyPages = _meter.CreateHistogram<long>("entityforge.db.pages.dirty");
        _latchWaitDuration = _meter.CreateHistogram<double>("entityforge.db.pages.cache.latch_wait.duration", "ms");
        _readDuration = _meter.CreateHistogram<double>("entityforge.db.pages.read.duration", "ms");
        _writeDuration = _meter.CreateHistogram<double>("entityforge.db.pages.write.duration", "ms");
        _flushDuration = _meter.CreateHistogram<double>("entityforge.db.pages.flush.duration", "ms");
        _evictionWritebackPages = _meter.CreateCounter<long>("entityforge.db.pages.cache.eviction_writeback.pages");
        _evictionWritebackDuration = _meter.CreateHistogram<double>(
            "entityforge.db.pages.cache.eviction_writeback.duration", "ms");
        _accessSeekDuration = _meter.CreateHistogram<double>("entityforge.db.access_path.seek.duration", "ms");
        _accessMutationDuration = _meter.CreateHistogram<double>("entityforge.db.access_path.mutation.duration", "ms");
        _accessEncodedBytes = _meter.CreateHistogram<long>("entityforge.db.access_path.encoded_bytes", "By");
        _accessRedoBytes = _meter.CreateHistogram<long>("entityforge.db.access_path.redo_bytes", "By");
        _accessTouchedPages = _meter.CreateHistogram<long>("entityforge.db.access_path.touched_pages", "{page}");
        _accessSplits = _meter.CreateCounter<long>("entityforge.db.access_path.splits");
        _accessMerges = _meter.CreateCounter<long>("entityforge.db.access_path.merges");
    }

    internal void Read(int bytes, TimeSpan duration)
    {
        if (_storageLayer == "device")
        {
            AppendMergeLookupTelemetry.Add(LookupWork.DeviceReads);
            AppendMergeLookupTelemetry.Add(LookupWork.DeviceBytes, bytes);
        }
        else if (_storageLayer == "buffer")
        {
            AppendMergeLookupTelemetry.Add(LookupWork.BufferReads);
            AppendMergeLookupTelemetry.Add(LookupWork.BufferBytes, bytes);
        }
        TagList tags = Tags(ReadDirection);
        _reads.Add(1, tags);
        _bytes.Add(bytes, tags);
        _readDuration.Record(duration.TotalMilliseconds, tags);
    }

    internal void LoadBytes(long bytes) => _loadBytes.Add(bytes, Tags());

    internal void LoadAdmissionWait(TimeSpan elapsed) => _loadAdmissionWait.Record(elapsed.TotalMilliseconds, Tags());

    internal bool ObserveLatchWait => _latchWaitDuration.Enabled;

    // Contended waits include cancellation; immediate acquisitions do not emit a sample.
    internal void LatchWait(long started) =>
        _latchWaitDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, Tags());

    internal void Write(int bytes, TimeSpan duration)
    {
        TagList tags = Tags(WriteDirection);
        _writes.Add(1, tags);
        _bytes.Add(bytes, tags);
        _writeDuration.Record(duration.TotalMilliseconds, tags);
    }

    internal void Hit()
    {
        AppendMergeLookupTelemetry.Add(LookupWork.CacheHits);
        _hits.Add(1, Tags());
    }

    internal void Miss()
    {
        AppendMergeLookupTelemetry.Add(LookupWork.CacheMisses);
        _misses.Add(1, Tags());
    }

    internal void Eviction()
    {
        AppendMergeLookupTelemetry.Add(LookupWork.CacheEvictions);
        _evictions.Add(1, Tags());
    }

    internal void Flush(TimeSpan duration)
    {
        TagList tags = Tags();
        _flushes.Add(1, tags);
        _flushDuration.Record(duration.TotalMilliseconds, tags);
    }

    internal void Corruption() => _corruption.Add(1);

    internal void EvictionWriteback(int pages, TimeSpan duration)
    {
        TagList tags = Tags();
        _evictionWritebackPages.Add(pages, tags);
        _evictionWritebackDuration.Record(duration.TotalMilliseconds, tags);
    }

    internal void Snapshot(int pins, int dirty)
    {
        _pins.Record(pins);
        _dirtyPages.Record(dirty);
    }

    internal void AccessSeek(TimeSpan duration) => _accessSeekDuration.Record(duration.TotalMilliseconds);

    internal void AccessMutation(TimeSpan duration, long encodedBytes, long redoBytes, int touchedPages,
        int splits, int merges)
    {
        _accessMutationDuration.Record(duration.TotalMilliseconds);
        _accessEncodedBytes.Record(encodedBytes);
        _accessRedoBytes.Record(redoBytes);
        _accessTouchedPages.Record(touchedPages);
        if (splits != 0)
        {
            _accessSplits.Add(splits);
        }

        if (merges != 0)
        {
            _accessMerges.Add(merges);
        }
    }

    private TagList Tags(string? direction = null)
    {
        var tags = new TagList
        {
            { WorkloadTag, PhysicalPageTelemetryContext.Workload },
            { StorageLayerTag, _storageLayer }
        };
        if (direction is not null)
        {
            tags.Add(IoDirectionTag, direction);
        }

        if (PhysicalPageTelemetryContext.Target is { } target)
        {
            tags.Add(MaintenanceTargetTag, target);
        }

        return tags;
    }

    /// <summary>Disposes this instance's meter and instruments without flushing or disposing database storage.</summary>
    public void Dispose() => _meter.Dispose();
}

internal interface IPhysicalPageTelemetrySource
{
    PhysicalPageTelemetry Telemetry
    {
        get;
    }
}
