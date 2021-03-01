using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Web;
using OSHttpServer.Exceptions;


namespace OSHttpServer
{
    /// <summary>
    /// Contains server side HTTP request information.
    /// </summary>
    public class HttpRequest : IHttpRequest
    {
        /// <summary>
        /// Chars used to split an URL path into multiple parts.
        /// </summary>
        public static readonly char[] UriSplitters = new[] { '/' };
        public static uint baseID = 0;

        private readonly NameValueCollection _headers = new NameValueCollection();
        private readonly HttpParam _param = new HttpParam(HttpInput.Empty, HttpInput.Empty);
        private Stream _body = new MemoryStream();
        private int _bodyBytesLeft;
        private ConnectionType _connection = ConnectionType.KeepAlive;
        private int _contentLength;
        private string _httpVersion = string.Empty;
        private string _method = string.Empty;
        private NameValueCollection _queryString = null;
        private Uri _uri = null;
        private string _uriPath;
        public readonly IHttpClientContext _context;
        IPEndPoint _remoteIPEndPoint = null;

        public HttpRequest(IHttpClientContext pContext)
        {
            ID = ++baseID;
            _context = pContext;
        }

        public uint ID { get; }

        /// <summary>
        /// Gets or sets a value indicating whether this <see cref="HttpRequest"/> is secure.
        /// </summary>
        public bool Secure => _context.IsSecured;

        public IHttpClientContext Context => _context;

        /// <summary>
        /// Path and query (will be merged with the host header) and put in Uri
        /// </summary>
        /// <see cref="Uri"/>
        public string UriPath
        {
            get => _uriPath;
            set => _uriPath = value;
        }

        /// <summary>
        /// Assign a form.
        /// </summary>
        /// <param name="form"></param>
        /*
        internal void AssignForm(HttpForm form)
        {
            _form = form;
        }
        */

        #region IHttpRequest Members

        /// <summary>
        /// Gets kind of types accepted by the client.
        /// </summary>
        public string[] AcceptTypes { get; private set; }

        /// <summary>
        /// Gets or sets body stream.
        /// </summary>
        public Stream Body
        {
            get => _body;
            set => _body = value;
        }

        /// <summary>
        /// Gets or sets kind of connection used for the session.
        /// </summary>
        public ConnectionType Connection
        {
            get => _connection;
            set => _connection = value;
        }

        /// <summary>
        /// Gets or sets number of bytes in the body.
        /// </summary>
        public int ContentLength
        {
            get => _contentLength;
            set
            {
                _contentLength = value;
                _bodyBytesLeft = value;
            }
        }

        /// <summary>
        /// Gets headers sent by the client.
        /// </summary>
        public NameValueCollection Headers => _headers;

        /// <summary>
        /// Gets or sets version of HTTP protocol that's used.
        /// </summary>
        /// <remarks>
        /// Probably <see cref="HttpHelper.HTTP10"/> or <see cref="HttpHelper.HTTP11"/>.
        /// </remarks>
        /// <seealso cref="HttpHelper"/>
        public string HttpVersion
        {
            get => _httpVersion;
            set => _httpVersion = value;
        }

        /// <summary>
        /// Gets or sets requested method.
        /// </summary>
        /// <value></value>
        /// <remarks>
        /// Will always be in upper case.
        /// </remarks>
        /// <see cref="OSHttpServer.Method"/>
        public string Method
        {
            get => _method;
            set => _method = value;
        }

        /// <summary>
        /// Gets variables sent in the query string
        /// </summary>
        public NameValueCollection QueryString
        {
            get
            {
                if(_queryString == null)
                {
                    if(_uri == null || _uri.Query.Length == 0)
                        _queryString = new NameValueCollection();
                    else
                    {
                        try
                        {
                            _queryString = HttpUtility.ParseQueryString(_uri.Query);
                        }
                        catch { _queryString = new NameValueCollection(); }
                    }
                }

            return _queryString;
            }
        }

        public static readonly Uri EmptyUri = new Uri("http://localhost/");
        /// <summary>
        /// Gets or sets requested URI.
        /// </summary>
        public Uri Uri
        {
            get => _uri;
            set => _uri = value ?? EmptyUri; // not safe
        }

        /// <summary>
        /// Gets parameter from <see cref="QueryString"/> or <see cref="Form"/>.
        /// </summary>
        public HttpParam Param => _param;

