using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Checks synchronization-context constraints before blocking operations.</summary>
    public static class SynchronizationContextHelper
    {
        /// <summary>Throws when called under a synchronization context that makes the operation unsafe to block.</summary>
        public static void ThrowOnSyncContext(SynchronizationContext syncContext, Exception ex)
        {
            if (syncContext == null)
            {
                throw new ArgumentNullException(nameof(syncContext));
            }

            if (ex == null)
            {
                throw new ArgumentNullException(nameof(ex));
            }

            // Preserve the exception stack trace, which mutates when rethrown
            var dispatchInfo = ExceptionDispatchInfo.Capture(ex);
            syncContext.Post(d =>
            {
                dispatchInfo.Throw();
            }, null);
        }
    }
}
