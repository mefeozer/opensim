using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using OSHttpServer.Exceptions;
using OSHttpServer.Parser;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace OSHttpServer
{
    /// <summary>
    /// Contains a connection to a browser/client.
    /// </summary>
    /// <remarks>
    /// Remember to <see cref="Start"/> after you have hooked the <see cref="RequestReceived"/> event.
    /// </remarks>
    public class HttpClientContext : IHttpClientContext, IDisposable
    {
        const int MAXREQUESTS = 20;
        const int MAXKEEPALIVE = 120000;

        static private int basecontextID;

        Queue<HttpRequest> _requests;
        readonly object _requestsLock = new object();
        public int _maxRequests = MAXREQUESTS;
        public bool _waitingResponse; 

        private readonly byte[] _ReceiveBuffer;
        private int _ReceiveBytesLeft;
        private ILogWriter _log;
        private readonly IHttpRequestParser _parser;
        private Socket _sock;

        public bool Available = true;
        public bool StreamPassedOff = false;

        public int LastActivityTimeMS = 0;
        public int MonitorKeepaliveStartMS = 0;
        public bool TriggerKeepalive = false;
        public int TimeoutFirstLine = 10000; // 10 seconds
        public int TimeoutRequestReceived = 30000; // 30 seconds

        public int TimeoutMaxIdle = 180000; // 3 minutes
        public int _TimeoutKeepAlive = 30000;

        public bool FirstRequestLineReceived;
        public bool FullRequestReceived;

        private bool isSendingResponse = false;
        private bool _isClosing = false;

        private HttpRequest _currentRequest;
        private HttpResponse _currentResponse;

        public int contextID { get; private set; }
        public int TimeoutKeepAlive
        {
            get => _TimeoutKeepAlive;
            set => _TimeoutKeepAlive = value > MAXKEEPALIVE ? MAXKEEPALIVE : value;
        }

        public bool IsClosing => _isClosing;

        public int MaxRequests
        {
            get => _maxRequests;
            set
            {
                if(value <= 1)
                    _maxRequests = 1;
                else
                   _maxRequests = value > MAXREQUESTS ? MAXREQUESTS : value;
            }
        }

        public bool IsSending()
        {
            return isSendingResponse;
        }

        public bool StopMonitoring;

        public IPEndPoint LocalIPEndPoint {get; set;}

        /// <summary>
        /// Initializes a new instance of the <see cref="HttpClientContext"/> class.
        /// </summary>
        /// <param name="secured">true if the connection is secured (SSL/TLS)</param>
        /// <param name="remoteEndPoint">client that connected.</param>
        /// <param name="stream">Stream used for communication</param>
        /// <param name="parserFactory">Used to create a <see cref="IHttpRequestParser"/>.</param>
        /// <param name="bufferSize">Size of buffer to use when reading data. Must be at least 4096 bytes.</param>
        /// <exception cref="SocketException">If <see cref="Socket.BeginReceive(byte[],int,int,SocketFlags,AsyncCallback,object)"/> fails</exception>
        /// <exception cref="ArgumentException">Stream must be writable and readable.</exception>
        public HttpClientContext(bool secured, IPEndPoint remoteEndPoint,
                                    Stream stream, ILogWriter _logWriter, Socket sock)
        {
            if (!stream.CanWrite || !stream.CanRead)
                throw new ArgumentException("Stream must be writable and readable.");

            LocalIPEndPoint = remoteEndPoint;
            _log = _logWriter;
            _isClosing = false;
            _parser = new HttpRequestParser(_log);
            _parser.RequestCompleted += OnRequestCompleted;
            _parser.RequestLineReceived += OnRequestLine;
            _parser.HeaderReceived += OnHeaderReceived;
            _parser.BodyBytesReceived += OnBodyBytesReceived;
            _currentRequest = new HttpRequest(this);
            IsSecured = secured;
            _stream = stream;
            _sock = sock;

            _ReceiveBuffer = new byte[16384];
            _requests = new Queue<HttpRequest>();

            SSLCommonName = "";
            if (secured)
            {
                SslStream _ssl = (SslStream)_stream;
                X509Certificate _cert1 = _ssl.RemoteCertificate;
                if (_cert1 != null)
                {
                    X509Certificate2 _cert2 = new X509Certificate2(_cert1);
                    if (_cert2 != null)
                        SSLCommonName = _cert2.GetNameInfo(X509NameType.SimpleName, false);
                }
            }

            ++basecontextID;
            if (basecontextID <= 0)
                basecontextID = 1;

            contextID = basecontextID;
        }

        public bool CanSend()
        {
            if (contextID < 0 || _isClosing)
                return false;

            if (_stream == null || _sock == null || !_sock.Connected)
                return false;

            return true;
        }

        /// <summary>
        /// Process incoming body bytes.
        /// </summary>
        /// <param name="sender"><see cref="IHttpRequestParser"/></param>
        /// <param name="e">Bytes</param>
        protected virtual void OnBodyBytesReceived(object sender, BodyEventArgs e)
        {
            _currentRequest.AddToBody(e.Buffer, e.Offset, e.Count);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected virtual void OnHeaderReceived(object sender, HeaderEventArgs e)
        {
            if (string.Compare(e.Name, "expect", true) == 0 && e.Value.Contains("100-continue"))
            {
                lock (_requestsLock)
                {
                    if (_maxRequests == MAXREQUESTS)
                        Respond("HTTP/1.1", HttpStatusCode.Continue, null);
                }
            }
            _currentRequest.AddHeader(e.Name, e.Value);
        }

        private void OnRequestLine(object sender, RequestLineEventArgs e)
        {
            _currentRequest.Method = e.HttpMethod;
            _currentRequest.HttpVersion = e.HttpVersion;
            _currentRequest.UriPath = e.UriPath;
            _currentRequest.AddHeader("remote_addr", LocalIPEndPoint.Address.ToString());
            _currentRequest.AddHeader("remote_port", LocalIPEndPoint.Port.ToString());
            _currentRequest.ArrivalTS = ContextTimeoutManager.GetTimeStamp();

            FirstRequestLineReceived = true;
            TriggerKeepalive = false;
            MonitorKeepaliveStartMS = 0;
            LastActivityTimeMS = ContextTimeoutManager.EnvironmentTickCount();
        }

        /// <summary>
        /// Start reading content.
        /// </summary>
        /// <remarks>
        /// Make sure to call base.Start() if you override this method.
        /// </remarks>
        public virtual void Start()
        {
            try
            {
                _stream.BeginRead(_ReceiveBuffer, 0, _ReceiveBuffer.Length, OnReceive, null);
            }
            catch (IOException err)
            {
                LogWriter.Write(this, LogPrio.Debug, err.ToString());
            }
            //Task.Run(async () => await ReceiveLoop()).ConfigureAwait(false);
        }

        /// <summary>
        /// Clean up context.
        /// </summary>
        /// <remarks>
        /// </remarks>
        public virtual void Cleanup()
        {
            if (StreamPassedOff)
                return;

            contextID = -100;

            if (_stream != null)
            {
                _stream.Close();
                _stream = null;
                _sock = null;
            }

            _currentRequest?.Clear();
            _currentRequest = null;
            _currentResponse?.Clear();
            _currentResponse = null;
            if(_requests != null)
            {
                while(_requests.Count > 0)
                {
                    HttpRequest req = _requests.Dequeue();
                    req.Clear();
                }
            }
            _requests.Clear();
            _requests = null;
            _parser.Clear();

            FirstRequestLineReceived = false;
            FullRequestReceived = false;
            LastActivityTimeMS = 0;
            StopMonitoring = true;
            MonitorKeepaliveStartMS = 0;
            TriggerKeepalive = false;

            isSendingResponse = false;
            _ReceiveBytesLeft = 0;
        }

        public void Close()
        {
            Dispose();
        }

        /// <summary>
        /// Using SSL or other encryption method.
        /// </summary>
        [Obsolete("Use IsSecured instead.")]
        public bool Secured => IsSecured;

        /// <summary>
        /// Using SSL or other encryption method.
        /// </summary>
        public bool IsSecured { get; internal set; }


        // returns the SSL commonName of remote Certificate
        public string SSLCommonName { get; internal set; }

        /// <summary>
        /// Specify which logger to use.
        /// </summary>
        public ILogWriter LogWriter
        {
            get => _log;
            set
            {
                _log = value ?? NullLogWriter.Instance;
                _parser.LogWriter = _log;
            }
        }

        private Stream _stream;

        /// <summary>
        /// Gets or sets the network stream.
        /// </summary>
        internal Stream Stream
        {
            get => _stream;
            set => _stream = value;
        }

        /// <summary>
        /// Disconnect from client
        /// </summary>
        /// <param name="error">error to report in the <see cref="Disconnected"/> event.</param>
        public void Disconnect(SocketError error)
        {
            // disconnect may not throw any exceptions
            try
            {
                try
                {
                    if (_stream != null)
                    {
                        if (error == SocketError.Success)
                        {
                            try
                            {
                                _stream.Flush();
                            }
                            catch { }

                        }
                        _stream.Close();
                        _stream = null;
                    }
                    _sock = null;
                }
                catch { }

                Disconnected?.Invoke(this, new DisconnectedEventArgs(error));
            }
            catch (Exception err)
            {
                LogWriter.Write(this, LogPrio.Error, "Disconnect threw an exception: " + err);
            }
        }

        private void OnReceive(IAsyncResult ar)
        {
            try
            {
                int bytesRead = 0;
                if (_stream == null)
                    return;
                try
                {
                    bytesRead = _stream.EndRead(ar);
                }
                catch (NullReferenceException)
                {
                    Disconnect(SocketError.ConnectionReset);
                    return;
                }

                if (bytesRead == 0)
                {
                    Disconnect(SocketError.Success);
                    return;
                }

                if (_isClosing)
                    return;

                _ReceiveBytesLeft += bytesRead;

                int offset = _parser.Parse(_ReceiveBuffer, 0, _ReceiveBytesLeft);
                if (_stream == null)
                    return; // "Connection: Close" in effect.

                while (offset != 0)
                {
                    int nextBytesleft = _ReceiveBytesLeft - offset;
                    if (nextBytesleft <= 0)
                        break;

                    int nextOffset = _parser.Parse(_ReceiveBuffer, offset, nextBytesleft);

                    if (_stream == null)
                        return; // "Connection: Close" in effect.

                    if (nextOffset == 0)
                        break;

                    offset = nextOffset;
                }

                // copy unused bytes to the beginning of the array
                if (offset > 0 && _ReceiveBytesLeft > offset)
                    Buffer.BlockCopy(_ReceiveBuffer, offset, _ReceiveBuffer, 0, _ReceiveBytesLeft - offset);

                _ReceiveBytesLeft -= offset;
                if (StreamPassedOff)
                    return; //?
                _stream.BeginRead(_ReceiveBuffer, _ReceiveBytesLeft, _ReceiveBuffer.Length - _ReceiveBytesLeft, OnReceive, null);
            }
            catch (BadRequestException err)
            {
                LogWriter.Write(this, LogPrio.Warning, "Bad request, responding with it. Error: " + err);
                try
                {
                    Respond("HTTP/1.1", HttpStatusCode.BadRequest, err.Message);
                }
                catch (Exception err2)
                {
                    LogWriter.Write(this, LogPrio.Fatal, "Failed to reply to a bad request. " + err2);
                }
                //Disconnect(SocketError.NoRecovery);
                Disconnect(SocketError.Success); // try to flush
            }
            catch (IOException err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive: " + err.Message);
                if (err.InnerException is SocketException)
                    Disconnect((SocketError)((SocketException)err.InnerException).ErrorCode);
                else
                    Disconnect(SocketError.ConnectionReset);
            }
            catch (ObjectDisposedException err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive : " + err.Message);
                Disconnect(SocketError.NotSocket);
            }
            catch (NullReferenceException err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive : NullRef: " + err.Message);
                Disconnect(SocketError.NoRecovery);
            }
            catch (Exception err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive: " + err.Message);
                Disconnect(SocketError.NoRecovery);
            }
        }

        /*
        private async Task ReceiveLoop()
        {
            _ReceiveBytesLeft = 0;
            try
            {
                while(true)
                {
                    if (_stream == null || !_stream.CanRead)
                        return;

                    int bytesRead = await _stream.ReadAsync(_ReceiveBuffer, _ReceiveBytesLeft, _ReceiveBuffer.Length - _ReceiveBytesLeft).ConfigureAwait(false);

                    if (bytesRead == 0)
                    {
                        Disconnect(SocketError.Success);
                        return;
                    }

                    if(_isClosing)
                        continue;

                    _ReceiveBytesLeft += bytesRead;

                    int offset = _parser.Parse(_ReceiveBuffer, 0, _ReceiveBytesLeft);
                    if (_stream == null)
                        return; // "Connection: Close" in effect.

                    while (offset != 0)
                    {
                        int nextBytesleft = _ReceiveBytesLeft - offset;
                        if(nextBytesleft <= 0)
                            break;

                        int nextOffset = _parser.Parse(_ReceiveBuffer, offset, nextBytesleft);

                        if (_stream == null)
                            return; // "Connection: Close" in effect.

                        if (nextOffset == 0)
                            break;

                        offset = nextOffset;
                    }

                    // copy unused bytes to the beginning of the array
                    if (offset > 0 && _ReceiveBytesLeft > offset)
                        Buffer.BlockCopy(_ReceiveBuffer, offset, _ReceiveBuffer, 0, _ReceiveBytesLeft - offset);

                    _ReceiveBytesLeft -= offset;
                    if (StreamPassedOff)
                        return; //?
                }
            }
            catch (BadRequestException err)
            {
                LogWriter.Write(this, LogPrio.Warning, "Bad request, responding with it. Error: " + err);
                try
                {
                    Respond("HTTP/1.1", HttpStatusCode.BadRequest, err.Message);
                }
                catch (Exception err2)
                {
                    LogWriter.Write(this, LogPrio.Fatal, "Failed to reply to a bad request. " + err2);
                }
                //Disconnect(SocketError.NoRecovery);
                Disconnect(SocketError.Success); // try to flush
            }
            catch (IOException err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive: " + err.Message);
                if (err.InnerException is SocketException)
                    Disconnect((SocketError)((SocketException)err.InnerException).ErrorCode);
                else
                    Disconnect(SocketError.ConnectionReset);
            }
            catch (ObjectDisposedException err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive : " + err.Message);
                Disconnect(SocketError.NotSocket);
            }
            catch (NullReferenceException err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive : NullRef: " + err.Message);
                Disconnect(SocketError.NoRecovery);
            }
            catch (Exception err)
            {
                LogWriter.Write(this, LogPrio.Debug, "Failed to end receive: " + err.Message);
                Disconnect(SocketError.NoRecovery);
            }
        }
        */

        private void OnRequestCompleted(object source, EventArgs args)
        {
            TriggerKeepalive = false;
            MonitorKeepaliveStartMS = 0;
            FullRequestReceived = true;
            LastActivityTimeMS = ContextTimeoutManager.EnvironmentTickCount();

            if (_maxRequests == 0)
                return;

            if (--_maxRequests == 0)
                _currentRequest.Connection = ConnectionType.Close;

            if(_currentRequest.Uri == null)
            {
                // should not happen
                try
                {
                    Uri uri = new Uri(_currentRequest.Secure ? "https://" : "http://" + _currentRequest.UriPath);
                    _currentRequest.Uri = uri;
                    _currentRequest.UriPath = uri.AbsolutePath;
                }
                catch
                {
                    return;
                }
            }

            // load cookies if they exist
            if(_currentRequest.Headers["cookie"] != null)
                _currentRequest.SetCookies(new RequestCookies(_currentRequest.Headers["cookie"]));

            _currentRequest.Body.Seek(0, SeekOrigin.Begin);

            bool donow = true;
            lock (_requestsLock)
            {
                if(_waitingResponse)
                {
                    _requests.Enqueue(_currentRequest);
                    donow = false;
                }
                else
                    _waitingResponse = true;
            }

            if(donow)
                RequestReceived?.Invoke(this, new RequestEventArgs(_currentRequest));

            _currentRequest = new HttpRequest(this);
        }

        public void StartSendResponse(HttpResponse response)
        {
            LastActivityTimeMS = ContextTimeoutManager.EnvironmentTickCount();
            isSendingResponse = true;
            _currentResponse = response;
            ContextTimeoutManager.EnqueueSend(this, response.Priority);
        }

        public bool TrySendResponse(int bytesLimit)
        {
            if(_currentResponse == null)
                return false;
            if (_currentResponse.Sent)
                return false;

            if(!CanSend())
                return false;

            LastActivityTimeMS = ContextTimeoutManager.EnvironmentTickCount();
            _currentResponse?.SendNextAsync(bytesLimit);
            return false;
        }

        public void ContinueSendResponse(bool notThrottled)
        {
            if(_currentResponse == null)
                return;
            ContextTimeoutManager.EnqueueSend(this, _currentResponse.Priority, notThrottled);
        }

        public void EndSendResponse(uint requestID, ConnectionType ctype)
        {
            isSendingResponse = false;
            _currentResponse?.Clear();
            _currentResponse = null;
            lock (_requestsLock)
                _waitingResponse = false;

            if(contextID < 0)
                return;

            if (ctype == ConnectionType.Close)
            {
                _isClosing = true;
                _requests.Clear();
                TriggerKeepalive = true;
                return;
            }
            else
            {
                LastActivityTimeMS = ContextTimeoutManager.EnvironmentTickCount();
                if (Stream == null || !Stream.CanWrite)
                    return;

                HttpRequest nextRequest = null;
                lock (_requestsLock)
                {
                    if (_requests != null && _requests.Count > 0)
                        nextRequest = _requests.Dequeue();
                    if (nextRequest != null && RequestReceived != null)
                    {
                        _waitingResponse = true;
                        TriggerKeepalive = false;
                    }
                    else
                        TriggerKeepalive = true;
                }
                if (nextRequest != null)
                    RequestReceived?.Invoke(this, new RequestEventArgs(nextRequest));
            }
        }

        /// <summary>
        /// Send a response.
        /// </summary>
        /// <param name="httpVersion">Either <see cref="HttpHelper.HTTP10"/> or <see cref="HttpHelper.HTTP11"/></param>
        /// <param name="statusCode">HTTP status code</param>
        /// <param name="reason">reason for the status code.</param>
        /// <param name="body">HTML body contents, can be null or empty.</param>
        /// <param name="contentType">A content type to return the body as, i.e. 'text/html' or 'text/plain', defaults to 'text/html' if null or empty</param>
        /// <exception cref="ArgumentException">If <paramref name="httpVersion"/> is invalid.</exception>
        public void Respond(string httpVersion, HttpStatusCode statusCode, string reason, string body, string contentType)
        {
            LastActivityTimeMS = ContextTimeoutManager.EnvironmentTickCount();

            if (string.IsNullOrEmpty(reason))
                reason = statusCode.ToString();

            byte[] buffer;
            if(string.IsNullOrEmpty(body))
                buffer = Encoding.ASCII.GetBytes(httpVersion + " " + (int)statusCode + " " + reason + "\r\n\r\n");
            else
            {
                if (string.IsNullOrEmpty(contentType))
                    contentType = "text/html";
                buffer = Encoding.UTF8.GetBytes(
                        string.Format("{0} {1} {2}\r\nContent-Type: {5}\r\nContent-Length: {3}\r\n\r\n{4}",
                                                  httpVersion, (int)statusCode, reason ?? statusCode.ToString(),
                                                  body.Length, body, contentType));
            }
            Send(buffer);
        }

        /// <summary>
        /// Send a response.
        /// </summary>
        /// <param name="httpVersion">Either <see cref="HttpHelper.HTTP10"/> or <see cref="HttpHelper.HTTP11"/></param>
        /// <param name="statusCode">HTTP status code</param>
        /// <param name="reason">reason for the status code.</param>
        public void Respond(string httpVersion, HttpStatusCode statusCode, string reason)
        {
            if (string.IsNullOrEmpty(reason))
                reason = statusCode.ToString();
            byte[] buffer = Encoding.ASCII.GetBytes(httpVersion + " " + (int)statusCode + " " + reason + "\r\n\r\n");
            Send(buffer);
        }

        /// <summary>
        /// send a whole buffer
        /// </summary>
        /// <param name="buffer">buffer to send</param>
        /// <exception cref="ArgumentNullException"></exception>
        public bool Send(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException("buffer");
            return Send(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Send data using the stream
        /// </summary>
        /// <param name="buffer">Contains data to send</param>
        /// <param name="offset">Start position in buffer</param>
        /// <param name="size">number of bytes to send</param>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentOutOfRangeException"></exception>

        private readonly object sendLock = new object();

        public bool Send(byte[] buffer, int offset, int size)
        {
            if (_stream == null || _sock == null || !_sock.Connected)
                return false;

            if (offset + size > buffer.Length)
                throw new ArgumentOutOfRangeException("offset", offset, "offset + size is beyond end of buffer.");

            LastActivityTimeMS = ContextTimeoutManager.EnvironmentTickCount();

            bool ok = true;
            ContextTimeoutManager.ContextEnterActiveSend();
            lock (sendLock) // can't have overlaps here
            {
                try
                {
                    _stream.Write(buffer, offset, size);
                }
                catch
                {
                    ok = false;
                }
            }

            ContextTimeoutManager.ContextLeaveActiveSend();
            if (!ok && _stream != null)
                Disconnect(SocketError.NoRecovery);
            return ok;
        }

        public async Task<bool> SendAsync(byte[] buffer, int offset, int size)
        {
            if (_stream == null || _sock == null || !_sock.Connected)
                return false;

            if (offset + size > buffer.Length)
                throw new ArgumentOutOfRangeException("offset", offset, "offset + size is beyond end of buffer.");

            bool ok = true;
            ContextTimeoutManager.ContextEnterActiveSend();
            try
            {
                await _stream.WriteAsync(buffer, offset, size).ConfigureAwait(false);
            }
            catch
            {
                ok = false;
            }

            ContextTimeoutManager.ContextLeaveActiveSend();

            if (!ok && _stream != null)
                Disconnect(SocketError.NoRecovery);
            return ok;
        }

        /// <summary>
        /// The context have been disconnected.
        /// </summary>
        /// <remarks>
        /// Event can be used to clean up a context, or to reuse it.
        /// </remarks>
        public event EventHandler<DisconnectedEventArgs> Disconnected;
        /// <summary>
        /// A request have been received in the context.
        /// </summary>
        public event EventHandler<RequestEventArgs> RequestReceived;

        public HTTPNetworkContext GiveMeTheNetworkStreamIKnowWhatImDoing()
        {
            StreamPassedOff = true;
            _parser.RequestCompleted -= OnRequestCompleted;
            _parser.RequestLineReceived -= OnRequestLine;
            _parser.HeaderReceived -= OnHeaderReceived;
            _parser.BodyBytesReceived -= OnBodyBytesReceived;
            _parser.Clear();

            _currentRequest?.Clear();
            _currentRequest = null;
            _currentResponse?.Clear();
            _currentResponse = null;
            if (_requests != null)
            {
                while (_requests.Count > 0)
                {
                    HttpRequest req = _requests.Dequeue();
                    req.Clear();
                }
            }
            _requests.Clear();
            _requests = null;

            return new HTTPNetworkContext() { Socket = _sock, Stream = _stream as NetworkStream };
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (contextID >= 0)
            {
                StreamPassedOff = false;
                Cleanup();
            }
        }
    }
}