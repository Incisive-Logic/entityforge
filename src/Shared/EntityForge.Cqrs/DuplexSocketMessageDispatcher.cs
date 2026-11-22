using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using EntityForge.Utility;

namespace EntityForge.Cqrs
{
    /// <summary>
    /// Message dispatcher for duplex socket services
    ///
    /// When using a duplex socket service designed for a single instance per application,
    /// for example web sockets, the dispatcher can be used to dispatch the socket messages
    /// received to multiple appropriate target services.
    /// </summary>
    public class DuplexSocketMessageDispatcher
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private readonly IDuplexSocketService _duplexSocketService;
        private readonly List<IDuplexSocketMessageTarget> _messageTargets = new List<IDuplexSocketMessageTarget>();

        private Task _receiveTask;

        private static DuplexSocketMessageDispatcher _instance;

        /// <summary>Creates a dispatcher over the supplied duplex service and observes connection changes.</summary>
        public DuplexSocketMessageDispatcher(IDuplexSocketService duplexSocketService)
        {
            _duplexSocketService = duplexSocketService;

            // Listen for the connection state changes
            _duplexSocketService.ConnectionChanged += async (s, e) => await OnDuplexSocketConnectionChangedAsync(s, e);
        }

        /// <summary>Cancels and drains the receive loop and removes registered message targets.</summary>
        public async Task DisposeAsync()
        {
            _cancellationTokenSource.Cancel();

            if (_receiveTask != null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected if the task was cancelled
                }

                _receiveTask = null;
            }

            ClearMessageTargets();
        }

        /// <summary>Disposes and clears the shared dispatcher instance when one exists.</summary>
        public static async Task DisposeInstanceAsync()
        {
            if (_instance == null)
            {
                return;
            }

            await _instance.DisposeAsync().ConfigureAwait(false);
            _instance = null;
        }

        /// <summary>Registers a target to receive accepted messages and connection notifications.</summary>
        public void AddMessageTarget(IDuplexSocketMessageTarget messageTarget)
        {
            lock (_messageTargets)
            {
                _messageTargets.Add(messageTarget);
            }
        }

        /// <summary>Removes a registered message target.</summary>
        public void RemoveMessageTarget(IDuplexSocketMessageTarget messageTarget)
        {
            lock (_messageTargets)
            {
                _messageTargets.Remove(messageTarget);
            }
        }

        /// <summary>Removes all registered message targets.</summary>
        public void ClearMessageTargets()
        {
            lock (_messageTargets)
            {
                _messageTargets.Clear();
            }
        }

        private async Task OnDuplexSocketConnectionChangedAsync(object sender, ConnectionChangedEventArgs e)
        {
            if (e.IsConnected)
            {
                // Start listening for incoming messages on the socket
                _receiveTask = ReceiveAsync();
            }
            else
            {
                if (_receiveTask == null)
                {
                    return;
                }

                // Cancel the dispatcher receive task
                _cancellationTokenSource.Cancel();

                // Wait for the task to complete
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected if the task was cancelled
                }

                _receiveTask = null;

                // Reset the cancellation token source
                _cancellationTokenSource.TryReset();
            }
        }

        private async Task ReceiveAsync()
        {
            while (_duplexSocketService.IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                string messageString = null;

                try
                {
                    // Receive the message. Note as the dispatcher, this is the only place in the application waiting
                    // to receive messages from the socket as is the requirement for web sockets
                    messageString = await _duplexSocketService.ReceiveAsync(_cancellationTokenSource.Token).ConfigureAwait(false);

                    object messageObject = null;

                    try
                    {
                        messageObject = SerializedMessage.Deserialize(messageString);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceError($"Could not deserialize message: {ex.Message}");
                        continue;
                    }

                    List<IDuplexSocketMessageTarget> messageTargets = null;

                    lock (_messageTargets)
                    {
                        messageTargets = _messageTargets.ToList();
                    }

                    // Dispatch the message to the appropriate target
                    foreach (IDuplexSocketMessageTarget messageTarget in messageTargets)
                    {
                        if (messageTarget.AcceptsMessage(messageObject.GetType()))
                        {
                            messageTarget.OnMessageReceived(messageObject);
                        }
                    }
                }
                catch (DuplexSocketServiceException ex)
                {
                    // We have had a problem - if we need to reconnect, then this delegate should be called
                    // again at a later point with 'connected' state
                    System.Diagnostics.Trace.TraceError($"Could not receive message: {ex.Message}. May need to reconnect...");
                }
                catch (OperationCanceledException)
                {
                    // Cancellation requested, e.g. on dispose
                }
            }
        }

        /// <summary>Creates the shared dispatcher using the supplied duplex service.</summary>
        public static DuplexSocketMessageDispatcher CreateInstance(IDuplexSocketService duplexSocketService)
        {
            if (_instance == null)
            {
                _instance = new DuplexSocketMessageDispatcher(duplexSocketService);
            }

            return _instance;
        }

        /// <summary>Gets the shared dispatcher instance.</summary>
        public static DuplexSocketMessageDispatcher Instance => _instance ?? throw new InvalidOperationException("Instance not created");
    }
}
