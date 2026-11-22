namespace EntityForge.Cqrs.Events.EventStreaming;

/// <summary>Advertises backend-supported journal retention operations; callers must check capabilities before relying on one.</summary>
[Flags]
public enum EventJournalRetentionCapabilities
{
    /// <summary>No retention capability is advertised.</summary>
    None = 0,
    /// <summary>The backend supports age-based retention.</summary>
    TimeRetention = 1,
    /// <summary>The backend supports size-based retention.</summary>
    SizeRetention = 2,
    /// <summary>The backend can discard history before a journal position.</summary>
    TruncateBeforePosition = 4,
    /// <summary>The backend supports deleting a topic.</summary>
    DeleteTopic = 8,
    /// <summary>The backend supports compaction by event key.</summary>
    KeyCompaction = 16,
    /// <summary>The backend supports archiving retained event history.</summary>
    Archive = 32
}

/// <summary>Describes a consumer checkpoint relative to the journal's retained bounds.</summary>
public enum EventJournalCursorHealth
{
    /// <summary>The backend cannot establish cursor health.</summary>
    Unknown,
    /// <summary>The cursor is within the supported journal range.</summary>
    Healthy,
    /// <summary>No consumer checkpoint has been recorded.</summary>
    NoCheckpoint,
    /// <summary>Required history has been removed by retention.</summary>
    BehindRetention,
    /// <summary>The cursor is beyond the journal's current end.</summary>
    AheadOfJournal,
    /// <summary>The requested topic does not exist.</summary>
    TopicNotFound
}

/// <summary>Reports retained journal endpoints and an optional event count.</summary>
/// <param name="EarliestPosition">The earliest retained journal position, when known.</param>
/// <param name="LatestPosition">The latest journal position, when known.</param>
/// <param name="EventCount">The retained event count, or null when the backend cannot provide it.</param>
public sealed record EventJournalTopicBounds(
    string EarliestPosition,
    string LatestPosition,
    long? EventCount);

/// <summary>Reports whether a consumer can resume from its checkpoint against current retained journal bounds.</summary>
/// <param name="Health">The classification of checkpoint usability.</param>
/// <param name="CommittedPosition">The consumer's last committed position, when available.</param>
/// <param name="TopicBounds">The journal bounds used to classify the cursor.</param>
public sealed record EventJournalConsumerCursorStatus(
    EventJournalCursorHealth Health,
    string CommittedPosition,
    EventJournalTopicBounds TopicBounds);

/// <summary>Exposes retention capabilities and checks whether consumer positions remain resumable.</summary>
public interface IEventJournalRetentionInspector
{
    /// <summary>Gets the retention operations supported by this backend.</summary>
    EventJournalRetentionCapabilities RetentionCapabilities
    {
        get;
    }

    /// <summary>Reads the topic's earliest and latest retained positions and optional event count.</summary>
    Task<EventJournalTopicBounds> GetTopicBoundsAsync(
        string topicName,
        CancellationToken token);

    /// <summary>Compares a consumer's committed checkpoint with the topic's current retained bounds.</summary>
    Task<EventJournalConsumerCursorStatus> GetConsumerCursorStatusAsync(
        string topicName,
        IEventHandlerHostObject eventConsumer,
        CancellationToken token);
}
