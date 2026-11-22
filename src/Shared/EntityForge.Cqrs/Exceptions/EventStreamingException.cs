using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Exceptions
{
    /// <summary>An event-stream operation failed.</summary>
    public class EventStreamingException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public EventStreamingException(string message, Exception ex) : base(message, ex)
        {
        }
    }
}
