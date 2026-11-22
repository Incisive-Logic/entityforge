using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.QueryJob
{
    /// <summary>
    /// Indicates a query has failed to be processed.
    /// </summary>
    public interface IQueryJobFailedEvent : IQueryEventMessage
    {
        /// <summary>Gets the failure reported by the query job.</summary>
        string Error
        {
            get;
        }
    }
}
