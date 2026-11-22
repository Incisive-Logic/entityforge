using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.QueryJob
{
    /// <summary>
    /// Base message for events related to queries, containing a correlation ID for
    /// matching the query event to the original query request.
    /// </summary>
    public interface IQueryEventMessage : IEventMessage
    {
        /// <summary>Gets the query identifier associated with this event.</summary>
        Guid QueryID
        {
            get;
        }
    }
}
