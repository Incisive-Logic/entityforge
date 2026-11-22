using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage.Pages;

internal sealed class RedoDurabilityTelemetry : IDisposable
{
    internal const string MeterName = "EntityForge.Db.Storage.Pages";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _groups;
    private readonly Counter<long> _transactions;
    private readonly Counter<long> _bytes;
    private readonly Histogram<double> _flushDuration;
    private readonly Counter<long> _uncertain;
    private readonly Counter<long> _reclaimedBytes;
    private readonly Histogram<double> _phaseDuration;
    private readonly Histogram<long> _phaseAllocatedBytes;
    private readonly Histogram<double> _batchWaitDuration;
    private readonly Histogram<double> _fileGateWaitDuration;
    private readonly Histogram<double> _reclamationFileGateHoldDuration;
    private readonly Histogram<double> _initializationPhaseDuration;
    private readonly Histogram<double> _reclamationPhaseDuration;
    private readonly Histogram<long> _reclamationSourceBytes;
    private readonly Histogram<long> _reclamationRemovedTransactions;
    internal const string ReclamationInstrumentPrefix = "entityforge.db.redo.reclamation.";
    internal const string PhaseDurationInstrument = "entityforge.db.redo.phase.duration";
    internal const string PhaseAllocatedInstrument = "entityforge.db.redo.phase.allocated_bytes";
    internal const string PhaseTag = "entityforge.db.phase";

    internal RedoDurabilityTelemetry()
    {
        _groups = _meter.CreateCounter<long>("entityforge.db.redo.groups");
        _transactions = _meter.CreateCounter<long>("entityforge.db.redo.transactions");
        _bytes = _meter.CreateCounter<long>("entityforge.db.redo.bytes", "By");
        _flushDuration = _meter.CreateHistogram<double>("entityforge.db.redo.flush.duration", "ms");
        _uncertain = _meter.CreateCounter<long>("entityforge.db.redo.uncertain");
        _reclaimedBytes = _meter.CreateCounter<long>("entityforge.db.redo.reclaimed.bytes", "By");
        _phaseDuration = _meter.CreateHistogram<double>(PhaseDurationInstrument, "ms");
        _phaseAllocatedBytes = _meter.CreateHistogram<long>(PhaseAllocatedInstrument, "By");
        _initializationPhaseDuration = _meter.CreateHistogram<double>("entityforge.db.redo.initialization.phase.duration", "ms");
        _fileGateWaitDuration = _meter.CreateHistogram<double>("entityforge.db.redo.file_gate.wait.duration", "ms");
        _reclamationFileGateHoldDuration = _meter.CreateHistogram<double>("entityforge.db.redo.reclamation.file_gate.hold.duration", "ms");
        _batchWaitDuration = _meter.CreateHistogram<double>("entityforge.db.redo.batch_wait.duration", "ms");
        _reclamationPhaseDuration = _meter.CreateHistogram<double>(ReclamationInstrumentPrefix + "phase.duration", "ms");
        _reclamationSourceBytes = _meter.CreateHistogram<long>(ReclamationInstrumentPrefix + "source.bytes", "By");
        _reclamationRemovedTransactions = _meter.CreateHistogram<long>(ReclamationInstrumentPrefix + "removed.transactions");
    }

    internal bool MeasureInitialization => _initializationPhaseDuration.Enabled;

    internal long InitializationPhase(string phase, long started)
    {
        if (started == 0)
        {
            return 0;
        }

        _initializationPhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new TagList { { PhaseTag, phase } });
        return Stopwatch.GetTimestamp();
    }

    internal bool MeasureFileGateWait => _fileGateWaitDuration.Enabled;

    internal void FileGateWait(string operation, long started)
    {
        if (started == 0)
        {
            return;
        }

        TagList tags = default;
        tags.Add("entityforge.db.operation", operation);
        _fileGateWaitDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
    }

    internal bool MeasureReclamationFileGateHold => _reclamationFileGateHoldDuration.Enabled;

    internal void ReclamationFileGateHold(string phase, double milliseconds) =>
        _reclamationFileGateHoldDuration.Record(milliseconds, new TagList { { "entityforge.db.operation", phase } });

    internal bool MeasureReclamation => _reclamationPhaseDuration.Enabled;

    // Independent from append diagnostics: subscribing must not instrument hot commits.
    internal long ReclamationPhase(string phase, long started)
    {
        if (started == 0)
        {
            return 0;
        }

        TagList tags = default;
        tags.Add(PhaseTag, phase);
        _reclamationPhaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        return Stopwatch.GetTimestamp();
    }

    internal void ReclamationWork(long sourceBytes, int removedTransactions)
    {
        _reclamationSourceBytes.Record(sourceBytes);
        _reclamationRemovedTransactions.Record(removedTransactions);
    }

    internal bool MeasurePhases => _phaseDuration.Enabled || _phaseAllocatedBytes.Enabled;

    internal long QueueTimestamp() => _phaseDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    internal void QueueWait(long started)
    {
        if (started != 0)
        {
            _phaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new TagList { { PhaseTag, "submission_queue_wait" } });
        }
    }

    internal void Group(int transactions, long bytes, long started)
    {
        _groups.Add(1);
        _transactions.Add(transactions);
        _bytes.Add(bytes);
        _flushDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    internal void Uncertain(int transactions) => _uncertain.Add(transactions);

    internal void Reclaimed(long bytes) => _reclaimedBytes.Add(bytes);

    internal void BatchWait(long started) =>
        _batchWaitDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    internal void Phase(string phase, long started, long allocatedBefore)
    {
        TagList tags = default;
        tags.Add(PhaseTag, phase);
        _phaseDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        _phaseAllocatedBytes.Record(Math.Max(0, GC.GetTotalAllocatedBytes() - allocatedBefore), tags);
    }

    public void Dispose() => _meter.Dispose();
}
