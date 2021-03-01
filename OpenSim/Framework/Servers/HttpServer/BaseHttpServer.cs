/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using OSHttpServer;
using tinyHTTPListener = OSHttpServer.OSHttpListener;
using log4net;
using Nwc.XmlRpc;
using OpenSim.Framework.Monitoring;
using OpenMetaverse.StructuredData;


namespace OpenSim.Framework.Servers.HttpServer
{
    public class BaseHttpServer : IHttpServer
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly HttpServerLogWriter httpserverlog = new HttpServerLogWriter();
        private static readonly Encoding UTF8NoBOM = new System.Text.UTF8Encoding(false);
        public static PollServiceRequestManager _pollServiceManager;
        private static readonly object _generalLock = new object();
        private string HTTP404;

        /// <summary>
        /// This is a pending websocket request before it got an sucessful upgrade response.
        /// The consumer must call handler.HandshakeAndUpgrade() to signal to the handler to
        /// start the connection and optionally provide an origin authentication method.
        /// </summary>
        /// <param name="servicepath"></param>
        /// <param name="handler"></param>
        public delegate void WebSocketRequestDelegate(string servicepath, WebSocketHttpServerHandler handler);

        /// <summary>
        /// Gets or sets the debug level.
        /// </summary>
        /// <value>
        /// See MainServer.DebugLevel.
        /// </value>
        public int DebugLevel { get; set; }

        /// <summary>
        /// Request number for diagnostic purposes.
        /// </summary>
        /// <remarks>
        /// This is an internal number.  In some debug situations an external number may also be supplied in the
        /// opensim-request-id header but we are not currently logging this.
        /// </remarks>
        public int RequestNumber { get; private set; }

        /// <summary>
        /// Statistic for holding number of requests processed.
        /// </summary>
        private Stat _requestsProcessedStat;

        private volatile int NotSocketErrors = 0;
        public volatile bool HTTPDRunning = false;

        protected tinyHTTPListener _httpListener;
        protected Dictionary<string, XmlRpcMethod> _rpcHandlers        = new Dictionary<string, XmlRpcMethod>();
        protected Dictionary<string, JsonRPCMethod> jsonRpcHandlers     = new Dictionary<string, JsonRPCMethod>();
        protected Dictionary<string, bool> _rpcHandlersKeepAlive       = new Dictionary<string, bool>();
        protected DefaultLLSDMethod _defaultLlsdHandler = null; // <--   Moving away from the monolithic..  and going to /registered/
        protected Dictionary<string, LLSDMethod> _llsdHandlers         = new Dictionary<string, LLSDMethod>();
        protected Dictionary<string, GenericHTTPMethod> _HTTPHandlers  = new Dictionary<string, GenericHTTPMethod>();
        //protected Dictionary<string, IHttpAgentHandler> _agentHandlers = new Dictionary<string, IHttpAgentHandler>();
        protected ConcurrentDictionary<string, PollServiceEventArgs> _pollHandlers = new ConcurrentDictionary<string, PollServiceEventArgs>();
        protected ConcurrentDictionary<string, PollServiceEventArgs> _pollHandlersVarPath = new ConcurrentDictionary<string, PollServiceEventArgs>();
        protected ConcurrentDictionary<string, WebSocketRequestDelegate> _WebSocketHandlers = new ConcurrentDictionary<string, WebSocketRequestDelegate>();

        protected ConcurrentDictionary<string, IRequestHandler> _streamHandlers = new ConcurrentDictionary<string, IRequestHandler>();
        protected ConcurrentDictionary<string, ISimpleStreamHandler> _simpleStreamHandlers = new ConcurrentDictionary<string, ISimpleStreamHandler>();
        protected ConcurrentDictionary<string, ISimpleStreamHandler> _simpleStreamVarPath = new ConcurrentDictionary<string, ISimpleStreamHandler>();
        protected ConcurrentDictionary<string, SimpleStreamMethod> _indexPHPmethods = new ConcurrentDictionary<string, SimpleStreamMethod>();
        protected ConcurrentDictionary<string, SimpleStreamMethod> _globalMethods = new ConcurrentDictionary<string, SimpleStreamMethod>();

        protected IRequestHandler _RootDefaultGET = null; // default method for root path. does override rpc xml and json, and old llsd login

        protected uint _port;
        protected bool _ssl;
        private X509Certificate2 _cert;
        protected string _SSLCommonName = "";
        protected List<string> _certNames = new List<string>();
        protected List<string> _certIPs = new List<string>();
        protected string _certCN= "";
        protected RemoteCertificateValidationCallback _certificateValidationCallback = null;

        protected IPAddress _listenIPAddress = IPAddress.Any;

        public string Protocol => _ssl ? "https://" : "http://";

        public uint SSLPort => _port;

        public string SSLCommonName => _SSLCommonName;

        public uint Port => _port;

        public bool UseSSL => _ssl;

        public IPAddress ListenIPAddress
        {
            get => _listenIPAddress;
            set => _listenIPAddress = value;
        }

        public BaseHttpServer(uint port)
        {
            _port = port;
            SetHTTP404();
        }

        public BaseHttpServer(uint port, bool ssl, string CN, string CPath, string CPass)
        {
            _port = port;
            if (ssl)
            {
                if (string.IsNullOrEmpty(CPath))
                    throw new Exception("invalid main http server cert path");

                if (Uri.CheckHostName(CN) == UriHostNameType.Unknown)
                    throw new Exception("invalid main http server CN (ExternalHostName)");

                _certNames.Clear();
                _certIPs.Clear();
                _certCN = "";

                _ssl = true;
                load_cert(CPath, CPass);

                if (!CheckSSLCertHost(CN))
                    throw new Exception("invalid main http server CN (ExternalHostName)");

                _SSLCommonName = CN;

                if (_cert.Issuer == _cert.Subject)
                    _log.Warn("Self signed certificate. Clients need to allow this (some viewers debug option NoVerifySSLcert must be set to true");
            }
            else
                _ssl = false;

            SetHTTP404();
        }

        public BaseHttpServer(uint port, bool ssl, string CPath, string CPass)
        {
            _port = port;
            if (ssl)
            {
                load_cert(CPath, CPass);
                if (_cert.Issuer == _cert.Subject)
                    _log.Warn("Self signed certificate. Http clients need to allow this");
                _ssl = true;
            }
            else
                _ssl = false;

            SetHTTP404();
        }

        public RemoteCertificateValidationCallback CertificateValidationCallback
        {
            set => _certificateValidationCallback = value;
        }

        private void load_cert(string CPath, string CPass)
        {
            try
            {
                 _cert = new X509Certificate2(CPath, CPass);
                X509Extension ext = _cert.Extensions["2.5.29.17"];
                if(ext != null)
                {
                    AsnEncodedData asndata = new AsnEncodedData(ext.Oid, ext.RawData);
                    string datastr = asndata.Format(true);
                    string[] lines = datastr.Split(new char[] {'\n','\r'});
                    foreach(string s in lines)
                    {
                        if(string.IsNullOrEmpty(s))
                            continue;
                        string[] parts = s.Split(new char[] {'='});
                        if(string.IsNullOrEmpty(parts[0]))
                            continue;
                        string entryName = parts[0].Replace(" ","");
                        if(entryName == "DNSName")
                            _certNames.Add(parts[1]);
                        else if(entryName == "IPAddress")
                            _certIPs.Add(parts[1]);
                        else if(entryName == "Unknown(135)") // stupid mono
                        {
                            try
                            {
                                if(parts[1].Length == 8)
                                {
                                    long tmp = long.Parse(parts[1], NumberStyles.AllowHexSpecifier);
                                    tmp = IPAddress.HostToNetworkOrder(tmp);
                                    tmp = (long)((ulong) tmp >> 32);
                                    IPAddress ia = new IPAddress(tmp);     
                                    _certIPs.Add(ia.ToString());
                                }
                            }
                            catch {}
                        }
                    }
                }
                _certCN = _cert.GetNameInfo(X509NameType.SimpleName, false);
            }
            catch
            {
                throw new Exception("SSL cert load error");
            }
        }

        static bool MatchDNS(string hostname, string dns)
        {
            int indx = dns.IndexOf('*');
            if (indx == -1)
                return string.Compare(hostname, dns, true, CultureInfo.InvariantCulture) == 0;

            int dnslen = dns.Length;
            dnslen--;
            if (indx == dnslen)
                return true; // just * ?

            if (indx > dnslen - 2)
                return false; // 2 short ?

            if (dns[indx + 1] != '.')
                return false;

            int indx2 = dns.IndexOf('*', indx + 1);
            if (indx2 != -1)
                return false; // there can only be one;

            string end = dns.Substring(indx + 1);
            int hostlen = hostname.Length;
            int endlen = end.Length;
            int length = hostlen - endlen;
            if (length <= 0)
                return false;

            if (string.Compare(hostname, length, end, 0, endlen, true, CultureInfo.InvariantCulture) != 0)
                return false;

            if (indx == 0)
            {
                indx2 = hostname.IndexOf('.');
                return indx2 == -1 || indx2 >= length;
            }

            string start = dns.Substring(0, indx);
            return string.Compare(hostname, 0, start, 0, start.Length, true, CultureInfo.InvariantCulture) == 0;
        }

