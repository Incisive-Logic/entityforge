using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    #region Supporting Classes

    /// <summary>Indicates that monitor acquisition exceeded its timeout.</summary>
    public class TimedLockException : Exception
    {
        /// <summary>Creates a timeout failure carrying the attempted lock duration.</summary>
        public TimedLockException(TimeSpan timeout) : base($"Timed out waiting for timed lock for {timeout}")
        {
        }
    }

    #endregion

    /// <summary>
    /// A lock which throws an exception if it is kept waiting for the lock for a specified period of time
    /// </summary>
    public class TimedLock : IDisposable
    {
        private readonly object _lockObject;

        /// <summary>Acquires a monitor on a new private lock object within the requested timeout.</summary>
        /// <param name="timeout">The maximum time to wait for monitor acquisition.</param>
        public TimedLock(TimeSpan timeout)
        {

            _lockObject = new object();
            TryEnterLock(timeout);
        }

        /// <summary>Acquires the supplied object's monitor within the requested timeout.</summary>
        /// <param name="timeout">The maximum time to wait for monitor acquisition.</param>
        /// <param name="lockObject">The shared monitor object used by all callers that must exclude one another.</param>
        /// <exception cref="ArgumentNullException">The lock object is null.</exception>
        /// <exception cref="TimedLockException">Another thread holds the monitor beyond the timeout.</exception>
        /// <remarks>Monitor ownership is thread-affine. Dispose the scope on the acquiring thread; do not hold it across an await.</remarks>
        public TimedLock(TimeSpan timeout, object lockObject)
        {
            ArgumentNullException.ThrowIfNull(lockObject, nameof(lockObject));

            _lockObject = lockObject;
            TryEnterLock(timeout);
        }

        private void TryEnterLock(TimeSpan timeout)
        {
            if (!Monitor.TryEnter(_lockObject, timeout))
            {
                throw new TimedLockException(timeout);
            }
        }

        /// <summary>Exits the monitor owned by this scope; dispose on the thread that acquired it.</summary>
        public void Dispose()
        {
            Monitor.Exit(_lockObject);
        }
    }
}
