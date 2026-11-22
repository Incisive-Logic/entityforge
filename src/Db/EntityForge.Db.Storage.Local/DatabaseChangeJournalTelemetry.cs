using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage.Local;

/// <summary>Records change-journal tracing, materialization, retention and subscription measurements.</summary>
public sealed class DatabaseChangeJournalTelemetry : IDisposable
{
    /// <summary>The Meter name used for change-journal metrics.</summary>
    public const string MeterName = "EntityForge.Db.ChangeJournal";
    /// <summary>The ActivitySource name used for change-journal tracing.</summary>
    public const string ActivitySourceName = MeterName;
    private readonly Meter _meter = new(MeterName);
    private readonly ActivitySource _activities = new(ActivitySourceName);
    private readonly Counter<long> _materialized;
    private readonly Counter<long> _published;
    private readonly Counter<long> _decoded;
    private readonly Counter<long> _indexedCandidates;
    private readonly Counter<long> _expired;
    private readonly Counter<long> _compactedBytes;
    private readonly Counter<long> _compactedRecords;
    private readonly Counter<long> _wakeUps;
    private readonly Counter<long> _recoveryArtifacts;
    private readonly Histogram<double> _pageReadDuration;
    private readonly Histogram<long> _pageScannedRecords;
    private readonly Histogram<long> _pageReturnedRecords;
    private readonly Histogram<double> _materializationPhaseDuration;
    private readonly Histogram<long> _materializationPhaseAllocatedBytes;
    /// <summary>The histogram name for change materialization phase duration.</summary>
    public const string MaterializationPhaseDurationInstrument =
        "entityforge.db.change_journal.materialization.phase.duration";

    /// <summary>The histogram name for bytes allocated during a materialization phase.</summary>
    public const string MaterializationPhaseAllocatedInstrument =
        "entityforge.db.change_journal.materialization.phase.allocated_bytes";

    /// <summary>The tag key identifying a change-journal phase.</summary>
    public const string PhaseTag = "entityforge.db.phase";

    /// <summary>Creates the change-journal tracing and metric instruments.</summary>
    public DatabaseChangeJournalTelemetry()
    {
        _materialized = _meter.CreateCounter<long>("entityforge.db.change_journal.materialized");
        _published = _meter.CreateCounter<long>("entityforge.db.change_journal.published");
        _decoded = _meter.CreateCounter<long>("entityforge.db.change_journal.decoded_records");
        _indexedCandidates = _meter.CreateCounter<long>("entityforge.db.change_journal.indexed_candidates");
        _expired = _meter.CreateCounter<long>("entityforge.db.change_journal.expired_cursors");
        _compactedBytes = _meter.CreateCounter<long>("entityforge.db.change_journal.compacted_bytes", "By");
        _compactedRecords = _meter.CreateCounter<long>("entityforge.db.change_journal.compacted_records");
        _wakeUps = _meter.CreateCounter<long>("entityforge.db.change_journal.subscription_wakeups");
        _recoveryArtifacts = _meter.CreateCounter<long>("entityforge.db.change_journal.recovery.artifact_checks");
        _pageReadDuration = _meter.CreateHistogram<double>(
            "entityforge.db.change_journal.page_read.duration", "ms");
        _pageScannedRecords = _meter.CreateHistogram<long>(
            "entityforge.db.change_journal.page_read.scanned_records", "{record}");
        _pageReturnedRecords = _meter.CreateHistogram<long>(
            "entityforge.db.change_journal.page_read.returned_records", "{record}");
        _materializationPhaseDuration = _meter.CreateHistogram<double>(
            MaterializationPhaseDurationInstrument, "ms");
        _materializationPhaseAllocatedBytes = _meter.CreateHistogram<long>(
            MaterializationPhaseAllocatedInstrument, "By");
    }

    /// <summary>Starts a change-journal activity when requested by a tracing listener.</summary>
    public Activity? Start(string operation) => _activities.StartActivity(operation);

    /// <summary>Records committed changes materialized into the journal.</summary>
    public void Materialized() => _materialized.Add(1);

    /// <summary>Records changes published to journal consumers.</summary>
    public void Published() => _published.Add(1);

    /// <summary>Records journal records decoded while reading.</summary>
    public void Decoded() => _decoded.Add(1);

    /// <summary>Records candidates selected by a journal index.</summary>
    public void IndexedCandidate() => _indexedCandidates.Add(1);

    /// <summary>Records a cursor rejected because its required history expired.</summary>
    public void Expired() => _expired.Add(1);

    /// <summary>Records journal bytes and records removed by compaction.</summary>
    public void Compacted(long bytes, long records)
    {
        _compactedBytes.Add(bytes);
        _compactedRecords.Add(records);
    }

    /// <summary>Records a waiting subscription being awakened.</summary>
    public void SubscriptionWakeUp() => _wakeUps.Add(1);

    internal void RecoveryArtifact() => _recoveryArtifacts.Add(1);

    /// <summary>Records page-read duration and scanned and returned record counts.</summary>
    public void PageRead(TimeSpan duration, int scannedRecords, int returnedRecords)
    {
        _pageReadDuration.Record(duration.TotalMilliseconds);
        _pageScannedRecords.Record(scannedRecords);
        _pageReturnedRecords.Record(returnedRecords);
    }

    /// <summary>Records phase duration and allocation growth from supplied counters.</summary>
    public void MaterializationPhase(string phase, long started, long allocatedBefore)
    {
        TagList tags = default;
        tags.Add(PhaseTag, phase);
        _materializationPhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        _materializationPhaseAllocatedBytes.Record(
            Math.Max(0, GC.GetTotalAllocatedBytes() - allocatedBefore), tags);
    }

    /// <summary>Disposes the journal's metric and tracing instruments.</summary>
    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}