        public bool CheckSSLCertHost(string hostname)
        {
            UriHostNameType htype = Uri.CheckHostName(hostname);

            if(htype == UriHostNameType.Unknown || htype == UriHostNameType.Basic)
                return false;
            if(htype == UriHostNameType.Dns)
            {
                foreach(string name in _certNames)
                {
                    if(MatchDNS(hostname, name))
                        return true;
                }
                if(MatchDNS(hostname, _certCN))
                    return true;
            }
            else
            {
                foreach(string ip in _certIPs)
                {
                    if (string.Compare(hostname, ip, true, CultureInfo.InvariantCulture) == 0)
                        return true;
                }               
            }

            return false;
        }
        /// <summary>
        /// Add a stream handler to the http server.  If the handler already exists, then nothing happens.
        /// </summary>
        /// <param name="handler"></param>
        public void AddStreamHandler(IRequestHandler handler)
        {
            string httpMethod = handler.HttpMethod;
            string path = handler.Path;

            if(path == "/")
            {
                if(httpMethod == "GET")
                    _RootDefaultGET = handler;

                return;
            }

            string handlerKey = GetHandlerKey(httpMethod, path);

            // _log.DebugFormat("[BASE HTTP SERVER]: Adding handler key {0}", handlerKey);
            _streamHandlers.TryAdd(handlerKey, handler);
        }

        public void AddGenericStreamHandler(IRequestHandler handler)
        {
            if(string.IsNullOrWhiteSpace(handler.Path))
                return;

            // _log.DebugFormat("[BASE HTTP SERVER]: Adding handler key {0}", handlerKey);
            _streamHandlers.TryAdd(handler.Path, handler);
        }

        public void AddSimpleStreamHandler(ISimpleStreamHandler handler, bool varPath = false)
        {
            if (varPath)
                _simpleStreamVarPath.TryAdd(handler.Path, handler);
            else
                _simpleStreamHandlers.TryAdd(handler.Path, handler);
        }

        public void AddWebSocketHandler(string servicepath, WebSocketRequestDelegate handler)
        {
            _WebSocketHandlers.TryAdd(servicepath, handler);
        }

        public void RemoveWebSocketHandler(string servicepath)
        {
            _WebSocketHandlers.TryRemove(servicepath, out WebSocketRequestDelegate dummy);
        }

        public List<string> GetStreamHandlerKeys()
        {
            return new List<string>(_streamHandlers.Keys);
        }

        public List<string> GetSimpleStreamHandlerKeys()
        {
            List<string> ssh = new List<string>(_simpleStreamHandlers.Keys);
            ssh.AddRange(new List<string>(_simpleStreamVarPath.Keys));
            return ssh;
        }

        public List<string> GetIndexPHPHandlerKeys()
        {
            return new List<string>(_indexPHPmethods.Keys);
        }

        public List<string> GetGLobalMethodsKeys()
        {
            return new List<string>(_globalMethods.Keys);
        }

        private static string GetHandlerKey(string httpMethod, string path)
        {
            return httpMethod + ":" + path;
        }

        public bool AddXmlRPCHandler(string method, XmlRpcMethod handler)
        {
            return AddXmlRPCHandler(method, handler, true);
        }

        public bool AddXmlRPCHandler(string method, XmlRpcMethod handler, bool keepAlive)
        {
            lock (_rpcHandlers)
            {
                _rpcHandlers[method] = handler;
                _rpcHandlersKeepAlive[method] = keepAlive; // default
            }

            return true;
        }

        public XmlRpcMethod GetXmlRPCHandler(string method)
        {
            lock (_rpcHandlers)
            {
                if (_rpcHandlers.ContainsKey(method))
                {
                    return _rpcHandlers[method];
                }
                else
                {
                    return null;
                }
            }
        }

        public bool TryGetXmlRPCHandler(string method, out XmlRpcMethod handler)
        {
            lock (_rpcHandlers)
            {
                if(_rpcHandlers.TryGetValue(method, out handler))
                    return true;
            }
            return false;
        }

        public List<string> GetXmlRpcHandlerKeys()
        {
            lock (_rpcHandlers)
                return new List<string>(_rpcHandlers.Keys);
        }

        // JsonRPC
        public bool AddJsonRPCHandler(string method, JsonRPCMethod handler)
        {
            lock(jsonRpcHandlers)
            {
                jsonRpcHandlers.Add(method, handler);
            }
            return true;
        }

        public JsonRPCMethod GetJsonRPCHandler(string method)
        {
            lock (jsonRpcHandlers)
            {
                if (jsonRpcHandlers.ContainsKey(method))
                {
                    return jsonRpcHandlers[method];
                }
                else
                {
                    return null;
                }
            }
        }

        public List<string> GetJsonRpcHandlerKeys()
        {
            lock (jsonRpcHandlers)
                return new List<string>(jsonRpcHandlers.Keys);
        }

        public bool AddHTTPHandler(string methodName, GenericHTTPMethod handler)
        {
            //_log.DebugFormat("[BASE HTTP SERVER]: Registering {0}", methodName);

            lock (_HTTPHandlers)
            {
                if (!_HTTPHandlers.ContainsKey(methodName))
                {
                    _HTTPHandlers.Add(methodName, handler);
                    return true;
                }
            }

            //must already have a handler for that path so return false
            return false;
        }

        public List<string> GetHTTPHandlerKeys()
        {
            lock (_HTTPHandlers)
                return new List<string>(_HTTPHandlers.Keys);
        }

        public bool AddPollServiceHTTPHandler(string url, PollServiceEventArgs args)
        {
            return _pollHandlers.TryAdd(url, args);
        }

        public bool AddPollServiceHTTPHandler(PollServiceEventArgs args)
        {
            return _pollHandlers.TryAdd(args.Url, args);
        }

        public bool AddPollServiceHTTPHandlerVarPath(PollServiceEventArgs args)
        {
            return _pollHandlersVarPath.TryAdd(args.Url, args);
        }

        public List<string> GetPollServiceHandlerKeys()
        {
            List<string> s = new List<string>(_pollHandlers.Keys);
            s.AddRange(_pollHandlersVarPath.Keys);
            return s;
        }

        public bool AddLLSDHandler(string path, LLSDMethod handler)
        {
            lock (_llsdHandlers)
            {
                if (!_llsdHandlers.ContainsKey(path))
                {
                    _llsdHandlers.Add(path, handler);
                    return true;
                }
            }
            return false;
        }

        public List<string> GetLLSDHandlerKeys()
        {
            lock (_llsdHandlers)
                return new List<string>(_llsdHandlers.Keys);
        }

        public bool SetDefaultLLSDHandler(DefaultLLSDMethod handler)
        {
            _defaultLlsdHandler = handler;
            return true;
        }

        public void AddIndexPHPMethodHandler(string key, SimpleStreamMethod sh)
        {
            _indexPHPmethods.TryAdd(key, sh);
        }

        public void RemoveIndexPHPMethodHandler(string key)
        {
            _indexPHPmethods.TryRemove(key, out SimpleStreamMethod sh);
        }

        public SimpleStreamMethod TryGetIndexPHPMethodHandler(string key)
        {
            if (!string.IsNullOrWhiteSpace(key) && _indexPHPmethods.TryGetValue(key, out SimpleStreamMethod sh))
                return sh;
            return null;
        }

        public void AddGloblaMethodHandler(string key, SimpleStreamMethod sh)
        {
            _globalMethods.TryAdd(key, sh);
        }

        public void RemoveGlobalPMethodHandler(string key)
        {
            _globalMethods.TryRemove(key, out SimpleStreamMethod sh);
        }

        public bool TryGetGlobalMethodHandler(string key, out SimpleStreamMethod sh)
        {
            if(string.IsNullOrWhiteSpace(key))
            {
                sh = null;
                return false;
            }
            return _globalMethods.TryGetValue(key, out sh);
        }

        public void OnRequest(object source, RequestEventArgs args)
        {
            RequestNumber++;
            try
            {
                IHttpRequest request = args.Request;
                OSHttpRequest osRequest = new OSHttpRequest(request);

                if(_WebSocketHandlers.TryGetValue(osRequest.RawUrl, out WebSocketRequestDelegate dWebSocketRequestDelegate))
                {
                    dWebSocketRequestDelegate?.Invoke(osRequest.Url.AbsolutePath, new WebSocketHttpServerHandler(osRequest, 8192));
                    return;
                }

                if (TryGetPollServiceHTTPHandler(Util.TrimEndSlash(request.UriPath), out PollServiceEventArgs psEvArgs))
                {
                    psEvArgs.RequestsReceived++;
                    PollServiceHttpRequest psreq = new PollServiceHttpRequest(psEvArgs, request);
                    if(psEvArgs.Request == null)
                        _pollServiceManager.Enqueue(psreq);
                    else
                    {
                        OSHttpResponse resp = psEvArgs.Request.Invoke(psreq.RequestID, osRequest);
                        if(resp == null)
                            _pollServiceManager.Enqueue(psreq);
                        else
                            resp.Send();
                    }
                    psreq = null;
                }
                else
                {
                    HandleRequest(osRequest, new OSHttpResponse(osRequest));
                }
            }
            catch (Exception e)
            {
                _log.Error(string.Format("[BASE HTTP SERVER]: OnRequest() failed: {0} ", e.Message), e);
            }
        }

