using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Telemetry;

/// <summary>Publishes database operation traces and runtime measurements through .NET diagnostics.</summary>
/// <remarks>
/// Register listeners for <see cref="ActivitySourceName"/> and <see cref="MeterName"/> to collect data.
/// This class does not configure exporters or retain history. Recording methods report caller-supplied
/// observations; they do not validate counts or enforce resource limits. Use bounded classification
/// values for metric tags, not query text, credentials or per-request identifiers.
/// </remarks>
public sealed class EntityForgeDbTelemetry : IDisposable
{
    private const long SingleOccurrence = 1;
    private const string ExceptionEventName = "exception";
    private const string ExceptionTypeTagName = "exception.type";
    private const string ExceptionMessageTagName = "exception.message";

    /// <summary>The activity source name used by database operation spans.</summary>
    public const string ActivitySourceName = "EntityForge.Db";

    /// <summary>The meter name used by database counters and histograms.</summary>
    public const string MeterName = "EntityForge.Db";
    private readonly ActivitySource _activities = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _commits;
    private readonly Counter<long> _conflicts;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _reclaimedRecords;
    private readonly Counter<long> _reclaimedBytes;
    private readonly Counter<long> _propertyBindingChanges;
    private readonly Counter<long> _governanceRuleChanges;
    private readonly Counter<long> _snapshotLeaseChanges;
    private readonly Counter<long> _resumableWriteChanges;
    private readonly Counter<long> _governanceDecisions;
    private readonly Histogram<long> _validationRecoveryFloor;
    private readonly Histogram<long> _validationRecordsReconstructed;
    private readonly Histogram<double> _operationDuration;
    private readonly Histogram<double> _commitQueueWait;
    private readonly Histogram<long> _commitQueueDepth;
    private readonly Histogram<double> _publicationDuration;
    private readonly Histogram<long> _publishedFieldRows;
    private readonly Histogram<long> _publishedRelationshipRows;
    private readonly Counter<long> _sourceSnapshotObserved;
    private readonly Counter<long> _sourceSnapshotRetracted;
    private readonly Histogram<long> _checkpointBytes;
    private readonly Histogram<long> _checkpointVersion;
    private readonly Counter<long> _recoveryRecords;
    private readonly Histogram<long> _walSegments;
    private readonly Histogram<long> _walRetainedBytes;
    private readonly Histogram<long> _walActiveSegmentBytes;
    private readonly Histogram<double> _planningStageDuration;
    private readonly Histogram<long> _planningStageAllocatedBytes;
    private readonly UpDownCounter<long> _activeQuerySubscriptions;
    private readonly Counter<long> _querySubscriptionEvents;
    private readonly Histogram<long> _propertyOptimizationObservedProperties;
    private readonly Counter<long> _propertyOptimizationDroppedObservations;
    private readonly Histogram<long> _propertyOptimizationCapacityReached;

