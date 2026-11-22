using EntityForge.Cqrs.Events.EventStreaming;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Queue
{
    /// <summary>Carries one queued topic message to an event listener.</summary>
    public class TopicMessageEventArgs : TopicEventArgs
    {
        /// <summary>Creates event arguments for the supplied topic message.</summary>
        public TopicMessageEventArgs(string topicName, IMessage message) : base(topicName)
        {
            Message = message;
        }

        /// <summary>Gets the topic message being reported.</summary>
        public IMessage Message
        {
            get; private set;
        }
    }

    /// <summary>
    /// A message queue service, which allows topic publish/subscribe support using an underlying queue
    /// Messages are ephemeral - that is, after consumption messages will be discarded
    /// </summary>
    public interface IMessageQueueService : IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Obtains a snapshot of the current topics registered on the message queue
        /// </summary>
        IEnumerable<(string, long, List<IMessage>)> GetCurrentTopics();

        /// <summary>
        /// Raised when a new topic is created on the message queue
        /// </summary>
        event EventHandler<TopicEventArgs> TopicCreated;

        /// <summary>
        /// Raised when a topic is deleted from the message queue
        /// </summary>
        event EventHandler<TopicEventArgs> TopicDeleted;

        /// <summary>
        /// Raised when a new consumer is registered against a topic
        /// </summary>
        event EventHandler<TopicEventArgs> TopicConsumerRegistered;

        /// <summary>
        /// Raised when a consumer is unregistered from a topic
        /// </summary>
        event EventHandler<TopicEventArgs> TopicConsumerUnregistered;

        /// <summary>
        /// Raised when an event is appended to a topic
        /// </summary>
        event EventHandler<TopicMessageEventArgs> MessageQueued;

        /// <summary>
        /// Raised when an event is consumed from a topic
        /// </summary>
        event EventHandler<TopicMessageEventArgs> MessageConsumed;

        /// <summary>
        /// Registers a topic of a given name on the message queue
        /// </summary>
        /// <param name="topic">The name of the topic</param>
        /// <param name="boundedCapacity">An optional maximum number of unconsumed messages the topic can hold</param>
        Task RegisterTopicAsync(string topic, int? boundedCapacity);

        /// <summary>
        /// Unregisters the topic of a given name from the message queue
        /// </summary>
        /// <param name="topic">The name of the topic</param>
        Task UnregisterTopicAsync(string topic);

        /// <summary>
        /// Appends a message to the topic
        /// </summary>
        /// <param name="topic">The name of the topic</param>
        /// <param name="message">The message</param>
        Task EnqueueAsync(string topic, IMessage message);

        /// <summary>
        /// Subscribes to a topic
        /// </summary>
        /// <typeparam name="T">The message type</typeparam>
        /// <param name="topic">The name of the topic</param>
        /// <param name="handler">The message handler that receives the message</param>
        /// <param name="boundedCapacity">An optional maximum number of unconsumed messages???</param>
        Task RegisterMessageHandlerAsync<T>(string topic, IMessageHandler<T> handler, int? boundedCapacity) where T : IMessage;

        /// <summary>
        /// Unsubscribes from a topic
        /// </summary>
        /// <typeparam name="T">The message type</typeparam>
        /// <param name="topic">The name of the topic</param>
        /// <param name="handler">The message handler that receives the message</param>
        Task UnregisterMessageHandlerAsync<T>(string topic, IMessageHandler<T> handler) where T : IMessage;

        /// <summary>Starts delivery of queued messages to registered consumers.</summary>
        void StartConsuming<T>(IMessageHandler<T> handler) where T : IMessage;

        /// <summary>Stops message consumption.</summary>
        void StopConsuming<T>(IMessageHandler<T> handler) where T : IMessage;
    }
}