        /// <summary>
        /// This methods is the start of incoming HTTP request handling.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        public virtual void HandleRequest(OSHttpRequest request, OSHttpResponse response)
        {
            string requestMethod = request.HttpMethod;
            string uriString = request.RawUrl;

            int requestStartTick = Environment.TickCount;

            // Will be adjusted later on.
            int requestEndTick = requestStartTick;

            IRequestHandler requestHandler = null;

            try
            {
                // OpenSim.Framework.WebUtil.OSHeaderRequestID
//                if (request.Headers["opensim-request-id"] != null)
//                    reqnum = String.Format("{0}:{1}",request.RemoteIPEndPoint,request.Headers["opensim-request-id"]);
                 //_log.DebugFormat("[BASE HTTP SERVER]: <{0}> handle request for {1}",reqnum,request.RawUrl);

                Culture.SetCurrentCulture();

                if (request.HttpMethod == "OPTIONS")
                {
                    //need to check this
                    response.AddHeader("Access-Control-Allow-Origin", "*");
                    response.AddHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, PUT, OPTIONS");
                    response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    response.StatusCode = (int)HttpStatusCode.OK;

                    if (request.InputStream != null && request.InputStream.CanRead)
                        request.InputStream.Dispose();

                    requestEndTick = Environment.TickCount;
                    response.Send();
                    return;
                }

                string path = request.UriPath;
                if (path == "/")
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound; // default

                    if (_RootDefaultGET != null && request.HttpMethod == "GET")
                    {
                        if(_RootDefaultGET is IStreamedRequestHandler)
                        {
                            IStreamedRequestHandler isrh = _RootDefaultGET as IStreamedRequestHandler;
                            response.RawBuffer = isrh.Handle(path, request.InputStream, request, response);
                            response.StatusCode = (int)HttpStatusCode.OK;
                        }
                        if (request.InputStream != null && request.InputStream.CanRead)
                            request.InputStream.Dispose();

                        requestEndTick = Environment.TickCount;
                        response.Send();
                        return;
                    }

                    switch (request.ContentType)
                    {
                        case "application/json-rpc":
                        {
                            if (DebugLevel >= 3)
                                LogIncomingToContentTypeHandler(request);

                            HandleJsonRpcRequests(request, response);
                            break;
                        }

                        case "application/llsd+xml":
                        {
                            HandleLLSDLogin(request, response);
                            break;
                        }
                        default: // not sure about xmlrpc content type coerence at this point
                        { 
                            // let legacy datasnapshot work
                            if(request.QueryString.Count > 0 && request.QueryAsDictionary.TryGetValue("method", out string method))
                            {
                                if(TryGetGlobalMethodHandler(method, out SimpleStreamMethod sm))
                                {
                                    sm?.Invoke(request, response);
                                    break;
                                }
                            }

                            if (DebugLevel >= 3)
                                LogIncomingToXmlRpcHandler(request);

                            HandleXmlRpcRequests(request, response);
                            break;
                        }
                    }

                    if (request.InputStream != null && request.InputStream.CanRead)
                        request.InputStream.Dispose();

                    requestEndTick = Environment.TickCount;
                    response.Send();
                    return;
                }

                path = Util.TrimEndSlash(path);

                if (TryGetSimpleStreamHandler(path, out ISimpleStreamHandler hdr))
                {
                    hdr.Handle(request, response);
                    if (request.InputStream != null && request.InputStream.CanRead)
                        request.InputStream.Dispose();

                    requestEndTick = Environment.TickCount;
                    response.Send();
                    return;
                }

                string handlerKey = GetHandlerKey(request.HttpMethod, path);
                byte[] buffer = null;

                if (TryGetStreamHandler(handlerKey, out requestHandler))
                {
                    if (DebugLevel >= 3)
                        LogIncomingToStreamHandler(request, requestHandler);

                    response.ContentType = requestHandler.ContentType; // Lets do this defaulting before in case handler has varying content type.

                    if (requestHandler is IStreamedRequestHandler)
                    {
                        IStreamedRequestHandler streamedRequestHandler = requestHandler as IStreamedRequestHandler;

                        buffer = streamedRequestHandler.Handle(path, request.InputStream, request, response);
                    }
                    else if (requestHandler is IGenericHTTPHandler)
                    {
                        //_log.Debug("[BASE HTTP SERVER]: Found Caps based HTTP Handler");
                        IGenericHTTPHandler HTTPRequestHandler = requestHandler as IGenericHTTPHandler;

                        string requestBody;
                        Encoding encoding = Encoding.UTF8;
                        using(StreamReader reader = new StreamReader(request.InputStream, encoding))
                            requestBody = reader.ReadToEnd();

                        Hashtable keysvals = new Hashtable();
                        Hashtable headervals = new Hashtable();
                        //string host = String.Empty;

                        string[] querystringkeys = request.QueryString.AllKeys;
                        string[] rHeaders = request.Headers.AllKeys;

                        foreach (string queryname in querystringkeys)
                        {
                            keysvals.Add(queryname, request.QueryString[queryname]);
                        }

                        foreach (string headername in rHeaders)
                        {
                            //_log.Warn("[HEADER]: " + headername + "=" + request.Headers[headername]);
                            headervals[headername] = request.Headers[headername];
                        }

                        keysvals.Add("requestbody", requestBody);
                        keysvals.Add("headers",headervals);
                        //if (keysvals.Contains("method"))
                        //{
                            //_log.Warn("[HTTP]: Contains Method");
                            //string method = (string)keysvals["method"];
                            //_log.Warn("[HTTP]: " + requestBody);
                        //}

                        buffer = DoHTTPGruntWork(HTTPRequestHandler.Handle(path, keysvals), response);
                    }
                    else
                    {
                        IStreamHandler streamHandler = (IStreamHandler)requestHandler;
                        using (MemoryStream memoryStream = new MemoryStream())
                        {
                            streamHandler.Handle(path, request.InputStream, memoryStream, request, response);
                            buffer = memoryStream.ToArray();
                        }
                    }
                }
                else
                {
                    switch (request.ContentType)
                    {
                        case null:
                        case "text/html":
                            if (DebugLevel >= 3)
                                LogIncomingToContentTypeHandler(request);

                            buffer = HandleHTTPRequest(request, response);
                            break;

                        case "application/llsd+xml":
                        case "application/xml+llsd":
                        case "application/llsd+json":
                            if (DebugLevel >= 3)
                                LogIncomingToContentTypeHandler(request);

                            buffer = HandleLLSDRequests(request, response);
                            break;

                        case "text/xml":
                        case "application/xml":
                        case "application/json":
                        default:
                            if (DoWeHaveALLSDHandler(request.RawUrl))
                            {
                                if (DebugLevel >= 3)
                                    LogIncomingToContentTypeHandler(request);

                                buffer = HandleLLSDRequests(request, response);
                            }
                            else if (DoWeHaveAHTTPHandler(request.RawUrl))
                            {
                                if (DebugLevel >= 3)
                                    LogIncomingToContentTypeHandler(request);

                                buffer = HandleHTTPRequest(request, response);
                            }
                            break;
                    }
                }

                if(request.InputStream != null && request.InputStream.CanRead)
                    request.InputStream.Dispose();

                if (buffer != null)
                {
                    if (WebUtil.DebugLevel >= 5)
                    {
                        string output = System.Text.Encoding.UTF8.GetString(buffer);

                        if (WebUtil.DebugLevel >= 6)
                        {
                            // Always truncate binary blobs. We don't have a ContentType, so detect them using the request name.
                            if (requestHandler != null && requestHandler.Name == "GetMesh")
                            {
                                if (output.Length > WebUtil.MaxRequestDiagLength)
                                    output = output.Substring(0, WebUtil.MaxRequestDiagLength) + "...";
                            }
                        }

                        WebUtil.LogResponseDetail(RequestNumber, output);
                    }

                    if (!response.SendChunked && response.ContentLength64 <= 0)
                        response.ContentLength64 = buffer.LongLength;

                    //response.OutputStream.Write(buffer, 0, buffer.Length);
                    response.RawBufferStart = 0;
                    response.RawBufferLen = buffer.Length;
                    response.RawBuffer = buffer;
                }

                // Do not include the time taken to actually send the response to the caller in the measurement
                // time.  This is to avoid logging when it's the client that is slow to process rather than the
                // server
                requestEndTick = Environment.TickCount;

                buffer = null;
                response.Send();
            }
            catch (SocketException e)
            {
                // At least on linux, it appears that if the client makes a request without requiring the response,
                // an unconnected socket exception is thrown when we close the response output stream.  There's no
                // obvious way to tell if the client didn't require the response, so instead we'll catch and ignore
                // the exception instead.
                //
                // An alternative may be to turn off all response write exceptions on the HttpListener, but let's go
                // with the minimum first
                _log.Warn(string.Format("[BASE HTTP SERVER]: HandleRequest threw {0}.\nNOTE: this may be spurious on Linux ", e.Message), e);
            }
            catch (IOException e)
            {
                _log.Error("[BASE HTTP SERVER]: HandleRequest() threw exception ", e);
            }
            catch (Exception e)
            {
                _log.Error("[BASE HTTP SERVER]: HandleRequest() threw exception ", e);
                try
                {
                    response.StatusCode =(int)HttpStatusCode.InternalServerError;
                    response.Send();
                }
                catch {}
            }
            finally
            {
                if(request.InputStream != null && request.InputStream.CanRead)
                    request.InputStream.Close();

                int tickdiff = requestEndTick - requestStartTick;
                if (tickdiff > 3000)
                {
                    _log.InfoFormat(
                        "[LOGHTTP] Slow handling of {0} {1} {2} {3} {4} from {5} took {6}ms",
                        RequestNumber,
                        requestMethod,
                        uriString,
                        requestHandler != null ? requestHandler.Name : "",
                        requestHandler != null ? requestHandler.Description : "",
                        request.RemoteIPEndPoint,
                        tickdiff);
                }
                else if (DebugLevel >= 4)
                {
                    _log.DebugFormat(
                        "[LOGHTTP] HTTP IN {0} :{1} took {2}ms",
                        RequestNumber,
                        Port,
                        tickdiff);
                }
            }
        }

        private void LogIncomingToStreamHandler(OSHttpRequest request, IRequestHandler requestHandler)
        {
            _log.DebugFormat(
                "[LOGHTTP] HTTP IN {0} :{1} stream handler {2} {3} {4} {5} from {6}",
                RequestNumber,
                Port,
                request.HttpMethod,
                request.Url.PathAndQuery,
                requestHandler.Name,
                requestHandler.Description,
                request.RemoteIPEndPoint);

            if (DebugLevel >= 5)
                LogIncomingInDetail(request);
        }

        private void LogIncomingToContentTypeHandler(OSHttpRequest request)
        {
            _log.DebugFormat(
                "[LOGHTTP] HTTP IN {0} :{1} {2} content type handler {3} {4} from {5}",
                RequestNumber,
                Port,
                string.IsNullOrEmpty(request.ContentType) ? "not set" : request.ContentType,
                request.HttpMethod,
                request.Url.PathAndQuery,
                request.RemoteIPEndPoint);

            if (DebugLevel >= 5)
                LogIncomingInDetail(request);
        }

        private void LogIncomingToXmlRpcHandler(OSHttpRequest request)
        {
            _log.DebugFormat(
                "[LOGHTTP] HTTP IN {0} :{1} assumed generic XMLRPC request {2} {3} from {4}",
                RequestNumber,
                Port,
                request.HttpMethod,
                request.Url.PathAndQuery,
                request.RemoteIPEndPoint);

            if (DebugLevel >= 5)
                LogIncomingInDetail(request);
        }

        private void LogIncomingInDetail(OSHttpRequest request)
        {
            if (request.ContentType == "application/octet-stream")
                return; // never log these; they're just binary data

            Stream inputStream = Util.Copy(request.InputStream);
            Stream innerStream = null;
            try
            {
                if (request.Headers["Content-Encoding"] == "gzip" || request.Headers["X-Content-Encoding"] == "gzip")
                {
                    innerStream = inputStream;
                    inputStream = new GZipStream(innerStream, System.IO.Compression.CompressionMode.Decompress);
                }

                using (StreamReader reader = new StreamReader(inputStream, Encoding.UTF8))
                {
                    string output;

                    if (DebugLevel == 5)
                    {
                        char[] chars = new char[WebUtil.MaxRequestDiagLength + 1];  // +1 so we know to add "..." only if needed
                        int len = reader.Read(chars, 0, WebUtil.MaxRequestDiagLength + 1);
                        output = new string(chars, 0, Math.Min(len, WebUtil.MaxRequestDiagLength));
                        if (len > WebUtil.MaxRequestDiagLength)
                            output += "...";
                    }
                    else
                    {
                        output = reader.ReadToEnd();
                    }

                    _log.DebugFormat("[LOGHTTP] {0}", Util.BinaryToASCII(output));
                }
            }
            finally
            {
                if (innerStream != null)
                    innerStream.Dispose();
                inputStream.Dispose();
            }
        }

        private bool TryGetStreamHandler(string handlerKey, out IRequestHandler streamHandler)
        {
            if(_streamHandlers.TryGetValue(handlerKey, out streamHandler))
                return true;

            string bestMatch = null;
            bool hasbest=false;

            lock (_streamHandlers)
            {
                foreach (string pattern in _streamHandlers.Keys)
                {
                    if (handlerKey.StartsWith(pattern))
                    {
                        if (!hasbest || pattern.Length > bestMatch.Length)
                        {
                            bestMatch = pattern;
                            hasbest = true;
                        }
                    }
                }
            }
            if (hasbest)
            {
                streamHandler = _streamHandlers[bestMatch];
                return true;
            }
            streamHandler = null;
            return false;
        }

        private bool TryGetPollServiceHTTPHandler(string handlerKey, out PollServiceEventArgs oServiceEventArgs)
        {
            if(_pollHandlers.TryGetValue(handlerKey, out oServiceEventArgs))
                return true;

            if(_pollHandlersVarPath.Count > 0 && handlerKey.Length >= 45)
            {
                // tuned for lsl requests, the only ones that should reach this, so be strict (/lslhttp/uuid.ToString())
                int indx = handlerKey.IndexOf('/', 44);
                if (indx < 44) //lsl requests
                {
                    if(_pollHandlersVarPath.TryGetValue(handlerKey, out oServiceEventArgs))
                        return true;
                }
                else if(_pollHandlersVarPath.TryGetValue(handlerKey.Substring(0, indx), out oServiceEventArgs))
                    return true;
            }

            oServiceEventArgs = null;
            return false;
        }

        private bool TryGetHTTPHandler(string handlerKey, out GenericHTTPMethod HTTPHandler)
        {
//            _log.DebugFormat("[BASE HTTP HANDLER]: Looking for HTTP handler for {0}", handlerKey);

            if(_HTTPHandlers.TryGetValue(handlerKey, out HTTPHandler))
                return true;

            string bestMatch = null;
            bool hasmatch = false;

            lock (_HTTPHandlers)
            {
                foreach (string pattern in _HTTPHandlers.Keys)
                {
                    if (handlerKey.StartsWith(pattern))
                    {
                        if (!hasmatch || pattern.Length > bestMatch.Length)
                        {
                            bestMatch = pattern;
                            hasmatch = true;
                        }
                    }
                }
            }
            if (hasmatch)
            {
                HTTPHandler = _HTTPHandlers[bestMatch];
                return true;
            }

            HTTPHandler = null;
            return false;
        }

        private bool TryGetSimpleStreamHandler(string uripath, out ISimpleStreamHandler handler)
        {
            if(_simpleStreamHandlers.TryGetValue(uripath, out handler))
                return true;

            // look only for keyword before second slash ( /keyword/someparameter/... )
            handler = null;
            if(uripath.Length < 3)
                return false;
            int indx = uripath.IndexOf('/', 2);
            if(indx < 0 || indx == uripath.Length - 1)
                return false;

            return _simpleStreamVarPath.TryGetValue(uripath.Substring(0,indx), out handler);
        }

        /// <summary>
        /// Try all the registered xmlrpc handlers when an xmlrpc request is received.
        /// Sends back an XMLRPC unknown request response if no handler is registered for the requested method.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="response"></param>
        public void HandleXmlRpcRequests(OSHttpRequest request, OSHttpResponse response)
        {
            Stream requestStream = request.InputStream;
            Stream innerStream = null;
            try
            {
                if (request.Headers["Content-Encoding"] == "gzip" || request.Headers["X-Content-Encoding"] == "gzip")
                {
                    innerStream = requestStream;
                    requestStream = new GZipStream(innerStream, CompressionMode.Decompress);
                }
            }
            catch
            {
                if (requestStream.CanRead)
                    requestStream.Dispose();
                if (innerStream != null && innerStream.CanRead)
                    innerStream.Dispose();

                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.KeepAlive = false;
                return;
            }

            XmlRpcRequest xmlRprcRequest = null;
            try
            {
                using (StreamReader reader = new StreamReader(requestStream, Encoding.UTF8))
                {
                    var xmlDes = new XmlRpcRequestDeserializer();
                    xmlRprcRequest = (XmlRpcRequest)xmlDes.Deserialize(reader);
                }
            }
            catch (Exception e)
            {
                _log.WarnFormat(
                    "[BASE HTTP SERVER]: Fail to decode XMLRPC request {0}: {1}",
                        request.RemoteIPEndPoint, e.Message);
            }
            finally
            {
                if (requestStream.CanRead)
                    requestStream.Dispose();
                if (innerStream != null && innerStream.CanRead)
                    innerStream.Dispose();
            }

            if (xmlRprcRequest == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.KeepAlive = false;
                return;
            }

            string methodName = xmlRprcRequest.MethodName;
            if (string.IsNullOrWhiteSpace(methodName))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.KeepAlive = false;
                return;
            }

            XmlRpcMethod method;
            bool methodWasFound;
            bool keepAlive = false;

            lock (_rpcHandlers)
            {
                methodWasFound = _rpcHandlers.TryGetValue(methodName, out method);
                if (methodWasFound)
                    keepAlive = _rpcHandlersKeepAlive[methodName];
            }

            XmlRpcResponse xmlRpcResponse;
            if (methodWasFound)
            {
                xmlRprcRequest.Params.Add(request.RemoteIPEndPoint); // Param[1]
                xmlRprcRequest.Params.Add(request.Url); // Param[2]

                string xff = "X-Forwarded-For";
                string xfflower = xff.ToLower();
                foreach (string s in request.Headers.AllKeys)
                {
                    if (s != null && s.Equals(xfflower))
                    {
                        xff = xfflower;
                        break;
                    }
                }
                xmlRprcRequest.Params.Add(request.Headers.Get(xff)); // Param[3]

                // reserve this for
                // ... by Fumi.Iseki for DTLNSLMoneyServer
                // BUT make its presence possible to detect/parse
                string rcn = request.IHttpClientContext.SSLCommonName;
                if(!string.IsNullOrWhiteSpace(rcn))
                {
                    rcn = "SSLCN:" + rcn;
                    xmlRprcRequest.Params.Add(rcn); // Param[4] or Param[5]
                }

                try
                {
                    xmlRpcResponse = method(xmlRprcRequest, request.RemoteIPEndPoint);
                }
                catch(Exception e)
                {
                    string errorMessage
                        = string.Format(
                            "Requested method [{0}] from {1} threw exception: {2} {3}",
                            methodName, request.RemoteIPEndPoint.Address, e.Message, e.StackTrace);

                    _log.ErrorFormat("[BASE HTTP SERVER]: {0}", errorMessage);

                    // if the registered XmlRpc method threw an exception, we pass a fault-code along
                    xmlRpcResponse = new XmlRpcResponse();

                    // Code probably set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                    xmlRpcResponse.SetFault(-32603, errorMessage);
                }
                response.AddHeader("Access-Control-Allow-Origin", "*");
            }
            else
            {
                xmlRpcResponse = new XmlRpcResponse();
                // Code set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                xmlRpcResponse.SetFault(
                    XmlRpcErrorCodes.SERVER_ERROR_METHOD,
                    string.Format("Requested method [{0}] not found", methodName));
            }

            using (MemoryStream outs = new MemoryStream(64 * 1024))
            {
                using (XmlTextWriter writer = new XmlTextWriter(outs, UTF8NoBOM))
                {
                    writer.Formatting = Formatting.None;
                    var xmlrpcSer = new XmlRpcResponseSerializer();
                    xmlrpcSer.Serialize(writer, xmlRpcResponse);
                    writer.Flush();
                    response.RawBuffer = outs.GetBuffer();
                    response.RawBufferLen = (int)outs.Length;
                }
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.KeepAlive = keepAlive;
            response.ContentType = "text/xml";
        }

        public void HandleXmlRpcRequests(OSHttpRequest request, OSHttpResponse response, Dictionary<string, XmlRpcMethod> rpcHandlers)
        {
            Stream requestStream = request.InputStream;
            Stream innerStream = null;
            try
            {
                if (request.Headers["Content-Encoding"] == "gzip" || request.Headers["X-Content-Encoding"] == "gzip")
                {
                    innerStream = requestStream;
                    requestStream = new GZipStream(innerStream, CompressionMode.Decompress);
                }
            }
            catch
            {
                if (requestStream.CanRead)
                    requestStream.Dispose();
                if (innerStream != null && innerStream.CanRead)
                    innerStream.Dispose();

                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.KeepAlive = false;
                return;
            }

            XmlRpcRequest xmlRprcRequest = null;
            try
            {
                using (StreamReader reader = new StreamReader(requestStream, Encoding.UTF8))
                {
                    var xmlDes = new XmlRpcRequestDeserializer();
                    xmlRprcRequest = (XmlRpcRequest)xmlDes.Deserialize(reader);
                }
            }
            catch (Exception e)
            {
                _log.WarnFormat(
                    "[BASE HTTP SERVER]: Fail to decode XMLRPC request {0}: {1}",
                        request.RemoteIPEndPoint, e.Message);
            }
            finally
            {
                if (requestStream.CanRead)
                    requestStream.Dispose();
                if (innerStream != null && innerStream.CanRead)
                    innerStream.Dispose();
            }

            if (xmlRprcRequest == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.KeepAlive = false;
                return;
            }

            string methodName = xmlRprcRequest.MethodName;
            if (string.IsNullOrWhiteSpace(methodName))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.KeepAlive = false;
                return;
            }

            XmlRpcMethod method;
            bool methodWasFound;

            methodWasFound = rpcHandlers.TryGetValue(methodName, out method);

            XmlRpcResponse xmlRpcResponse;
            if (methodWasFound)
            {
                xmlRprcRequest.Params.Add(request.RemoteIPEndPoint); // Param[1]
                xmlRprcRequest.Params.Add(request.Url); // Param[2]

                string xff = "X-Forwarded-For";
                string xfflower = xff.ToLower();
                foreach (string s in request.Headers.AllKeys)
                {
                    if (s != null && s.Equals(xfflower))
                    {
                        xff = xfflower;
                        break;
                    }
                }
                xmlRprcRequest.Params.Add(request.Headers.Get(xff)); // Param[3]

                // reserve this for
                // ... by Fumi.Iseki for DTLNSLMoneyServer
                // BUT make its presence possible to detect/parse
                string rcn = request.IHttpClientContext.SSLCommonName;
                if (!string.IsNullOrWhiteSpace(rcn))
                {
                    rcn = "SSLCN:" + rcn;
                    xmlRprcRequest.Params.Add(rcn); // Param[4] or Param[5]
                }

                try
                {
                    xmlRpcResponse = method(xmlRprcRequest, request.RemoteIPEndPoint);
                }
                catch (Exception e)
                {
                    string errorMessage = string.Format(
                            "Requested method [{0}] from {1} threw exception: {2} {3}",
                            methodName, request.RemoteIPEndPoint.Address, e.Message, e.StackTrace);

                    _log.ErrorFormat("[BASE HTTP SERVER]: {0}", errorMessage);

                    // if the registered XmlRpc method threw an exception, we pass a fault-code along
                    xmlRpcResponse = new XmlRpcResponse();

                    // Code probably set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                    xmlRpcResponse.SetFault(-32603, errorMessage);
                }
                response.AddHeader("Access-Control-Allow-Origin", "*");
            }
            else
            {
                xmlRpcResponse = new XmlRpcResponse();
                // Code set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                xmlRpcResponse.SetFault(
                    XmlRpcErrorCodes.SERVER_ERROR_METHOD,
                    string.Format("Requested method [{0}] not found", methodName));
            }

            using (MemoryStream outs = new MemoryStream(64 * 1024))
            {
                using (XmlTextWriter writer = new XmlTextWriter(outs, UTF8NoBOM))
                {
                    writer.Formatting = Formatting.None;
                    var xmlrpcSer = new XmlRpcResponseSerializer();
                    xmlrpcSer.Serialize(writer, xmlRpcResponse);
                    writer.Flush();
                    response.RawBuffer = outs.GetBuffer();
                    response.RawBufferLen = (int)outs.Length;
                }
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.KeepAlive = false;
            response.ContentType = "text/xml";
        }

        // JsonRpc (v2.0 only)
        // Batch requests not yet supported
        private void HandleJsonRpcRequests(OSHttpRequest request, OSHttpResponse response)
        {
            JsonRpcResponse jsonRpcResponse = new JsonRpcResponse();
            OSDMap jsonRpcRequest = null;

            try
            {
                jsonRpcRequest = (OSDMap)OSDParser.DeserializeJson(request.InputStream);
            }
            catch (LitJson.JsonException e)
            {
                jsonRpcResponse.Error.Code = ErrorCode.InternalError;
                jsonRpcResponse.Error.Message = e.Message;
            }

            if (request.InputStream != null && request.InputStream.CanRead)
                request.InputStream.Dispose();

            if (jsonRpcRequest != null)
            {
                // If we have no id, then it's a "notification"
                if (jsonRpcRequest.TryGetValue("id", out OSD val))
                    jsonRpcResponse.Id = val.AsString();

                if (jsonRpcRequest.TryGetValue("jsonrpc", out OSD ver) && ver.AsString() == "2.0")
                {
                    jsonRpcResponse.JsonRpc = "2.0";

                    string methodname = jsonRpcRequest["method"];
                    if (!string.IsNullOrWhiteSpace(methodname) && jsonRpcHandlers.TryGetValue(methodname, out JsonRPCMethod method))
                    {
                        try
                        {
                            if(!method(jsonRpcRequest, ref jsonRpcResponse))
                            {
                                // The handler sent back an unspecified error
                                if(jsonRpcResponse.Error.Code == 0)
                                {
                                    jsonRpcResponse.Error.Code = ErrorCode.InternalError;
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            string ErrorMessage = string.Format("[BASE HTTP SERVER]: Json-Rpc Handler Error method {0} - {1}", methodname, e.Message);
                            _log.Error(ErrorMessage);
                            jsonRpcResponse.Error.Code = ErrorCode.InternalError;
                            jsonRpcResponse.Error.Message = ErrorMessage;
                        }
                    }
                    else // Error no handler defined for requested method
                    {
                        jsonRpcResponse.Error.Code = ErrorCode.InvalidRequest;
                        jsonRpcResponse.Error.Message = string.Format ("No handler defined for {0}", methodname);
                    }
                }
                else // not json-rpc 2.0
                {
                    jsonRpcResponse.Error.Code = ErrorCode.InvalidRequest;
                    jsonRpcResponse.Error.Message = "Must be valid json-rpc 2.0 see: http://www.jsonrpc.org/specification";
                }
            }

            string responseData = jsonRpcResponse.Serialize();
            response.RawBuffer = Util.UTF8NBGetbytes(responseData);
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private void HandleLLSDLogin(OSHttpRequest request, OSHttpResponse response)
        {
            if (_defaultLlsdHandler == null)
                return;

            response.StatusCode = (int)HttpStatusCode.BadRequest;

            try
            {
                OSD llsdRequest = OSDParser.DeserializeLLSDXml(request.InputStream);
                if (llsdRequest == null || !(llsdRequest is OSDMap))
                    return;

                OSD llsdResponse = _defaultLlsdHandler(llsdRequest, request.RemoteIPEndPoint);
                if (llsdResponse != null)
                {
                    response.ContentType = "application/llsd+xml";
                    response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(llsdResponse);
                    response.StatusCode = (int)HttpStatusCode.OK;
                    return;
                }
            }
            catch {}
            response.StatusCode = (int)HttpStatusCode.BadRequest;
        }

        private byte[] HandleLLSDRequests(OSHttpRequest request, OSHttpResponse response)
        {
            //_log.Warn("[BASE HTTP SERVER]: We've figured out it's a LLSD Request");
            if (!TryGetLLSDHandler(request.RawUrl, out LLSDMethod llsdhandler))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return null;
            }

            //_log.DebugFormat("[OGP]: {0}:{1}", request.RawUrl, requestBody);

            OSD llsdRequest = null;
            try
            {
                llsdRequest = OSDParser.Deserialize(request.InputStream);
            }
            catch (Exception ex)
            {
                _log.Warn("[BASE HTTP SERVER]: Error - " + ex.Message);
            }

            if (llsdRequest == null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            OSD llsdResponse = null;
            try
            {
                llsdResponse = llsdhandler(request.RawUrl, llsdRequest, request.RemoteIPEndPoint.ToString());
            }
            catch
            {
                llsdResponse = null;
            }

            if (llsdResponse == null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            byte[] buffer = new byte[0];
            if (llsdResponse.ToString() == "shutdown404!")
            {
                response.ContentType = "text/plain";
                response.StatusCode = (int)HttpStatusCode.NotFound;
            }
            else
            {
                // Select an appropriate response format
                buffer = BuildLLSDResponse(request, response, llsdResponse);
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentLength64 = buffer.Length;
            response.ContentEncoding = Encoding.UTF8;

            return buffer;
        }

        private byte[] BuildLLSDResponse(OSHttpRequest request, OSHttpResponse response, OSD llsdResponse)
        {
            if (request.AcceptTypes != null && request.AcceptTypes.Length > 0)
            {
                foreach (string strAccept in request.AcceptTypes)
                {
                    switch (strAccept)
                    {
                        case "application/llsd+xml":
                        case "application/xml":
                        case "text/xml":
                            response.ContentType = strAccept;
                            return OSDParser.SerializeLLSDXmlBytes(llsdResponse);
                        case "application/llsd+json":
                        case "application/json":
                            response.ContentType = strAccept;
                            return Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(llsdResponse));
                    }
                }
            }

            if (!string.IsNullOrEmpty(request.ContentType))
            {
                switch (request.ContentType)
                {
                    case "application/llsd+xml":
                    case "application/xml":
                    case "text/xml":
                        response.ContentType = request.ContentType;
                        return OSDParser.SerializeLLSDXmlBytes(llsdResponse);
                    case "application/llsd+json":
                    case "application/json":
                        response.ContentType = request.ContentType;
                        return Encoding.UTF8.GetBytes(OSDParser.SerializeJsonString(llsdResponse));
                }
            }

            // response.ContentType = "application/llsd+json";
            // return Util.UTF8.GetBytes(OSDParser.SerializeJsonString(llsdResponse));
            response.ContentType = "application/llsd+xml";
            return OSDParser.SerializeLLSDXmlBytes(llsdResponse);
        }

        /// <summary>
        /// Checks if we have an Exact path in the LLSD handlers for the path provided
        /// </summary>
        /// <param name="path">URI of the request</param>
        /// <returns>true if we have one, false if not</returns>
        private bool DoWeHaveALLSDHandler(string path)
        {
            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i = 1; i < pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length - 1 != i)
                    searchquery += "/";
            }

            string bestMatch = null;

            lock (_llsdHandlers)
            {
                foreach (string pattern in _llsdHandlers.Keys)
                {
                    if (searchquery.StartsWith(pattern) && searchquery.Length >= pattern.Length)
                        bestMatch = pattern;
                }
            }

            if (string.IsNullOrEmpty(bestMatch))
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        /// <summary>
        /// Checks if we have an Exact path in the HTTP handlers for the path provided
        /// </summary>
        /// <param name="path">URI of the request</param>
        /// <returns>true if we have one, false if not</returns>
        private bool DoWeHaveAHTTPHandler(string path)
        {
            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i = 1; i < pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length - 1 != i)
                    searchquery += "/";
            }

            string bestMatch = null;

            //_log.DebugFormat("[BASE HTTP HANDLER]: Checking if we have an HTTP handler for {0}", searchquery);

            lock (_HTTPHandlers)
            {
                foreach (string pattern in _HTTPHandlers.Keys)
                {
                    if (searchquery.StartsWith(pattern) && searchquery.Length >= pattern.Length)
                    {
                        bestMatch = pattern;
                    }
                }

                if (string.IsNullOrEmpty(bestMatch))
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
        }

        private bool TryGetLLSDHandler(string path, out LLSDMethod llsdHandler)
        {
            llsdHandler = null;
            // Pull out the first part of the path
            // splitting the path by '/' means we'll get the following return..
            // {0}/{1}/{2}
            // where {0} isn't something we really control 100%

            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i=1; i<pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length-1 != i)
                    searchquery += "/";
            }

            // while the matching algorithm below doesn't require it, we're expecting a query in the form
            //
            //   [] = optional
            //   /resource/UUID/action[/action]
            //
            // now try to get the closest match to the reigstered path
            // at least for OGP, registered path would probably only consist of the /resource/

            string bestMatch = null;

            lock (_llsdHandlers)
            {
                foreach (string pattern in _llsdHandlers.Keys)
                {
                    if (searchquery.ToLower().StartsWith(pattern.ToLower()))
                    {
                        if (string.IsNullOrEmpty(bestMatch) || searchquery.Length > bestMatch.Length)
                        {
                            // You have to specifically register for '/' and to get it, you must specificaly request it
                            //
                            if (pattern == "/" && searchquery == "/" || pattern != "/")
                                bestMatch = pattern;
                        }
                    }
                }

                if (string.IsNullOrEmpty(bestMatch))
                {
                    llsdHandler = null;
                    return false;
                }
                else
                {
                    llsdHandler = _llsdHandlers[bestMatch];
                    return true;
                }
            }
        }

        // legacy should go
        public byte[] HandleHTTPRequest(OSHttpRequest request, OSHttpResponse response)
        {
            //            _log.DebugFormat(
            //                "[BASE HTTP SERVER]: HandleHTTPRequest for request to {0}, method {1}",
            //                request.RawUrl, request.HttpMethod);
            if (!TryGetHTTPHandlerPathBased(request.RawUrl, out GenericHTTPMethod requestprocessor))
            {
                return SendHTML404(response);
            }

            //  _log.DebugFormat("[BASE HTTP SERVER]: HandleContentVerbs for request to {0}", request.RawUrl);

            // This is a test.  There's a workable alternative..  as this way sucks.
            // We'd like to put this into a text file parhaps that's easily editable.
            //
            // For this test to work, I used the following secondlife.exe parameters
            // "C:\Program Files\SecondLifeWindLight\SecondLifeWindLight.exe" -settings settings_windlight.xml -channel "Second Life WindLight"  -set SystemLanguage en-us -loginpage http://10.1.1.2:8002/?show_login_form=TRUE -loginuri http://10.1.1.2:8002 -user 10.1.1.2
            //
            // Even after all that, there's still an error, but it's a start.
            //
            // I depend on show_login_form being in the secondlife.exe parameters to figure out
            // to display the form, or process it.
            // a better way would be nifty.

            byte[] buffer;

            string requestBody;
            using(StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8))
                requestBody = reader.ReadToEnd();

            Hashtable keysvals = new Hashtable();
            Hashtable headervals = new Hashtable();

            Hashtable requestVars = new Hashtable();

            string host = string.Empty;

            string[] querystringkeys = request.QueryString.AllKeys;
            string[] rHeaders = request.Headers.AllKeys;

            keysvals.Add("body", requestBody);
            keysvals.Add("uri", request.RawUrl);
            keysvals.Add("content-type", request.ContentType);
            keysvals.Add("http-method", request.HttpMethod);

            foreach (string queryname in querystringkeys)
            {
//                _log.DebugFormat(
//                    "[BASE HTTP SERVER]: Got query paremeter {0}={1}", queryname, request.QueryString[queryname]);
                keysvals.Add(queryname, request.QueryString[queryname]);
                requestVars.Add(queryname, keysvals[queryname]);
            }

            foreach (string headername in rHeaders)
            {
//                _log.Debug("[BASE HTTP SERVER]: " + headername + "=" + request.Headers[headername]);
                headervals[headername] = request.Headers[headername];
            }

            keysvals.Add("headers", headervals);
            keysvals.Add("querystringkeys", querystringkeys);
            keysvals.Add("requestvars", requestVars);
