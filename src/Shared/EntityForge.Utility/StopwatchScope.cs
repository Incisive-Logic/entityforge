using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Measures a disposable scope using a Stopwatch.</summary>
    public class StopwatchScope : IDisposable
    {
        private readonly string _description;
        private readonly Stopwatch _stopwatch;

        /// <summary>Starts timing the scope with the supplied completion callback.</summary>
        public StopwatchScope(string description)
        {
            ArgumentException.ThrowIfNullOrEmpty(description, nameof(description));

            _stopwatch = Stopwatch.StartNew();
            _description = description;
        }

        /// <summary>Stops timing and reports the elapsed duration.</summary>
        public void Dispose()
        {
            _stopwatch.Stop();
            Debug.WriteLine($"{_description}: {_stopwatch.Elapsed}");
        }
    }
}
