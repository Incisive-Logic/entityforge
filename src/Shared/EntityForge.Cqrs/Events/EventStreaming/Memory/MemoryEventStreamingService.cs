using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using EntityForge.Cqrs.Exceptions;
using EntityForge.Cqrs.Observability;
using EntityForge.Telemetry;
using EntityForge.Utility;

namespace EntityForge.Cqrs.Events.EventStreaming.Memory
{
    /// <summary>
    /// A simple event streaming implementation. This is memory based, and is intended only
    /// for unit testing purposes where an external event streaming platform is not available.
    ///
    /// Topic event journals are stored in memory, not on disk - i.e. they only last as long
    /// as the process lifetime. There is no persistence subscription support.
    /// </summary>
    public partial class MemoryEventStreamingService :
        IDirectEventStreamingService,
        IEventJournalRetentionInspector
    {
        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly Dictionary<string, Topic> _topicsByTopicName = new Dictionary<string, Topic>();

        /// <summary>Gets whether the in-memory event service is available.</summary>
        public bool IsConnected => true;

        /// <inheritdoc />
        public string ShortDescription => "Memory";

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicCreated;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicDeleted;

        /// <inheritdoc />
        public event EventHandler<TopicEventsEventArgs> EventsAppended;

        /// <inheritdoc />
        public event EventHandler<TopicEventsEventArgs> EventsConsumed;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicEventHandlerRegistered;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicEventHandlerUnregistered;

#pragma warning disable CS0067
        // This is never used, but we accept this for the memory case
        /// <summary>Occurs when the service's connection state changes.</summary>
        public event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;
#pragma warning restore CS0067

        /// <summary>Creates an in-memory event journal with independent topic and consumer state.</summary>
        public MemoryEventStreamingService(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Disposes of all topics on the event streamer
        /// </summary>
        public void Dispose()
        {
            lock (_topicsByTopicName)
            {
                foreach (var topic in _topicsByTopicName.Values)
                {
                    topic.Dispose();
                }
            }
        }

        /// <summary>Stops and drains topic consumers and releases journal resources.</summary>
        public async ValueTask DisposeAsync()
        {
            Topic[] topics;
            lock (_topicsByTopicName)
            {
                topics = _topicsByTopicName.Values.ToArray();
            }

            await Task.WhenAll(
                topics.Select(topic => topic.DisposeAsync().AsTask()))
                .ConfigureAwait(false);

            lock (_topicsByTopicName)
            {
                _topicsByTopicName.Clear();
            }

            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public Task<IEnumerable<(string, long, List<IEventMessage>)>>
            GetCurrentTopicsAsync(CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            lock (_topicsByTopicName)
            {
                var topics = new List<(string, long, List<IEventMessage>)>();

                foreach (var topic in _topicsByTopicName)
                {
                    topics.Add((topic.Key, topic.Value.SubscriberCount, topic.Value.GetCurrentEvents().ToList()));
                }

                return Task.FromResult<
                    IEnumerable<(string, long, List<IEventMessage>)>>(topics);
            }
        }

        /// <summary>
        /// Registers an event publisher with the topic name
        /// </summary>
        public Task RegisterEventTopicAsync(string topicName, string description, CancellationToken token)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));

            lock (_topicsByTopicName)
            {
                // Create if necessary a topic for this topic name
                if (!_topicsByTopicName.TryGetValue(topicName, out Topic topic))
                {
                    _topicsByTopicName.Add(topicName, new Topic(_logger, this, topicName, description));
                    TopicCreated?.Invoke(this, new TopicEventArgs(topicName));
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Unregisters an event publisher with the topic name
        /// </summary>
        public Task UnregisterEventTopicAsync(string topicName)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));

            lock (_topicsByTopicName)
            {
                if (_topicsByTopicName.Remove(topicName))
                {
                    TopicDeleted?.Invoke(this, new TopicEventArgs(topicName));
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>Raises the notification for an appended in-memory event batch.</summary>
        protected void RaiseEventsAppended(string topicName, IReadOnlyList<IEventMessage> events)
        {
            EventsAppended?.Invoke(this, new TopicEventsEventArgs(topicName, events));
        }

        /// <summary>Raises the notification for a consumed in-memory event batch.</summary>
        protected void RaiseEventsConsumed(string topicName, IReadOnlyList<IEventMessage> events)
        {
            foreach (IEventMessage eventMessage in events)
            {
                EventJournalMetrics.DeliveryCompleted(
                    ShortDescription,
                    "In-Memory",
                    "processed",
                    eventMessage.CreationTime);
            }
            EventsConsumed?.Invoke(this, new TopicEventsEventArgs(topicName, events));
        }

        /// <summary>
        /// Registers an event handler with the topic name
        /// It is not necessary to have a publisher yet registered to subscribe to an event route
        /// </summary>
        public Task RegisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token) where TEvent : IEventMessage
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            ArgumentNullException.ThrowIfNull(eventHandler, nameof(eventHandler));

            lock (_topicsByTopicName)
            {
                if (!_topicsByTopicName.TryGetValue(topicName, out Topic eventTopic))
                {
                    RegisterEventTopicAsync(topicName, "<TODO>", token);
                }

                _topicsByTopicName[topicName].SubscribeEventHandlerToTopic(eventHandler);
                TopicEventHandlerRegistered?.Invoke(this, new TopicEventArgs(topicName));
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Unregisters an event handler with the topic name
        /// </summary>
        public Task UnregisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token) where TEvent : IEventMessage
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            ArgumentNullException.ThrowIfNull(eventHandler, nameof(eventHandler));

            lock (_topicsByTopicName)
            {
                // Create if necessary a publishers list for this event key
                if (_topicsByTopicName.TryGetValue(topicName, out Topic eventTopic))
                {
                    eventTopic.UnsubscribeEventHandlerFromTopic(eventHandler);
                    TopicEventHandlerUnregistered?.Invoke(this, new TopicEventArgs(topicName));
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Commits the current consumer stream position for the topic
        /// On reversion this is the position to which the event position will return
        /// </summary>
        public Task CommitTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));

            lock (_topicsByTopicName)
            {
                if (_topicsByTopicName.TryGetValue(topicName, out Topic eventTopic))
                {
                    eventTopic.CommitStreamPosition(eventConsumer);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Reverts the stream position to the last committed position
        /// </summary>
        public Task RevertTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventHandler)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));

            lock (_topicsByTopicName)
            {
                if (_topicsByTopicName.TryGetValue(topicName, out Topic eventTopic))
                {
                    eventTopic.RevertStreamPosition(eventHandler);
                }
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Appends events to a topic
        /// </summary>
        public Task AppendEventsAsync(string topicName, IEnumerable<IEventMessage> events, CancellationToken _)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            List<IEventMessage> eventList = events.ToList();
            Stopwatch stopwatch = Stopwatch.StartNew();
            using Activity activity = MessagingTelemetry.StartProducerActivity(
                "EventStreamingService.AppendBatch",
                TelemetryLevel.Detailed,
                "In-Memory Event Journal",
                "In-Memory",
                topicName,
                eventList.Count);
            MessagingTelemetry.InjectCurrentContext(eventList);

            lock (_topicsByTopicName)
            {
                if (_topicsByTopicName.TryGetValue(topicName, out Topic topic))
                {
                    topic.AppendEventMessages(eventList);
                }
            }

            EventJournalMetrics.AppendCompleted(
                ShortDescription,
                "In-Memory",
                "success",
                stopwatch.Elapsed,
                eventList.Count);

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RegisterAllTopicEventsHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            ArgumentNullException.ThrowIfNull(eventHandler, nameof(eventHandler));

            lock (_topicsByTopicName)
            {
                if (!_topicsByTopicName.ContainsKey(topicName))
                {
                    RegisterEventTopicAsync(topicName, "<TODO>", token);
                }

                _topicsByTopicName[topicName].SubscribeAllEventsHandlerToTopic(eventHandler);
                TopicEventHandlerRegistered?.Invoke(this, new TopicEventArgs(topicName));
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task UnregisterAllTopicsEventHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            ArgumentNullException.ThrowIfNull(eventHandler, nameof(eventHandler));

            lock (_topicsByTopicName)
            {
                if (_topicsByTopicName.TryGetValue(topicName, out Topic eventTopic))
                {
                    eventTopic.UnsubscribeAllEventsHandlerFromTopic(eventHandler);
                    TopicEventHandlerUnregistered?.Invoke(this, new TopicEventArgs(topicName));
                }
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ConnectAsync()
        {
            // Nothing to do in memory case
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DisconnectAsync()
        {
            // Nothing to do in memory case
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StreamEventsAsync(IEventHandlerHostObject eventConsumer, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(eventConsumer, nameof(eventConsumer));

            Topic[] topics;
            lock (_topicsByTopicName)
            {
                // Take a snapshot; no async work in the lock
                topics = _topicsByTopicName.Values.ToArray();
            }

            // Fan out starts in parallel; each should observe 'token'
            var tasks = topics.Select(t => t.StartConsumingAsync(eventConsumer, token));

            // If one fails, you'll see an AggregateException here
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task StopStreamingEventsAsync(IEventHandlerHostObject eventConsumer, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(eventConsumer, nameof(eventConsumer));

            Topic[] topics;
            lock (_topicsByTopicName)
            {
                // Take a snapshot; no async work in the lock
                topics = _topicsByTopicName.Values.ToArray();
            }

            // Fan out starts in parallel; each should observe 'token'
            var tasks = topics.Select(t => t.StopConsumingAsync(eventConsumer, token));

            // If one fails, you'll see an AggregateException here
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