        /// <summary>
        /// Gets form parameters.
        /// </summary>
        /*
        public HttpForm Form
        {
            get { return _form; }
        }
        */
        /// <summary>
        /// Gets whether the request was made by Ajax (Asynchronous JavaScript)
        /// </summary>
        public bool IsAjax { get; private set; }

        /// <summary>
        /// Gets cookies that was sent with the request.
        /// </summary>
        public RequestCookies Cookies { get; private set; }

        public double ArrivalTS { get; set;}
        ///<summary>
        ///Creates a new object that is a copy of the current instance.
        ///</summary>
        ///
        ///<returns>
        ///A new object that is a copy of this instance.
        ///</returns>
        ///<filterpriority>2</filterpriority>
        public object Clone()
        {
            // this method was mainly created for testing.
            // dont use it that much...
            var request = new HttpRequest(Context)
            {
                Method = _method
            };
            if (AcceptTypes != null)
            {
                request.AcceptTypes = new string[AcceptTypes.Length];
                AcceptTypes.CopyTo(request.AcceptTypes, 0);
            }
            request._httpVersion = _httpVersion;
            request._queryString = _queryString;
            request.Uri = _uri;

            var buffer = new byte[_body.Length];
            _body.Read(buffer, 0, (int)_body.Length);
            request.Body = new MemoryStream();
            request.Body.Write(buffer, 0, buffer.Length);
            request.Body.Seek(0, SeekOrigin.Begin);
            request.Body.Flush();

            request._headers.Clear();
            foreach (string key in _headers)
            {
                string[] values = _headers.GetValues(key);
                if (values != null)
                    foreach (string value in values)
                        request.AddHeader(key, value);
            }
            return request;
        }

        /// <summary>
        /// Decode body into a form.
        /// </summary>
        /// <param name="providers">A list with form decoders.</param>
        /// <exception cref="InvalidDataException">If body contents is not valid for the chosen decoder.</exception>
        /// <exception cref="InvalidOperationException">If body is still being transferred.</exception>
        /*
        public void DecodeBody(FormDecoderProvider providers)
        {
            if (_bodyBytesLeft > 0)
                throw new InvalidOperationException("Body have not yet been completed.");

            _form = providers.Decode(_headers["content-type"], _body, Encoding.UTF8);
            if (_form != HttpInput.Empty)
                _param.SetForm(_form);
        }
        */
        ///<summary>
        /// Cookies
        ///</summary>
        ///<param name="cookies">the cookies</param>
        public void SetCookies(RequestCookies cookies)
        {
            Cookies = cookies;
        }

        public IPEndPoint LocalIPEndPoint => _context.LocalIPEndPoint;

