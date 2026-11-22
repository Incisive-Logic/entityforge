using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Dispatches callbacks through the synchronization context captured on first access.</summary>
    public class UIDispatcher
    {
        private readonly SynchronizationContext _syncContext;

        private static UIDispatcher _instance;

        private UIDispatcher(SynchronizationContext syncContext)
        {
            _syncContext = syncContext ?? throw new ArgumentNullException(nameof(syncContext));
        }

        /// <summary>Runs an action synchronously through the captured synchronization context.</summary>
        public void Send(Action action)
        {
            _syncContext.Send((d) => action(), null);
        }

        /// <summary>Queues an action asynchronously through the captured synchronization context.</summary>
        public void Post(Action action)
        {
            _syncContext.Post((d) => action(), null);
        }

        // Call on UI thread
        /// <summary>Gets the shared dispatcher; first access must occur on the UI thread with a current synchronization context.</summary>
        public static UIDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new UIDispatcher(SynchronizationContext.Current);
                }

                return _instance;
            }
        }
    }
}
