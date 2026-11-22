using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace EntityForge.Utility
{
    /// <summary>
    /// Provides a duplex web socket client service implementation
    /// </summary>
    public class WebSocketDuplexSocketService : IDuplexSocketService
    {
        private readonly object _webSocketLock = new object();

        private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _receiveSemaphore = new SemaphoreSlim(1, 1);

        private ClientWebSocket _webSocket;
        private const int BufferBlockSize = 32 * 1024;      // 32k

        private readonly DuplexSocketServiceOptions _options;

        private bool _isConnected;

        /// <inheritdoc />
        public event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;

        /// <summary>Creates a duplex text transport using the configured WebSocket endpoint.</summary>
        public WebSocketDuplexSocketService(DuplexSocketServiceOptions options)
        {
            _options = options;
        }

        private void HandleException(Exception ex, string message)
        {
            // WasmWebSocket, for time being being, does not inform us of a change in socket state
            // and so we will take the opportunity here to update the connection state, as this exception
            // may have been due to a connection failure
            IsConnected = _webSocket != null && _webSocket.State == WebSocketState.Open;

            throw new DuplexSocketServiceException(message, ex);
        }

        /// <inheritdoc />
        public async Task ConnectAsync(CancellationToken token)
        {
            lock (_webSocketLock)
            {
                if (IsConnected)
                {
                    return;    // No need connect if already connected
                }

                // Once a connection is closed, the lifetime of the web socket is over so we always
                // create a new one on first connect or reconnect
                _webSocket = new ClientWebSocket();
            }

            // Attempt to connect. Note we currently can't use await on Uno WASM so we wait
            // synchronously for the connection to complete within an outer task for now
            try
            {
                await _webSocket.ConnectAsync(_options.Uri, token);

                // Indicate we have connected - in theory should be open as no exception was thrown by above
                IsConnected = _webSocket.State == WebSocketState.Open;
            }
            catch (Exception ex)
            {
                HandleException(ex, $"Failed to connect to web socket server at {_options.Uri}");
            }
        }

        /// <inheritdoc />
        public async Task CloseAsync(CancellationToken token)
        {
            lock (_webSocketLock)
            {
                // No need to close if already closed
                if (_webSocket == null || _webSocket.State == WebSocketState.Closed)
                {
                    return;
                }
            }

            // Attempt to close. Note we currently can't use await on Uno WASM so we wait
            // synchronously for the connection to complete within an outer task for now
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Gracefully closed by client", token);

                // Indicate we have disconnected - in theory should have as no exception thrown by the above
                IsConnected = _webSocket.State != WebSocketState.Open;
            }
            catch (Exception ex)
            {
                HandleException(ex, "Failed to close the connection");
            }
        }

        /// <inheritdoc />
        public async Task SendAsync(string message, CancellationToken token)
        {
            // Note that a web socket can only have one outstanding send at a time
            await _sendSemaphore.WaitAsync(token);

            try
            {
                // Send text message - we always send a complete message for now
                await _webSocket.SendAsync(new ArraySegment<byte>(System.Text.Encoding.UTF8.GetBytes(message)),
                    WebSocketMessageType.Binary, true, token);
            }
            catch (Exception ex)
            {
                HandleException(ex, "Failed to send message");
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        /// <inheritdoc />
        public async Task<string> ReceiveAsync(CancellationToken token)
        {
            await _receiveSemaphore.WaitAsync(token);

            try
            {
                // Note that a web socket can only have one outstanding receive at a time
                byte[] buffer = new byte[BufferBlockSize];
                bool isComplete = false;

                StringBuilder messageBuilder = new StringBuilder();

                while (!isComplete)
                {
                    WebSocketReceiveResult webSocketReceiveResult = null;

                    try
                    {
                        webSocketReceiveResult = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                        if (webSocketReceiveResult.MessageType == WebSocketMessageType.Binary)
                        {
                            // Process the message
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, webSocketReceiveResult.Count));

                            // If it is not completed, we will go around again and append the new data
                            // Otherwise, we will finish with the complete message
                            isComplete = webSocketReceiveResult.EndOfMessage;
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        HandleException(ex, "Failed to receive message");
                    }

                    if (webSocketReceiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        // Server has closed the connection when we have been calling to receive a message
                        // buffer, so we close our end and throw an exception which must be handled by the caller
                        await CloseAsync(token);

                        throw new DuplexSocketServiceException("Server has closed the connection");
                    }

                    // We only support text and close messages, at the moment
                    throw new DuplexSocketServiceException($"Unsupported message type {webSocketReceiveResult.MessageType}");
                }

                // Return the complete message
                return messageBuilder.ToString();
            }
            finally
            {
                _receiveSemaphore.Release();
            }
        }

        /// <inheritdoc />
        public bool IsConnected
        {
            get
            {
                lock (_webSocketLock)
                {
                    return _isConnected;
                }
            }

            set
            {
                lock (_webSocketLock)
                {
                    if (_isConnected != value)
                    {
                        _isConnected = value;

                        if (!_isConnected)
                        {
                            // Once the connection is closed, it is the end of the web socket lifetime
                            // and we cannot reconnect again
                            _webSocket = null;
                        }
                    }
                }

                ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs() { IsConnected = value });
            }
        }

        /// <summary>
        /// The URI to which the web socket connection is made
        /// </summary>
        public Uri ConnectionUri => _options.Uri;
    }
}
