using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Observability
{
    /// <summary>
    /// Well-known OpenTelemetry tag (attribute) names used in CQRS and event streaming activities.
    /// Combines standard OpenTelemetry messaging conventions with IncisiveLogic-specific extensions.
    ///
    /// See:
    /// https://github.com/open-telemetry/semantic-conventions/blob/main/specification/trace/semantic_conventions/messaging.md
    /// </summary>
    public static class ActivityTagNames
    {
        // ---------------------------------------------------------------------
        // 🧩 Standard OpenTelemetry Messaging Attributes
        // ---------------------------------------------------------------------

        /// <summary>
        /// The messaging system being used, e.g. "inmemory", "kafka", "rabbitmq", etc.
        /// </summary>
        public const string MessagingSystem = "messaging.system";

        /// <summary>
        /// The kind of messaging destination, e.g. "topic", "queue"
        /// </summary>
        public const string MessagingDestinationKind = "messaging.destination_kind";

        /// <summary>
        /// The destination name, e.g. the topic or queue name
        /// </summary>
        public const string MessagingDestination = "messaging.destination";

        /// <summary>
        /// Messaging operation being performed: "publish", "process", or "receive".
        /// </summary>
        public const string MessagingOperation = "messaging.operation";

        /// <summary>
        /// Identifier for the consumer/handler that processed the message.
        /// </summary>
        public const string MessagingConsumerId = "messaging.consumer_id";

        /// <summary>
        /// Unique message identifier (e.g. event ID or queue message ID).
        /// </summary>
        public const string MessagingMessageId = "messaging.message.id";

        /// <summary>
        /// Number of messages included in a batch operation.
        /// </summary>
        public const string MessagingBatchMessageCount = "messaging.batch.message_count";

        /// <summary>
        /// Message payload size in bytes, if known.
        /// </summary>
        public const string MessagingMessagePayloadSize = "messaging.message.payload_size_bytes";

        /// <summary>
        /// Transport protocol used by the messaging system (e.g. "inproc", "tcp", "grpc" etc.).
        /// </summary>
        public const string MessagingProtocol = "messaging.protocol";

        // ---------------------------------------------------------------------
        // 🧱 IncisiveLogic CQRS / EventStreaming Extensions
        // ---------------------------------------------------------------------

        /// <summary>
        /// The logical CQRS topic or stream name.
        /// </summary>
        public const string CqrsTopic = "incisivelogic.cqrs.topic";

        /// <summary>
        /// Fully-qualified event type name (e.g. "OrderCreatedEvent").
        /// </summary>
        public const string CqrsEventType = "incisivelogic.cqrs.event_type";

        /// <summary>
        /// Number of event handlers currently processing the message.
        /// </summary>
        public const string CqrsHandlerCount = "incisivelogic.cqrs.handler_count";

        /// <summary>
        /// The name or description of the handler instance.
        /// </summary>
        public const string CqrsHandlerName = "incisivelogic.cqrs.handler_name";

        /// <summary>
        /// The current retry attempt number for this message, if applicable.
        /// </summary>
        public const string CqrsRetryAttempt = "incisivelogic.cqrs.retry_attempt";

        /// <summary>
        /// Whether this event was redelivered (true/false).
        /// </summary>
        public const string CqrsRedelivered = "incisivelogic.cqrs.redelivered";

        /// <summary>
        /// The latency in milliseconds between the event’s creation and processing.
        /// </summary>
        public const string CqrsDeliveryLatency = "incisivelogic.cqrs.delivery_latency_ms";
    }
}
