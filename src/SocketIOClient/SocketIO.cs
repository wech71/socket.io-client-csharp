using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SocketIOClient.Extensions;
using SocketIOClient.JsonSerializer;
using SocketIOClient.Messages;
using SocketIOClient.Transport;
using SocketIOClient.Transport.Http;
using SocketIOClient.Transport.WebSockets;
using SocketIOClient.UriConverters;
using static System.Net.Mime.MediaTypeNames;

namespace SocketIOClient
{
    /// <summary>
    /// socket.io client class
    /// </summary>
    public class SocketIO : IDisposable
    {
        /// <summary>
        /// Create SocketIO object with default options
        /// </summary>
        /// <param name="uri"></param>
        public SocketIO(string uri) : this(new Uri(uri)) { }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        public SocketIO(Uri uri) : this(uri, new SocketIOOptions()) { }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="options"></param>
        public SocketIO(string uri, SocketIOOptions options) : this(new Uri(uri), options) { }

        /// <summary>
        /// Create SocketIO object with options
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="options"></param>
        public SocketIO(Uri uri, SocketIOOptions options)
        {
            ServerUri = uri ?? throw new ArgumentNullException("uri");
            Options = options ?? throw new ArgumentNullException("options");
            Initialize();
        }

        Uri _serverUri;
        private Uri ServerUri
        {
            get => _serverUri;
            set
            {
                if (_serverUri != value)
                {
                    _serverUri = value;
                    if (value != null && value.AbsolutePath != "/")
                    {
                        _namespace = value.AbsolutePath;
                    }
                }
            }
        }

        /// <summary>
        /// An unique identifier for the socket session. Set after the connect event is triggered, and updated after the reconnect event.
        /// </summary>
        public string Id { get; private set; }

        string _namespace;

        /// <summary>
        /// Whether or not the socket is connected to the server.
        /// </summary>
        public bool Connected { get; private set; }

        int _attempts;

        [Obsolete]
        /// <summary>
        /// Whether or not the socket is disconnected from the server.
        /// </summary>
        public bool Disconnected => !Connected;

        public SocketIOOptions Options { get; }

        public IJsonSerializer JsonSerializer { get; set; }        

        public Func<IClientWebSocket> ClientWebSocketProvider { get; set; }
        public Func<IHttpClientAdapter> HttpClientAdapterProvider { get; set; }

        List<IDisposable> _resources = new List<IDisposable>();

        BaseTransport _transport;
        TransportProtocol _upgradeTo = TransportProtocol.Polling;

        List<Type> _expectedExceptions;

        int _packetId;
        bool _isConnectCoreRunning;
        Uri _realServerUri;
        Exception _connectCoreException;
        Dictionary<int, Action<SocketIOResponse>> _ackHandlers;
        List<OnAnyHandler> _onAnyHandlers;
        Dictionary<string, Action<SocketIOResponse>> _eventHandlers;
        CancellationTokenSource _connectionTokenSource;
        double _reconnectionDelay;
        bool _hasError;
        bool _isFaild;
        readonly static object _connectionLock = new object();

        #region Socket.IO event
        public event EventHandler OnConnected;
        //public event EventHandler<string> OnConnectError;
        //public event EventHandler<string> OnConnectTimeout;
        public event EventHandler<string> OnError;
        public event EventHandler<string> OnDisconnected;

        /// <summary>
        /// Fired upon a successful reconnection.
        /// </summary>
        public event EventHandler<int> OnReconnected;

        /// <summary>
        /// Fired upon an attempt to reconnect.
        /// </summary>
        public event EventHandler<int> OnReconnectAttempt;

        /// <summary>
        /// Fired upon a reconnection attempt error.
        /// </summary>
        public event EventHandler<Exception> OnReconnectError;

        /// <summary>
        /// Fired when couldn’t reconnect within reconnectionAttempts
        /// </summary>
        public event EventHandler OnReconnectFailed;
        public event EventHandler OnPing;
        public event EventHandler<TimeSpan> OnPong;

        #endregion

        #region Observable Event
        //Subject<Unit> _onConnected;
        //public IObservable<Unit> ConnectedObservable { get; private set; }
        #endregion

