using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Exceptions
{
    /// <summary>An entity operation failed.</summary>
    public class EntityException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public EntityException(string message) : base(message)
        {
        }
    }
}
