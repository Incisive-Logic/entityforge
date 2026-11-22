using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>Publishes events through a persistence outbox before relaying them to the underlying event stream.</summary>
    public interface IOutboxEventStreamingService : IEventStreamingService
    {
        /// <summary>
        /// Gets the journal transport to which committed outbox records are relayed.
        /// </summary>
        IDirectEventStreamingService DirectEventStreamingService
        {
            get;
        }

        /// <summary>
        /// Raised after events have been durably persisted to the outbox.
        /// </summary>
        event EventHandler<TopicEventsEventArgs> EventsPersistedToOutbox;
    }
}
