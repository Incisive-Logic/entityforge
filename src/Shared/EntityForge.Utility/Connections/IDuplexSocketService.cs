using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace EntityForge.Utility
{
    /// <summary>
    /// Wraps appropriate exceptions when thrown by an IDuplexSocketService implementation
    /// </summary>
    public class DuplexSocketServiceException : Exception
    {
        /// <summary>Creates a duplex transport failure with a description and optional underlying cause.</summary>
        public DuplexSocketServiceException(string message) : base(message)
        {
        }

        /// <summary>Creates a duplex transport failure with a description and optional underlying cause.</summary>
        public DuplexSocketServiceException(string message, Exception ex) : base(message, ex)
        {
        }
    }

    /// <summary>Configures the endpoint used by a duplex socket service.</summary>
    public class DuplexSocketServiceOptions
    {
        /// <summary>Gets or sets the remote connection URI.</summary>
        public Uri Uri
        {
            get; set;
        }
    }

    /// <summary>Receives accepted messages and connection notifications from a duplex dispatcher.</summary>
    public interface IDuplexSocketMessageTarget
    {
        /// <summary>Checks whether this target accepts the supplied message CLR type.</summary>
        bool AcceptsMessage(Type messageType);

        /// <summary>Delivers one accepted message to the target.</summary>
        void OnMessageReceived(object message);

        /// <summary>Notifies the target of a connected or disconnected transport.</summary>
        void OnConnectionChanged(bool isConnected);
    }

    /// <summary>
    /// Represents a duplex socket service that can both send and receive messages (two way communication)
    /// </summary>
    public interface IDuplexSocketService : IConnection
    {
        /// <summary>Connects the duplex transport, observing cancellation.</summary>
        Task ConnectAsync(CancellationToken token);

        /// <summary>Closes the duplex transport, observing cancellation.</summary>
        Task CloseAsync(CancellationToken token);

        /// <summary>Sends one text message over the connected transport.</summary>
        Task SendAsync(string message, CancellationToken token);

        /// <summary>Receives one text message from the transport.</summary>
        Task<string> ReceiveAsync(CancellationToken token);

        /// <summary>Gets the remote endpoint used by the service.</summary>
        Uri ConnectionUri
        {
            get;
        }
    }
}
