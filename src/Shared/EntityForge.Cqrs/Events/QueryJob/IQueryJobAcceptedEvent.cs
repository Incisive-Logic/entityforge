using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.QueryJob
{
    /// <summary>
    /// Indicates that a query has been accepted for processing.
    /// </summary>
    public interface IQueryJobAcceptedEvent : IQueryEventMessage
    {
    }
}
