using EntityForge.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace EntityForge.Cqrs.Events.EventStreaming.Memory
{
    public partial class MemoryEventStreamingService
    {
        private class GetNewEventsResult
        {
            public bool Cancelled
            {
                get; set;
            }

            public bool Reverting
            {
                get; set;
            }

            public List<IEventMessage> NewEvents
            {
                get; set;
            }
        }

        /// <summary>
        /// Represents an event topic, to which publishers produce events
        /// and subscribers consume events
        /// </summary>
        public sealed class Topic : IDisposable, IAsyncDisposable
        {
            private class TopicEventConsumerInfo
            {
                private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

                public TopicEventConsumerInfo()
                {
                    HandlerDelegatesByEventType = new Dictionary<Type, MessageHandlerDelegate>();
                }

                /// <summary>
                /// The task responsible for processing the consuming of events
                /// on this topic
                /// </summary>
                public Task EventConsumingTask
                {
                    get; set;
                }

                /// <summary>
                /// The position of the last successfully consumed topic event
                /// </summary>
                public long? CurrentConsumedEventPosition
                {
                    get; private set;
                }

                /// <summary>
                /// The position of the last commited event position for this topic
                /// </summary>
                public long? LastCommittedEventPosition
                {
                    get; private set;
                }

                /// <summary>
                /// The handler methods on the event handler object by the event type they handle
                /// </summary>
                public Dictionary<Type, MessageHandlerDelegate> HandlerDelegatesByEventType
                {
                    get;
                }

                /// <summary>
                /// The cancellation token source for the event processing task
                /// </summary>
                public bool IsCancelled => _cancellationTokenSource.IsCancellationRequested;

                public CancellationToken CancellationToken => _cancellationTokenSource.Token;

                /// <summary>
                /// Indicates whether the event consuming task is running
                /// </summary>
                public bool IsConsumingActive => EventConsumingTask != null;

                /// <summary>
                /// Called to signal the consumer thread to stop consuming
                /// </summary>
                public void Cancel()
                {
                    _cancellationTokenSource.Cancel();
                }

                public void PrepareToRestart()
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = new CancellationTokenSource();
                }

                /// <summary>
                /// Called to commit the current event position
                /// If the topic consumption is reverted, this is the position to which
                /// the current position will be set
                /// </summary>
                public void CommitEventPosition()
                {
                    LastCommittedEventPosition = CurrentConsumedEventPosition;
                }

                /// <summary>
                /// Called to revert the current event position to the last commited event position
                /// </summary>
                public void RevertEventPosition()
                {
                    CurrentConsumedEventPosition = LastCommittedEventPosition;
                }

                public bool IsReverting
                {
                    get; set;
                }

                /// <summary>
                /// Moves the current event position forward by one event
                /// </summary>
                public void IncrementEventPosition()
                {
                    if (CurrentConsumedEventPosition == null)
                    {
                        CurrentConsumedEventPosition = 0;
                    }
                    else
                    {
                        ++CurrentConsumedEventPosition;
                    }
                }

                /// <summary>
                /// Resets the current event position to null (start)
                /// </summary>
                public void ResetEventPosition()
                {
                    CurrentConsumedEventPosition = null;
                }
            }

            private const string MessageSystemDescription = "In-Memory Event Streaming Service";
            private const string MessageSystemProtocol = "In-Memory";
            private static readonly TimeSpan MessageRetryDelay = TimeSpan.FromMilliseconds(100);

            // Append-only linear journal of events - there is one events journal per topic
            // that can contain multiple event types
            private readonly List<IEventMessage> _eventsJournal = new List<IEventMessage>();

            private readonly Dictionary<IEventHandlerHostObject, TopicEventConsumerInfo> _topicConsumerInfosByConsumer
                = new Dictionary<IEventHandlerHostObject, TopicEventConsumerInfo>();

            private readonly Microsoft.Extensions.Logging.ILogger _logger;
            private readonly MemoryEventStreamingService _eventStreamingService;
            private readonly string _name;
            private readonly string _description;

            private long _subscriberCount;

            private volatile bool _disposed;

            /// <summary>Creates a topic journal and its consumer tracking state.</summary>
            public Topic(Microsoft.Extensions.Logging.ILogger logger, MemoryEventStreamingService eventStreamingService, string name, string description)
            {
                ArgumentNullException.ThrowIfNull(eventStreamingService, nameof(eventStreamingService));
                ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));

                _logger = logger ?? NullLogger.Instance;
                _eventStreamingService = eventStreamingService;
                _name = name;
                _description = description;
            }

            /// <summary>Stops and releases resources belonging to this topic.</summary>
            public void Dispose()
            {
                _disposed = true;

                lock (_eventsJournal)
                {
                    // Signal to processing threads to finish
                    Monitor.PulseAll(_eventsJournal);
                }
            }

            /// <summary>Asynchronously stops topic delivery and releases owned resources.</summary>
            public async ValueTask DisposeAsync()
            {
                Dispose();
                Task[] consumingTasks = _topicConsumerInfosByConsumer.Values
                    .Select(i => i.EventConsumingTask)
                    .Where(task => task != null)
                    .ToArray();

                if (consumingTasks.Length > 0)
                {
                    await Task.WhenAll(consumingTasks).ConfigureAwait(false);
                }

                GC.SuppressFinalize(this);
            }

            /// <summary>Returns the topic's currently retained event sequence.</summary>
            public IEnumerable<IEventMessage> GetCurrentEvents()
            {
                lock (_eventsJournal)
                {
                    return _eventsJournal.ToList();
                }
            }

            /// <summary>
            /// Appends new event messages to the in-memory events journal topic
            /// </summary>
            public void AppendEventMessages(IEnumerable<IEventMessage> eventMessages)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MemoryEventStreamingService));
                }

                lock (_eventsJournal)
                {
                    foreach (var eventMessage in eventMessages)
                    {
                        Debug.WriteLine($"Added event {eventMessage.GetType().Name} {eventMessage} to event topic '{_name}'");
                        Debug.WriteLine($"Currently {_topicConsumerInfosByConsumer.Count} event handlers subscribed to event topic '{_name}'");

                        // Commit to the event journal (these events are immutable)
                        // The journal is appended regardless of whether there are any
                        // subscribers at the moment
                        _eventsJournal.Add(eventMessage);
                    }

                    // Signal to any listening threads that the journal has been updated
                    // Note there may not be any consumers at the moment
                    Monitor.PulseAll(_eventsJournal);
                }

                // Raise events appended event on event streaming service
                _eventStreamingService.RaiseEventsAppended(_name, eventMessages.ToList().AsReadOnly());
            }

            /// <summary>
            /// Subscribes an event handler to this event topic
            /// Note: More than one event handler interface may be implemented by the same object
            /// </summary>
            public void SubscribeEventHandlerToTopic<TEvent>(IEventHandler<TEvent> eventHandler)
                where TEvent : IEventMessage
            {
                ArgumentNullException.ThrowIfNull(eventHandler, nameof(eventHandler));
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MemoryEventStreamingService));
                }

                lock (_eventsJournal)
                {
                    TopicEventConsumerInfo topicEventConsumerInfo = null;

                    // If this event handler object hasn't subscribed to any event before, add a dictionary entry for it
                    if (!_topicConsumerInfosByConsumer.TryGetValue(eventHandler, out topicEventConsumerInfo))
                    {
                        topicEventConsumerInfo = new TopicEventConsumerInfo();
                        _topicConsumerInfosByConsumer.Add(eventHandler, topicEventConsumerInfo);
                    }

                    MessageHandlerDelegate handlerInvoker = null;

                    // If this event type for this event handler has never been subscribed to before for this event handler object,
                    // add a new handler delegate for this particular event type
                    if (!topicEventConsumerInfo.HandlerDelegatesByEventType.TryGetValue(typeof(TEvent), out handlerInvoker))
                    {
                        Debug.WriteLine($"Subscribing to {_description} topic '{_name}' event '{typeof(TEvent).Name}' for handler {eventHandler}");

                        // Note: reflection is expensive, but this is only done once on subscription so is considered acceptable
                        handlerInvoker = (uow, msg) => eventHandler.HandleAsync((TEvent)msg, uow);
                        topicEventConsumerInfo.HandlerDelegatesByEventType.Add(typeof(TEvent), handlerInvoker);

                        _subscriberCount++;
                    }
                }
            }

            /// <summary>Registers a consumer that handles every event type in this topic.</summary>
            public void SubscribeAllEventsHandlerToTopic(IEventHandler eventHandler)
            {
                ArgumentNullException.ThrowIfNull(eventHandler);
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MemoryEventStreamingService));
                }

                lock (_eventsJournal)
                {
                    if (!_topicConsumerInfosByConsumer.TryGetValue(eventHandler, out TopicEventConsumerInfo consumerInfo))
                    {
                        consumerInfo = new TopicEventConsumerInfo();
                        _topicConsumerInfosByConsumer.Add(eventHandler, consumerInfo);
                    }

                    if (!consumerInfo.HandlerDelegatesByEventType.ContainsKey(typeof(IEventMessage)))
                    {
                        consumerInfo.HandlerDelegatesByEventType.Add(
                            typeof(IEventMessage),
                            (uow, message) => eventHandler.HandleAsync((IEventMessage)message, uow));
                        _subscriberCount++;
                    }
                }
            }

            /// <summary>
            /// Unsubscribes from a topic
            /// </summary>
            public void UnsubscribeEventHandlerFromTopic<TEvent>(IEventHandler<TEvent> eventHandler)
                where TEvent : IEventMessage
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MemoryEventStreamingService));
                }

                if (eventHandler == null)
                {
                    throw new ArgumentNullException(nameof(eventHandler));
                }

                lock (_eventsJournal)
                {
                    if (_topicConsumerInfosByConsumer.TryGetValue(eventHandler, out TopicEventConsumerInfo topicEventConsumerInfo))
                    {
                        _subscriberCount--;

                        // Will remove if present in the dictionary
                        topicEventConsumerInfo.HandlerDelegatesByEventType.Remove(typeof(TEvent));
                    }
                }
            }

            private async Task<MessageHandlingResult> ConsumeEventAsync(TopicEventConsumerInfo topicEventConsumerInfo, IEventHandlerHostObject eventConsumer, IEventMessage eventMessage)
            {
                // Find the handler delegates that match this event type
                var matchingMethodInfos = topicEventConsumerInfo.HandlerDelegatesByEventType.Where(e =>
                    e.Key.IsAssignableFrom(eventMessage.GetType())).Select(e => e.Value).ToList();

                if (matchingMethodInfos.Count == 0)
                {
                    throw new InvalidOperationException($"You have registered an event handler object '{eventConsumer}' for topic '{_name}' that does not handle all topic event types - add a handler for event type {eventMessage.GetType().Name}");
                }

                Debug.WriteLine($"EventDispatch {eventMessage.GetType().Name} {eventMessage} to {matchingMethodInfos.Count} handlers");

                // Invoke the handler delegate for each matching event type
                MessageHandlingResult aggregateResult = MessageHandlingResult.AlreadyProcessed;
                foreach (var matchingMethodInfo in matchingMethodInfos)
                {
                    // Invoke the handler method idempotently
                    MessageHandlingResult result = await InboxMessageHandler.HandleTopicMessageAsync(
                        _logger, MessageSystemDescription, MessageSystemProtocol, _name,
                        eventMessage, eventConsumer, matchingMethodInfo);

                    if (result == MessageHandlingResult.Retry)
                    {
                        return result;
                    }

                    if (result == MessageHandlingResult.Processed)
                    {
                        aggregateResult = result;
                    }

                    // Inform the event has been consumed
                    _eventStreamingService.RaiseEventsConsumed(_name, Enumerable.Repeat(eventMessage, 1).ToList().AsReadOnly());
                }

                return aggregateResult;
            }

            private GetNewEventsResult WaitForNewEvents(long? offset, TopicEventConsumerInfo topicEventConsumerInfo)
            {
                var result = new GetNewEventsResult();
                result.NewEvents = new List<IEventMessage>();

                // Start by pushing all events since start offset (zero in this case). Events are immutable,
                // so there is no risk of them changing whilst we iterate through the snapshotted initial
                // list. Only the list itself can change by being appended
                lock (_eventsJournal)
                {
                    // Start the offset from the current event position
                    offset = topicEventConsumerInfo.CurrentConsumedEventPosition;

                    // Ensure we are set as not reverting any more
                    topicEventConsumerInfo.IsReverting = false;

                    while (!_disposed && (!_eventsJournal.Any() || (offset.HasValue && offset.Value >= _eventsJournal.Count - 1)) &&
                        !topicEventConsumerInfo.IsCancelled &&
                        !topicEventConsumerInfo.IsReverting)
                    {
                        // Releases the lock when it waits. Can be triggered by more events
                        // being added to the journal, which affects all subscribed to topics
                        Monitor.Wait(_eventsJournal);
                    }

                    // If we are reverting, then start from the current position again
                    if (topicEventConsumerInfo.IsReverting)
                    {
                        result.Reverting = true;
                        return result;
                    }

                    // No longer consuming, so quit
                    if (topicEventConsumerInfo.IsCancelled)
                    {
                        result.Cancelled = true;
                        return result;
                    }

                    // The stored position is the last successfully consumed event, so resume
                    // from the next journal entry rather than replaying the current one.
                    long startIndex = offset.HasValue ? offset.Value + 1 : 0;

                    for (long newIndex = startIndex; newIndex < _eventsJournal.Count; newIndex++)
                    {
                        result.NewEvents.Add(_eventsJournal[(int)newIndex]);
                    }
                }

                return result;
            }

            /// <summary>Returns the current earliest and latest retained topic positions.</summary>
            public EventJournalTopicBounds GetBounds()
            {
                lock (_eventsJournal)
                {
                    return _eventsJournal.Count == 0
                        ? new EventJournalTopicBounds(null, null, 0)
                        : new EventJournalTopicBounds(
                            EarliestPosition: "0",
                            LatestPosition: (_eventsJournal.Count - 1).ToString(),
                            EventCount: _eventsJournal.Count);
                }
            }

            /// <summary>Removes an all-event consumer from the topic.</summary>
            public void UnsubscribeAllEventsHandlerFromTopic(IEventHandler eventHandler)
            {
                ArgumentNullException.ThrowIfNull(eventHandler);
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MemoryEventStreamingService));
                }

                lock (_eventsJournal)
                {
                    if (_topicConsumerInfosByConsumer.TryGetValue(eventHandler, out TopicEventConsumerInfo consumerInfo) &&
                        consumerInfo.HandlerDelegatesByEventType.Remove(typeof(IEventMessage)))
                    {
                        _subscriberCount--;
                    }
                }
            }

            /// <summary>Classifies the consumer's saved position against retained topic bounds.</summary>
            public EventJournalConsumerCursorStatus GetCursorStatus(
                IEventHandlerHostObject eventConsumer)
            {
                ArgumentNullException.ThrowIfNull(eventConsumer);

                lock (_eventsJournal)
                {
                    EventJournalTopicBounds bounds = GetBounds();
                    if (!_topicConsumerInfosByConsumer.TryGetValue(
                        eventConsumer,
                        out TopicEventConsumerInfo consumerInfo))
                    {
                        return new EventJournalConsumerCursorStatus(
                            EventJournalCursorHealth.NoCheckpoint,
                            CommittedPosition: null,
                            bounds);
                    }

                    long? committed = consumerInfo.LastCommittedEventPosition;
                    if (!committed.HasValue)
                    {
                        return new EventJournalConsumerCursorStatus(
                            EventJournalCursorHealth.NoCheckpoint,
                            CommittedPosition: null,
                            bounds);
                    }

                    EventJournalCursorHealth health =
                        committed.Value >= _eventsJournal.Count
                            ? EventJournalCursorHealth.AheadOfJournal
                            : EventJournalCursorHealth.Healthy;
                    return new EventJournalConsumerCursorStatus(
                        health,
                        committed.Value.ToString(),
                        bounds);
                }
            }

            // In subscriber journal thread
            private async Task SubscriberEventJournalReadingAsync(TopicEventConsumerInfo topicEventConsumerInfo, IEventHandlerHostObject eventConsumer)
            {
                Debug.WriteLine($"Started consumption of {_description} topic '{_name}' for handler '{eventConsumer.ToString()}'.");

                // Read offset always starts at zero
                long? offset = null;

                lock (_eventsJournal)
                {
                    // Start from the last consumed event position
                    // Note that this may be null if no event position has been committed yet
                    topicEventConsumerInfo.RevertEventPosition();
                }

                while (!_disposed && !topicEventConsumerInfo.IsCancelled)
                {
                    GetNewEventsResult newEventsResult = await Task.Run(() => WaitForNewEvents(offset, topicEventConsumerInfo));

                    if (_disposed)
                    {
                        return;       // Quit the thread - when restarted, events should be replayed again
                    }

                    // Note: Event messages are immutable, so they won't change outside of the events journal lock -
                    // so no need to clone them

                    // Process each event serially before moving onto the next event
                    foreach (var eventMessage in newEventsResult.NewEvents)
                    {
                        MessageHandlingResult result = await ConsumeEventAsync(
                            topicEventConsumerInfo, eventConsumer, eventMessage);

                        if (result == MessageHandlingResult.Retry)
                        {
                            try
                            {
                                await Task.Delay(MessageRetryDelay, topicEventConsumerInfo.CancellationToken);
                            }
                            catch (OperationCanceledException) when (topicEventConsumerInfo.IsCancelled)
                            {
                                return;
                            }
                            break;
                        }

                        lock (_eventsJournal)
                        {
                            if (topicEventConsumerInfo.IsReverting || topicEventConsumerInfo.IsCancelled)
                            {
                                break;
                            }

                            topicEventConsumerInfo.IncrementEventPosition();

                        }
                    }

                    newEventsResult.NewEvents.Clear();
                }
            }

            /// <summary>
            /// Starts consuming events from the in-memory event journal for the specified event handler object
            /// </summary>
            public Task StartConsumingAsync(IEventHandlerHostObject eventConsumer, CancellationToken token)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MemoryEventStreamingService));
                }

                lock (_eventsJournal)
                {
                    // The event handler is the object on which the handler method is invoked. Each one
                    // of those objects can have multiple handlers for different event types. A topic
                    // can contain events of multiple different types.
                    if (_topicConsumerInfosByConsumer.TryGetValue(eventConsumer, out TopicEventConsumerInfo topicEventHandlerInfo) &&
                        topicEventHandlerInfo.EventConsumingTask == null)
                    {
                        // If this is a new subscriber, we need to start its handling task
                        topicEventHandlerInfo.EventConsumingTask = SubscriberEventJournalReadingAsync(topicEventHandlerInfo, eventConsumer);

                        Debug.WriteLine($"Started consumption of event handler '{eventConsumer}' (currently has {topicEventHandlerInfo.HandlerDelegatesByEventType.Count} event handler methods registered)");
                    }
                    Monitor.PulseAll(_eventsJournal);
                }

                return Task.CompletedTask;
            }

            /// <summary>Stops and drains event delivery for the selected topic consumer.</summary>
            public async Task StopConsumingAsync(IEventHandlerHostObject eventConsumer, CancellationToken token)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(MemoryEventStreamingService));
                }

                Task eventHandlingTask = null;

                lock (_eventsJournal)
                {
                    if (_topicConsumerInfosByConsumer.TryGetValue(eventConsumer, out TopicEventConsumerInfo topicEventConsumerInfo))
                    {
                        // Signal the topic to stop consuming, which should end the task thread
                        topicEventConsumerInfo.Cancel();
                        Monitor.PulseAll(_eventsJournal);

                        eventHandlingTask = topicEventConsumerInfo.EventConsumingTask;
                    }
                }

                // Wait for the consumer thread to stop
                if (eventHandlingTask != null)
                {
                    await eventHandlingTask;
                }

                lock (_eventsJournal)
                {
                    if (_topicConsumerInfosByConsumer.TryGetValue(eventConsumer, out TopicEventConsumerInfo topicEventConsumerInfo) &&
                        topicEventConsumerInfo.EventConsumingTask == eventHandlingTask)
                    {
                        topicEventConsumerInfo.EventConsumingTask = null;
                        topicEventConsumerInfo.PrepareToRestart();
                    }
                }
            }

            /// <summary>
            /// Commits the current stream position for the topic; meaning if revert is called
            /// or the event streaming service restarted, this will be the position consumption resumes
            /// </summary>
            public void CommitStreamPosition(IEventHandlerHostObject eventConsumer)
            {
                lock (_eventsJournal)
                {
                    if (_topicConsumerInfosByConsumer.TryGetValue(eventConsumer, out TopicEventConsumerInfo topicEventConsumerInfo))
                    {
                        topicEventConsumerInfo.CommitEventPosition();
                    }
                }
            }

            /// <summary>
            /// Reverts the current stream position for the topic to the last committed position
            /// </summary>
            public void RevertStreamPosition(IEventHandlerHostObject eventConsumer)
            {
                lock (_eventsJournal)
                {
                    if (_topicConsumerInfosByConsumer.TryGetValue(eventConsumer, out TopicEventConsumerInfo topicEventConsumerInfo))
                    {
                        topicEventConsumerInfo.RevertEventPosition();
                        topicEventConsumerInfo.IsReverting = true;
                        Monitor.PulseAll(_eventsJournal);
                    }
                }
            }

            /// <summary>Gets the number of registered topic subscribers.</summary>
            public long SubscriberCount
            {
                get
                {
                    lock (_eventsJournal)
                    {
                        return _subscriberCount;
                    }
                }
            }
        }
    }
}
