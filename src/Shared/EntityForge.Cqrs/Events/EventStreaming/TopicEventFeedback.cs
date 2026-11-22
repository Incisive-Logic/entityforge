using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>Carries a topic's event payload back to a remote consumer.</summary>
    public class TopicEventFeedback
    {
        /// <summary>Gets or sets the topic whose event is being delivered.</summary>
        public string TopicName
        {
            get; set;
        }

        /// <summary>Gets or sets the serialized event payload.</summary>
        public string EventData
        {
            get; set;
        }
    }

    /// <summary>Carries a batch of topic event deliveries to a remote consumer.</summary>
    public class TopicEventFeedbackBatch
    {
        /// <summary>Creates a feedback batch from the supplied topic deliveries.</summary>
        public TopicEventFeedbackBatch()
        {
        }

        /// <summary>Gets or sets the delivered topic-event batch.</summary>
        public List<TopicEventFeedback> EventBatch
        {
            get; set;
        }
    }
}
