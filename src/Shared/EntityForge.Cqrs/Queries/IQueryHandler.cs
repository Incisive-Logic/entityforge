using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Queue;

namespace EntityForge.Cqrs.Queries
{
    /// <summary>
    /// A message handler that handles query messages
    /// </summary>
    public interface IQueryHandler<TQueryMessage> : IMessageHandler<TQueryMessage>
        where TQueryMessage : IQueryMessage
    {
    }
}
