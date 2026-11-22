using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using EntityForge.Cqrs.Events.EventStreaming;
using EntityForge.Cqrs.Exceptions;
using EntityForge.Cqrs.Observability;
using EntityForge.Cqrs.Queue.Exceptions;
using EntityForge.Cqrs.Repository;
using EntityForge.Cqrs.Repository.Inbox;
using EntityForge.Telemetry;
using EntityForge.Utility;
using System.Reflection;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;

namespace EntityForge.Cqrs.Queue
{
    /// <summary>
    /// A simple in-memory message queue implementation to provide messaging production and consumption.
    /// Not for production use, only intended for test purposes as provides no persistence.
    /// </summary>
    public class MemoryMessageQueueService : IMessageQueueService
    {
        #region Message Topic

        // Simple topic creates a queue per subscriber
        private class MessageTopic : IDisposable, IAsyncDisposable
        {
            private const string MessageSystemDescription = "In-Memory Message Queue Service";
            private const string MessageSystemProtocol = "In-Memory";

            public event EventHandler<TopicMessageEventArgs> MessageQueued;

            public event EventHandler<TopicMessageEventArgs> MessageConsumed;

            private readonly Microsoft.Extensions.Logging.ILogger _logger;
            private readonly string _topicName;
            private readonly int? _boundedCapacity;
            private readonly Channel<IMessage> _messageChannel;
            private readonly CancellationToken _token;

            private ImmutableDictionary<Type, ImmutableArray<(IMessageHandlerHostObject, MessageHandlerDelegate)>> _messageHandlersByMessageType =
                ImmutableDictionary<Type, ImmutableArray<(IMessageHandlerHostObject, MessageHandlerDelegate)>>.Empty;

            private readonly ConcurrentBag<(long, IMessage)> _history = new();
            private readonly ConcurrentDictionary<Task, byte> _activeHandlerTasks =
                new();

            private readonly Task _messageDispatcher;

            private const int HistoryMax = 1000;
            private static readonly TimeSpan MessageRetryDelay = TimeSpan.FromMilliseconds(100);
            private long _seq;

            public MessageTopic(Microsoft.Extensions.Logging.ILogger logger, string topic, int? boundedCapacity, CancellationToken token)
            {
                _logger = logger ?? NullLogger.Instance;
                _topicName = topic;
                _boundedCapacity = boundedCapacity;

                _messageChannel = _boundedCapacity.HasValue
                  ? Channel.CreateBounded<IMessage>(new BoundedChannelOptions(_boundedCapacity.Value)
                  {
                      FullMode = BoundedChannelFullMode.Wait,
                      SingleReader = true,
                      SingleWriter = false
                  })
                  : Channel.CreateUnbounded<IMessage>(new UnboundedChannelOptions
                  {
                      SingleReader = true,
                      SingleWriter = false
                  });

                _token = token;
                _messageDispatcher = ProcessMessagesAsync();
            }

            public void Dispose()
            {
                _history.Clear();
                _messageChannel.Writer.TryComplete();
            }

            public async ValueTask DisposeAsync()
            {
                Dispose();
                try
                {
                    await _messageDispatcher.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Graceful shutdown
                }

                Task[] activeHandlers = _activeHandlerTasks.Keys.ToArray();
                if (activeHandlers.Length > 0)
                {
                    await Task.WhenAll(activeHandlers).ConfigureAwait(false);
                }

                GC.SuppressFinalize(this);
            }

            public IEnumerable<IMessage> GetMessages()
            {
                return _history.OrderBy(m => m.Item1).Select(m => m.Item2).ToList();
            }

            /// <summary>
            /// Enqueues a message onto the message queue
            /// If the queue has reached bounded capacity, will block until capacity
            /// becomes available
            /// </summary>
            public async Task EnqueueAsync(IMessage message)
            {
                var s = Interlocked.Increment(ref _seq);
                _history.Add((s, message));

                while (_history.Count > HistoryMax && _history.TryTake(out _))
                {
                }

                await _messageChannel.Writer.WriteAsync(message, _token);
                MessageQueued?.Invoke(this, new TopicMessageEventArgs(_topicName, message));
            }

