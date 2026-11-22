using EntityForge.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>
    /// An event producer registers a given event topic by name, and then appends events
    /// to the topic by producing events. Note that we do NOT expect event stream service
    /// implementations to support write transactions as this is not the model provided by
    /// many event streaming implementations
    /// </summary>
    public interface IEventStreamProducer : IConnectableEventStream, IConnection
    {
        /// <summary>
        /// Event that indicates a topic has been created on the event journal
        /// </summary>
        event EventHandler<TopicEventArgs> TopicCreated;

        /// <summary>
        /// Event that indicates a topic has been deleted from the event journal
        /// </summary>
        event EventHandler<TopicEventArgs> TopicDeleted;

        /// <summary>
        /// Event that indicates an event has been appended to a topic on the event journal
        /// </summary>
        event EventHandler<TopicEventsEventArgs> EventsAppended;

        /// <summary>
        /// Gets the current topics registered on the event producer
        /// including the current events on the topic. This should ONLY
        /// be used for diagnostics and monitoring purposes.
        /// </summary>
        Task<IEnumerable<(string, long, List<IEventMessage>)>> GetCurrentTopicsAsync(
            CancellationToken token = default);

        /// <summary>
        /// Register a topic on the event producer
        /// </summary>
        Task RegisterEventTopicAsync(string topicName, string description, CancellationToken token);

        /// <summary>
        /// Unregister a topic on the event producer
        /// This removes the topic and its events
        /// </summary>
        Task UnregisterEventTopicAsync(string topicName);

        /// <summary>
        /// Appends events to a given topic
        /// </summary>
        /// <exception cref="EntityForge.Cqrs.Exceptions.EventStreamingException"/>
        Task AppendEventsAsync(string topicName, IEnumerable<IEventMessage> events, CancellationToken token);
    }
}
