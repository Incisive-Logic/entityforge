using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.QueryJob
{
    /// <summary>
    /// Indicates that a query job has completed processing.
    /// </summary>
    public interface IQueryJobCompletedEvent : IQueryEventMessage
    {
    }
}