            private async Task ProcessMessagesAsync()
            {
                try
                {
                    await foreach (var message in _messageChannel.Reader.ReadAllAsync(_token))
                    {
                        var snapshot = Volatile.Read(ref _messageHandlersByMessageType);

                        var handlerDelegates = snapshot
                            .Where(kvp => kvp.Key.IsAssignableFrom(message.GetType()))
                            .SelectMany(kvp => kvp.Value)
                            .ToList();

                        if (handlerDelegates.Count > 0)
                        {
                            var handlerTasks = new List<Task<MessageHandlingResult>>();

                            // Just process in parallel, like a real out of process
                            // message queue would function
                            foreach (var handler in handlerDelegates)
                            {
                                var startSignal = new TaskCompletionSource(
                                    TaskCreationOptions.RunContinuationsAsynchronously);
                                Task<MessageHandlingResult> handlerTask = HandleMessageAsync(
                                    startSignal.Task,
                                    message,
                                    handler);
                                handlerTasks.Add(handlerTask);
                                _activeHandlerTasks.TryAdd(handlerTask, 0);
                                startSignal.TrySetResult();
                            }

                            try
                            {
                                MessageHandlingResult[] results = await Task.WhenAll(handlerTasks);

                                if (results.Any(result => result == MessageHandlingResult.Retry))
                                {
                                    await Task.Delay(MessageRetryDelay, _token);
                                    await _messageChannel.Writer.WriteAsync(message, _token);
                                    continue;
                                }
                            }
                            finally
                            {
                                foreach (Task handlerTask in handlerTasks)
                                {
                                    _activeHandlerTasks.TryRemove(handlerTask, out _);
                                }
                            }

                            // And raise that the message has been consumed
                            MessageConsumed?.Invoke(this, new TopicMessageEventArgs(_topicName, message));
                        }
                        else
                        {
                            // For test purposes, we don't have a dead letter queue (DLQ) - we just log and drop the message
                            _logger.LogDebug(EventIds.MessageDropped, "No handlers registered for message of type {MessageType} on topic {TopicName}. Dropping message with ID {MessageID}.",
                                message.GetType().Name, _topicName, message.Id);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Graceful shutdown
                }
            }

            private async Task<MessageHandlingResult> HandleMessageAsync(
                Task startSignal,
                IMessage message,
                (IMessageHandlerHostObject, MessageHandlerDelegate) handler)
            {
                await startSignal.ConfigureAwait(false);
                return await InboxMessageHandler.HandleTopicMessageAsync(
                    _logger,
                    MessageSystemDescription,
                    MessageSystemProtocol,
                    _topicName,
                    message,
                    handler.Item1,
                    handler.Item2).ConfigureAwait(false);
            }

            public void RegisterMessageHandler<TMessage>(IMessageHandler<TMessage> handler) where TMessage : IMessage
            {
                MessageHandlerDelegate handlerDelegate = (uow, msg) => handler.HandleAsync((TMessage)msg, uow);

                ImmutableInterlocked.AddOrUpdate(ref _messageHandlersByMessageType,
                    typeof(TMessage),
                    _ => ImmutableArray.Create(((IMessageHandlerHostObject)handler, handlerDelegate)),
                    (_, arr) =>
                        arr.Any(t => ReferenceEquals(t.Item1, handler))
                            ? arr
                            : arr.Add(((IMessageHandlerHostObject)handler, handlerDelegate)));
            }

            public void UnregisterMessageHandler<TMessage>(IMessageHandler<TMessage> handler) where TMessage : IMessage
            {
                ImmutableInterlocked.Update(ref _messageHandlersByMessageType, dict =>
                {
                    if (!dict.TryGetValue(typeof(TMessage), out var arr))
                    {
                        return dict;
                    }

                    var trimmed = arr.RemoveAll(h => ReferenceEquals(h.Item1, handler));
                    return trimmed.IsEmpty ? dict.Remove(typeof(TMessage)) : dict.SetItem(typeof(TMessage), trimmed);
                });
            }

            public string TopicName => _topicName;

            public long SubscriberCount
            {
                get
                {
                    return _messageHandlersByMessageType.Values.SelectMany(t => t).Count();
                }
            }
        }

        #endregion

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly Microsoft.Extensions.Logging.ILogger _logger;
        private readonly ConcurrentDictionary<string, MessageTopic> _topicsByTopicName = new(StringComparer.Ordinal);

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicCreated;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicDeleted;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicConsumerRegistered;

        /// <inheritdoc />
        public event EventHandler<TopicEventArgs> TopicConsumerUnregistered;

        /// <inheritdoc />
        public event EventHandler<TopicMessageEventArgs> MessageQueued;

        /// <inheritdoc />
        public event EventHandler<TopicMessageEventArgs> MessageConsumed;

        /// <summary>Creates an in-memory message queue and its consumer dispatch infrastructure.</summary>
        public MemoryMessageQueueService(Microsoft.Extensions.Logging.ILogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>Stops the in-memory queue and releases its owned resources.</summary>
        public void Dispose()
        {
            _cts.Cancel();

            foreach (var topic in _topicsByTopicName.Values)
            {
                topic.Dispose();
            }
        }

        /// <summary>Asynchronously stops queue consumption and releases owned resources.</summary>
        public async ValueTask DisposeAsync()
        {
            Dispose();
            await Task.WhenAll(
                _topicsByTopicName.Values
                    .Select(topic => topic.DisposeAsync().AsTask()))
                .ConfigureAwait(false);
            _topicsByTopicName.Clear();
            _cts.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public async Task EnqueueAsync(string topic, IMessage message)
        {
            MessageTopic matchingTopic = null;
            if (!_topicsByTopicName.TryGetValue(topic, out matchingTopic))
            {
                throw new MessageQueueException($"Topic '{topic}' has not been registered");
            }

            using Activity activity = MessagingTelemetry.StartProducerActivity(
                "MessageQueueService.Enqueue",
                TelemetryLevel.Deep,
                "In-Memory Message Queue Service",
                "In-Memory",
                topic);
            activity?.SetTag(ActivityTagNames.MessagingMessageId, message.Id.ToString());
            MessagingTelemetry.InjectCurrentContext(new[] { message });

            await matchingTopic.EnqueueAsync(message).ConfigureAwait(false);
        }

        private void OnTopicMessageQueued(object sender, TopicMessageEventArgs e)
        {
            MessageQueued?.Invoke(this, e);
        }

        private void OnTopicMessageConsumed(object sender, TopicMessageEventArgs e)
        {
            MessageConsumed?.Invoke(this, e);
        }

        private MessageTopic InternalRegisterTopic(string topicName, int? boundedCapacity, bool isAutoRegister)
        {
            bool created = false;

            var topic = _topicsByTopicName.GetOrAdd(topicName, _ =>
            {
                created = true;
                var messageTopic = new MessageTopic(_logger, topicName, boundedCapacity, _cts.Token);
                messageTopic.MessageQueued += OnTopicMessageQueued;
                messageTopic.MessageConsumed += OnTopicMessageConsumed;
                return messageTopic;
            });

            if (created)
            {
                if (isAutoRegister)
                {
                    _logger.LogTrace(EventIds.RegisterTopic, "Autoregistered topic {TopicName}.", topic);
                }
                else
                {
                    _logger.LogTrace(EventIds.RegisterTopic, "Manually registered topic {TopicName}.", topic);
                }

                TopicCreated?.Invoke(this, new TopicEventArgs(topicName));
            }

            return topic;
        }

        /// <inheritdoc />
        public void StartConsuming<T>(IMessageHandler<T> handler) where T : IMessage
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public void StopConsuming<T>(IMessageHandler<T> handler) where T : IMessage
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public IEnumerable<(string, long, List<IMessage>)> GetCurrentTopics()
        {
            // ConcurrentDictionary supports snapshot enumeration
            return _topicsByTopicName.Select(kvp => (kvp.Key, kvp.Value.SubscriberCount, kvp.Value.GetMessages().ToList())).ToList();
        }

        /// <inheritdoc />
        public Task RegisterTopicAsync(string topic, int? boundedCapacity)
        {
            InternalRegisterTopic(topic, boundedCapacity, false);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task UnregisterTopicAsync(string topic)
        {
            if (_topicsByTopicName.TryRemove(topic, out var t))
            {
                t.MessageConsumed -= OnTopicMessageConsumed;
                t.MessageQueued -= OnTopicMessageQueued;
                await t.DisposeAsync().ConfigureAwait(false);

                _logger.LogTrace(EventIds.UnregisterTopic, "Unregistered topic {TopicName}.", topic);
                TopicDeleted?.Invoke(this, new TopicEventArgs(topic));
            }
        }

        /// <summary>
        /// Registers an event handler for a specific topic and message type.
        /// </summary>
        /// <remarks>If the specified topic is not already registered, it will be automatically
        /// registered. A topic can only support one handler type, and topics with different message types cannot share
        /// the same name.</remarks>
        /// <typeparam name="T">The type of message that the handler processes. Must implement <see cref="IMessage"/>.</typeparam>
        /// <param name="topic">The name of the topic to associate with the handler.</param>
        /// <param name="handler">The event handler to register for the specified topic. The handler processes messages of type <typeparamref
        /// name="T"/>.</param>
        /// <param name="boundedCapacity">The optional bounded capacity for the topic's message queue. If specified, this value limits the number of
        /// messages that can be queued for the topic at any given time.</param>
        public Task RegisterMessageHandlerAsync<T>(string topic, IMessageHandler<T> handler, int? boundedCapacity) where T : IMessage
        {
            // Autoregister if topic not already registered
            MessageTopic messageTopic = InternalRegisterTopic(topic, boundedCapacity, true);

            messageTopic.RegisterMessageHandler(handler);
            TopicConsumerRegistered?.Invoke(this, new TopicEventArgs(topic));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Unregisters an event handler from a specific topic and message type if it is currently registered.
        /// </summary>
        /// <typeparam name="T">The type of message that the handler processes. Must implement <see cref="IMessage"/>.</typeparam>
        /// <param name="topic">The name of the topic to associate with the handler.</param>
        /// <param name="handler">The event handler to register for the specified topic. The handler processes messages of type <typeparamref
        /// name="T"/>.</param>
        public Task UnregisterMessageHandlerAsync<T>(string topic, IMessageHandler<T> handler) where T : IMessage
        {
            if (_topicsByTopicName.TryGetValue(topic, out var t))
            {
                t.UnregisterMessageHandler(handler);
                TopicConsumerUnregistered?.Invoke(this, new TopicEventArgs(topic));
            }

            return Task.CompletedTask;
        }
    }
}
