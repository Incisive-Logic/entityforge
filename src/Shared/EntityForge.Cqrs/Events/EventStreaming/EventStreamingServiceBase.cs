using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using EntityForge.Cqrs.Exceptions;
using EntityForge.Cqrs.Observability;
using EntityForge.Telemetry;
using EntityForge.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>
    /// Base class for event streaming services. This class provides the basic functionality for subscribing to topics
    /// and maintaining a connection to the event streaming service.
    /// </summary>
    public abstract class EventStreamingServiceBase : IEventStreamingService
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

        /// <summary>Occurs when the backend connection state changes.</summary>
        public event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;

        private long _isConnectedState = (int)ConnectionState.Disconnected;

        private enum ConnectionState
        {
            Disconnected = 0,
            Connected = 1
        }

        /// <summary>
        /// Metadata class that holds information about whether the topic has currently been successfully
        /// subscribed to, and the event consumers that are subscribed to it
        ///
        /// RegisterTopic Handler - add to topic consumers entry, IsConsuming, IsSubscribed = no
        /// StartConsuming(handler) sets the consumer IsConsuming = true.
        /// On next subscription retry…if successful IsSubscribed = true
        /// </summary>
        protected class TopicSubscriptionInfo
        {
            /// <summary>
            /// Metadata class that holds the event handler methods for each topic that the consumer is subscribed to
            /// Note: Each handler has its own cursor on the event topic and so each handler has its OWN subscription
            /// </summary>
            public class RegisteredConsumerInfo
            {
                /// <summary>Creates empty typed-handler and all-event-handler collections for one consumer subscription.</summary>
                public RegisteredConsumerInfo()
                {
                    HandlerDelegatesByEventType = new Dictionary<Type, List<(IEventHandlerHostObject, MessageHandlerDelegate)>>();
                    AllEventTypesHandlerMethods = new HashSet<IEventHandler>();
                }

                /// <summary>
                /// The handler methods on the event handler object by the event type they handle
                /// </summary>
                public Dictionary<Type, List<(IEventHandlerHostObject, MessageHandlerDelegate)>> HandlerDelegatesByEventType
                {
                    get;
                }

                /// <summary>
                /// The handler methods on the event handler objects which accept any event type
                /// </summary>
                public HashSet<IEventHandler> AllEventTypesHandlerMethods
                {
                    get;
                }

                /// <summary>
                /// Indicates whether the consumer is currently subscribed to the topic on the event streaming service
                /// </summary>
                public bool IsStreamingEvents
                {
                    get; set;
                }

                /// <summary>
                /// The event consumer object, that holds the event handler methods for the event topic
                /// </summary>
                public IEventHandlerHostObject EventHandlerHostObject
                {
                    get; set;
                }

                /// <summary>
                /// The cancellation token source for the event consumer, called to stop consuming events
                /// </summary>
                public CancellationTokenSource CancellationTokenSource
                {
                    get; set;
                }
            }

            /// <summary>Creates subscription state for the named topic, tracking consumer hosts by reference identity.</summary>
            public TopicSubscriptionInfo(string topicName)
            {
                TopicName = topicName;
                ConsumerInfoByEventHandlerHosts = new Dictionary<IEventHandlerHostObject, RegisteredConsumerInfo>(ReferenceEqualityComparer.Instance);
            }

            /// <summary>Marks retained consumers as no longer subscribed so they can be reconnected.</summary>
            public void ResetSubscriptionState()
            {
                foreach (var consumerInfo in ConsumerInfoByEventHandlerHosts.Values)
                {
                    consumerInfo.IsStreamingEvents = false;
                }
            }

            /// <summary>
            /// The name of the event topic that is subscribed to
            /// </summary>
            public string TopicName
            {
                get;
            }

            /// <summary>
            /// The consumers - event handlers - that are subscribed to the topic
            /// </summary>
            public Dictionary<IEventHandlerHostObject, TopicSubscriptionInfo.RegisteredConsumerInfo> ConsumerInfoByEventHandlerHosts
            {
                get;
            }
        }

        private readonly Microsoft.Extensions.Logging.ILogger _logger;

        private PeriodicTimer _connectionRetryTimer;
        private Task _connectionRetryTask;
        private CancellationTokenSource _connectionRetryTokenSource;
        private CancellationTokenSource _combinedConnectionRetryTokenSource;

        private PeriodicTimer _subscriptionRetryTimer;
        private Task _subscriptionRetryTask;
        private CancellationTokenSource _subscriptionRetryTokenSource;
        private CancellationTokenSource _combinedSubscriptionRetryTokenSource;

        private readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();  // Service wide token

        private readonly SemaphoreSlim _eventStreamingLock = new SemaphoreSlim(1, 1);

        private readonly Dictionary<string, TopicSubscriptionInfo> _topicSubscriptionInfoByTopicName = new Dictionary<string, TopicSubscriptionInfo>();
        private readonly HashSet<IEventHandlerHostObject> _streamActiveEventHandlerHostObjects = new HashSet<IEventHandlerHostObject>();

        private readonly object _disposeSync = new();
        private Task _beginDisposeTask;
        private int _disposeCompleted;

        /// <summary>Creates event-stream subscription tracking with the supplied logger and service options.</summary>
        protected EventStreamingServiceBase(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>Stops the service and releases its owned subscriptions and resources.</summary>
        public void Dispose()
        {
            _cts.Cancel();
        }

        /// <summary>Asynchronously stops the service and drains owned subscriptions.</summary>
        public virtual async ValueTask DisposeAsync()
        {
            await BeginDisposeAsync().ConfigureAwait(false);
            CompleteDispose();
        }

        /// <summary>Begins asynchronous subscription and connection shutdown.</summary>
        protected Task BeginDisposeAsync()
        {
            lock (_disposeSync)
            {
                return _beginDisposeTask ??= BeginDisposeCoreAsync();
            }
        }

        private async Task BeginDisposeCoreAsync()
        {
            _cts.Cancel();
            await StopRetryTimersAsync().ConfigureAwait(false);
        }

        /// <summary>Completes resource cleanup after asynchronous shutdown.</summary>
        protected void CompleteDispose()
        {
            if (Interlocked.Exchange(ref _disposeCompleted, 1) == 1)
            {
                return;
            }

            _cts.Dispose();
            // A transport callback may already be queued while shutdown completes. Keep this
            // lightweight managed semaphore available so a stale callback can observe the
            // cleared subscription state instead of failing with ObjectDisposedException.
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Called by implementors when the connection state to the event streaming service changes
        /// </summary>
        /// <param name="isConnected">Whether or not the event streaming service is considered connected</param>
        protected void OnConnectionChanged(bool isConnected)
        {
            long newConnectionState = isConnected ?
                (long)ConnectionState.Connected : (long)ConnectionState.Disconnected;

            // Set the connected state
            long oldConnectionState = Interlocked.Exchange(ref _isConnectedState, newConnectionState);

            if (oldConnectionState != newConnectionState)
            {
                if (isConnected)
                {
                    _logger.LogDebug(EventIds.Connect, "Connected to {Service} event streaming service.", ShortDescription);
                }
                else
                {
                    _logger.LogDebug(EventIds.Disconnect, "Disconnected from {Service} event streaming service.", ShortDescription);
                }

                ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs() { IsConnected = isConnected });
            }
        }

        private async Task ConnectionTimerRetryAsync()
        {
            try
            {
                while (await _connectionRetryTimer.WaitForNextTickAsync(_combinedConnectionRetryTokenSource.Token))
                {
                    if (IsConnected)
                    {
                        continue;        // No need to connect if already connected
                    }

                    try
                    {
                        // Now wait to reconnect
                        await InternalConnectAsync(_combinedConnectionRetryTokenSource.Token);

                        // Indicate we have connected
                        OnConnectionChanged(true);
                    }
                    catch (EventStreamingException ex)
                    {
                        string errorMessage = ex.Message;

                        if (ex.InnerException != null)
                        {
                            errorMessage += $" ({ex.InnerException.Message})";
                        }

                        // Log the error but we will retry
                        _logger.LogError(EventIds.Connect, "Failed to reconnect to the {Service} event streaming service ('{Error}'). Will retry...",
                            ShortDescription, errorMessage);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful exit
            }
        }

        private async Task SubscriptionRetryTimerAsync()
        {
            try
            {
                while (await _subscriptionRetryTimer.WaitForNextTickAsync(_combinedSubscriptionRetryTokenSource.Token))
                {
                    if (!IsConnected)
                    {
                        // If we are no longer connected, we need to ensure that all topics are marked as unsubscribed
                        // so that when we connect again, we go to subscribe to event streaming active topics
                        await ResetTopicSubscriptionStatesAsync();
                        continue;
                    }

                    // Get all registered event handler hosts that are currently streaming active, but are not currently subscribed
                    // We need to subscribe to these
                    var unsubscribedRegisteredConsumerInfos = await GetUnsubscribedStreamingActiveConsumers();

                    // Subscribe outside of sempahore lock so as not to hold up other operations
                    foreach ((string, List<TopicSubscriptionInfo.RegisteredConsumerInfo>)
                        unsubscribedRegisteredConsumerInfo in unsubscribedRegisteredConsumerInfos)
                    {
                        foreach (var consumerInfo in unsubscribedRegisteredConsumerInfo.Item2)
                        {
                            // Go to subscribe the handler ot the event topic
                            await SubscribeToEventTopicAsync(unsubscribedRegisteredConsumerInfo.Item1, consumerInfo);
                        }
                    }

                    // Now find any topics that are currently streaming, but are not currently active
                    // These need to be unsubscribed
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful exit
            }
        }

        private async Task StartRetryTimersAsync()
        {
            if (!AutoConnect)
            {
                return;
            }

            await _eventStreamingLock.WaitAsync();

            try
            {
                if (_connectionRetryTimer == null)
                {
                    // Have a per timer token source so that we can cancel the timer tasks without cancelling the overall service token
                    _connectionRetryTokenSource = new CancellationTokenSource();
                    _combinedConnectionRetryTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _connectionRetryTokenSource.Token);

                    _connectionRetryTimer = new PeriodicTimer(RetryInterval);
                    _connectionRetryTask = ConnectionTimerRetryAsync();
                }

                if (_subscriptionRetryTimer == null)
                {
                    // Have a per timer token source so that we can cancel the timer tasks without cancelling the overall service token
                    _subscriptionRetryTokenSource = new CancellationTokenSource();
                    _combinedSubscriptionRetryTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, _subscriptionRetryTokenSource.Token);

                    _subscriptionRetryTimer = new PeriodicTimer(RetryInterval);
                    _subscriptionRetryTask = SubscriptionRetryTimerAsync();
                }
            }
            finally
            {
                _eventStreamingLock.Release();
            }
        }

        private async Task StopRetryTimersAsync()
        {
            PeriodicTimer connectionTimer = null;
            Task connectionTask = null;
            CancellationTokenSource connectionTokenSource = null;
            CancellationTokenSource combinedConnectionTokenSource = null;
            PeriodicTimer subscriptionTimer = null;
            Task subscriptionTask = null;
            CancellationTokenSource subscriptionTokenSource = null;
            CancellationTokenSource combinedSubscriptionTokenSource = null;

            await _eventStreamingLock.WaitAsync();
            try
            {
                connectionTimer = _connectionRetryTimer;
                connectionTask = _connectionRetryTask;
                connectionTokenSource = _connectionRetryTokenSource;
                combinedConnectionTokenSource =
                    _combinedConnectionRetryTokenSource;
                subscriptionTimer = _subscriptionRetryTimer;
                subscriptionTask = _subscriptionRetryTask;
                subscriptionTokenSource = _subscriptionRetryTokenSource;
                combinedSubscriptionTokenSource =
                    _combinedSubscriptionRetryTokenSource;

                _connectionRetryTimer = null;
                _connectionRetryTask = null;
                _connectionRetryTokenSource = null;
                _combinedConnectionRetryTokenSource = null;
                _subscriptionRetryTimer = null;
                _subscriptionRetryTask = null;
                _subscriptionRetryTokenSource = null;
                _combinedSubscriptionRetryTokenSource = null;

                combinedConnectionTokenSource?.Cancel();
                connectionTokenSource?.Cancel();
                combinedSubscriptionTokenSource?.Cancel();
                subscriptionTokenSource?.Cancel();
            }
            finally
            {
                _eventStreamingLock.Release();
            }

            if (connectionTask != null)
            {
                await connectionTask.ConfigureAwait(false);
            }

            if (subscriptionTask != null)
            {
                await subscriptionTask.ConfigureAwait(false);
            }

            connectionTimer?.Dispose();
            combinedConnectionTokenSource?.Dispose();
            connectionTokenSource?.Dispose();
            subscriptionTimer?.Dispose();
            combinedSubscriptionTokenSource?.Dispose();
            subscriptionTokenSource?.Dispose();
        }

        /// <summary>Connects the concrete backend as part of the service connection lifecycle.</summary>
        protected abstract Task InternalConnectAsync(CancellationToken cancellationToken);

        /// <inheritdoc />
        public async Task ConnectAsync()
        {
            try
            {
                await InternalConnectAsync(_cts.Token);
                OnConnectionChanged(true);
            }
            catch (EventStreamingException ex)
            {
                // Log the error but we will retry
                _logger.LogError(EventIds.Connect, "Failed to connect to the {Service} event streaming service ('{Error}'). Will retry...", ShortDescription, ex.Message);
            }

            await StartRetryTimersAsync();
        }

        /// <summary>Disconnects the concrete backend after subscription shutdown.</summary>
        protected abstract Task InternalDisconnectAsync();

        /// <inheritdoc />
        public async Task DisconnectAsync()
        {
            if (!IsConnected)
            {
                return;
            }

            await StopRetryTimersAsync();

            await InternalDisconnectAsync();

            // Indicate we have connected
            OnConnectionChanged(false);
        }

        /// <summary>Raises the topic-created notification.</summary>
        protected void OnTopicCreated(string topicName)
        {
            TopicCreated?.Invoke(this, new TopicEventArgs(topicName));
        }

        /// <summary>Raises the topic-deleted notification.</summary>
        protected void OnTopicDeleted(string topicName)
        {
            TopicDeleted?.Invoke(this, new TopicEventArgs(topicName));
        }

        /// <summary>Raises the notification for events appended to a topic.</summary>
        protected void OnEventsAppended(string topicName, IEnumerable<IEventMessage> events)
        {
            EventsAppended?.Invoke(this, new TopicEventsEventArgs(topicName, events));
        }

        private void OnEventConsumed(
            string topicName,
            IEventMessage eventMessage,
            MessageHandlingResult result)
        {
            EventJournalMetrics.DeliveryCompleted(
                ShortDescription,
                TransportProtocol,
                result.ToString(),
                eventMessage.CreationTime);
            EventsConsumed?.Invoke(
                this,
                new TopicEventsEventArgs(
                    topicName,
                    new List<IEventMessage> { eventMessage }));
        }

        /// <summary>Handles a dropped subscription and coordinates its retry or shutdown.</summary>
        protected async Task OnSubscriptionDroppedAsync(string topicName)
        {
            await _eventStreamingLock.WaitAsync();

            try
            {
                if (_topicSubscriptionInfoByTopicName.TryGetValue(topicName, out TopicSubscriptionInfo topicSubscriptionInfo))
                {
                    // Set all registered consumers to currently unsubscribed. At the next autosubscription interval,
                    // any currently unsubscribed consumers will be resubscribed if they are consumer active
                    topicSubscriptionInfo.ResetSubscriptionState();
                }
            }
            finally
            {
                _eventStreamingLock.Release();
            }
        }

        /// <summary>Delivers an event to the selected registered handlers and reports the processing outcome.</summary>
        protected async Task<MessageHandlingResult> DispatchEventMessageAsync(IEventHandlerHostObject eventHandlerHostObject, string topicName, IEventMessage eventMessage)
        {
            if (Volatile.Read(ref _disposeCompleted) == 1)
            {
                return MessageHandlingResult.Retry;
            }

            TopicSubscriptionInfo.RegisteredConsumerInfo topicConsumerInfo = null;
            List<MessageHandlerDelegate> matchingMessageHandlerDelegates = null;
            List<IEventHandler> matchingAllEventHandlers = null;

            await _eventStreamingLock.WaitAsync();

            try
            {
                if (Volatile.Read(ref _disposeCompleted) == 1)
                {
                    return MessageHandlingResult.Retry;
                }

                if (!_topicSubscriptionInfoByTopicName.TryGetValue(topicName, out TopicSubscriptionInfo topicSubscriptionInfo))
                {
                    // No handler ran. A durable transport must not acknowledge this event.
                    return MessageHandlingResult.Retry;
                }

                if (!topicSubscriptionInfo.ConsumerInfoByEventHandlerHosts.TryGetValue(eventHandlerHostObject, out topicConsumerInfo))
                {
                    return MessageHandlingResult.Retry;
                }

                if (!_streamActiveEventHandlerHostObjects.Contains(eventHandlerHostObject) || !topicConsumerInfo.IsStreamingEvents)
                {
                    // Pausing consumption is not successful handling; retain the event for resume.
                    return MessageHandlingResult.Retry;
                }

                var eventType = eventMessage.GetType();

                matchingMessageHandlerDelegates = topicConsumerInfo.HandlerDelegatesByEventType
                    .Where(h => h.Key.IsAssignableFrom(eventType))
                    .SelectMany(k => k.Value).Select(t => t.Item2).ToList();

                matchingAllEventHandlers = topicConsumerInfo.AllEventTypesHandlerMethods.ToList();
            }
            finally
            {
                _eventStreamingLock.Release();
            }

            MessageHandlingResult aggregateResult = MessageHandlingResult.AlreadyProcessed;

            // Dispatch to the specific event type handler methods
            foreach (var messageHandlerDelegate in matchingMessageHandlerDelegates)
            {
                try
                {
                    // Invoke the handler method on the event consumer with idempotency handling
                    MessageHandlingResult result = await InboxMessageHandler.HandleTopicMessageAsync(_logger,
                                    Description,
                                    TransportProtocol,
                                    topicName,
                                    eventMessage,
                                    eventHandlerHostObject,
                                    messageHandlerDelegate);

                    if (result == MessageHandlingResult.Retry)
                    {
                        EventJournalMetrics.DeliveryCompleted(
                            ShortDescription,
                            TransportProtocol,
                            result.ToString(),
                            eventMessage.CreationTime);
                        return result;
                    }
                    if (result == MessageHandlingResult.Processed)
                    {
                        aggregateResult = result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(EventIds.HandleMessage, "FATAL: Unhandled error for '{EventType}' on topic '{TopicName}' by typed event handler ('{Error}').",
                        eventMessage.GetType().Name, topicName, ex.Message);
                    _logger.LogError(EventIds.HandleMessage, ex.StackTrace);

                    // Handlers should handle any expected exceptions
                    ExceptionHelper.Crash(ex);
                }

                OnEventConsumed(topicName, eventMessage, aggregateResult);
            }

            // Dispatch to the 'all type' event handler methods
            foreach (var eventHandler in matchingAllEventHandlers)
            {
                try
                {
                    MessageHandlingResult result = await InboxMessageHandler.HandleTopicMessageAsync(_logger,
                        Description,
                        TransportProtocol,
                        topicName,
                        eventMessage,
                        eventHandler);

                    if (result == MessageHandlingResult.Retry)
                    {
                        EventJournalMetrics.DeliveryCompleted(
                            ShortDescription,
                            TransportProtocol,
                            result.ToString(),
                            eventMessage.CreationTime);
                        return result;
                    }
                    if (result == MessageHandlingResult.Processed)
                    {
                        aggregateResult = result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(EventIds.HandleMessage, "FATAL: Unhandled error for '{EventType}' on topic '{TopicName}' by typed event handler ('{Error}').",
                        eventMessage.GetType().Name, topicName, ex.Message);
                    _logger.LogError(EventIds.HandleMessage, ex.StackTrace);

                    // Handlers should handle any expected exceptions
                    ExceptionHelper.Crash(ex);
                }

                OnEventConsumed(topicName, eventMessage, aggregateResult);
            }

            return aggregateResult;
        }

        /// <summary>Creates a backend subscription for the selected topic and consumer.</summary>
        protected abstract Task SubscribeToEventTopicAsync(string topicName, IEventHandlerHostObject eventHandler, CancellationTokenSource source);

        /// <summary>Releases a backend subscription for the selected topic and consumer.</summary>
        protected abstract Task UnsubscribeFromEventTopicAsync(string topicName, IEventHandlerHostObject eventHandler, CancellationTokenSource source);

        private async Task ResetTopicSubscriptionStatesAsync()
        {
            await _eventStreamingLock.WaitAsync();

            try
            {
                // Ensure that all topics are marked as unsubscribed so that on reconnection
                // we will resubscribe to them. The event streaming service should be able to
                // treat a duplicate subscription as a no-op.
                foreach (var subscriptionInfo in _topicSubscriptionInfoByTopicName.Values)
                {
                    subscriptionInfo.ResetSubscriptionState();
                }
            }
            finally
            {
                _eventStreamingLock.Release();
            }
        }

        private async Task<List<(string, List<TopicSubscriptionInfo.RegisteredConsumerInfo>)>> GetUnsubscribedStreamingActiveConsumers()
        {
            // Find the topics that are currently streaming active, but are not currently streaming (i.e. subscribed)
            var unsubscribedRegisteredConsumers = new List<(string, List<TopicSubscriptionInfo.RegisteredConsumerInfo>)>();

            await _eventStreamingLock.WaitAsync();

            try
            {
                foreach (var topicSubscriptionInfo in _topicSubscriptionInfoByTopicName.Values)
                {
                    var unsubscribedRegisteredConsumersForTopic = new List<TopicSubscriptionInfo.RegisteredConsumerInfo>();

                    foreach (var consumerInfo in topicSubscriptionInfo.ConsumerInfoByEventHandlerHosts.Values)
                    {
                        if (!consumerInfo.IsStreamingEvents && _streamActiveEventHandlerHostObjects.Contains(consumerInfo.EventHandlerHostObject))
                        {
                            unsubscribedRegisteredConsumersForTopic.Add(consumerInfo);
                        }
                    }

                    if (unsubscribedRegisteredConsumersForTopic.Any())
                    {
                        unsubscribedRegisteredConsumers.Add((topicSubscriptionInfo.TopicName, unsubscribedRegisteredConsumersForTopic));
                    }
                }
            }
            finally
            {
                _eventStreamingLock.Release();
            }

            return unsubscribedRegisteredConsumers;
        }

        private async Task SubscribeToEventTopicAsync(string topicName, TopicSubscriptionInfo.RegisteredConsumerInfo registerConsumerInfo)
        {
            try
            {
                var handlerCancellationTokenSource = new CancellationTokenSource();
                registerConsumerInfo.CancellationTokenSource = handlerCancellationTokenSource;

                // Send a message to the server to subscribe to the topic
                // Events should then arrive in the receive thread for the handler object
                await SubscribeToEventTopicAsync(topicName,
                    registerConsumerInfo.EventHandlerHostObject,
                    handlerCancellationTokenSource);

                _logger.LogInformation(EventIds.RegisterMessageHandler, "{Service} subscribed to event topic '{TopicName}'", ShortDescription, topicName);

                // Mark as subscribed
                registerConsumerInfo.IsStreamingEvents = true;
            }
            catch (EventStreamingException ex)
            {
                // Will retry again later
                _logger.LogInformation(EventIds.RegisterMessageHandler, "{Service} failed to subscribe to topic '{TopicName}' ('{Error}'). Will retry...",
                    ShortDescription, topicName, ex.Message);

                registerConsumerInfo.IsStreamingEvents = false;

                registerConsumerInfo.CancellationTokenSource?.Cancel();
                registerConsumerInfo.CancellationTokenSource?.Dispose();
                registerConsumerInfo.CancellationTokenSource = null;
            }
        }

        private async Task UnsubscribeFromEventTopicAsync(string topicName, TopicSubscriptionInfo.RegisteredConsumerInfo registeredConsumerInfo)
        {
            try
            {
                // Send a message to the server to unsubscribe from the event topic
                // Events should then arrive in the receive thread for the handler object
                await UnsubscribeFromEventTopicAsync(topicName,
                    registeredConsumerInfo.EventHandlerHostObject,
                    registeredConsumerInfo.CancellationTokenSource);

                _logger.LogInformation(EventIds.UnregisterMessageHandler,
                    "{Service}: Unsubscribed from event topic '{TopicName}'",
                    ShortDescription, topicName);

                // Mark as unsubscribed
                registeredConsumerInfo.IsStreamingEvents = false;
            }
            catch (EventStreamingException ex)
            {
                // Just warn, but we don't throw in this case
                _logger.LogError(EventIds.UnregisterMessageHandler,
                    "{Service}: Failed to unsubscribe from topic '{TopicName}' ('{Error}').",
                    ShortDescription, topicName, ex.Message);
            }
            finally
            {
                registeredConsumerInfo.CancellationTokenSource?.Cancel();
                registeredConsumerInfo.CancellationTokenSource?.Dispose();
                registeredConsumerInfo.CancellationTokenSource = null;
            }
        }

        /// <summary>Appends the supplied event batch through the concrete backend.</summary>
        protected abstract Task InternalAppendEventsAsync(string topicName, IEnumerable<IEventMessage> events, CancellationToken token);

        /// <inheritdoc />
        public async Task AppendEventsAsync(string topicName, IEnumerable<IEventMessage> events, CancellationToken token)
        {
            if (topicName is null)
            {
                throw new ArgumentNullException(nameof(topicName));
            }

            if (events is null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            List<IEventMessage> eventList = events.ToList();
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (!IsConnected)
            {
                EventJournalMetrics.AppendCompleted(
                    ShortDescription,
                    TransportProtocol,
                    "unavailable",
                    stopwatch.Elapsed,
                    eventList.Count);
                throw new EventStreamingException("Not connected to event streaming service", null);
            }

            using Activity activity = MessagingTelemetry.StartProducerActivity(
                "EventStreamingService.AppendBatch",
                TelemetryLevel.Detailed,
                Description,
                TransportProtocol,
                topicName,
                eventList.Count);
            MessagingTelemetry.InjectCurrentContext(eventList);

            try
            {
                await InternalAppendEventsAsync(topicName, eventList, token);

                // Having not raised an error, this is taken to mean the events have been successfully appended
                OnEventsAppended(topicName, eventList);
                EventJournalMetrics.AppendCompleted(
                    ShortDescription,
                    TransportProtocol,
                    "success",
                    stopwatch.Elapsed,
                    eventList.Count);
            }
            catch (OperationCanceledException)
            {
                EventJournalMetrics.AppendCompleted(
                    ShortDescription,
                    TransportProtocol,
                    "cancelled",
                    stopwatch.Elapsed,
                    eventList.Count);
                throw;
            }
            catch
            {
                EventJournalMetrics.AppendCompleted(
                    ShortDescription,
                    TransportProtocol,
                    "failure",
                    stopwatch.Elapsed,
                    eventList.Count);
                throw;
            }
        }

        /// <inheritdoc />
        public abstract Task CommitTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer);

        /// <inheritdoc />
        public abstract Task RevertTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer);

        /// <inheritdoc />
        public abstract Task<IEnumerable<(string, long, List<IEventMessage>)>>
            GetCurrentTopicsAsync(CancellationToken token = default);

        private async Task InternalRegisterEventHandlerAsync<TEventHandler>(string topicName, TEventHandler eventHandler, Action<TopicSubscriptionInfo.RegisteredConsumerInfo> registerHandler, CancellationToken token)
            where TEventHandler : IEventHandlerHostObject
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            ArgumentNullException.ThrowIfNull(eventHandler, nameof(eventHandler));

            await _eventStreamingLock.WaitAsync(token);

            try
            {
                // If this is the first time a handler has been registered for this topic, create a new topic subscription info object
                if (!_topicSubscriptionInfoByTopicName.TryGetValue(topicName, out TopicSubscriptionInfo topicSubscriptionInfo))
                {
                    topicSubscriptionInfo = new TopicSubscriptionInfo(topicName);
                    _topicSubscriptionInfoByTopicName.Add(topicName, topicSubscriptionInfo);
                }

                // If this is the first time this handler host object has been registered, create a new registered consumer info object
                if (!topicSubscriptionInfo.ConsumerInfoByEventHandlerHosts.TryGetValue(eventHandler, out TopicSubscriptionInfo.RegisteredConsumerInfo consumerInfo))
                {
                    // Create a topic info metadata class for this topic
                    // Consumer will be registered, but by default won't be subscribed to the topic
                    consumerInfo = new TopicSubscriptionInfo.RegisteredConsumerInfo();

                    consumerInfo.EventHandlerHostObject = eventHandler;

                    topicSubscriptionInfo.ConsumerInfoByEventHandlerHosts.Add(eventHandler, consumerInfo);

                    TopicEventHandlerRegistered?.Invoke(this, new TopicEventArgs(topicName));
                }

                // Call the register handler delegate. For generic handlers, this will add the handler to the list of generic handlers
                // For 'all events' handlers, this will add to the list of all events handlers
                registerHandler(consumerInfo);
            }
            finally
            {
                _eventStreamingLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task RegisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token) where TEvent : IEventMessage
        {
            await InternalRegisterEventHandlerAsync(topicName, eventHandler, (registeredConsumerInfo) =>
            {
                List<(IEventHandlerHostObject, MessageHandlerDelegate)> messageHandlerDelegates = null;

                if (!registeredConsumerInfo.HandlerDelegatesByEventType.TryGetValue(typeof(TEvent), out messageHandlerDelegates))
                {
                    messageHandlerDelegates = new List<(IEventHandlerHostObject, MessageHandlerDelegate)>();
                    registeredConsumerInfo.HandlerDelegatesByEventType.Add(typeof(TEvent), messageHandlerDelegates);
                }

                if (messageHandlerDelegates.Any(item => ReferenceEquals(item.Item1, eventHandler)))
                {
                    return;
                }

                // The same host may implement handlers for several event types, but each
                // host/event-type pair must be registered only once.
                MessageHandlerDelegate messageHandlerDelegate =
                    (uow, ev) => eventHandler.HandleAsync((TEvent)ev, uow);
                messageHandlerDelegates.Add((eventHandler, messageHandlerDelegate));

                _logger.LogDebug(EventIds.RegisterMessageHandler,
                    "Registered event handler '{Handler}' for event type '{EventType}' for topic '{TopicName}'.",
                        eventHandler.HandlerDescription, typeof(TEvent).Name, topicName);
            }, token);
        }

        private async Task InternalUnregisterEventsHandlerAsync(string topicName, IEventHandlerHostObject eventHandler, Action<TopicSubscriptionInfo.RegisteredConsumerInfo> unregisterHandler, CancellationToken token)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            ArgumentNullException.ThrowIfNull(eventHandler, nameof(eventHandler));

            await _eventStreamingLock.WaitAsync(token);

            try
            {
                // See if any handler has previously been registered for this topic
                if (_topicSubscriptionInfoByTopicName.TryGetValue(topicName, out TopicSubscriptionInfo topicSubscriptionInfo))
                {
                    TopicSubscriptionInfo.RegisteredConsumerInfo consumerInfo = null;

                    // And now see if this particular handler host object has been registered for this topic
                    if (topicSubscriptionInfo.ConsumerInfoByEventHandlerHosts.TryGetValue(eventHandler, out consumerInfo))
                    {
                        unregisterHandler(consumerInfo);
                    }
                }
            }
            finally
            {
                _eventStreamingLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task UnregisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token)
            where TEvent : IEventMessage
        {
            await InternalUnregisterEventsHandlerAsync(topicName, eventHandler, (ci) =>
            {
                // Now see if this particular handler method has been registered for this event type, and
                // if so unregister it
                if (ci.HandlerDelegatesByEventType.TryGetValue(typeof(TEvent),
                    out List<(IEventHandlerHostObject, MessageHandlerDelegate)> messageHandlerDelegates))
                {
                    if (messageHandlerDelegates.RemoveAll(d => d.Item1 == eventHandler) > 0)
                    {
                        TopicEventHandlerUnregistered?.Invoke(this, new TopicEventArgs(topicName));
                    }
                }
            }, token);
        }

        /// <inheritdoc />
        public abstract Task RegisterEventTopicAsync(string topicName, string description, CancellationToken token);

        /// <inheritdoc />
        public abstract Task UnregisterEventTopicAsync(string topicName);

        /// <inheritdoc />
        public async Task StreamEventsAsync(IEventHandlerHostObject host, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(host, nameof(host));

            List<(string topic, TopicSubscriptionInfo.RegisteredConsumerInfo ci)> toSubscribe;

            await _eventStreamingLock.WaitAsync(token);
            try
            {
                if (_streamActiveEventHandlerHostObjects.Add(host))
                {
                    _logger.LogInformation(EventIds.StartConsuming, "Set handler '{Handler}' to stream active.", host.HandlerDescription);
                }

                toSubscribe = _topicSubscriptionInfoByTopicName.Values
                    .Select(t => (t.TopicName, ci: t.ConsumerInfoByEventHandlerHosts.TryGetValue(host, out var ci) ? ci : null))
                    .Where(x => x.ci is { IsStreamingEvents: false })
                    .Select(x => (x.TopicName, x.ci!))
                    .ToList();
            }
            finally
            {
                _eventStreamingLock.Release();
            }

            foreach (var (topic, ci) in toSubscribe)
            {
                await SubscribeToEventTopicAsync(topic, ci);
            }
        }

        /// <inheritdoc />
        public async Task StopStreamingEventsAsync(IEventHandlerHostObject eventConsumer, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(eventConsumer, nameof(eventConsumer));

            await _eventStreamingLock.WaitAsync(token);

            try
            {
                // Remove the handler from the stream active set
                if (_streamActiveEventHandlerHostObjects.Remove(eventConsumer))
                {
                    _logger.LogInformation(EventIds.StopConsuming, "Set event handler '{Handler}' to stream inactive.",
                        eventConsumer.HandlerDescription);
                }
            }
            finally
            {
                _eventStreamingLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task RegisterAllTopicEventsHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token)
        {
            await InternalRegisterEventHandlerAsync(topicName, eventHandler, (registeredConsumerInfo) =>
            {
                if (registeredConsumerInfo.AllEventTypesHandlerMethods.Add(eventHandler))
                {
                    TopicEventHandlerRegistered?.Invoke(this, new TopicEventArgs(topicName));
                    _logger.LogDebug(EventIds.RegisterMessageHandler, "Registered event handler '{Handler}' for all event types for topic '{TopicName}'.",
                        eventHandler.HandlerDescription, topicName);
                }
            }, token);
        }

        /// <inheritdoc />
        public async Task UnregisterAllTopicsEventHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token)
        {
            await InternalUnregisterEventsHandlerAsync(topicName, eventHandler, (ci) =>
            {
                if (ci.AllEventTypesHandlerMethods.Remove(eventHandler))
                {
                    TopicEventHandlerUnregistered?.Invoke(this, new TopicEventArgs(topicName));
                    _logger.LogDebug(EventIds.UnregisterMessageHandler, "Unregistered event handler '{Handler}' for all event types for topic '{TopicName}'.",
                        eventHandler.HandlerDescription, topicName);
                }
            }, token);
        }

        /// <summary>Gets whether the service currently considers its backend connected.</summary>
        public bool IsConnected => Interlocked.Read(ref _isConnectedState) == (long)ConnectionState.Connected;

        /// <inheritdoc />
        public abstract string ShortDescription
        {
            get;
        }

        /// <summary>Gets the backend's descriptive name.</summary>
        public abstract string Description
        {
            get;
        }

        /// <summary>Gets the backend transport protocol identifier.</summary>
        public abstract string TransportProtocol
        {
            get;
        }

        /// <summary>
        /// The cancellation token source for the event streaming service
        /// </summary>
        protected CancellationTokenSource CancellationTokenSource => _cts;

        /// <summary>
        /// Indicates whether the event streaming service should automatically connect
        /// by attempting to periodically reconnect
        /// </summary>
        protected virtual bool AutoConnect => true;

        /// <summary>Gets the logger used for service and subscription diagnostics.</summary>
        protected Microsoft.Extensions.Logging.ILogger Logger => _logger;
    }
}