        private void Initialize()
        {
            _packetId = -1;
            _ackHandlers = new Dictionary<int, Action<SocketIOResponse>>();
            _eventHandlers = new Dictionary<string, Action<SocketIOResponse>>();
            _onAnyHandlers = new List<OnAnyHandler>();

            JsonSerializer = new SystemTextJsonSerializer();

            ClientWebSocketProvider = () => new SystemNetWebSocketsClientWebSocket();
            HttpClientAdapterProvider = () => new DefaultHttpClientAdapter();
            _expectedExceptions = new List<Type>
            {
                typeof(TimeoutException),
                typeof(WebSocketException),
                typeof(HttpRequestException),
                typeof(OperationCanceledException),
                typeof(TaskCanceledException)
            };
        }

        private BaseTransport CreateTransport(TransportProtocol transportProtocol, List<IDisposable> resources)
        {
            var transportOptions = new TransportOptions
            {
                EIO = Options.EIO,
                Query = Options.Query,
                Auth = GetAuth(Options.Auth),
                ConnectionTimeout = Options.ConnectionTimeout
            };

            BaseTransport transport;
            if (transportProtocol== TransportProtocol.Polling)
            {
                var adapter = CreateHttpClientAdapter();
                if (adapter is null)
                {
                    throw new ArgumentNullException(nameof(HttpClientAdapterProvider), $"{HttpClientAdapterProvider} returns a null");
                }
                resources.Add(adapter);
                var handler = HttpPollingHandler.CreateHandler(transportOptions.EIO, adapter);
                transport = new HttpTransport(transportOptions, handler);
            }
            else
            {
                var ws = ClientWebSocketProvider();
                if (ws is null)
                {
                    throw new ArgumentNullException(nameof(ClientWebSocketProvider), $"{ClientWebSocketProvider} returns a null");
                }
                resources.Add(ws);
                transport = new WebSocketTransport(transportOptions, ws);
            }
            transport.Namespace = _namespace;
            SetHeaders(transport);
            transport.SetProxy(Options.Proxy);
            resources.Add(transport);
            return transport;
        }

        private IHttpClientAdapter CreateHttpClientAdapter()
        {
            var httpClientAdapter = HttpClientAdapterProvider();
            httpClientAdapter.SetProxy(Options.Proxy);
            return httpClientAdapter;
        }

        private void CreateTransport()
        {
            var connectResponse = GetProtocolAsync().GetAwaiter().GetResult();
            _transport = CreateTransport(Options.Transport, _resources);

            if (connectResponse != null)
            {
                if (Options.Transport != connectResponse.Protocol)
                {
                    if (connectResponse.Protocol == TransportProtocol.WebSocket)
                    {
                        _upgradeTo = TransportProtocol.WebSocket;
                        Options.Sid = connectResponse.Sid;
                        Task.Factory.StartNew(async () =>
                        {
                            await TryUpgrade(connectResponse.Protocol, connectResponse.Sid);
                        });
                    }
                }
            }

        }

        private async Task<bool> TryUpgrade(TransportProtocol protocol, string sid)
        {
            List<IDisposable> tmpResources = new List<IDisposable>();
            try
            {
                var cancellationTokenSource = new CancellationTokenSource();
                var websocketTransport = CreateTransport(protocol, tmpResources);

                ////connect to default namespace "/" message(4) connect(0) => "40"
                ////https://github.com/socketio/socket.io-protocol#4-message  Request n°2
                //Uri uri = UriConverter.GetServerUri(false, ServerUri, Options.EIO, sid, Options.Path, Options.Query);
                //string text = await(await HttpClient.PostAsync(uri, new StringContent("40"))).Content.ReadAsStringAsync();
                //if (text != "ok")
                //    throw new Exception("unexpected response for message 0" + text);

                var upgradeProbeTimeout = new CancellationTokenSource();
                var upgradedSignal = new ManualResetEvent(false);
                websocketTransport.OnReceived = async (msg) => 
                {
                    if (msg is PongMessage && ((PongMessage)msg).Payload == "probe")
                    {
                        await _transport.Pause();
                        await _transport.DisconnectAsync(cancellationTokenSource.Token);
                        DisposeResources();
                        _resources.AddRange(tmpResources);
                        tmpResources.Clear();
                        _transport = websocketTransport;
                        upgradedSignal.Set();
                        await websocketTransport.SendAsync(new UpgradeMessage(), cancellationTokenSource.Token);
                    }
                };

                Uri uri = UriConverter.GetServerUri(true, ServerUri, Options.EIO, sid, Options.Path, Options.Query);
                await websocketTransport.ConnectAsync(uri, cancellationTokenSource.Token);
                await websocketTransport.SendAsync(new PingMessage("probe"), cancellationTokenSource.Token);

                if(WaitHandle.WaitAny(new WaitHandle[] { upgradedSignal, cancellationTokenSource.Token.WaitHandle }, Options.ConnectionTimeout) == WaitHandle.WaitTimeout)
                {
                    cancellationTokenSource.Cancel();
                    return false;
                }

                //while (true)
                //{
                //    //text = await HttpClient.GetStringAsync(uri); // => 40{"sid":"wZX3oN0bSVIhsaknAAAI"}
                //    //OnTextReceived(text);
                //}
            }
            finally 
            {
                foreach (IDisposable disposable in tmpResources) 
                    disposable.Dispose();
            }
            return true;
        }

