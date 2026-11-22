using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Exceptions
{
    /// <summary>A repository operation failed.</summary>
    public class RepositoryException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public RepositoryException(string message) : base(message)
        {
        }
    }
}
