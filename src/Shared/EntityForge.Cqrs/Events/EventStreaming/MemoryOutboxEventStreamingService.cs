using EntityForge.Cqrs.Exceptions;
using EntityForge.Cqrs.Events;
using EntityForge.Cqrs.Outbox;
using EntityForge.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>
    /// Persists integration events to an outbox before relaying them to an event journal.
    /// </summary>
    public class OutboxEventStreamingService :
        IOutboxEventStreamingService,
        IEventJournalRetentionInspector
    {
        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicEventHandlerRegistered;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicEventHandlerUnregistered;

        /// <inheritdoc />
        public event EventHandler<TopicEventsEventArgs> EventsConsumed;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicCreated;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicDeleted;

        /// <inheritdoc />
        public event EventHandler<TopicEventsEventArgs> EventsAppended;

        /// <inheritdoc />
        public event EventHandler<TopicEventsEventArgs> EventsPersistedToOutbox;

        /// <summary>Occurs when the underlying event-stream connection changes.</summary>
        public event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;

        private readonly IEventStreamingService _eventStreamingService;
        private readonly IIntegrationEventOutboxWriterStore _outboxWriter;
        private readonly IntegrationEventOutboxRelay _relay;

        /// <summary>Gets whether the underlying event stream is connected.</summary>
        public bool IsConnected => _eventStreamingService.IsConnected;

        /// <inheritdoc />
        public IDirectEventStreamingService DirectEventStreamingService
        {
            get;
        }

        /// <inheritdoc />
        public string ShortDescription => $"Outbox -> {_eventStreamingService.ShortDescription}";

        /// <inheritdoc />
        public EventJournalRetentionCapabilities RetentionCapabilities =>
            (_eventStreamingService as IEventJournalRetentionInspector)?
                .RetentionCapabilities ??
            EventJournalRetentionCapabilities.None;

        /// <summary>Creates an event-stream wrapper that persists outgoing events to its configured outbox.</summary>
        public OutboxEventStreamingService(
            IDirectEventStreamingService eventStreamingService,
            IIntegrationEventOutboxWriterStore outboxWriter,
            IIntegrationEventOutboxRelayStore outboxRelayStore,
            Serialization.IEventSerializer serializer = null,
            Contracts.IIntegrationEventContractRegistry contractRegistry = null,
            IntegrationEventOutboxRelayOptions options = null)
        {
            if (eventStreamingService == null)
            {
                throw new ArgumentNullException(nameof(eventStreamingService));
            }

            _eventStreamingService = eventStreamingService;
            DirectEventStreamingService = eventStreamingService;
            _outboxWriter = outboxWriter ?? throw new ArgumentNullException(nameof(outboxWriter));
            ArgumentNullException.ThrowIfNull(outboxRelayStore);

            _eventStreamingService.ConnectionChanged += (s, e) => ConnectionChanged?.Invoke(s, e);
            _eventStreamingService.TopicEventHandlerRegistered += (s, e) => TopicEventHandlerRegistered?.Invoke(s, e);
            _eventStreamingService.TopicEventHandlerUnregistered += (s, e) => TopicEventHandlerUnregistered?.Invoke(s, e);
            _eventStreamingService.EventsConsumed += (s, e) => EventsConsumed?.Invoke(s, e);
            _eventStreamingService.TopicCreated += (s, e) => TopicCreated?.Invoke(s, e);
            _eventStreamingService.TopicDeleted += (s, e) => TopicDeleted?.Invoke(s, e);
            _eventStreamingService.EventsAppended += (s, e) => EventsAppended?.Invoke(s, e);
            _relay = new IntegrationEventOutboxRelay(
                outboxRelayStore,
                eventStreamingService,
                serializer,
                contractRegistry,
                options ?? new IntegrationEventOutboxRelayOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    InitialRetryDelay = TimeSpan.FromMilliseconds(10),
                    MaximumRetryDelay = TimeSpan.FromMilliseconds(100)
                });
            _ = _relay.StartAsync();
        }

        /// <summary>Stops the outbox-backed service and releases its owned resources.</summary>
        public void Dispose()
        {
            _relay.RequestStop();
            _eventStreamingService.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>Asynchronously stops relay and subscription work owned by this service.</summary>
        public async ValueTask DisposeAsync()
        {
            await _relay.DisposeAsync().ConfigureAwait(false);
            await _eventStreamingService.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public async Task AppendEventsAsync(string topicName, IEnumerable<IEventMessage> events, CancellationToken token)
        {
            IReadOnlyList<IIntegrationEvent> eventList = events
                .Select(message => message as IIntegrationEvent ??
                    throw new ArgumentException(
                        $"Outbox message '{message?.GetType()}' is not an integration event.", nameof(events)))
                .ToArray();
            await _outboxWriter.AppendAsync(topicName, eventList, token).ConfigureAwait(false);
            EventsPersistedToOutbox?.Invoke(this, new TopicEventsEventArgs(topicName, events.ToList()));
        }

        /// <inheritdoc />
        public Task CommitTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer)
        {
            // Just forward straight on to the real event streaming service
            return _eventStreamingService.CommitTopicStreamPositionAsync(topicName, eventConsumer);
        }

        /// <inheritdoc />
        public Task<IEnumerable<(string, long, List<IEventMessage>)>>
            GetCurrentTopicsAsync(CancellationToken token = default)
        {
            return _eventStreamingService.GetCurrentTopicsAsync(token);
        }

        /// <inheritdoc />
        public Task RegisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token) where TEvent : IEventMessage
        {
            return _eventStreamingService.RegisterEventHandlerAsync<TEvent>(topicName, eventHandler, token);
        }

        /// <inheritdoc />
        public Task RegisterEventTopicAsync(string topicName, string description, CancellationToken token)
        {
            return _eventStreamingService.RegisterEventTopicAsync(topicName, description, token);
        }

        /// <inheritdoc />
        public Task RevertTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer)
        {
            return _eventStreamingService.RevertTopicStreamPositionAsync(topicName, eventConsumer);
        }

        /// <inheritdoc />
        public Task UnregisterEventTopicAsync(string topicName)
        {
            return _eventStreamingService.UnregisterEventTopicAsync(topicName);
        }

        /// <inheritdoc />
        public Task RegisterAllTopicEventsHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token)
        {
            return _eventStreamingService.RegisterAllTopicEventsHandlerAsync(topicName, eventHandler, token);
        }

        /// <inheritdoc />
        public Task UnregisterAllTopicsEventHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token)
        {
            return _eventStreamingService.UnregisterAllTopicsEventHandlerAsync(topicName, eventHandler, token);
        }

        /// <inheritdoc />
        public Task ConnectAsync()
        {
            return _eventStreamingService.ConnectAsync();
        }

        /// <inheritdoc />
        public Task UnregisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token) where TEvent : IEventMessage
        {
            return _eventStreamingService.UnregisterEventHandlerAsync(topicName, eventHandler, token);
        }

        /// <inheritdoc />
        public Task StreamEventsAsync(IEventHandlerHostObject eventConsumer, CancellationToken token)
        {
            return _eventStreamingService.StreamEventsAsync(eventConsumer, token);
        }

        /// <inheritdoc />
        public Task StopStreamingEventsAsync(IEventHandlerHostObject eventConsumer, CancellationToken token)
        {
            return _eventStreamingService.StopStreamingEventsAsync(eventConsumer, token);
        }

        /// <inheritdoc />
        public Task DisconnectAsync()
        {
            return _eventStreamingService.DisconnectAsync();
        }

        /// <inheritdoc />
        public Task<EventJournalTopicBounds> GetTopicBoundsAsync(
            string topicName,
            CancellationToken token)
        {
            return _eventStreamingService is IEventJournalRetentionInspector inspector
                ? inspector.GetTopicBoundsAsync(topicName, token)
                : Task.FromResult<EventJournalTopicBounds>(null);
        }

        /// <inheritdoc />
        public Task<EventJournalConsumerCursorStatus> GetConsumerCursorStatusAsync(
            string topicName,
            IEventHandlerHostObject eventConsumer,
            CancellationToken token)
        {
            return _eventStreamingService is IEventJournalRetentionInspector inspector
                ? inspector.GetConsumerCursorStatusAsync(
                    topicName,
                    eventConsumer,
                    token)
                : Task.FromResult(new EventJournalConsumerCursorStatus(
                    EventJournalCursorHealth.Unknown,
                    CommittedPosition: null,
                    TopicBounds: null));
        }
    }
}