        private string GetAuth(object auth)
        {
            if (auth == null)
                return string.Empty;
            var result = JsonSerializer.Serialize(new[] { auth });
            return result.Json.TrimStart('[').TrimEnd(']');
        }

        private void SetHeaders()
        {
            SetHeaders(_transport);
        }

        private void SetHeaders(BaseTransport transport)
        {
            if (Options.ExtraHeaders != null)
            {
                foreach (var item in Options.ExtraHeaders)
                {
                    transport.AddHeader(item.Key, item.Value);
                }
            }
        }

        private void SyncExceptionToMain(Exception e)
        {
            _connectCoreException = e;
            _isConnectCoreRunning = false;
        }

        private string GetExceptionMessage()
        {
            return $"Cannot connect to server '{ServerUri}'";
        }

        private void DisposeResources()
        {
            foreach (var item in _resources)
            {
                item.TryDispose();
            }
            _resources.Clear();
        }

        private void ConnectCore()
        {
            DisposeForReconnect();
            _reconnectionDelay = Options.ReconnectionDelay;
            _connectionTokenSource = new CancellationTokenSource();
            var cct = _connectionTokenSource.Token;
            Task.Factory.StartNew(async () =>
            {
                while (true)
                {
                    DisposeResources();
                    CreateTransport();
                    _realServerUri = UriConverter.GetServerUri(Options.Transport == TransportProtocol.WebSocket, ServerUri, Options.EIO, Options.Sid, Options.Path, Options.Query);
                    try
                    {
                        if (cct.IsCancellationRequested)
                            break;
                        if (_attempts > 0)
                            OnReconnectAttempt.TryInvoke(this, _attempts);
                        var timeoutCts = new CancellationTokenSource(Options.ConnectionTimeout);
                        _transport.OnReceived = OnMessageReceived;
                        _transport.OnError = OnErrorReceived;
                        await _transport.ConnectAsync(_realServerUri, timeoutCts.Token).ConfigureAwait(false);

                        if (Options.EIO == EngineIO.V4)
                        {
                            var connectNamespaceMessage = new ConnectedMessage(); //connect to namespace (https://github.com/socketio/socket.io-protocol#4-message Request n°2 (namespace connection request))
                            try
                            {
                                await _transport.SendAsync(connectNamespaceMessage, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception e)
                            {
#if DEBUG
                                System.Diagnostics.Debug.WriteLine(e);
#endif
                            }
                        }
                        break;
                    }
                    catch (Exception e)
                    {
                        if (_expectedExceptions.Contains(e.GetType()))
                        {
                            if (!Options.Reconnection)
                            {
                                SyncExceptionToMain(e);
                                throw;
                            }
                            if (_attempts > 0)
                            {
                                OnReconnectError.TryInvoke(this, e);
                            }
                            _attempts++;
                            if (_attempts <= Options.ReconnectionAttempts)
                            {
                                if (_reconnectionDelay < Options.ReconnectionDelayMax)
                                {
                                    _reconnectionDelay += 2 * Options.RandomizationFactor;
                                }
                                if (_reconnectionDelay > Options.ReconnectionDelayMax)
                                {
                                    _reconnectionDelay = Options.ReconnectionDelayMax;
                                }
                                Thread.Sleep((int)_reconnectionDelay);
                            }
                            else
                            {
                                _isFaild = true;
                                OnReconnectFailed.TryInvoke(this, EventArgs.Empty);
                                break;
                            }
                        }
                        else
                        {
                            SyncExceptionToMain(e);
                            throw;
                        }
                    }
                }
                _isConnectCoreRunning = false;
            });
        }

        // https://github.com/socketio/socket.io-protocol#4-message Request n°1 (open packet)
        private async Task<OpenedMessage> GetProtocolAsync()
        {
            if (Options.Transport == TransportProtocol.Polling && Options.AutoUpgrade)
            {
                Uri uri = UriConverter.GetServerUri(false, ServerUri, Options.EIO, Options.Sid, Options.Path, Options.Query);
                try
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine($"GetStringAsync({uri})");
#endif

                    HttpClient httpClient = CreateHttpClientAdapter().HttpClient;
                    foreach (var entry in Options.ExtraHeaders)
                        httpClient.DefaultRequestHeaders.Add(entry.Key, entry.Value);

                    string text = await httpClient.GetStringAsync(uri);
                    if (text.StartsWith("0")) //engine.io Open Message?
                    {
                        var openedMessage = new OpenedMessage();
                        openedMessage.Read(text.Substring(1));
                        if (openedMessage.Upgrades.Contains("websocket"))
                        {
                            openedMessage.Protocol = TransportProtocol.WebSocket;
                        }
                        return openedMessage;
                    }
                }
                catch (Exception e)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine(e);
#endif
                }
            }

