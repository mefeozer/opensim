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
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Text;
using System.Net;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Scripting.LSLHttp
{
    public class UrlData
    {
        public UUID hostID;
        public UUID groupID;
        public UUID itemID;
        public IScriptModule engine;
        public string url;
        public UUID urlcode;
        public Dictionary<UUID, RequestData> requests;
        public bool isSsl;
        public Scene scene;
        public bool allowXss;
    }

    public class RequestData
    {
        public UUID requestID;
        public Dictionary<string, string> headers;
        public string body;
        public int responseCode;
        public string responseBody;
        public string responseType = "text/plain";
        //public ManualResetEvent ev;
        public bool requestDone;
        public int startTime;
        public bool responseSent;
        public string uri;
        public UUID hostID;
        public Scene scene;
    }

    /// <summary>
    /// This module provides external URLs for in-world scripts.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "UrlModule")]
    public class UrlModule : ISharedRegionModule, IUrlModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected readonly Dictionary<UUID, UrlData> _RequestMap = new Dictionary<UUID, UrlData>();
        protected readonly Dictionary<string, UrlData> _UrlMap = new Dictionary<string, UrlData>();
        protected readonly Dictionary<UUID, int> _countsPerSOG = new Dictionary<UUID, int>();

        protected bool _enabled = false;
        protected string _ErrorStr;
        protected uint _HttpsPort = 0;
        protected IHttpServer _HttpServer = null;
        protected IHttpServer _HttpsServer = null;

        private string _lsl_shard = "OpenSim";
        private string _lsl_user_agent = string.Empty;

        public string ExternalHostNameForLSL { get; protected set; }

        /// <summary>
        /// The default maximum number of urls
        /// </summary>
        public const int DefaultTotalUrls = 15000;

        /// <summary>
        /// Maximum number of external urls that can be set up by this module.
        /// </summary>
        public int TotalUrls { get; set; }

        public Type ReplaceableInterface => typeof(IUrlModule);

        public string Name => "UrlModule";

        public void Initialise(IConfigSource config)
        {
            IConfig networkConfig = config.Configs["Network"];
            _enabled = false;

            if (networkConfig != null)
            {
                _lsl_shard = networkConfig.GetString("shard", _lsl_shard);
                _lsl_user_agent = networkConfig.GetString("user_agent", _lsl_user_agent);

                ExternalHostNameForLSL = config.Configs["Network"].GetString("ExternalHostNameForLSL", null);

                bool ssl_enabled = config.Configs["Network"].GetBoolean("https_listener", false);

                if (ssl_enabled)
                    _HttpsPort = (uint)config.Configs["Network"].GetInt("https_port", (int)_HttpsPort);
            }
            else
            {
                _ErrorStr = "[Network] configuration missing, HTTP listener for LSL disabled";
                _log.Warn("[URL MODULE]: " + _ErrorStr);
                return;
            }

            if (string.IsNullOrWhiteSpace(ExternalHostNameForLSL))
            {
                _ErrorStr = "ExternalHostNameForLSL not defined in configuration, HTTP listener for LSL disabled";
                _log.Warn("[URL MODULE]: " + _ErrorStr);
                return;
            }

            IPAddress ia = null;
            ia = Util.GetHostFromDNS(ExternalHostNameForLSL);
            if (ia == null)
            {
                _ErrorStr = "Could not resolve ExternalHostNameForLSL, HTTP listener for LSL disabled";
                _log.Warn("[URL MODULE]: " + _ErrorStr);
                return;
            }

            _enabled = true;
            _ErrorStr = string.Empty;

            IConfig llFunctionsConfig = config.Configs["LL-Functions"];

            if (llFunctionsConfig != null)
                TotalUrls = llFunctionsConfig.GetInt("max_external_urls_per_simulator", DefaultTotalUrls);
            else
                TotalUrls = DefaultTotalUrls;
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (_enabled && _HttpServer == null)
            {
                // There can only be one
                //
                _HttpServer = MainServer.Instance;
                //
                // We can use the https if it is enabled
                if (_HttpsPort > 0)
                {
                    _HttpsServer = MainServer.GetHttpServer(_HttpsPort);
                }
            }

            scene.RegisterModuleInterface<IUrlModule>(this);

            scene.EventManager.OnScriptReset += OnScriptReset;
        }

        public void RegionLoaded(Scene scene)
        {
            IScriptModule[] scriptModules = scene.RequestModuleInterfaces<IScriptModule>();
            foreach (IScriptModule scriptModule in scriptModules)
            {
                scriptModule.OnScriptRemoved += ScriptRemoved;
                scriptModule.OnObjectRemoved += ObjectRemoved;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            // Drop references to that scene
            foreach (KeyValuePair<string, UrlData> kvp in _UrlMap)
            {
                if (kvp.Value.scene == scene)
                    kvp.Value.scene = null;
            }
            foreach (KeyValuePair<UUID, UrlData> kvp in _RequestMap)
            {
                if (kvp.Value.scene == scene)
                    kvp.Value.scene = null;
            }
        }

        public void Close()
        {
        }

        public UUID RequestURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options)
        {
            UUID urlcode = UUID.Random();

            if(!_enabled)
            {
                engine.PostScriptEvent(itemID, "http_request", new object[] { urlcode.ToString(), "URL_REQUEST_DENIED", _ErrorStr });
                return urlcode;
            }

            lock (_UrlMap)
            {
                if (_UrlMap.Count >= TotalUrls)
                {
                    engine.PostScriptEvent(itemID, "http_request", new object[] { urlcode.ToString(), "URL_REQUEST_DENIED",
                        "Too many URLs already open" });
                    return urlcode;
                }
                string url = "http://" + ExternalHostNameForLSL + ":" + _HttpServer.Port.ToString() + "/lslhttp/" + urlcode.ToString();

                UUID groupID = host.ParentGroup.UUID;
                UrlData urlData = new UrlData()
                {
                    hostID = host.UUID,
                    groupID = groupID,
                    itemID = itemID,
                    engine = engine,
                    url = url,
                    urlcode = urlcode,
                    isSsl = false,
                    requests = new Dictionary<UUID, RequestData>(),
                    scene = host.ParentGroup.Scene
                };

                if (options != null && options["allowXss"] != null)
                    urlData.allowXss = true;
                else
                    urlData.allowXss = false;

                _UrlMap[url] = urlData;

                if (_countsPerSOG.TryGetValue(groupID, out int urlcount))
                    _countsPerSOG[groupID] = ++urlcount;
                else
                    _countsPerSOG[groupID] = 1;

                string uri = "/lslhttp/" + urlcode.ToString();

                PollServiceEventArgs args
                    = new PollServiceEventArgs(HttpRequestHandler, uri, HasEvents, GetEvents, NoEvents, Drop, urlcode, 25000);

                _HttpServer.AddPollServiceHTTPHandlerVarPath(args);

                //_log.DebugFormat(
                //    "[URL MODULE]: Set up incoming request url {0} for {1} in {2} {3}",
                //     uri, itemID, host.Name, host.LocalId);

                engine.PostScriptEvent(itemID, "http_request", new object[] { urlcode.ToString(), "URL_REQUEST_GRANTED", url + "/"});
            }

            return urlcode;
        }

        public UUID RequestSecureURL(IScriptModule engine, SceneObjectPart host, UUID itemID, Hashtable options)
        {
            UUID urlcode = UUID.Random();

            if(!_enabled)
            {
                engine.PostScriptEvent(itemID, "http_request", new object[] { urlcode.ToString(), "URL_REQUEST_DENIED",  _ErrorStr });
                return urlcode;
            }

            if (_HttpsServer == null)
            {
                engine.PostScriptEvent(itemID, "http_request", new object[] { urlcode.ToString(), "URL_REQUEST_DENIED", "" });
                return urlcode;
            }

            lock (_UrlMap)
            {
                if (_UrlMap.Count >= TotalUrls)
                {
                    engine.PostScriptEvent(itemID, "http_request", new object[] { urlcode.ToString(), "URL_REQUEST_DENIED",
                        "Too many URLs already open" });
                    return urlcode;
                }
                string url = "https://" + ExternalHostNameForLSL + ":" + _HttpsServer.Port.ToString() + "/lslhttps/" + urlcode.ToString();

                UUID groupID = host.ParentGroup.UUID;
                UrlData urlData = new UrlData()
                {
                    hostID = host.UUID,
                    groupID = groupID,
                    itemID = itemID,
                    engine = engine,
                    url = url,
                    urlcode = urlcode,
                    isSsl = true,
                    requests = new Dictionary<UUID, RequestData>(),
                    scene = host.ParentGroup.Scene
                };

                if (options != null && options["allowXss"] != null)
                    urlData.allowXss = true;
                else
                    urlData.allowXss = false;

                _UrlMap[url] = urlData;

                if (_countsPerSOG.TryGetValue(groupID, out int urlcount))
                    _countsPerSOG[groupID] = ++urlcount;
                else
                    _countsPerSOG[groupID] = 1;

                string uri = "/lslhttps/" + urlcode.ToString();

                PollServiceEventArgs args
                    = new PollServiceEventArgs(HttpRequestHandler, uri, HasEvents, GetEvents, NoEvents, Drop, urlcode, 25000);
                _HttpsServer.AddPollServiceHTTPHandlerVarPath(args);

                //_log.DebugFormat(
                //    "[URL MODULE]: Set up incoming secure request url {0} for {1} in {2} {3}",
                //     uri, itemID, host.Name, host.LocalId);
                // keep ending / because legacy
                engine.PostScriptEvent(itemID, "http_request", new object[] { urlcode.ToString(), "URL_REQUEST_GRANTED", url + "/"});
            }

            return urlcode;
        }

        public void ReleaseURL(string url)
        {
            lock (_UrlMap)
            {
                UrlData data;
                url = url.TrimEnd(new char[]{'/'});
                if (!_UrlMap.TryGetValue(url, out data))
                {
                    return;
                }

                lock (_RequestMap)
                {
                    foreach (UUID req in data.requests.Keys)
                        _RequestMap.Remove(req);
                }

//                _log.DebugFormat(
//                    "[URL MODULE]: Releasing url {0} for {1} in {2}",
//                    url, data.itemID, data.hostID);

                RemoveUrl(data);
                _UrlMap.Remove(url);
            }
        }

        public void HttpContentType(UUID request, string type)
        {
            lock (_UrlMap)
            {
                UrlData urlData;
                if (_RequestMap.TryGetValue(request, out urlData) && urlData != null)
                {
                    urlData.requests[request].responseType = type;
                }
                else
                {
                    _log.Info("[HttpRequestHandler] There is no http-in request with id " + request.ToString());
                }
            }
        }

        public void HttpResponse(UUID request, int status, string body)
        {
            lock (_RequestMap)
            {
                UrlData urlData;
                RequestData rd;
                if (_RequestMap.TryGetValue(request, out urlData) && urlData != null)
                {
                    lock(urlData.requests)
                    {
                        if (urlData.requests.TryGetValue(request, out rd) && rd != null)
                        {
                            if (!rd.responseSent)
                            {
                                string responseBody = body;

                                if (rd.responseType.Equals("text/plain"))
                                {
                                    string value;
                                    if (rd.headers.TryGetValue("user-agent", out value))
                                    {
                                        if (value != null && value.IndexOf("MSIE") >= 0)
                                        {
                                            // wrap the html escaped response if the target client is IE
                                            // It ignores "text/plain" if the body is html
                                            responseBody = "<html>" + System.Web.HttpUtility.HtmlEncode(body) + "</html>";
                                        }
                                    }
                                }

                                rd.responseCode = status;
                                rd.responseBody = responseBody;
                                //urlData.requests[request].ev.Set();
                                rd.requestDone = true;
                                rd.responseSent = true;
                            }
                        }
                    }
                }
                else
                {
                    _log.Info("[HttpRequestHandler] There is no http-in request with id " + request.ToString());
                }
            }
        }

        public string GetHttpHeader(UUID requestId, string header)
        {
            lock (_RequestMap)
            {
                UrlData urlData;
                if (_RequestMap.TryGetValue(requestId, out urlData) && urlData != null)
                {
                    string value;
                    if (urlData.requests[requestId].headers.TryGetValue(header, out value))
                        return value;
                }
                else
                {
                    _log.Warn("[HttpRequestHandler] There was no http-in request with id " + requestId);
                }
            }
            return string.Empty;
        }

        public int GetFreeUrls()
        {
            lock (_UrlMap)
                return TotalUrls - _UrlMap.Count;
        }

        public void ScriptRemoved(UUID itemID)
        {
//            _log.DebugFormat("[URL MODULE]: Removing script {0}", itemID);

            lock (_UrlMap)
            {
                List<string> removeURLs = new List<string>();

                foreach (KeyValuePair<string, UrlData> url in _UrlMap)
                {
                    if (url.Value.itemID == itemID)
                    {
                        RemoveUrl(url.Value);
                        removeURLs.Add(url.Key);
                        lock (_RequestMap)
                        {
                            foreach (UUID req in url.Value.requests.Keys)
                                _RequestMap.Remove(req);
                        }
                    }
                }

                foreach (string urlname in removeURLs)
                    _UrlMap.Remove(urlname);
            }
        }

        public void ObjectRemoved(UUID objectID)
        {
            lock (_UrlMap)
            {
                List<string> removeURLs = new List<string>();

                foreach (KeyValuePair<string, UrlData> url in _UrlMap)
                {
                    if (url.Value.hostID == objectID)
                    {
                        RemoveUrl(url.Value);
                        removeURLs.Add(url.Key);
                        lock (_RequestMap)
                        {
                            foreach (UUID req in url.Value.requests.Keys)
                                _RequestMap.Remove(req);
                        }
                    }
                }

                foreach (string urlname in removeURLs)
                    _UrlMap.Remove(urlname);
            }
        }

        protected void RemoveUrl(UrlData data)
        {
            if (data.isSsl)
                _HttpsServer.RemovePollServiceHTTPHandler("", "/lslhttps/"+data.urlcode.ToString());
            else
                _HttpServer.RemovePollServiceHTTPHandler("", "/lslhttp/"+data.urlcode.ToString());

            if(_countsPerSOG.TryGetValue(data.groupID, out int count))
            {
                --count;
                if(count <= 0)
                    _countsPerSOG.Remove(data.groupID);
                else
                    _countsPerSOG[data.groupID] = count;
            }
        }

        protected Hashtable NoEvents(UUID requestID, UUID sessionID)
        {
            Hashtable response = new Hashtable();
            UrlData url;
            int startTime = 0;
            lock (_RequestMap)
            {
                if (!_RequestMap.TryGetValue(requestID, out url))
                    return response;
                startTime = url.requests[requestID].startTime;
            }

            if (System.Environment.TickCount - startTime < 25000)
                return response;

            //remove from map
            lock (url.requests)
            {
                url.requests.Remove(requestID);
            }
            lock (_RequestMap)
            {
                _RequestMap.Remove(requestID);
            }

            response["int_response_code"] = 500;
            response["str_response_string"] = "Script timeout";
            response["content_type"] = "text/plain";
            response["keepalive"] = false;
            return response;
        }

        protected bool HasEvents(UUID requestID, UUID sessionID)
        {
            UrlData url=null;

            lock (_RequestMap)
            {
                if (!_RequestMap.TryGetValue(requestID, out url))
                    return false;
            }
            lock (url.requests)
            {
                RequestData rd;
                if (!url.requests.TryGetValue(requestID, out rd) || rd == null)
                    return false;

                if (System.Environment.TickCount - rd.startTime > 25000)
                    return true;

                return rd.requestDone;
            }
        }

        protected void Drop(UUID requestID, UUID sessionID)
        {
            UrlData url = null;
            lock (_RequestMap)
            {
                if (_RequestMap.TryGetValue(requestID, out url))
                {
                    _RequestMap.Remove(requestID);
                    if(url != null)
                    {
                        lock (url.requests)
                            url.requests.Remove(requestID);
                    }
                }
            }
        }

        protected Hashtable GetEvents(UUID requestID, UUID sessionID)
        {
            UrlData url = null;

            lock (_RequestMap)
            {
                if (!_RequestMap.TryGetValue(requestID, out url))
                    return NoEvents(requestID,sessionID);
            }

            RequestData requestData = null;
            lock (url.requests)
            {
                requestData = url.requests[requestID];
                if (requestData == null || !requestData.requestDone)
                    return NoEvents(requestID, sessionID);

                url.requests.Remove(requestID);
                lock (_RequestMap)
                {
                    _RequestMap.Remove(requestID);
                }
            }

            Hashtable response = new Hashtable();

            if (System.Environment.TickCount - requestData.startTime > 25000)
            {
                response["int_response_code"] = 500;
                response["str_response_string"] = "Script timeout";
                response["content_type"] = "text/plain";
                response["keepalive"] = false;
                return response;
            }
            //put response
            response["int_response_code"] = requestData.responseCode;
            response["str_response_string"] = requestData.responseBody;
            response["content_type"] = requestData.responseType;
            response["keepalive"] = false;

            if (url.allowXss)
                response["access_control_allow_origin"] = "*";

            Hashtable headers = new Hashtable();
            if(url.scene != null)
            {
                SceneObjectPart sop = url.scene.GetSceneObjectPart(url.hostID);
                if(sop != null)
                {
                    RegionInfo ri = url.scene.RegionInfo;
                    Vector3 position = sop.AbsolutePosition;
                    Vector3 velocity = sop.Velocity;
                    Quaternion rotation = sop.GetWorldRotation();

                    if (!string.IsNullOrWhiteSpace(_lsl_shard))
                        headers["X-SecondLife-Shard"] = _lsl_shard;
                    headers["X-SecondLife-Object-Name"] = sop.Name;
                    headers["X-SecondLife-Object-Key"] = sop.UUID.ToString();
                    headers["X-SecondLife-Region"] = string.Format("{0} ({1}, {2})", ri.RegionName, ri.WorldLocX, ri.WorldLocY);
                    headers["X-SecondLife-Local-Position"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000})", position.X, position.Y, position.Z);
                    headers["X-SecondLife-Local-Velocity"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000})", velocity.X, velocity.Y, velocity.Z);
                    headers["X-SecondLife-Local-Rotation"] = string.Format("({0:0.000000}, {1:0.000000}, {2:0.000000}, {3:0.000000})", rotation.X, rotation.Y, rotation.Z, rotation.W);
                    //headers["X-SecondLife-Owner-Name"] = ownerName;
                    headers["X-SecondLife-Owner-Key"] = sop.OwnerID.ToString();
                    if (!string.IsNullOrWhiteSpace(_lsl_user_agent))
                        headers["User-Agent"] = _lsl_user_agent;
                }
            }
            if(url.isSsl)
                headers.Add("Accept-CH","UA");
            response["headers"] = headers;
            return response;
        }

        private OSHttpResponse errorResponse(OSHttpRequest request, int error)
        {
            OSHttpResponse resp = new OSHttpResponse(request)
            {
                StatusCode = error
            };
            return resp;
        }

        public OSHttpResponse HttpRequestHandler(UUID requestID, OSHttpRequest request)
        {
            lock (request)
            {
                string uri = request.RawUrl;
                if(uri.Length < 45)
                {
                    request.InputStream.Dispose();
                    return errorResponse(request, (int)HttpStatusCode.BadRequest);
                }

                try
                {
                    //string uri_full = "http://" + ExternalHostNameForLSL + ":" + _HttpServer.Port.ToString() + uri;// "/lslhttp/" + urlcode.ToString() + "/";

                    string uri_tmp;
                    string pathInfo;

                    int pos = uri.IndexOf("/", 45); // /lslhttp/uuid/ <-
                    if (pos >= 45)
                    {
                        uri_tmp = uri.Substring(0, pos);
                        pathInfo = uri.Substring(pos);
                    }
                    else
                    {
                        uri_tmp = uri;
                        pathInfo = string.Empty;
                    }

                    string urlkey;
                    if (uri.Contains("lslhttps"))
                        urlkey = "https://" + ExternalHostNameForLSL + ":" + _HttpsServer.Port.ToString() + uri_tmp;
                    //_UrlMap[];
                    else
                        urlkey = "http://" + ExternalHostNameForLSL + ":" + _HttpServer.Port.ToString() + uri_tmp;

                    if (!_UrlMap.TryGetValue(urlkey, out UrlData url))
                    {
                            //_log.Warn("[HttpRequestHandler]: http-in request failed; no such url: "+urlkey.ToString());
                            request.InputStream.Dispose();
                            return errorResponse(request, (int)HttpStatusCode.NotFound);
                    }

                    //for llGetHttpHeader support we need to store original URI here
                    //to make x-path-info / x-query-string / x-script-url / x-remote-ip headers
                    //as per http://wiki.secondlife.com/wiki/LlGetHTTPHeader
                    RequestData requestData = new RequestData()
                    {
                        requestID = requestID,
                        requestDone = false,
                        startTime = System.Environment.TickCount,
                        uri = uri,
                        hostID = url.hostID,
                        scene = url.scene
                    };

                    if (requestData.headers == null)
                        requestData.headers = new Dictionary<string, string>();

                    NameValueCollection headers = request.Headers;
                    if (headers.Count > 0)
                    {
                        for(int i = 0; i < headers.Count; ++i)
                        {
                            string name = headers.GetKey(i);
                            if (!string.IsNullOrEmpty(name))
                                requestData.headers[name] = headers[i];
                        }
                    }

                    NameValueCollection query = request.QueryString;
                    if (query.Count > 0)
                    {
                        StringBuilder sb = new StringBuilder();
                        for (int i = 0; i < query.Count; ++i)
                        {
                            string key = query.GetKey(i);
                            if (string.IsNullOrEmpty(key))
                                sb.AppendFormat("{0}&", query[i]);
                            else
                                sb.AppendFormat("{0}={1}&", key, query[i]);
                        }
                        if (sb.Length > 1)
                            sb.Remove(sb.Length - 1, 1);
                        requestData.headers["x-query-string"] = sb.ToString();
                    }
                    else
                        requestData.headers["x-query-string"] = string.Empty;

                    //if this machine is behind DNAT/port forwarding, currently this is being
                    //set to address of port forwarding router
                    requestData.headers["x-remote-ip"] = request.RemoteIPEndPoint.Address.ToString();
                    requestData.headers["x-path-info"] = pathInfo;
                    requestData.headers["x-script-url"] = url.url;

                    //requestData.ev = new ManualResetEvent(false);
                    lock (url.requests)
                    {
                        url.requests.Add(requestID, requestData);
                    }
                    lock (_RequestMap)
                    {
                        //add to request map
                        _RequestMap.Add(requestID, url);
                    }

                    string requestBody;
                    if (request.InputStream.Length > 0)
                    {
                        using (StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8))
                            requestBody = reader.ReadToEnd();
                    }
                    else
                        requestBody = string.Empty;

                    request.InputStream.Dispose();

                    url.engine.PostScriptEvent(url.itemID, "http_request", new object[] { requestID.ToString(), request.HttpMethod, requestBody });

                    return null;

                }
                catch (Exception we)
                {
                    //Hashtable response = new Hashtable();
                    _log.Warn("[HttpRequestHandler]: http-in request failed");
                    _log.Warn(we.Message);
                    _log.Warn(we.StackTrace);
                }

                return errorResponse(request, (int)HttpStatusCode.BadRequest);
            }
        }

        protected void OnScriptReset(uint localID, UUID itemID)
        {
            ScriptRemoved(itemID);
        }

        public int GetUrlCount(UUID groupID)
        {
            if (!_enabled)
                return 0;

            lock (_UrlMap)
            { 
                _countsPerSOG.TryGetValue(groupID, out int count);
                return count;
            }
        }
    }
}
