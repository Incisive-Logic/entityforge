using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Utility;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>
    /// Defines methods provided by an object that is capable of consuming a stream of events
    /// </summary>
    public interface IEventStreamConsumer : IConnectableEventStream, IConnection
    {
        /// <summary>
        /// An event that indicates a new topic consumer has been registered for the topic
        /// </summary>
        event EventHandler<TopicEventArgs> TopicEventHandlerRegistered;

        /// <summary>
        /// An event that indicates a topic consumer has been unregistered for the topic
        /// </summary>
        event EventHandler<TopicEventArgs> TopicEventHandlerUnregistered;

        /// <summary>
        /// An event that indicates new events have been consumed by a consumer
        /// </summary>
        event EventHandler<TopicEventsEventArgs> EventsConsumed;

        /// <summary>
        /// Registers an event handler that handles all event types for the given topic
        /// </summary>
        /// <param name="topicName">The event topic name.</param>
        /// <param name="eventHandler">The event handler.</param>
        /// <param name="token">Cancels the registration operation.</param>
        /// <returns>A task that completes when registration has been updated.</returns>
        Task RegisterAllTopicEventsHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token);

        /// <summary>
        /// Unregisters an event handler that handles all event types for the given topic.
        /// </summary>
        /// <param name="topicName">The event topic name.</param>
        /// <param name="eventHandler">The event handler.</param>
        /// <param name="token">Cancels the registration operation.</param>
        /// <returns>A task that completes when registration has been updated.</returns>
        Task UnregisterAllTopicsEventHandlerAsync(string topicName, IEventHandler eventHandler, CancellationToken token);

        /// <summary>
        /// Register a consumer of the events (will create a topic if one doesn't exist already)
        /// </summary>
        Task RegisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token)
            where TEvent : IEventMessage;

        /// <summary>
        /// Unregisters a consumer of the events
        /// </summary>
        /// <typeparam name="TEvent"></typeparam>
        Task UnregisterEventHandlerAsync<TEvent>(string topicName, IEventHandler<TEvent> eventHandler, CancellationToken token)
            where TEvent : IEventMessage;

        /// <summary>
        /// Start consuming the events registered against this consumer from the last committed event position.
        /// The event consumer object may implement handlers for more than one event type
        /// </summary>
        /// <param name="eventConsumer">The host object whose registered handlers consume events.</param>
        /// <param name="token">Cancels the streaming state change.</param>
        Task StreamEventsAsync(IEventHandlerHostObject eventConsumer, CancellationToken token);

        /// <summary>
        /// Stop consuming the events registered against this handler
        /// Note: The event handler object may implement handlers for more than one event type
        /// </summary>
        /// <param name="eventConsumer">The host object whose registered handlers consume events.</param>
        /// <param name="token">Cancels the streaming state change.</param>
        Task StopStreamingEventsAsync(IEventHandlerHostObject eventConsumer, CancellationToken token);

        /// <summary>
        /// Commits the current event position for the event consumer
        /// This means that if reverted, the event journal will return to this event position
        /// </summary>
        Task CommitTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer);

        /// <summary>
        /// Reverts the topic stream position to the last committed position
        /// </summary>
        /// <param name="topicName">The topic whose position is restored.</param>
        /// <param name="eventConsumer">The consumer whose committed position is restored.</param>
        Task RevertTopicStreamPositionAsync(string topicName, IEventHandlerHostObject eventConsumer);
    }
}