            return null;
        }

        public async Task ConnectAsync()
        {
            if (Connected || _isConnectCoreRunning)
                return;

            lock (_connectionLock)
            {
                if (_isConnectCoreRunning)
                    return;
                _isConnectCoreRunning = true;
            }
            ConnectCore();
            while (_isConnectCoreRunning)
            {
                await Task.Delay(100);
            }
            if (_connectCoreException != null)
            {
                //Logger.LogError(_connectCoreException, _connectCoreException.Message);
                throw new ConnectionException(GetExceptionMessage(), _connectCoreException);
            }
            int ms = 0;
            while (!Connected)
            {
                if (_hasError)
                {
                    //Logger.LogWarning($"Got a connection error, try to use '{nameof(OnError)}' to detect it.");
                    break;
                }
                if (_isFaild)
                {
                    //Logger.LogWarning($"Reconnect failed, try to use '{nameof(OnReconnectFailed)}' to detect it.");
                    break;
                }
                ms += 100;
                if (ms > Options.ConnectionTimeout.TotalMilliseconds)
                {
                    throw new ConnectionException(GetExceptionMessage(), new TimeoutException());
                }
                await Task.Delay(100);
            }
        }

        private void PingHandler()
        {
            OnPing.TryInvoke(this, EventArgs.Empty);
        }

        private void PongHandler(PongMessage msg)
        {
            OnPong.TryInvoke(this, msg.Duration);
        }

        private void ConnectedHandler(ConnectedMessage msg)
        {
            Id = msg.Sid ?? Options.Sid; //socket.io v5 sends sid, v4 does not
            Connected = true;
            OnConnected.TryInvoke(this, EventArgs.Empty);
            if (_attempts > 0)
            {
                OnReconnected.TryInvoke(this, _attempts);
            }
            _attempts = 0;
        }

        private void DisconnectedHandler()
        {
            _ = InvokeDisconnect(DisconnectReason.IOServerDisconnect);
        }

        private void EventMessageHandler(EventMessage m)
        {
            var res = new SocketIOResponse(m.JsonElements, this)
            {
                PacketId = m.Id
            };
            foreach (var item in _onAnyHandlers)
            {
                item.TryInvoke(m.Event, res);
            }
            if (_eventHandlers.ContainsKey(m.Event))
            {
                _eventHandlers[m.Event].TryInvoke(res);
            }
        }

        private void AckMessageHandler(ClientAckMessage m)
        {
            if (_ackHandlers.ContainsKey(m.Id))
            {
                var res = new SocketIOResponse(m.JsonElements, this);
                _ackHandlers[m.Id].TryInvoke(res);
                _ackHandlers.Remove(m.Id);
            }
        }

        private void ErrorMessageHandler(ErrorMessage msg)
        {
            _hasError = true;
            OnError.TryInvoke(this, msg.Message);
        }

