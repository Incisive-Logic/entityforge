using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Provides event-based log and performance tracking hooks.</summary>
    public class EventTrackingBase : IEventTracking
    {
        /// <inheritdoc />
        public event PerformanceTrackingEventHandler PerformanceTrackingEvent;

        /// <inheritdoc />
        public event LogTrackingEventHandler LogTrackingEvent;

        /// <summary>Raises a performance notification for a completed timing scope.</summary>
        public void RaisePerformanceTrackingEvent(PerformanceTimerScope scope)
        {
            if (PerformanceTrackingEvent != null)
            {
                PerformanceTrackingEvent(this, scope);
            }
        }

        private void RaisePerformanceTrackingEvent(IEventTracking sender, PerformanceTimerScope scope)
        {
            if (PerformanceTrackingEvent != null)
            {
                PerformanceTrackingEvent(sender, scope);
            }
        }

        /// <summary>Emits a tracking notification for the supplied event or operation.</summary>
        protected void Track(TraceLevel traceLevel, string message)
        {
            if (LogTrackingEvent != null)
            {
                LogTrackingEvent(this, traceLevel, message);
            }
        }

        private void Track(IEventTracking eventTracking, TraceLevel traceLevel, string message)
        {
            if (LogTrackingEvent != null)
            {
                LogTrackingEvent(eventTracking, traceLevel, message);
            }
        }

        /// <summary>Begins forwarding tracking events from the supplied source.</summary>
        protected void StartTracking(IEventTracking eventTracking)
        {
            eventTracking.LogTrackingEvent += Track;
            eventTracking.PerformanceTrackingEvent += RaisePerformanceTrackingEvent;
        }

        /// <summary>Stops forwarding tracking events from the supplied source.</summary>
        protected void StopTracking(IEventTracking eventTracking)
        {
            eventTracking.PerformanceTrackingEvent -= RaisePerformanceTrackingEvent;
            eventTracking.LogTrackingEvent -= Track;
        }
    }
}
