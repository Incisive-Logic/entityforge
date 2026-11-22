using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Exceptions
{
    /// <summary>A unit-of-work operation failed.</summary>
    public class UnitOfWorkException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public UnitOfWorkException(string message) : base(message)
        {
        }
    }
}
