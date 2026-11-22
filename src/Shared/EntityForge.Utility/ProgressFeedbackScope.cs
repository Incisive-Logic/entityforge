using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>
    /// Allows for processes that produce progress feedback to throttle the feedback to a specified interval
    /// </summary>
    public class ProgressFeedbackScope : IDisposable
    {
        private readonly Timer _progressTimer;
        private readonly Action _progressCallback;

        /// <summary>Creates a scoped progress feedback callback for the supplied operation.</summary>
        public ProgressFeedbackScope(TimeSpan interval, long totalProgress, Action progressCallback)
        {
            ArgumentNullException.ThrowIfNull(progressCallback, nameof(progressCallback));

            _progressTimer = new Timer(OnProgressTimerCallback,
                null,
                interval,
                interval);

            _progressCallback = progressCallback;
        }

        /// <summary>Completes the scope's progress feedback.</summary>
        public void Dispose()
        {
            // Won't wait for final timer callback to complete
            _progressTimer.Dispose();
        }

        private void OnProgressTimerCallback(object state)
        {
            if (Monitor.TryEnter(_progressTimer))
            {
                try
                {
                    _progressCallback?.Invoke();
                }
                finally
                {
                    Monitor.Exit(_progressTimer);
                }
            }
        }
    }
}