    /// <summary>Creates the activity source, meter and database instruments.</summary>
    public EntityForgeDbTelemetry()
    {
        _commits = _meter.CreateCounter<long>("entityforge.db.commits");
        _conflicts = _meter.CreateCounter<long>("entityforge.db.write_conflicts");
        _failures = _meter.CreateCounter<long>("entityforge.db.failures");
        _reclaimedRecords = _meter.CreateCounter<long>("entityforge.db.log.reclaimed_records");
        _reclaimedBytes = _meter.CreateCounter<long>("entityforge.db.log.reclaimed_bytes", "By");
        _propertyBindingChanges = _meter.CreateCounter<long>("entityforge.db.catalog.property_binding_changes");
        _governanceRuleChanges = _meter.CreateCounter<long>("entityforge.db.catalog.governance_rule_changes");
        _snapshotLeaseChanges = _meter.CreateCounter<long>("entityforge.db.snapshot_lease.changes");
        _resumableWriteChanges = _meter.CreateCounter<long>("entityforge.db.resumable_write.changes");
        _governanceDecisions = _meter.CreateCounter<long>("entityforge.db.governance.decisions");
        _validationRecoveryFloor = _meter.CreateHistogram<long>("entityforge.db.validation.recovery_floor");
        _validationRecordsReconstructed = _meter.CreateHistogram<long>(
            "entityforge.db.validation.records_reconstructed");
        _operationDuration = _meter.CreateHistogram<double>("entityforge.db.operation.duration", "ms");
        _commitQueueWait = _meter.CreateHistogram<double>("entityforge.db.commit.queue_wait", "ms");
        _commitQueueDepth = _meter.CreateHistogram<long>("entityforge.db.commit.queue_depth");
        _publicationDuration = _meter.CreateHistogram<double>("entityforge.db.publication.duration", "ms");
        _publishedFieldRows = _meter.CreateHistogram<long>("entityforge.db.publication.field_rows");
        _publishedRelationshipRows = _meter.CreateHistogram<long>(
            "entityforge.db.publication.relationship_rows");
        _sourceSnapshotObserved = _meter.CreateCounter<long>(
            "entityforge.db.source_snapshot.observed_items");
        _sourceSnapshotRetracted = _meter.CreateCounter<long>(
            "entityforge.db.source_snapshot.retracted_items");
        _checkpointBytes = _meter.CreateHistogram<long>("entityforge.db.checkpoint.bytes", "By");
        _checkpointVersion = _meter.CreateHistogram<long>("entityforge.db.checkpoint.version");
        _recoveryRecords = _meter.CreateCounter<long>("entityforge.db.recovery.records");
        _walSegments = _meter.CreateHistogram<long>("entityforge.db.wal.segments");
        _walRetainedBytes = _meter.CreateHistogram<long>("entityforge.db.wal.retained_bytes", "By");
        _walActiveSegmentBytes = _meter.CreateHistogram<long>("entityforge.db.wal.active_segment_bytes", "By");
        _planningStageDuration = _meter.CreateHistogram<double>(
            "entityforge.db.semantic.planning.stage.duration", "ms");
        _planningStageAllocatedBytes = _meter.CreateHistogram<long>(
            "entityforge.db.semantic.planning.stage.allocated_bytes", "By");
        _activeQuerySubscriptions = _meter.CreateUpDownCounter<long>(
            "entityforge.db.query.subscription.active");
        _querySubscriptionEvents = _meter.CreateCounter<long>(
            "entityforge.db.query.subscription.events");
        _propertyOptimizationObservedProperties = _meter.CreateHistogram<long>(
            "entityforge.db.semantic.property_optimization.observed_properties", "{property}");
        _propertyOptimizationDroppedObservations = _meter.CreateCounter<long>(
            "entityforge.db.semantic.property_optimization.dropped_observations", "{observation}");
        _propertyOptimizationCapacityReached = _meter.CreateHistogram<long>(
            "entityforge.db.semantic.property_optimization.capacity_reached");
    }

    /// <summary>Adds the records and bytes removed by one log-reclamation operation to cumulative counters.</summary>
    /// <param name="recordsRemoved">Number of records removed, not the number remaining.</param>
    /// <param name="bytesRemoved">Number of bytes removed.</param>
    public void RecordLogReclamation(long recordsRemoved, long bytesRemoved)
    {
        _reclaimedRecords.Add(recordsRemoved);
        _reclaimedBytes.Add(bytesRemoved);
    }

    /// <summary>Counts one catalog property-binding change, tagged by storage kind.</summary>
    /// <param name="storageKind">The storage-kind classification of the changed binding.</param>
    public void RecordPropertyBindingChange(string storageKind) =>
        _propertyBindingChanges.Add(SingleOccurrence,
            new TagList { { "entityforge.db.property.storage_kind", storageKind } });

