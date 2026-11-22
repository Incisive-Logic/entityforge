using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Observability
{
    /// <summary>
    /// Logging event ID constants.
    /// </summary>
    public static class EventIds
    {
        /// <summary>The numeric logging identifier for connection.</summary>
        public const int ConnectEventId = 1000;
        /// <summary>The numeric logging identifier for disconnection.</summary>
        public const int DisconnectEventId = 1001;

        /// <summary>The numeric logging identifier for topic registration.</summary>
        public const int RegisterTopicEventId = 2000;
        /// <summary>The numeric logging identifier for topic removal.</summary>
        public const int UnregisterTopicEventId = 2001;
        /// <summary>The numeric logging identifier for message-handler registration.</summary>
        public const int RegisterMessageHandlerEventId = 2002;
        /// <summary>The numeric logging identifier for message-handler removal.</summary>
        public const int UnregisterMessageHandlerEventId = 2003;
        /// <summary>The numeric logging identifier for consumer start.</summary>
        public const int StartConsumingEventId = 2004;
        /// <summary>The numeric logging identifier for consumer stop.</summary>
        public const int StopConsumingEventId = 2005;

        /// <summary>The numeric logging identifier for message handling.</summary>
        public const int HandleMessageEventId = 3000;

        /// <summary>The numeric logging identifier for message dropping.</summary>
        public const int MessageDroppedEventId = 4000;

        /// <summary>The structured logging event for connection.</summary>
        public static readonly EventId Connect = new EventId(ConnectEventId, nameof(Connect));
        /// <summary>The structured logging event for disconnection.</summary>
        public static readonly EventId Disconnect = new EventId(DisconnectEventId, nameof(Disconnect));

        /// <summary>The structured logging event for topic registration.</summary>
        public static readonly EventId RegisterTopic = new EventId(RegisterTopicEventId, nameof(RegisterTopic));
        /// <summary>The structured logging event for topic removal.</summary>
        public static readonly EventId UnregisterTopic = new EventId(UnregisterTopicEventId, nameof(UnregisterTopic));
        /// <summary>The structured logging event for message-handler registration.</summary>
        public static readonly EventId RegisterMessageHandler = new EventId(RegisterMessageHandlerEventId, nameof(RegisterMessageHandler));
        /// <summary>The structured logging event for message-handler removal.</summary>
        public static readonly EventId UnregisterMessageHandler = new EventId(UnregisterMessageHandlerEventId, nameof(UnregisterMessageHandler));
        /// <summary>The structured logging event for consumer start.</summary>
        public static readonly EventId StartConsuming = new EventId(StartConsumingEventId, nameof(StartConsuming));
        /// <summary>The structured logging event for consumer stop.</summary>
        public static readonly EventId StopConsuming = new EventId(StopConsumingEventId, nameof(StopConsuming));

        /// <summary>The structured logging event for message dropping.</summary>
        public static readonly EventId MessageDropped = new EventId(MessageDroppedEventId, nameof(MessageDropped));

        /// <summary>The structured logging event for message handling.</summary>
        public static readonly EventId HandleMessage = new EventId(HandleMessageEventId, nameof(HandleMessage));
    }
}
