using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Telemetry
{
    /// <summary>Marks an event delivered through the telemetry event infrastructure.</summary>
    public interface ITelemetryEvent
    {
        /// <summary>
        /// Indicates the severity of the telemetry event - e.g. just informational, or a warning, or an error
        /// </summary>
        TraceLevel TraceLevel
        {
            get;
        }
    }
}
