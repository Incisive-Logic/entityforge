using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Measures a named scope and reports warning or error threshold overruns on disposal.</summary>
    public class PerformanceTimerScope : IDisposable
    {
        private static readonly TimeSpan s_defaultWarningThreshold = TimeSpan.FromSeconds(2.0);
        private static readonly TimeSpan s_defaultErrorThreshold = TimeSpan.FromSeconds(6.0);

        private readonly string _name;
        private readonly TimeSpan _warningThreshold;
        private readonly TimeSpan _errorThreshold;
        private readonly IPerformanceTimerScopeHost _host;

        private readonly Stopwatch _stopwatch;

        /// <summary>Starts timing a named scope; default warning and error thresholds are two and six seconds.</summary>
        public PerformanceTimerScope(string name, IPerformanceTimerScopeHost host, TimeSpan? warningThreshold = null, TimeSpan? errorThreshold = null)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
            ArgumentNullException.ThrowIfNull(host, nameof(host));

            _name = name;
            _host = host;
            _warningThreshold = warningThreshold.HasValue ? warningThreshold.Value : s_defaultWarningThreshold;
            _errorThreshold = errorThreshold.HasValue ? errorThreshold.Value : s_defaultErrorThreshold;

            _stopwatch = Stopwatch.StartNew();
        }

        private void RaiseThresholdOverrun(string description, TimeSpan threshold)
        {
            if (_host != null)
            {
                _host.RaisePerformanceTrackingEvent(this);
            }
        }

        /// <summary>Stops timing and reports an exceeded threshold, preferring the error threshold when both are exceeded.</summary>
        public void Dispose()
        {
            _stopwatch.Stop();

            // Check errors first
            if (IsOverErrorThreshold)
            {
                RaiseThresholdOverrun("error", ErrorThreshold);
            }
            else if (IsOverWarningThreshold)
            {
                RaiseThresholdOverrun("warning", WarningThreshold);
            }
        }

        /// <summary>Gets the operation name associated with this scope.</summary>
        public string Name
        {
            get
            {
                return _name;
            }
        }

        /// <summary>Gets the measured elapsed duration.</summary>
        public TimeSpan ScopeTimeSpan
        {
            get
            {
                return _stopwatch.Elapsed;
            }
        }

        /// <summary>Gets the duration above which warning-level tracking is triggered.</summary>
        public TimeSpan WarningThreshold
        {
            get
            {
                return _warningThreshold;
            }
        }

        /// <summary>Gets the duration above which error-level tracking is triggered.</summary>
        public TimeSpan ErrorThreshold
        {
            get
            {
                return _errorThreshold;
            }
        }

        /// <summary>Gets whether elapsed time is strictly greater than the warning threshold.</summary>
        public bool IsOverWarningThreshold
        {
            get
            {
                return ScopeTimeSpan > WarningThreshold;
            }
        }

        /// <summary>Gets whether elapsed time is strictly greater than the error threshold.</summary>
        public bool IsOverErrorThreshold
        {
            get
            {
                return ScopeTimeSpan > ErrorThreshold;
            }
        }
    }
}
