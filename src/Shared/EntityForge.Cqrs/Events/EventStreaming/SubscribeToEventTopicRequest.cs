using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>Carries a request to subscribe a consumer to an event topic.</summary>
    public class SubscribeToEventTopicRequest
    {
        /// <summary>
        /// The name of the event topic to subscribe to
        /// </summary>
        public string TopicName
        {
            get; set;
        }

        /// <summary>
        /// Whether this is a subscribe, or unsubscribe, request
        /// </summary>
        public bool Subscribe
        {
            get; set;
        }
    }
}
