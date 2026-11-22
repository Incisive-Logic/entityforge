using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Receives elapsed-time notifications from performance scopes.</summary>
    public interface IPerformanceTimerScopeHost
    {
        /// <summary>Reports a completed scope whose timing threshold was exceeded.</summary>
        void RaisePerformanceTrackingEvent(PerformanceTimerScope scope);
    }
}
