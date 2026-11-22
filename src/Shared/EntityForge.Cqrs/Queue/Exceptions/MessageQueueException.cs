using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Queue.Exceptions
{
    /// <summary>A queued-message operation failed.</summary>
    public class MessageQueueException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public MessageQueueException(string message) : base(message)
        {
        }
    }
}
