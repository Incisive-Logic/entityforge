using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Cqrs.Events.EventStreaming;

/// <summary>
/// Vendor-neutral .NET metrics for event-journal persistence and delivery.
/// </summary>
public static class EventJournalMetrics
{
    /// <summary>The Meter name used for event-journal instrumentation.</summary>
    public const string MeterName = "EntityForge.Cqrs.EventStreaming";

    private static readonly Meter _meter = new(MeterName);
    private static readonly Counter<long> _appendAttempts = _meter.CreateCounter<long>(
        "entityforge.event_journal.append.attempts", unit: "{attempt}",
        description: "Event-journal append attempts.");

    private static readonly Histogram<double> _appendDuration = _meter.CreateHistogram<double>(
        "entityforge.event_journal.append.duration", unit: "ms",
        description: "Time spent appending an event batch to the journal backend.");

    private static readonly Histogram<long> _appendBatchSize = _meter.CreateHistogram<long>(
        "entityforge.event_journal.append.batch_size", unit: "{event}",
        description: "Number of events in an event-journal append attempt.");

    private static readonly Counter<long> _eventsAppended = _meter.CreateCounter<long>(
        "entityforge.event_journal.events.appended", unit: "{event}",
        description: "Events successfully appended to an event journal.");

    private static readonly Counter<long> _deliveries = _meter.CreateCounter<long>(
        "entityforge.event_journal.deliveries", unit: "{delivery}",
        description: "Event deliveries completed by journal subscribers.");

    private static readonly Histogram<double> _deliveryAge = _meter.CreateHistogram<double>(
        "entityforge.event_journal.delivery.age", unit: "ms",
        description: "Age of an event when subscriber delivery completes.");

    private static readonly Counter<long> _retryEvents = _meter.CreateCounter<long>(
        "entityforge.event_journal.retry.events", unit: "{event}",
        description: "Events queued or replayed by the transient journal retry facade.");

    private static readonly Counter<long> _retryAttempts = _meter.CreateCounter<long>(
        "entityforge.event_journal.retry.attempts", unit: "{attempt}",
        description: "Retry-facade journal replay attempts.");

    private static readonly UpDownCounter<long> _pendingRetryEvents = _meter.CreateUpDownCounter<long>(
        "entityforge.event_journal.retry.pending", unit: "{event}",
        description: "Events currently held by the transient journal retry facade.");

    /// <summary>Records completion of an event append and its measured work.</summary>
    public static void AppendCompleted(
        string backend,
        string protocol,
        string outcome,
        TimeSpan duration,
        long eventCount)
    {
        TagList tags = CreateTransportTags(backend, protocol);
        tags.Add("outcome", Normalize(outcome));
        _appendAttempts.Add(1, tags);
        _appendDuration.Record(duration.TotalMilliseconds, tags);
        _appendBatchSize.Record(eventCount, tags);
        if (outcome.Equals("success", StringComparison.OrdinalIgnoreCase) && eventCount > 0)
        {
            _eventsAppended.Add(eventCount, CreateTransportTags(backend, protocol));
        }
    }

    /// <summary>Records completion of event delivery.</summary>
    public static void DeliveryCompleted(
        string backend,
        string protocol,
        string outcome,
        DateTime creationTime)
    {
        TagList tags = CreateTransportTags(backend, protocol);
        tags.Add("outcome", Normalize(outcome));
        _deliveries.Add(1, tags);
        if (creationTime == default)
        {
            return;
        }

        double ageMilliseconds = Math.Max(
            0,
            (DateTime.UtcNow - creationTime.ToUniversalTime()).TotalMilliseconds);
        _deliveryAge.Record(ageMilliseconds, tags);
    }

    /// <summary>Records an event delivery scheduled for retry.</summary>
    public static void RetryQueued(string backend, long eventCount)
    {
        if (eventCount <= 0)
        {
            return;
        }

        _retryEvents.Add(eventCount, CreateRetryTags(backend, "queued", "pending"));
        _pendingRetryEvents.Add(eventCount, CreateBackendTags(backend));
    }

    /// <summary>Records completion of a retried event delivery.</summary>
    public static void RetryCompleted(string backend, string outcome, long eventCount)
    {
        TagList tags = CreateRetryTags(backend, "replay", outcome);
        _retryAttempts.Add(1, tags);
        if (eventCount <= 0)
        {
            return;
        }

        _retryEvents.Add(eventCount, tags);
        if (outcome.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            _pendingRetryEvents.Add(-eventCount, CreateBackendTags(backend));
        }
    }

    private static TagList CreateTransportTags(string backend, string protocol) => new()
    {
        { "journal.backend", Normalize(backend) },
        { "network.protocol.name", Normalize(protocol) }
    };

    private static TagList CreateBackendTags(string backend) => new()
    {
        { "journal.backend", Normalize(backend) }
    };

    private static TagList CreateRetryTags(string backend, string action, string outcome) => new()
    {
        { "journal.backend", Normalize(backend) },
        { "retry.action", action },
        { "outcome", Normalize(outcome) }
    };

    private static string Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().ToLowerInvariant();
}