    /// <summary>Counts one governance-rule change, tagged by rule kind and enabled state.</summary>
    /// <param name="kind">The rule-kind classification.</param>
    /// <param name="enabled">Whether the changed rule is enabled.</param>
    public void RecordGovernanceRuleChange(string kind, bool enabled) =>
        _governanceRuleChanges.Add(SingleOccurrence, new TagList
        {
            { "entityforge.db.governance.kind", kind },
            { "entityforge.db.governance.enabled", enabled }
        });

    /// <summary>Counts one snapshot-lease lifecycle event; this is not an active-lease gauge.</summary>
    /// <param name="kind">The lifecycle-event classification.</param>
    public void RecordSnapshotLeaseChange(string kind) =>
        _snapshotLeaseChanges.Add(SingleOccurrence,
            new TagList { { "entityforge.db.snapshot_lease.change_kind", kind } });

    /// <summary>Counts one resumable-write lifecycle event.</summary>
    /// <param name="kind">The lifecycle-event classification.</param>
    public void RecordResumableWriteChange(string kind) =>
        _resumableWriteChanges.Add(SingleOccurrence,
            new TagList { { "entityforge.db.resumable_write.change_kind", kind } });

    /// <summary>Counts one governance decision, tagged by rule kind and decision.</summary>
    /// <param name="ruleKind">The evaluated rule-kind classification.</param>
    /// <param name="decision">The decision classification.</param>
    public void RecordGovernanceDecision(string ruleKind, string decision) =>
        _governanceDecisions.Add(SingleOccurrence, new TagList
        {
            { "entityforge.db.governance.kind", ruleKind },
            { "entityforge.db.governance.decision", decision }
        });

    /// <summary>Records the validation recovery floor and reconstructed-record count as histogram observations.</summary>
    /// <param name="recoveryFloor">The recovered validation version floor.</param>
    /// <param name="recordsReconstructed">Number of validation records reconstructed during recovery.</param>
    public void RecordValidationRecovery(long recoveryFloor, long recordsReconstructed)
    {
        _validationRecoveryFloor.Record(recoveryFloor);
        _validationRecordsReconstructed.Record(recordsReconstructed);
    }

    /// <summary>Records commit-admission wait in milliseconds and observed queue depth, tagged by outcome.</summary>
    /// <param name="wait">Elapsed time waiting for admission.</param>
    /// <param name="depth">Queue depth observed by the caller; this histogram is not a live gauge.</param>
    /// <param name="outcome">Admission outcome, such as admitted, rejected or cancelled.</param>
    public void RecordCommitQueue(TimeSpan wait, int depth, string outcome)
    {
        TagList tags = new() { { "entityforge.db.outcome", outcome } };
        _commitQueueWait.Record(wait.TotalMilliseconds, tags);
        _commitQueueDepth.Record(depth, tags);
    }

    /// <summary>Records publication duration in milliseconds and the published field/relationship row counts.</summary>
    /// <param name="duration">Elapsed publication time.</param>
    /// <param name="fieldRows">Number of field rows published by this operation.</param>
    /// <param name="relationshipRows">Number of relationship rows published by this operation.</param>
    public void RecordPublication(TimeSpan duration, long fieldRows, long relationshipRows)
    {
        _publicationDuration.Record(duration.TotalMilliseconds);
        _publishedFieldRows.Record(fieldRows);
        _publishedRelationshipRows.Record(relationshipRows);
    }

    /// <summary>Adds source-snapshot observed and retracted item counts to cumulative counters.</summary>
    /// <param name="observedItems">Items observed by this snapshot operation.</param>
    /// <param name="retractedItems">Items retracted by this snapshot operation.</param>
    public void RecordSourceSnapshot(long observedItems, long retractedItems)
    {
        _sourceSnapshotObserved.Add(observedItems);
        _sourceSnapshotRetracted.Add(retractedItems);
    }