//            keysvals.Add("form", request.Form);

            Hashtable responsedata2 = requestprocessor(keysvals);
            buffer = DoHTTPGruntWork(responsedata2, response);
            return buffer;
        }

        private bool TryGetHTTPHandlerPathBased(string path, out GenericHTTPMethod httpHandler)
        {
            httpHandler = null;
            // Pull out the first part of the path
            // splitting the path by '/' means we'll get the following return..
            // {0}/{1}/{2}
            // where {0} isn't something we really control 100%

            string[] pathbase = path.Split('/');
            string searchquery = "/";

            if (pathbase.Length < 1)
                return false;

            for (int i = 1; i < pathbase.Length; i++)
            {
                searchquery += pathbase[i];
                if (pathbase.Length - 1 != i)
                    searchquery += "/";
            }

            // while the matching algorithm below doesn't require it, we're expecting a query in the form
            //
            //   [] = optional
            //   /resource/UUID/action[/action]
            //
            // now try to get the closest match to the reigstered path
            // at least for OGP, registered path would probably only consist of the /resource/

            string bestMatch = null;

//            _log.DebugFormat(
//                "[BASE HTTP HANDLER]: TryGetHTTPHandlerPathBased() looking for HTTP handler to match {0}", searchquery);

            lock (_HTTPHandlers)
            {
                foreach (string pattern in _HTTPHandlers.Keys)
                {
                    if (searchquery.ToLower().StartsWith(pattern.ToLower()))
                    {
                        if (string.IsNullOrEmpty(bestMatch) || searchquery.Length > bestMatch.Length)
                        {
                            // You have to specifically register for '/' and to get it, you must specifically request it
                            if (pattern == "/" && searchquery == "/" || pattern != "/")
                                bestMatch = pattern;
                        }
                    }
                }

                if (string.IsNullOrEmpty(bestMatch))
                {
                    httpHandler = null;
                    return false;
                }
                else
                {
                    if (bestMatch == "/" && searchquery != "/")
                        return false;

                    httpHandler =  _HTTPHandlers[bestMatch];
                    return true;
                }
            }
        }

        internal byte[] DoHTTPGruntWork(Hashtable responsedata, OSHttpResponse response)
        {
            int responsecode;
            string responseString = string.Empty;
            byte[] responseData = null;
            string contentType;

            if (responsedata == null)
            {
                responsecode = 500;
                responseString = "No response could be obtained";
                contentType = "text/plain";
                responsedata = new Hashtable();
            }
            else
            {
                try
                {
                    //_log.Info("[BASE HTTP SERVER]: Doing HTTP Grunt work with response");
                    responsecode = (int)responsedata["int_response_code"];
                    if (responsedata["bin_response_data"] != null)
                        responseData = (byte[])responsedata["bin_response_data"];
                    else
                        responseString = (string)responsedata["str_response_string"];
                    contentType = (string)responsedata["content_type"];
                    if (responseString == null)
                        responseString = string.Empty;
                }
                catch
                {
                    responsecode = 500;
                    responseString = "No response could be obtained";
                    contentType = "text/plain";
                    responsedata = new Hashtable();
                }
            }

            if (responsedata.ContainsKey("error_status_text"))
            {
                response.StatusDescription = (string)responsedata["error_status_text"];
            }
            if (responsedata.ContainsKey("http_protocol_version"))
            {
                response.ProtocolVersion = (string)responsedata["http_protocol_version"];
            }

            if (responsedata.ContainsKey("keepalive"))
            {
                bool keepalive = (bool)responsedata["keepalive"];
                response.KeepAlive = keepalive;
            }

            // Cross-Origin Resource Sharing with simple requests
            if (responsedata.ContainsKey("access_control_allow_origin"))
                response.AddHeader("Access-Control-Allow-Origin", (string)responsedata["access_control_allow_origin"]);

            //Even though only one other part of the entire code uses HTTPHandlers, we shouldn't expect this
            //and should check for NullReferenceExceptions

            if (string.IsNullOrEmpty(contentType))
            {
                contentType = "text/html";
            }

            // The client ignores anything but 200 here for web login, so ensure that this is 200 for that

            response.StatusCode = responsecode;

            if (responsecode == (int)HttpStatusCode.Moved)
            {
                response.AddHeader("Location:", (string)responsedata["str_redirect_location"]);
                response.StatusCode = responsecode;
            }

            response.AddHeader("Content-Type", contentType);
            if (responsedata.ContainsKey("headers"))
            {
                Hashtable headerdata = (Hashtable)responsedata["headers"];

                foreach (string header in headerdata.Keys)
                    response.AddHeader(header, headerdata[header].ToString());
            }

            byte[] buffer;

            if (responseData != null)
            {
                buffer = responseData;
            }
            else
            {
                if (!(contentType.Contains("image")
                    || contentType.Contains("x-shockwave-flash")
                    || contentType.Contains("application/x-oar")
                    || contentType.Contains("application/vnd.ll.mesh")))
                {
                    // Text
                    buffer = Encoding.UTF8.GetBytes(responseString);
                }
                else
                {
                    // Binary!
                    buffer = Convert.FromBase64String(responseString);
                }

                response.ContentLength64 = buffer.Length;
                response.ContentEncoding = Encoding.UTF8;
            }

            return buffer;
        }

        public byte[] SendHTML404(OSHttpResponse response)
        {
            response.StatusCode = 404;
            response.ContentType = "text/html";

            string responseString = GetHTTP404();
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);

            response.ContentLength64 = buffer.Length;
            response.ContentEncoding = Encoding.UTF8;

            return buffer;
        }

        public void Start()
        {
            Start(true, true);
        }

        /// <summary>
        /// Start the http server
        /// </summary>
        /// <param name='processPollRequestsAsync'>
        /// If true then poll responses are performed asynchronsly.
        /// Option exists to allow regression tests to perform processing synchronously.
        /// </param>
        public void Start(bool performPollResponsesAsync, bool runPool)
        {
            _log.InfoFormat(
                "[BASE HTTP SERVER]: Starting {0} server on port {1}", UseSSL ? "HTTPS" : "HTTP", Port);

            try
            {
                //_httpListener = new HttpListener();

                NotSocketErrors = 0;
                if (!_ssl)
                {
                    _httpListener = tinyHTTPListener.Create(_listenIPAddress, (int)_port);
                    _httpListener.ExceptionThrown += httpServerException;
                    if (DebugLevel > 0)
                    {
                        _httpListener.LogWriter = httpserverlog;
                        httpserverlog.DebugLevel = 1;
                    }
                    // Uncomment this line in addition to those in HttpServerLogWriter
                    // if you want more detailed trace information from the HttpServer
                    //_httpListener2.DisconnectHandler = httpServerDisconnectMonitor;
                }
                else
                {
                    _httpListener = tinyHTTPListener.Create(IPAddress.Any, (int)_port, _cert);
                    if(_certificateValidationCallback != null)
                        _httpListener.CertificateValidationCallback = _certificateValidationCallback;
                    _httpListener.ExceptionThrown += httpServerException;
                    if (DebugLevel > 0)
                    {
                        _httpListener.LogWriter = httpserverlog;
                        httpserverlog.DebugLevel = 1;
                    }
                }

                _httpListener.RequestReceived += OnRequest;
                _httpListener.Start(64);

                lock(_generalLock)
                {
                    if (runPool)
                    {
                        if(_pollServiceManager == null)
                            _pollServiceManager = new PollServiceRequestManager(performPollResponsesAsync, 2, 25000);
                        _pollServiceManager.Start();
                    }
                }

                HTTPDRunning = true;
            }
            catch (Exception e)
            {
                _log.Error("[BASE HTTP SERVER]: Error - " + e.Message);
                _log.Error("[BASE HTTP SERVER]: Tip: Do you have permission to listen on port " + _port + "?");

                // We want this exception to halt the entire server since in current configurations we aren't too
                // useful without inbound HTTP.
                throw e;
            }

            _requestsProcessedStat = new Stat(
                    "HTTPRequestsServed",
                    "Number of inbound HTTP requests processed",
                    "",
                    "requests",
                    "httpserver",
                    Port.ToString(),
                    StatType.Pull,
                    MeasuresOfInterest.AverageChangeOverTime,
                    stat => stat.Value = RequestNumber,
                    StatVerbosity.Debug);

            StatsManager.RegisterStat(_requestsProcessedStat);
        }

        public void httpServerDisconnectMonitor(IHttpClientContext source, SocketError err)
        {
            switch (err)
            {
                case SocketError.NotSocket:
                    NotSocketErrors++;

                    break;
            }
        }

        public void httpServerException(object source, Exception exception)
        {
            if (source.ToString() == "HttpServer.HttpListener" && exception.ToString().StartsWith("Mono.Security.Protocol.Tls.TlsException"))
                return;
            _log.ErrorFormat("[BASE HTTP SERVER]: {0} had an exception {1}", source.ToString(), exception.ToString());
        }

        public void Stop(bool stopPool = false)
        {
            HTTPDRunning = false;

            StatsManager.DeregisterStat(_requestsProcessedStat);

            try
            {
                lock(_generalLock)
                {
                    if (stopPool && _pollServiceManager != null)
                        _pollServiceManager.Stop();
                }

                _httpListener.ExceptionThrown -= httpServerException;
                //_httpListener2.DisconnectHandler = null;

                _httpListener.LogWriter = null;
                _httpListener.RequestReceived -= OnRequest;
                _httpListener.Stop();
            }
            catch (NullReferenceException)
            {
                _log.Warn("[BASE HTTP SERVER]: Null Reference when stopping HttpServer.");
            }
        }

        public void RemoveStreamHandler(string httpMethod, string path)
        {
            if (_streamHandlers.TryRemove(path, out IRequestHandler dummy))
                return;

            string handlerKey = GetHandlerKey(httpMethod, path);

            //_log.DebugFormat("[BASE HTTP SERVER]: Removing handler key {0}", handlerKey);

            _streamHandlers.TryRemove(handlerKey, out dummy);
        }

        public void RemoveStreamHandler(string path)
        {
            _streamHandlers.TryRemove(path, out IRequestHandler dummy);
        }

        public void RemoveSimpleStreamHandler(string path)
        {
            if(_simpleStreamHandlers.TryRemove(path, out ISimpleStreamHandler dummy))
                return;
            _simpleStreamVarPath.TryRemove(path, out ISimpleStreamHandler dummy2);
        }

        public void RemoveHTTPHandler(string httpMethod, string path)
        {
            if (path == null) return; // Caps module isn't loaded, tries to remove handler where path = null
            lock (_HTTPHandlers)
            {
                if (httpMethod != null && httpMethod.Length == 0)
                {
                    _HTTPHandlers.Remove(path);
                    return;
                }

                _HTTPHandlers.Remove(GetHandlerKey(httpMethod, path));
            }
        }

        public void RemovePollServiceHTTPHandler(string httpMethod, string path)
        {
            if(!_pollHandlers.TryRemove(path, out PollServiceEventArgs dummy))
                _pollHandlersVarPath.TryRemove(path, out PollServiceEventArgs dummy2);
        }

        public void RemovePollServiceHTTPHandler(string path)
        {
            if(!_pollHandlers.TryRemove(path, out PollServiceEventArgs dummy))
                _pollHandlersVarPath.TryRemove(path, out PollServiceEventArgs dummy2);
        }

        //public bool RemoveAgentHandler(string agent, IHttpAgentHandler handler)
        //{
        //    lock (_agentHandlers)
        //    {
        //      IHttpAgentHandler foundHandler;
        //      if (_agentHandlers.TryGetValue(agent, out foundHandler) && foundHandler == handler)
        //      {
        //         _agentHandlers.Remove(agent);
        //         return true;
        //      }
        //    }
        //
        //    return false;
        //}

        public void RemoveXmlRPCHandler(string method)
        {
            lock (_rpcHandlers)
                _rpcHandlers.Remove(method);
        }

        public void RemoveJsonRPCHandler(string method)
        {
            lock(jsonRpcHandlers)
                jsonRpcHandlers.Remove(method);
        }

        public bool RemoveLLSDHandler(string path, LLSDMethod handler)
        {
            lock (_llsdHandlers)
            {
                LLSDMethod foundHandler;

                if (_llsdHandlers.TryGetValue(path, out foundHandler) && foundHandler == handler)
                {
                    _llsdHandlers.Remove(path);
                    return true;
                }
            }

            return false;
        }

        // Fallback HTTP responses in case the HTTP error response files don't exist
        private static string getDefaultHTTP404()
        {
            return "<HTML><HEAD><TITLE>404 Page not found</TITLE><BODY><BR /><H1>Ooops!</H1><P>The page you requested has been obsconded with by knomes. Find hippos quick!</P></BODY></HTML>";
        }

        public void SetHTTP404()
        {
            string file = Path.Combine(".", "http_404.html");
            try
            {
                if (File.Exists(file))
                {
                    using (StreamReader sr = File.OpenText(file))
                        HTTP404 = sr.ReadToEnd();
                    if(string.IsNullOrWhiteSpace(HTTP404))
                        HTTP404 = getDefaultHTTP404();
                    return;
                }
            }
            catch { }
            HTTP404 = getDefaultHTTP404();
            }

        public string GetHTTP404()
        {
            return HTTP404;
        }
    }

    public class HttpServerContextObj
    {
        public IHttpClientContext context = null;
        public IHttpRequest req = null;
        public OSHttpRequest oreq = null;
        public OSHttpResponse oresp = null;

        public HttpServerContextObj(IHttpClientContext contxt, IHttpRequest reqs)
        {
            context = contxt;
            req = reqs;
        }

        public HttpServerContextObj(OSHttpRequest osreq, OSHttpResponse osresp)
        {
            oreq = osreq;
            oresp = osresp;
        }
    }

    /// <summary>
    /// Relays HttpServer log messages to our own logging mechanism.
    /// </summary>
    /// To use this you must uncomment the switch section
    ///
    /// You may also be able to get additional trace information from HttpServer if you uncomment the UseTraceLogs
    /// property in StartHttp() for the HttpListener
    /// 
    public class HttpServerLogWriter : ILogWriter
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public int DebugLevel {get; set;} = (int)LogPrio.Error;

        public void Write(object source, LogPrio priority, string message)
        {
            if((int)priority < DebugLevel)
                return;

            switch (priority)
            {
                case LogPrio.Trace:
                    _log.DebugFormat("[{0}]: {1}", source, message);
                    break;
                case LogPrio.Debug:
                    _log.DebugFormat("[{0}]: {1}", source, message);
                    break;
                case LogPrio.Error:
                    _log.ErrorFormat("[{0}]: {1}", source, message);
                    break;
                case LogPrio.Info:
                    _log.InfoFormat("[{0}]: {1}", source, message);
                    break;
                case LogPrio.Warning:
                    _log.WarnFormat("[{0}]: {1}", source, message);
                    break;
                case LogPrio.Fatal:
                    _log.ErrorFormat("[{0}]: FATAL! - {1}", source, message);
                    break;
                default:
                    break;
            }
            return;
        }
    }

    public class IndexPHPHandler : SimpleStreamHandler
    {
        readonly BaseHttpServer _server;

        public IndexPHPHandler(BaseHttpServer server)
            : base("/index.php")
        {
            _server = server;
        }

        protected override void ProcessRequest(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            httpResponse.KeepAlive = false;
            if (_server == null || !_server.HTTPDRunning)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (httpRequest.QueryString.Count == 0)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Redirect;
                httpResponse.AddHeader("Location", "http://opensimulator.org");
                return;
            }
            if (httpRequest.QueryFlags.Contains("about"))
            {

                httpResponse.StatusCode = (int)HttpStatusCode.Redirect;
                httpResponse.AddHeader("Location", "http://opensimulator.org/wiki/0.9.2.0_Release");
                return;
            }
            if (!httpRequest.QueryAsDictionary.TryGetValue("method", out string methods) || string.IsNullOrWhiteSpace(methods))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound; ;
                return;
            }

            string[] splited = methods.Split(new char[] { ',' });
            string method = splited[0];
            if (string.IsNullOrWhiteSpace(method))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            SimpleStreamMethod sh = _server.TryGetIndexPHPMethodHandler(method);
            if (sh == null)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            try
            {
                sh?.Invoke(httpRequest, httpResponse);
            }
            catch
            {
                httpResponse.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
        }
    }
}
