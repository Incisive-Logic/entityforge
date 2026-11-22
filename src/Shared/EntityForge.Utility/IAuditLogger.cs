using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>
    /// Provides methods for logging audit information at various trace levels.
    /// </summary>
    public interface IAuditLogger
    {
        /// <summary>Records an audit event with the supplied message and context.</summary>
        void Audit(TraceLevel traceLevel, string message);
    }
}
