using EntityForge.Utility;

namespace EntityForge.Cqrs.Events.EventStreaming.Memory;

public partial class MemoryEventStreamingService
{
    /// <inheritdoc />
    public EventJournalRetentionCapabilities RetentionCapabilities =>
        EventJournalRetentionCapabilities.None;

    /// <inheritdoc />
    public Task<EventJournalTopicBounds> GetTopicBoundsAsync(
        string topicName,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
        token.ThrowIfCancellationRequested();

        lock (_topicsByTopicName)
        {
            return Task.FromResult(
                _topicsByTopicName.TryGetValue(topicName, out Topic topic)
                    ? topic.GetBounds()
                    : null);
        }
    }

    /// <inheritdoc />
    public Task<EventJournalConsumerCursorStatus> GetConsumerCursorStatusAsync(
        string topicName,
        IEventHandlerHostObject eventConsumer,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
        ArgumentNullException.ThrowIfNull(eventConsumer);
        token.ThrowIfCancellationRequested();

        lock (_topicsByTopicName)
        {
            return Task.FromResult(
                _topicsByTopicName.TryGetValue(topicName, out Topic topic)
                    ? topic.GetCursorStatus(eventConsumer)
                    : new EventJournalConsumerCursorStatus(
                        EventJournalCursorHealth.TopicNotFound,
                        CommittedPosition: null,
                        TopicBounds: null));
        }
    }
}
