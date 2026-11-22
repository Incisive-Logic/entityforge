using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Exceptions
{
    /// <summary>A requested aggregate could not be found.</summary>
    public class AggregateNotFoundException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public AggregateNotFoundException(string message) : base(message)
        {
        }
    }
}
