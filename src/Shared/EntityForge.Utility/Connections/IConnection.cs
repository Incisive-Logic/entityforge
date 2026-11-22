using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Reports a connection's new connected or disconnected state.</summary>
    public class ConnectionChangedEventArgs : EventArgs
    {
        /// <summary>Gets or sets the reported connection state.</summary>
        public bool IsConnected
        {
            get; set;
        }
    }

    /// <summary>Exposes connection state and change notifications.</summary>
    public interface IConnection
    {
        /// <summary>Occurs when the connection state changes.</summary>
        event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;

        /// <summary>Gets whether the transport is currently connected.</summary>
        bool IsConnected
        {
            get;
        }
    }
}