        public IPEndPoint RemoteIPEndPoint
        {
            get
            {
                if(_remoteIPEndPoint == null)
                {
                    string addr = _headers["x-forwarded-for"];
                    if(!string.IsNullOrEmpty(addr))
                    {
                        int port = _context.LocalIPEndPoint.Port;
                        try
                        {
                            _remoteIPEndPoint = new IPEndPoint(IPAddress.Parse(addr), port);
                        }
                        catch
                        {
                            _remoteIPEndPoint = null;
                        }
                    }
                }
                if (_remoteIPEndPoint == null)
                    _remoteIPEndPoint = _context.LocalIPEndPoint;

                return _remoteIPEndPoint;
            }
        }
        /*
        /// <summary>
        /// Create a response object.
        /// </summary>
        /// <returns>A new <see cref="IHttpResponse"/>.</returns>
        public IHttpResponse CreateResponse(IHttpClientContext context)
        {
            return new HttpResponse(context, this);
        }
        */
        /// <summary>
        /// Called during parsing of a <see cref="IHttpRequest"/>.
        /// </summary>
        /// <param name="name">Name of the header, should not be URL encoded</param>
        /// <param name="value">Value of the header, should not be URL encoded</param>
        /// <exception cref="BadRequestException">If a header is incorrect.</exception>
        public void AddHeader(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
                throw new BadRequestException("Invalid header name: " + name ?? "<null>");
            if (string.IsNullOrEmpty(value))
                throw new BadRequestException("Header '" + name + "' do not contain a value.");

            name = name.ToLowerInvariant();

            switch (name)
            {
                case "http_x_requested_with":
                case "x-requested-with":
                    if (string.Compare(value, "XMLHttpRequest", true) == 0)
                        IsAjax = true;
                    break;
                case "accept":
                    AcceptTypes = value.Split(',');
                    for (int i = 0; i < AcceptTypes.Length; ++i)
                        AcceptTypes[i] = AcceptTypes[i].Trim();
                    break;
                case "content-length":
                    if (!int.TryParse(value, out int t))
                        throw new BadRequestException("Invalid content length.");
                    ContentLength = t;
                    break; //todo: maybe throw an exception
                case "host":
                    try
                    {
                        _uri = new Uri((Secure ? "https://" : "http://") + value + _uriPath);
                        _uriPath = _uri.AbsolutePath;
                    }
                    catch (UriFormatException err)
                    {
                        throw new BadRequestException("Failed to parse uri: " + value + _uriPath, err);
                    }
                    break;
                case "remote_addr":
                    if (_headers[name] == null)
                        _headers.Add(name, value);
                    break;

                case "forwarded":
                    string[] parts = value.Split(new char[]{';'});
                    string addr = string.Empty;
                    for(int i = 0; i < parts.Length; ++i)
                    {
                        string s = parts[i].TrimStart();
                        if(s.Length < 10)
                            continue;
                        if(s.StartsWith("for", StringComparison.InvariantCultureIgnoreCase))
                        {
                            int indx = s.IndexOf("=", 3);
                            if(indx < 0 || indx >= s.Length - 1)
                                continue;
                            s = s.Substring(indx);
                            addr = s.Trim();
                        }
                    }
                    if(addr.Length > 7)
                    {
                        _headers.Add("x-forwarded-for", addr);
                    }
                    break;
                case "x-forwarded-for":
                    if (value.Length > 7)
                    {
                        string[] xparts = value.Split(new char[]{','});
                        if(xparts.Length > 0)
                        {
                            string xs = xparts[0].Trim();
                            if(xs.Length > 7)
                                _headers.Add("x-forwarded-for", xs);
                        }
                    }
                    break;
                case "connection":
                    if (string.Compare(value, "close", true) == 0)
                        Connection = ConnectionType.Close;
                    else if (value.StartsWith("keep-alive", StringComparison.CurrentCultureIgnoreCase))
                        Connection = ConnectionType.KeepAlive;
                    else if (value.StartsWith("Upgrade", StringComparison.CurrentCultureIgnoreCase))
                        Connection = ConnectionType.KeepAlive;
                    else
                        throw new BadRequestException("Unknown 'Connection' header type.");
                    break;

                /*
                case "expect":
                    if (value.Contains("100-continue"))
                    {

                    }
                    _headers.Add(name, value);
                    break;
                case "user-agent":

                    break;
                */
                default:
                    _headers.Add(name, value);
                    break;
            }
        }

        /// <summary>
        /// Add bytes to the body
        /// </summary>
        /// <param name="bytes">buffer to read bytes from</param>
        /// <param name="offset">where to start read</param>
        /// <param name="length">number of bytes to read</param>
        /// <returns>Number of bytes actually read (same as length unless we got all body bytes).</returns>
        /// <exception cref="InvalidOperationException">If body is not writable</exception>
        /// <exception cref="ArgumentNullException"><c>bytes</c> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><c>offset</c> is out of range.</exception>
        public int AddToBody(byte[] bytes, int offset, int length)
        {
            if (bytes == null)
                throw new ArgumentNullException("bytes");
            if (offset + length > bytes.Length)
                throw new ArgumentOutOfRangeException("offset");
            if (length == 0)
                return 0;
            if (!_body.CanWrite)
                throw new InvalidOperationException("Body is not writable.");

            if (length > _bodyBytesLeft)
            {
                length = _bodyBytesLeft;
            }

            _body.Write(bytes, offset, length);
            _bodyBytesLeft -= length;

            return length;
        }

        /// <summary>
        /// Clear everything in the request
        /// </summary>
        public void Clear()
        {
            if (_body != null && _body.CanRead)
                _body.Dispose();
            _body = null;
            _contentLength = 0;
            _method = string.Empty;
            _uri = null;
            _queryString = null;
            _bodyBytesLeft = 0;
            _headers.Clear();
            _connection = ConnectionType.KeepAlive;
            IsAjax = false;
            //_form.Clear();
        }

        #endregion
    }
}