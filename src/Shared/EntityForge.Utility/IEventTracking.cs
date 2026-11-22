using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    #region Supporting Classes

    /// <summary>Handles a log-tracking notification.</summary>
    public delegate void LogTrackingEventHandler(IEventTracking sender, TraceLevel traceLevel, string message);

    /// <summary>Handles a performance-timing notification.</summary>
    public delegate void PerformanceTrackingEventHandler(IEventTracking sender, PerformanceTimerScope scope);

    #endregion

    /// <summary>Exposes log and performance notifications from an observable component.</summary>
    public interface IEventTracking : IPerformanceTimerScopeHost
    {
        /// <summary>Occurs when the component reports a performance-timing event.</summary>
        event PerformanceTrackingEventHandler PerformanceTrackingEvent;

        /// <summary>Occurs when the component reports a tracked log event.</summary>
        event LogTrackingEventHandler LogTrackingEvent;
    }
}
