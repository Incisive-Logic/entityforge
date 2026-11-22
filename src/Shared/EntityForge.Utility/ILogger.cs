using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>
    /// Defines a contract for logging messages at various levels of severity.
    /// </summary>
    /// <remarks>This interface provides methods for logging messages categorized by severity, including
    /// success, debug, informational, warning, and error levels. Implementations of this interface may handle logging
    /// in different ways, such as writing to a file, displaying in a console, or sending to a remote server.</remarks>
    public interface ILogger
    {
        /// <summary>Writes a success-level message.</summary>
        void Success(string message);

        /// <summary>Writes a diagnostic debug message.</summary>
        void Debug(string message);

        /// <summary>Writes an informational message.</summary>
        void Info(string message);

        /// <summary>Writes a warning message.</summary>
        void Warning(string message);

        /// <summary>Writes an error message.</summary>
        void Error(string message, Exception ex = null);           // Exception is optional
    }
}
