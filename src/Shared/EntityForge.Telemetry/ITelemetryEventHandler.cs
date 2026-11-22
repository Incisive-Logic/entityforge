using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Telemetry
{
    /// <summary>Handles telemetry events selected by event type.</summary>
    public interface ITelemetryEventHandler
    {
        /// <summary>Checks whether this handler accepts the supplied telemetry event type.</summary>
        bool CanHandleEvent(ITelemetryEvent telemetryEvent);

        /// <summary>Processes an accepted telemetry event asynchronously.</summary>
        Task HandleAsync(ITelemetryEvent telemetryEvent);
    }
}
