using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Tracks a finite operation budget with a monotonic Stopwatch.</summary>
    public class TimeoutHelper : IDisposable
    {
        /// <summary>The two-second short operation budget.</summary>
        public static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(2.0);
        /// <summary>The five-second standard operation budget.</summary>
        public static readonly TimeSpan StandardTimeout = TimeSpan.FromSeconds(5.0);
        /// <summary>The ten-second long operation budget.</summary>
        public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(10.0);

        private readonly Stopwatch _stopwatch;
        private readonly TimeSpan _timeout;

        /// <summary>Starts measuring elapsed time against the supplied timeout.</summary>
        public TimeoutHelper(TimeSpan timeout)
        {

            _timeout = timeout;
            _stopwatch = Stopwatch.StartNew();
        }

        /// <summary>Returns the unused budget or throws TimeoutException after it has expired.</summary>
        public TimeSpan GetRemainingTime()
        {
            TimeSpan remainingTime = _timeout - _stopwatch.Elapsed;
            if (remainingTime < TimeSpan.Zero)
            {
                throw new TimeoutException("No remaining time");
            }

            return remainingTime;
        }

        /// <summary>Stops the timer and checks the remaining budget; disposal itself throws TimeoutException if the budget expired.</summary>
        public void Dispose()
        {
            _stopwatch.Stop();
            GetRemainingTime();
        }
    }
}
