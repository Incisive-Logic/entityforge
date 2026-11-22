using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.QueryJob
{
    /// <summary>
    /// Represents a page of results from a query.
    /// </summary>
    public interface IQueryJobPageResultEvent : IQueryEventMessage
    {
    }
}
