using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Exceptions
{
    /// <summary>A repository update conflicts with a newer version.</summary>
    public class OptimisticConcurrencyException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public OptimisticConcurrencyException(string message) : base(message)
        {
        }
    }
}
