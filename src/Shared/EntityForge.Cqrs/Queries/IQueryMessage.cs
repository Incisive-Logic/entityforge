using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Queries
{
    /// <summary>
    /// A message that represents a query (i.e. a request for
    /// information in a response)
    /// </summary>
    public interface IQueryMessage : IMessage
    {
        /// <summary>Gets the identity that correlates a query request with its outcome.</summary>
        Guid QueryID
        {
            get;
        }
    }
}