        private void BinaryMessageHandler(BinaryMessage msg)
        {
            var response = new SocketIOResponse(msg.JsonElements, this)
            {
                PacketId = msg.Id,
            };
            response.InComingBytes.AddRange(msg.IncomingBytes);
            foreach (var item in _onAnyHandlers)
            {
                item.TryInvoke(msg.Event, response);
            }
            if (_eventHandlers.ContainsKey(msg.Event))
            {
                _eventHandlers[msg.Event].TryInvoke(response);
            }
        }

        private void BinaryAckMessageHandler(ClientBinaryAckMessage msg)
        {
            if (_ackHandlers.ContainsKey(msg.Id))
            {
                var response = new SocketIOResponse(msg.JsonElements, this)
                {
                    PacketId = msg.Id,
                };
                response.InComingBytes.AddRange(msg.IncomingBytes);
                _ackHandlers[msg.Id].TryInvoke(response);
            }
        }

        private void OnErrorReceived(Exception ex)
        {
            //Logger.LogError(ex, ex.Message);
            _ = InvokeDisconnect(DisconnectReason.TransportClose);
        }

        private void OnMessageReceived(IMessage msg)
        {
            try
            {
                switch (msg.Type)
                {
                    case MessageType.Ping:
                        PingHandler();
                        break;
                    case MessageType.Pong:
                        PongHandler(msg as PongMessage);
                        break;
                    case MessageType.Connected:
                        ConnectedHandler(msg as ConnectedMessage);
                        break;
                    case MessageType.Disconnected:
                        DisconnectedHandler();
                        break;
                    case MessageType.EventMessage:
                        EventMessageHandler(msg as EventMessage);
                        break;
                    case MessageType.AckMessage:
                        AckMessageHandler(msg as ClientAckMessage);
                        break;
                    case MessageType.ErrorMessage:
                        ErrorMessageHandler(msg as ErrorMessage);
                        break;
                    case MessageType.BinaryMessage:
                        BinaryMessageHandler(msg as BinaryMessage);
                        break;
                    case MessageType.BinaryAckMessage:
                        BinaryAckMessageHandler(msg as ClientBinaryAckMessage);
                        break;
                }
            }
            catch (Exception e)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine(e);
#endif
            }
        }

        public async Task DisconnectAsync()
        {
            if (Connected)
            {
                var msg = new DisconnectedMessage
                {
                    Namespace = _namespace
                };
                try
                {
                    await _transport.SendAsync(msg, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine(e);
#endif
                }
                await InvokeDisconnect(DisconnectReason.IOClientDisconnect);
            }
        }

        /// <summary>
        /// Register a new handler for the given event.
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="callback"></param>
        public void On(string eventName, Action<SocketIOResponse> callback)
        {
            if (_eventHandlers.ContainsKey(eventName))
            {
                _eventHandlers.Remove(eventName);
            }
            _eventHandlers.Add(eventName, callback);
        }



        /// <summary>
        /// Unregister a new handler for the given event.
        /// </summary>
        /// <param name="eventName"></param>
        public void Off(string eventName)
        {
            if (_eventHandlers.ContainsKey(eventName))
            {
                _eventHandlers.Remove(eventName);
            }
        }

        public void OnAny(OnAnyHandler handler)
        {
            if (handler != null)
            {
                _onAnyHandlers.Add(handler);
            }
        }

        public void PrependAny(OnAnyHandler handler)
        {
            if (handler != null)
            {
                _onAnyHandlers.Insert(0, handler);
            }
        }

        public void OffAny(OnAnyHandler handler)
        {
            if (handler != null)
            {
                _onAnyHandlers.Remove(handler);
            }
        }

        public OnAnyHandler[] ListenersAny() => _onAnyHandlers.ToArray();

        internal async Task ClientAckAsync(int packetId, CancellationToken cancellationToken, params object[] data)
        {
            IMessage msg;
            if (data != null && data.Length > 0)
            {
                var result = JsonSerializer.Serialize(data);
                if (result.Bytes.Count > 0)
                {
                    msg = new ServerBinaryAckMessage
                    {
                        Id = packetId,
                        Namespace = _namespace,
                        Json = result.Json
                    };
                    msg.OutgoingBytes = new List<byte[]>(result.Bytes);
                }
                else
                {
                    msg = new ServerAckMessage
                    {
                        Namespace = _namespace,
                        Id = packetId,
                        Json = result.Json
                    };
                }
            }
            else
            {
                msg = new ServerAckMessage
                {
                    Namespace = _namespace,
                    Id = packetId
                };
            }
            await _transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Emits an event to the socket
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="data">Any other parameters can be included. All serializable datastructures are supported, including byte[]</param>
        /// <returns></returns>
        public async Task EmitAsync(string eventName, params object[] data)
        {
            await EmitAsync(eventName, CancellationToken.None, data).ConfigureAwait(false);
        }

        public async Task EmitAsync(string eventName, CancellationToken cancellationToken, params object[] data)
        {
            if (data != null && data.Length > 0)
            {
                var result = JsonSerializer.Serialize(data);
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Send] {eventName} {result}");
#endif

                if (result.Bytes.Count > 0)
                {
                    var msg = new BinaryMessage
                    {
                        Namespace = _namespace,
                        OutgoingBytes = new List<byte[]>(result.Bytes),
                        Event = eventName,
                        Json = result.Json
                    };
                    await _transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var msg = new EventMessage
                    {
                        Namespace = _namespace,
                        Event = eventName,
                        Json = result.Json
                    };
                    await _transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Send] {eventName}");
#endif

                var msg = new EventMessage
                {
                    Namespace = _namespace,
                    Event = eventName
                };
                await _transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Emits an event to the socket
        /// </summary>
        /// <param name="eventName"></param>
        /// <param name="ack">will be called with the server answer.</param>
        /// <param name="data">Any other parameters can be included. All serializable datastructures are supported, including byte[]</param>
        /// <returns></returns>
        public async Task EmitAsync(string eventName, Action<SocketIOResponse> ack, params object[] data)
        {
            await EmitAsync(eventName, CancellationToken.None, ack, data).ConfigureAwait(false);
        }

        public async Task EmitAsync(string eventName, CancellationToken cancellationToken, Action<SocketIOResponse> ack, params object[] data)
        {
            _ackHandlers.Add(++_packetId, ack);
            if (data != null && data.Length > 0)
            {
                var result = JsonSerializer.Serialize(data);
                if (result.Bytes.Count > 0)
                {
                    var msg = new ClientBinaryAckMessage
                    {
                        Event = eventName,
                        Namespace = _namespace,
                        Json = result.Json,
                        Id = _packetId,
                        OutgoingBytes = new List<byte[]>(result.Bytes)
                    };
                    await _transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var msg = new ClientAckMessage
                    {
                        Event = eventName,
                        Namespace = _namespace,
                        Id = _packetId,
                        Json = result.Json
                    };
                    await _transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var msg = new ClientAckMessage
                {
                    Event = eventName,
                    Namespace = _namespace,
                    Id = _packetId
                };
                await _transport.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task InvokeDisconnect(string reason)
        {
            if (Connected)
            {
                Connected = false;
                Id = null;
                OnDisconnected.TryInvoke(this, reason);
                try
                {
                    await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
#if DEBUG
                    System.Diagnostics.Debug.WriteLine(e);
#endif
                }
                if (reason != DisconnectReason.IOServerDisconnect && reason != DisconnectReason.IOClientDisconnect)
                {
                    //In the this cases (explicit disconnection), the client will not try to reconnect and you need to manually call socket.connect().
                    if (Options.Reconnection)
                    {
                        ConnectCore();
                    }
                }
            }
        }

        public void AddExpectedException(Type type)
        {
            if (!_expectedExceptions.Contains(type))
            {
                _expectedExceptions.Add(type);
            }
        }

        private void DisposeForReconnect()
        {
            _hasError = false;
            _isFaild = false;
            _packetId = -1;
            _ackHandlers.Clear();
            _connectCoreException = null;
            _hasError = false;
            _connectionTokenSource.TryCancel();
            _connectionTokenSource.TryDispose();
        }

        public void Dispose()
        {
            _transport.TryDispose();
            _ackHandlers.Clear();
            _onAnyHandlers.Clear();
            _eventHandlers.Clear();
            _connectionTokenSource.TryCancel();
            _connectionTokenSource.TryDispose();
        }
    }
}