    /// <summary>Records a checkpoint version and, when known, its size in bytes.</summary>
    /// <param name="version">The checkpoint's database version, recorded as a histogram observation.</param>
    /// <param name="bytes">Checkpoint size; a negative value omits the size observation without omitting the version.</param>
    public void RecordCheckpoint(long version, long bytes)
    {
        _checkpointVersion.Record(version);
        if (bytes >= 0)
        {
            _checkpointBytes.Record(bytes);
        }
    }

    /// <summary>Counts one recovered record, tagged by commit kind.</summary>
    /// <param name="kind">The recovered record's commit-kind classification.</param>
    public void RecordRecoveryRecord(string kind) => _recoveryRecords.Add(SingleOccurrence,
        new TagList { { "entityforge.db.commit.kind", kind } });

    /// <summary>Increments the active query-subscription counter; pair with one end notification.</summary>
    public void QuerySubscriptionStarted() => _activeQuerySubscriptions.Add(SingleOccurrence);

    /// <summary>Decrements the active query-subscription counter for a previously started subscription.</summary>
    /// <remarks>This method does not track subscription identities or suppress duplicate end notifications.</remarks>
    public void QuerySubscriptionEnded() => _activeQuerySubscriptions.Add(-SingleOccurrence);

    /// <summary>Counts one query-subscription event, tagged by event kind.</summary>
    /// <param name="kind">The event-kind classification.</param>
    public void RecordQuerySubscriptionEvent(string kind) => _querySubscriptionEvents.Add(SingleOccurrence,
        new TagList { { "entityforge.db.query.subscription.event_kind", kind } });

    /// <summary>Counts one dropped property-optimization observation.</summary>
    public void RecordPropertyOptimizationObservationDropped() =>
        _propertyOptimizationDroppedObservations.Add(SingleOccurrence);

    /// <summary>Records the number of observed properties and a capacity-reached sample of zero or one.</summary>
    /// <param name="observedProperties">Current number of properties represented in the observation store.</param>
    /// <param name="capacityReached">Whether observation capacity has been reached at this observation.</param>
    public void RecordPropertyOptimizationObservationStatus(int observedProperties, bool capacityReached)
    {
        _propertyOptimizationObservedProperties.Record(observedProperties);
        _propertyOptimizationCapacityReached.Record(capacityReached ? 1 : 0);
    }

    /// <summary>Records write-ahead-log segment count and byte sizes as histogram observations.</summary>
    /// <param name="segments">Number of retained segments at the observation time.</param>
    /// <param name="retainedBytes">Total retained log size in bytes.</param>
    /// <param name="activeSegmentBytes">Active segment size in bytes.</param>
    public void RecordWalStatus(int segments, long retainedBytes, long activeSegmentBytes)
    {
        _walSegments.Record(segments);
        _walRetainedBytes.Record(retainedBytes);
        _walActiveSegmentBytes.Record(activeSegmentBytes);
    }

    /// <summary>Records elapsed planning-stage time in milliseconds and process-wide allocated-byte growth.</summary>
    /// <param name="stage">The planning-stage classification used as a metric tag.</param>
    /// <param name="started">Start timestamp obtained from <see cref="Stopwatch.GetTimestamp"/> in this process.</param>
    /// <param name="allocatedBefore">The process allocation counter from <see cref="GC.GetTotalAllocatedBytes"/> at stage start.</param>
    /// <remarks>Concurrent work can contribute to the allocation delta; it is not retained memory or isolated per-query allocation.</remarks>
    public void RecordPlanningStage(string stage, long started, long allocatedBefore)
    {
        var tags = new TagList { { "entityforge.db.stage", stage } };
        _planningStageDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        _planningStageAllocatedBytes.Record(GC.GetTotalAllocatedBytes() - allocatedBefore, tags);
    }

