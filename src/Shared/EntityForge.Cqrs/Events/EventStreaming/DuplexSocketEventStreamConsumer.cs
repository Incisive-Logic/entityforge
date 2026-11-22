using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Options;

using EntityForge.Cqrs.Events;
using EntityForge.Cqrs.Events.EventStreaming;
using EntityForge.Cqrs.Events.Serialization;
using EntityForge.Cqrs.Exceptions;

using EntityForge.Utility;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>
    /// Represents an event message that has been received from the server
    /// Includes both the event payload and the type information
    /// </summary>
    public class EventMessage
    {
        /// <summary>Creates an event message carrying event identity and metadata.</summary>
        public EventMessage(string topicName, byte[] messagePayload, string typeName)
        {
            TopicName = topicName;
            MessagePayload = messagePayload;
            TypeName = typeName;
        }

        /// <summary>
        /// The name of the topic that this event message has been consumed from
        /// </summary>
        public string TopicName
        {
            get;
        }

        /// <summary>
        /// The serialized event message payload
        /// </summary>
        public byte[] MessagePayload
        {
            get;
        }

        /// <summary>
        /// The type name of the event message
        /// </summary>
        public string TypeName
        {
            get;
        }
    }

    /// <summary>
    /// A client that consumes events via a duplex socket service. As only a consumer,
    /// cannot create or remove topics.
    /// </summary>
    public class DuplexSocketEventStreamConsumer : EventStreamingServiceBase, IDuplexSocketMessageTarget
    {
        private static readonly TimeSpan MessageRetryDelay = TimeSpan.FromMilliseconds(100);

        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly IDuplexSocketService _duplexSocketService;
        private readonly IGrpcEventSerializer _eventSerializer;
        private readonly IOptions<EventStreamingOptions> _options;

        private readonly List<IEventMessage> _eventMessagesBatch = new List<IEventMessage>();

        private readonly Dictionary<string, HashSet<IEventHandlerHostObject>> _eventHandlerHostObjectsByTopicName =
            new Dictionary<string, HashSet<IEventHandlerHostObject>>();

        private readonly HashSet<string> _remotelySubscribedTopicNames = new HashSet<string>();
        private readonly object _topicSubscriptionsLock = new object();

        private Task _dispatchMessagesTask;
        private Task _connectionStateTask = Task.CompletedTask;

        private readonly Channel<object> _dispatcherEventsChannel;
        private readonly CancellationTokenSource _dispatcherStoppingToken = new CancellationTokenSource();
        private readonly SemaphoreSlim _connectionStateLock = new(1, 1);
        private readonly EventHandler<ConnectionChangedEventArgs> _connectionChangedHandler;

        /// <summary>Creates a remote event consumer using the supplied duplex transport and dispatch configuration.</summary>
        public DuplexSocketEventStreamConsumer(Microsoft.Extensions.Logging.ILogger<DuplexSocketEventStreamConsumer> logger, IDuplexSocketService duplexSocketService, IGrpcEventSerializer eventSerializer, IOptions<EventStreamingOptions> options)
            : base(logger)
        {
            _logger = logger ?? NullLogger<DuplexSocketEventStreamConsumer>.Instance;
            _duplexSocketService = duplexSocketService ?? throw new ArgumentNullException(nameof(duplexSocketService));
            _eventSerializer = eventSerializer ?? throw new ArgumentNullException(nameof(eventSerializer));
            _options = options ?? throw new ArgumentNullException(nameof(options));

            _dispatcherEventsChannel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions()
            {
                SingleReader = true,
                SingleWriter = false
            });

            _connectionChangedHandler = OnDuplexSocketConnectionChanged;
            duplexSocketService.ConnectionChanged += _connectionChangedHandler;
            _connectionStateTask = RefreshConnectionStateAsync();
        }

        private async Task RefreshConnectionStateAsync()
        {
            await _connectionStateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                OnConnectionChanged(_duplexSocketService.IsConnected);

                if (_duplexSocketService.IsConnected)
                {
                    _dispatchMessagesTask ??= DispatchEventMessageToHandlersAsync();
                    return;
                }

                if (_dispatchMessagesTask != null)
                {
                    _dispatcherStoppingToken.Cancel();

                    try
                    {
                        await _dispatchMessagesTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Graceful dispatcher shutdown
                    }

                    _dispatchMessagesTask = null;
                    _dispatcherStoppingToken.TryReset();
                }
            }
            finally
            {
                _connectionStateLock.Release();
            }
        }

        private void OnDuplexSocketConnectionChanged(object sender, ConnectionChangedEventArgs e)
        {
            _connectionStateTask = ObserveConnectionStateChangeAsync();
        }

        private async Task ObserveConnectionStateChangeAsync()
        {
            try
            {
                await RefreshConnectionStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to update duplex event-consumer connection state.");
            }
        }

        /// <summary>Unregisters the remote consumer and releases its owned subscriptions.</summary>
        public override async ValueTask DisposeAsync()
        {
            _duplexSocketService.ConnectionChanged -= _connectionChangedHandler;
            _dispatcherStoppingToken.Cancel();
            _dispatcherEventsChannel.Writer.TryComplete();

            await _connectionStateTask.ConfigureAwait(false);
            if (_dispatchMessagesTask != null)
            {
                try
                {
                    await _dispatchMessagesTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Graceful dispatcher shutdown
                }
            }

            await base.DisposeAsync().ConfigureAwait(false);
            _dispatcherStoppingToken.Dispose();
            _connectionStateLock.Dispose();
        }

        /// <summary>Checks whether an incoming envelope is a supported event feedback message.</summary>
        public bool AcceptsMessage(Type messageType)
        {
            return messageType == typeof(AcknowledgementResponse) || messageType == typeof(TopicEventFeedbackBatch);
        }

        /// <summary>Dispatches accepted topic-event feedback to registered consumers.</summary>
        public void OnMessageReceived(object messageObject)
        {
            // Get off the handler thread as quickly as possible so as to allow the dispatcher to continue
            // dispatching to other targets
            _dispatcherEventsChannel.Writer.TryWrite(messageObject);
        }

        private async Task DispatchEventMessageToHandlersAsync()
        {
            while (_duplexSocketService.IsConnected && !_dispatcherStoppingToken.IsCancellationRequested)
            {
                // Consume the incoming message objects and process serialially, in order
                object messageObject = await _dispatcherEventsChannel.Reader.ReadAsync(_dispatcherStoppingToken.Token);
                {
                    if (messageObject is AcknowledgementResponse acknowledgementResponse)
                    {
                        // Nothing to do apart from log, for the time being
                    }
                    else if (messageObject is TopicEventFeedbackBatch topicEventFeedbackBatch)
                    {
                        foreach (TopicEventFeedback topicEventFeedback in topicEventFeedbackBatch.EventBatch)
                        {
                            EventMessage eventMessage = JsonSerializer.Deserialize<EventMessage>(topicEventFeedback.EventData);

                            // Process the incoming message into a topic event, and then dispatch the event to any registered subscribers
                            var eventType = Type.GetType(eventMessage.TypeName);

                            if (eventType == null)
                            {
                                System.Diagnostics.Trace.TraceWarning($"Unknown event stream event type {eventMessage.TypeName} has been ignored.");
                                return;
                            }

                            try
                            {
                                IEventMessage deserializedEvent = _eventSerializer.Deserialize(eventMessage.MessagePayload, eventType);

                                if (_eventHandlerHostObjectsByTopicName.TryGetValue(eventMessage.TopicName, out HashSet<IEventHandlerHostObject> eventHandlerHostObjects))
                                {
                                    foreach (IEventHandlerHostObject eventHandlerHostObject in eventHandlerHostObjects)
                                    {
                                        MessageHandlingResult result;
                                        do
                                        {
                                            // The socket protocol has no remote acknowledgement yet, so retain the
                                            // received event in this ordered dispatcher until local handling succeeds.
                                            result = await DispatchEventMessageAsync(
                                                eventHandlerHostObject,
                                                eventMessage.TopicName,
                                                deserializedEvent);

                                            if (result == MessageHandlingResult.Retry)
                                            {
                                                await Task.Delay(
                                                    MessageRetryDelay,
                                                    _dispatcherStoppingToken.Token);
                                            }
                                        }
                                        while (result == MessageHandlingResult.Retry);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.TraceError($"Could not deserialize event stream type {eventType} ('{ex.Message}'). Ignored.");

                                if (ex.InnerException != null)
                                {
                                    System.Diagnostics.Trace.TraceError($" Inner exception: {ex.InnerException.ToString()}");
                                }
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Trace.TraceWarning($"Message type {messageObject.GetType()} is not handled. Ignored.");
                    }
                }
            }

            System.Diagnostics.Trace.TraceInformation("Event message dispatcher has stopped.");
        }

        /// <inheritdoc />
        public override Task CommitTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override Task RevertTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        protected override async Task SubscribeToEventTopicAsync(string topicName, IEventHandlerHostObject eventHandlerHostObject, CancellationTokenSource source)
        {
            try
            {
                bool subscribeRemotely;
                lock (_topicSubscriptionsLock)
                {
                    if (!_eventHandlerHostObjectsByTopicName.TryGetValue(topicName, out HashSet<IEventHandlerHostObject> handlers))
                    {
                        handlers = new HashSet<IEventHandlerHostObject>();
                        _eventHandlerHostObjectsByTopicName.Add(topicName, handlers);
                    }

                    handlers.Add(eventHandlerHostObject);
                    subscribeRemotely = _remotelySubscribedTopicNames.Add(topicName);
                }

                if (subscribeRemotely)
                {
                    await _duplexSocketService.SendAsync(
                        SerializedMessage.Serialize(new SubscribeToEventTopicRequest
                        {
                            TopicName = topicName,
                            Subscribe = true
                        }),
                        source.Token);

                }
            }
            catch (DuplexSocketServiceException ex)
            {
                lock (_topicSubscriptionsLock)
                {
                    _remotelySubscribedTopicNames.Remove(topicName);
                }
                throw new EventStreamingException($"Could not subscribe to topic '{topicName}'", ex);
            }
        }

        /// <inheritdoc />
        protected override async Task InternalConnectAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _duplexSocketService.ConnectAsync(cancellationToken);
            }
            catch (DuplexSocketServiceException ex)
            {
                throw new EventStreamingException("Could not connect to duplex socket service", ex);
            }
        }

        /// <inheritdoc />
        protected override async Task InternalDisconnectAsync()
        {
            try
            {
                await _duplexSocketService.CloseAsync(CancellationToken.None);
            }
            catch (DuplexSocketServiceException ex)
            {
                // Do not throw but log this error
                System.Diagnostics.Trace.TraceError($"Could not disconnect from duplex socket service: {ex.Message}");
            }
        }

        /// <inheritdoc />
        protected override Task InternalAppendEventsAsync(string topicName, IEnumerable<IEventMessage> events, CancellationToken _)
        {
            throw new NotSupportedException("Only event consumption is supported");
        }

        /// <inheritdoc />
        public override Task<IEnumerable<(string, long, List<IEventMessage>)>>
            GetCurrentTopicsAsync(CancellationToken token = default)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public override Task RegisterEventTopicAsync(string topicName, string description, CancellationToken token)
        {
            throw new NotSupportedException("Only event consumption is supported");
        }

        /// <inheritdoc />
        public override Task UnregisterEventTopicAsync(string topicName)
        {
            throw new NotSupportedException("Only event consumption is supported");
        }

        /// <inheritdoc />
        protected override async Task UnsubscribeFromEventTopicAsync(string topicName, IEventHandlerHostObject eventHandler, CancellationTokenSource source)
        {
            bool unsubscribeRemotely = false;
            lock (_topicSubscriptionsLock)
            {
                if (_eventHandlerHostObjectsByTopicName.TryGetValue(topicName, out HashSet<IEventHandlerHostObject> handlers))
                {
                    handlers.Remove(eventHandler);
                    if (handlers.Count == 0)
                    {
                        _eventHandlerHostObjectsByTopicName.Remove(topicName);
                        unsubscribeRemotely = _remotelySubscribedTopicNames.Remove(topicName);
                    }
                }
            }

            if (unsubscribeRemotely && _duplexSocketService.IsConnected)
            {
                await _duplexSocketService.SendAsync(
                    SerializedMessage.Serialize(new SubscribeToEventTopicRequest
                    {
                        TopicName = topicName,
                        Subscribe = false
                    }),
                    source.Token);
            }
        }

        void IDuplexSocketMessageTarget.OnConnectionChanged(bool isConnected)
        {
            if (!isConnected)
            {
                lock (_topicSubscriptionsLock)
                {
                    _remotelySubscribedTopicNames.Clear();
                }
            }

            base.OnConnectionChanged(isConnected);
        }

        /// <inheritdoc />
        public override string ShortDescription => "Duplex Socket Event Consumer";

        /// <summary>Gets the transport description presented to diagnostics or user interfaces.</summary>
        public override string Description => "Duplex Socket Service Event Consumer";

        /// <summary>Gets the transport protocol identifier.</summary>
        public override string TransportProtocol => "TCP";
    }
}
