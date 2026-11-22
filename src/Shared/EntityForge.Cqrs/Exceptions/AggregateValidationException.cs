using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Exceptions
{
    /// <summary>An aggregate violates a required domain invariant.</summary>
    public class AggregateValidationException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public AggregateValidationException(string message) : base(message)
        {
        }
    }
}
