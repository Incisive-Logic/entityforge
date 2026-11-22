using EntityForge.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>Configures event topic subscription and checkpoint persistence behavior.</summary>
    public class EventStreamingOptions
    {
        /// <summary>Gets or sets whether the service automatically subscribes registered consumers.</summary>
        public bool AutoSubscription
        {
            get; set;
        }

        /// <summary>EventStoreDB: retain each successfully handled position for restart, using a stable handler ID.
        /// Only one active consumer may own a topic/handler pair.</summary>
        public bool PersistConsumerPositions
        {
            get; set;
        }
    }

    /// <summary>Identifies the event-stream topic affected by a notification.</summary>
    public class TopicEventArgs : EventArgs
    {
        /// <summary>Creates topic event arguments, rejecting an empty topic name.</summary>
        public TopicEventArgs(string topicName)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            TopicName = topicName;
        }

        /// <summary>Gets the affected topic name.</summary>
        public string TopicName
        {
            get; private set;
        }
    }

    /// <summary>Carries an owned read-only event batch associated with one topic.</summary>
    public class TopicEventsEventArgs : TopicEventArgs
    {
        /// <summary>Creates topic event arguments and copies the supplied event sequence into a read-only list.</summary>
        public TopicEventsEventArgs(string topicName, IEnumerable<IEventMessage> eventMessages) : base(topicName)
        {
            ArgumentException.ThrowIfNullOrEmpty(topicName, nameof(topicName));
            ArgumentNullException.ThrowIfNull(eventMessages, nameof(eventMessages));

            EventMessages = eventMessages.ToList().AsReadOnly();
        }

        /// <summary>Gets the event batch associated with the notification.</summary>
        public IReadOnlyList<IEventMessage> EventMessages
        {
            get; private set;
        }
    }

    /// <summary>
    /// Interface applied to event streaming services that directly publish and consume from an
    /// underlying event stream without an outbox
    /// </summary>
    public interface IDirectEventStreamingService : IEventStreamingService
    {
    }

    /// <summary>Controls connection and disconnection of an event-stream transport.</summary>
    public interface IConnectableEventStream
    {
        /// <summary>
        /// Connects to the event streaming service asynchronously
        /// </summary>
        Task ConnectAsync();

        /// <summary>
        /// Disconnects from the event streaming service asynchronously
        /// </summary>
        Task DisconnectAsync();
    }

    /// <summary>
    /// The event streaming service offers an events journal for particular event topics which
    /// clients can produce to, or consume from
    ///
    /// Typically used for streaming data changes or domain/integration events to interested consumers.
    ///
    /// Producers and consumers must appropriately manage the lifetime of their events on the journal.
    ///
    /// Concrete implementations include e.g. EventStoreDB, Apache Kafka, RedPanda or Azure Event Hubs
    ///
    /// Retention behavior is backend-specific. Implementations that can report retention
    /// capabilities and cursor health should also implement IEventJournalRetentionInspector.
    /// Consumers must not assume that arbitrary event deletion or truncation is available.
    /// </summary>
    public interface IEventStreamingService :
        IEventStreamConsumer,
        IEventStreamProducer,
        IDisposable,
        IAsyncDisposable
    {
        /// <summary>Gets a concise description of the streaming backend.</summary>
        string ShortDescription
        {
            get;
        }
    }
}
