using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace OSHttpServer
{
    public class HttpResponse : IHttpResponse
    {
        public event EventHandler<BandWitdhEventArgs> BandWitdhEvent;

        private const string DefaultContentType = "text/html;charset=UTF-8";
        private readonly IHttpClientContext _context;
        private readonly ResponseCookies _cookies = new ResponseCookies();
        private readonly NameValueCollection _headers = new NameValueCollection();
        private string _httpVersion;
        private Stream _body;
        private long _contentLength;
        private string _contentType;
        private Encoding _encoding = Encoding.UTF8;
        private int _keepAlive = 60;
        public uint requestID { get; }
        public byte[] RawBuffer { get; set; }
        public int RawBufferStart { get; set; }
        public int RawBufferLen { get; set; }
        public double RequestTS { get; }

        internal byte[] _headerBytes = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="IHttpResponse"/> class.
        /// </summary>
        /// <param name="context">Client that send the <see cref="IHttpRequest"/>.</param>
        /// <param name="request">Contains information of what the client want to receive.</param>
        /// <exception cref="ArgumentException"><see cref="IHttpRequest.HttpVersion"/> cannot be empty.</exception>
        public HttpResponse(IHttpRequest request)
        {
            _httpVersion = request.HttpVersion;
            if (string.IsNullOrEmpty(_httpVersion))
                _httpVersion = "HTTP/1.1";

            Status = HttpStatusCode.OK;
            _context = request.Context;
            _Connetion = request.Connection;
            requestID = request.ID;
            RequestTS = request.ArrivalTS;
            RawBufferStart = -1;
            RawBufferLen = -1;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IHttpResponse"/> class.
        /// </summary>
        /// <param name="context">Client that send the <see cref="IHttpRequest"/>.</param>
        /// <param name="httpVersion">Version of HTTP protocol that the client uses.</param>
        /// <param name="connectionType">Type of HTTP connection used.</param>
        internal HttpResponse(IHttpClientContext context, string httpVersion, ConnectionType connectionType)
        {
            Status = HttpStatusCode.OK;
            _context = context;
            _httpVersion = httpVersion;
            _Connetion = connectionType;
        }
        private ConnectionType _Connetion;
        public ConnectionType Connection
        {
            get => _Connetion;
            set => _Connetion = value;
        }

        private int _priority = 0;
        public int Priority
        {
            get => _priority;
            set => _priority = value > 0 && _priority < 3? value : 0;
        }

        #region IHttpResponse Members

        /// <summary>
        /// The body stream is used to cache the body contents
        /// before sending everything to the client. It's the simplest
        /// way to serve documents.
        /// </summary>
        public Stream Body
        {
            get
            { 
                if(_body == null)
                    _body = new MemoryStream();
                return _body;
            }
        }

        /// <summary>
        /// The chunked encoding modifies the body of a message in order to
        /// transfer it as a series of chunks, each with its own size indicator,
        /// followed by an OPTIONAL trailer containing entity-header fields. This
        /// allows dynamically produced content to be transferred along with the
        /// information necessary for the recipient to verify that it has
        /// received the full message.
        /// </summary>
        public bool Chunked { get; set; }


        /// <summary>
        /// Defines the version of the HTTP Response for applications where it's required
        /// for this to be forced.
        /// </summary>
        public string ProtocolVersion
        {
            get => _httpVersion;
            set => _httpVersion = value;
        }

        /// <summary>
        /// Encoding to use when sending stuff to the client.
        /// </summary>
        /// <remarks>Default is UTF8</remarks>
        public Encoding Encoding
        {
            get => _encoding;
            set => _encoding = value;
        }


        /// <summary>
        /// Number of seconds to keep connection alive
        /// </summary>
        /// <remarks>Only used if Connection property is set to <see cref="ConnectionType.KeepAlive"/>.</remarks>
        public int KeepAlive
        {
            get => _keepAlive;
            set
            {
                if (value > 400)
                    _keepAlive = 400;
                else if (value <= 0)
                    _keepAlive = 0;
                else
                    _keepAlive = value;
            }
        }

        /// <summary>
        /// Status code that is sent to the client.
        /// </summary>
        /// <remarks>Default is <see cref="HttpStatusCode.OK"/></remarks>
        public HttpStatusCode Status { get; set; }

        /// <summary>
        /// Information about why a specific status code was used.
        /// </summary>
        public string Reason { get; set; }

        /// <summary>
        /// Size of the body. MUST be specified before sending the header,
        /// </summary>
        public long ContentLength
        {
            get => _contentLength;
            set => _contentLength = value;
        }

        /// <summary>
        /// Kind of content
        /// </summary>
        /// <remarks>Default type is "text/html"</remarks>
        public string ContentType
        {
            get => _contentType;
            set => _contentType = value;
        }

        /// <summary>
        /// Headers have been sent to the client-
        /// </summary>
        /// <remarks>You can not send any additional headers if they have already been sent.</remarks>
        public bool HeadersSent { get; private set; }

        /// <summary>
        /// The whole response have been sent.
        /// </summary>
        public bool Sent { get; private set; }

        /// <summary>
        /// Cookies that should be created/changed.
        /// </summary>
        public ResponseCookies Cookies => _cookies;

        /// <summary>
        /// Add another header to the document.
        /// </summary>
        /// <param name="name">Name of the header, case sensitive, use lower cases.</param>
        /// <param name="value">Header values can span over multiple lines as long as each line starts with a white space. New line chars should be \r\n</param>
        /// <exception cref="InvalidOperationException">If headers already been sent.</exception>
        /// <exception cref="ArgumentException">If value conditions have not been met.</exception>
        /// <remarks>Adding any header will override the default ones and those specified by properties.</remarks>
        public void AddHeader(string name, string value)
        {
            if (HeadersSent)
                throw new InvalidOperationException("Headers have already been sent.");

            for (int i = 1; i < value.Length; ++i)
            {
                if (value[i] == '\r' && !char.IsWhiteSpace(value[i - 1]))
                    throw new ArgumentException("New line in value do not start with a white space.");
                if (value[i] == '\n' && value[i - 1] != '\r')
                    throw new ArgumentException("Invalid new line sequence, should be \\r\\n (crlf).");
            }

            _headers[name] = value;
        }

        public byte[] GetHeaders()
        {
            HeadersSent = true;

            var sb = new StringBuilder();
            if(string.IsNullOrWhiteSpace(_httpVersion))
                sb.AppendFormat("HTTP/1.1 {0} {1}\r\n", (int)Status,
                                string.IsNullOrEmpty(Reason) ? Status.ToString() : Reason);
            else
                sb.AppendFormat("{0} {1} {2}\r\n", _httpVersion, (int)Status,
                                string.IsNullOrEmpty(Reason) ? Status.ToString() : Reason);

            if (_headers["Date"] == null)
                sb.AppendFormat("Date: {0}\r\n", DateTime.Now.ToString("r"));
            if (_headers["Content-Length"] == null)
            {
                long len = _contentLength;
                if (len == 0)
                {
                    len = Body.Length;
                    if (RawBuffer != null && RawBufferLen > 0)
                        len += RawBufferLen;
                }
                sb.AppendFormat("Content-Length: {0}\r\n", len);
            }
            if (_headers["Content-Type"] == null)
                sb.AppendFormat("Content-Type: {0}\r\n", _contentType ?? DefaultContentType);
            if (_headers["Server"] == null)
                sb.Append("Server: OSWebServer\r\n");

            if(Status != HttpStatusCode.OK)
            {
                sb.Append("Connection: close\r\n");
                Connection = ConnectionType.Close;
            }
            else
            {
                int keepaliveS = _context.TimeoutKeepAlive / 1000;
                if (Connection == ConnectionType.KeepAlive && keepaliveS > 0 && _context.MaxRequests > 0)
                {
                    sb.AppendFormat("Keep-Alive:timeout={0}, max={1}\r\n", keepaliveS, _context.MaxRequests);
                    sb.Append("Connection: Keep-Alive\r\n");
                }
                else
                {
                    sb.Append("Connection: close\r\n");
                    Connection = ConnectionType.Close;
                }
            }

            if (_headers["Connection"] != null)
                _headers["Connection"] = null;
            if (_headers["Keep-Alive"] != null)
                _headers["Keep-Alive"] = null;

            for (int i = 0; i < _headers.Count; ++i)
            {
                string headerName = _headers.AllKeys[i];
                string[] values = _headers.GetValues(i);
                if (values == null) continue;
                foreach (string value in values)
                    sb.AppendFormat("{0}: {1}\r\n", headerName, value);
            }

            foreach (ResponseCookie cookie in Cookies)
                sb.AppendFormat("Set-Cookie: {0}\r\n", cookie);

            sb.Append(Environment.NewLine);

            _headers.Clear();

            return Encoding.GetBytes(sb.ToString());
        }

        public void Send()
        {
            if(_context.IsClosing)
                return;

            if (Sent)
                throw new InvalidOperationException("Everything have already been sent.");

            if (_context.MaxRequests == 0 || _keepAlive == 0)
            {
                Connection = ConnectionType.Close;
                _context.TimeoutKeepAlive = 0;
            }
            else
            {
                if (_keepAlive > 0)
                    _context.TimeoutKeepAlive = _keepAlive * 1000;
            }

            if (RawBuffer != null)
            {
                if (RawBufferStart > RawBuffer.Length)
                    return;

                if (RawBufferStart < 0)
                    RawBufferStart = 0;

                if (RawBufferLen < 0)
                    RawBufferLen = RawBuffer.Length;

                if (RawBufferLen + RawBufferStart > RawBuffer.Length)
                    RawBufferLen = RawBuffer.Length - RawBufferStart;
            }

            _headerBytes = GetHeaders();
            /*
            if (RawBuffer != null)
            {
                int tlen = _headerBytes.Length + RawBufferLen;
                if(RawBufferLen > 0 && tlen < 16384)
                {
                    byte[] tmp = new byte[tlen];
                    Buffer.BlockCopy(_headerBytes, 0, tmp, 0, _headerBytes.Length);
                    Buffer.BlockCopy(RawBuffer, RawBufferStart, tmp, _headerBytes.Length, RawBufferLen);
                    _headerBytes = null;
                    RawBuffer = tmp;
                    RawBufferStart = 0;
                    RawBufferLen = tlen;
                }
            }
            */
            _context.StartSendResponse(this);
        }

        public async Task SendNextAsync(int bytesLimit)
        {
            if (_headerBytes != null)
            {
                if(!await _context.SendAsync(_headerBytes, 0, _headerBytes.Length).ConfigureAwait(false))
                {
                    if (_context.CanSend())
                    {
                        _context.ContinueSendResponse(true);
                        return;
                    }
                    if (_body != null)
                        _body.Dispose();
                    RawBuffer = null;
                    Sent = true;
                    return;
                }
                bytesLimit -= _headerBytes.Length;
                _headerBytes = null;
                if(bytesLimit <= 0)
                {
                    _context.ContinueSendResponse(true);
                    return;
                }
            }

            if (RawBuffer != null)
            {
                if (RawBufferLen > 0)
                {
                    if(BandWitdhEvent!=null)
                        bytesLimit = CheckBandwidth(RawBufferLen, bytesLimit);

                    bool sendRes;
                    if(RawBufferLen > bytesLimit)
                    {
                        sendRes = await _context.SendAsync(RawBuffer, RawBufferStart, bytesLimit).ConfigureAwait(false);
                        if (sendRes)
                        {
                            RawBufferLen -= bytesLimit;
                            RawBufferStart += bytesLimit;
                        }
                    }
                    else
                    {
                        sendRes = await _context.SendAsync(RawBuffer, RawBufferStart, RawBufferLen).ConfigureAwait(false);
                        if(sendRes)
                            RawBufferLen = 0;
                    }

                    if (!sendRes)
                    {
                        if (_context.CanSend())
                        {
                            _context.ContinueSendResponse(true);
                            return;
                        }

                        RawBuffer = null;
                        if(_body != null)
                            Body.Dispose();
                        Sent = true;
                        return;
                    }
                }
                if (RawBufferLen <= 0)
                    RawBuffer = null;
                else
                {
                    _context.ContinueSendResponse(true);
                    return;
                }
            }

            if (_body != null && _body.Length != 0)
            {
                MemoryStream mb = _body as MemoryStream;
                RawBuffer = mb.GetBuffer();
                RawBufferStart = 0; // must be a internal buffer, or starting at 0
                RawBufferLen = (int)mb.Length;
                mb.Dispose();
                _body = null;

                if(RawBufferLen > 0)
                {
                    bool sendRes;
                    if (RawBufferLen > bytesLimit)
                    {
                        sendRes = await _context.SendAsync(RawBuffer, RawBufferStart, bytesLimit).ConfigureAwait(false);
                        if (sendRes)
                        {
                            RawBufferLen -= bytesLimit;
                            RawBufferStart += bytesLimit;
                        }
                    }
                    else
                    {
                        sendRes = await _context.SendAsync(RawBuffer, RawBufferStart, RawBufferLen).ConfigureAwait(false);
                        if (sendRes)
                            RawBufferLen = 0;
                    }

                    if (!sendRes)
                    {
                        if (_context.CanSend())
                        {
                            _context.ContinueSendResponse(true);
                            return;
                        }
                        RawBuffer = null;
                        Sent = true;
                        return;
                    }
                }
                if (RawBufferLen > 0)
                {
                    _context.ContinueSendResponse(false);
                    return;
                }
            }

            if (_body != null)
                _body.Dispose();
            Sent = true;
            _context.EndSendResponse(requestID, Connection);
        }

        private int CheckBandwidth(int request, int bytesLimit)
        {
            if(request > bytesLimit)
                request = bytesLimit;
            var args = new BandWitdhEventArgs(request);
            BandWitdhEvent?.Invoke(this, args);
            if(args.Result > 8196)
                return args.Result;

            return 8196;
        }

        public void Clear()
        {
            if(Body != null && Body.CanRead)
                Body.Dispose();
        }
        #endregion
    }
}