    /// <summary>Starts an operation timing scope and, when a listener samples it, an internal activity.</summary>
    /// <param name="name">A bounded operation classification used as the activity name and metric tag.</param>
    /// <param name="sessionId">Optional session identifier attached to the activity, not to metric tags.</param>
    /// <returns>A scope to annotate and dispose when the operation ends.</returns>
    /// <remarks>Metrics are recorded on disposal even when no activity was sampled. Keep this telemetry instance alive until scopes finish.</remarks>
    public EntityForgeDbOperation StartOperation(string name, Guid? sessionId = null)
    {
        Activity? activity = _activities.StartActivity(name, ActivityKind.Internal);
        if (sessionId is not null)
        {
            activity?.SetTag("entityforge.db.session.id", sessionId.Value);
        }

        return new(this, name, activity, Stopwatch.GetTimestamp());
    }

    internal void Complete(string operation, long started, Activity? activity, string outcome, Exception? error)
    {
        TagList tags = new() { { "entityforge.db.operation", operation }, { "entityforge.db.outcome", outcome } };
        _operationDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds, tags);
        if (operation == "db.commit" && outcome is "applied" or "idempotent_replay")
        {
            _commits.Add(SingleOccurrence, tags);
        }

        if (outcome == "conflict")
        {
            _conflicts.Add(SingleOccurrence, tags);
        }

        if (error is not null)
        {
            _failures.Add(SingleOccurrence, tags);
            activity?.SetStatus(ActivityStatusCode.Error, error.Message);
            activity?.AddEvent(new ActivityEvent(ExceptionEventName, tags: new ActivityTagsCollection
            {
                { ExceptionTypeTagName, error.GetType().FullName },
                { ExceptionMessageTagName, error.Message }
            }));
        }
        activity?.SetTag("entityforge.db.outcome", outcome);
        activity?.Dispose();
    }

    /// <summary>Disposes the activity source and meter; it does not flush external exporters.</summary>
    public void Dispose()
    {
        _activities.Dispose();
        _meter.Dispose();
    }
}

/// <summary>Holds outcome and error annotations for one timed database operation.</summary>
/// <remarks>Set annotations before disposal; concurrent annotation changes are not supported. Disposing more than once records completion only once.</remarks>
public sealed class EntityForgeDbOperation : IDisposable
{
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly string _name;
    private readonly Activity? _activity;
    private readonly long _started;
    private string _outcome = "success";
    private Exception? _error;
    private int _completed;

    internal EntityForgeDbOperation(EntityForgeDbTelemetry telemetry, string name, Activity? activity, long started)
    {
        _telemetry = telemetry;
        _name = name;
        _activity = activity;
        _started = started;
    }

    /// <summary>Replaces the outcome classification recorded when this scope is disposed.</summary>
    /// <param name="outcome">A bounded classification; the initial value is success.</param>
    /// <remarks>For db.commit, applied and idempotent_replay increment the commit counter; conflict increments the conflict counter.</remarks>
    public void SetOutcome(string outcome) => _outcome = outcome;

    /// <summary>Sets an activity tag when this operation has a sampled activity; does not add a metric tag.</summary>
    /// <param name="name">Tag name.</param>
    /// <param name="value">Tag value, or null to remove the tag.</param>
    public void SetTag(string name, object? value) => _activity?.SetTag(name, value);

    /// <summary>Stores the exception and outcome to report on disposal, replacing earlier error annotations.</summary>
    /// <param name="exception">The failure whose type and message may be emitted to the sampled activity.</param>
    /// <param name="outcome">Outcome classification; defaults to error.</param>
    /// <remarks>Increments the failure counter on completion. Exception messages are not redacted by this scope; callers must not include secrets.</remarks>
    public void RecordException(Exception exception, string outcome = "error")
    {
        _error = exception;
        _outcome = outcome;
    }

    /// <summary>Records duration, outcome and applicable counters, then ends the activity, once per scope.</summary>
    /// <remarks>Repeated or concurrent disposal calls do not emit duplicate measurements.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _telemetry.Complete(_name, _started, _activity, _outcome, _error);
        }
    }
}
