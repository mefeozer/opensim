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
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using OSHttpServer;
using log4net;

namespace OpenSim.Framework.Servers.HttpServer
{
    public class OSHttpRequest : IOSHttpRequest
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected IHttpRequest _request = null;
        protected IHttpClientContext _context = null;

        public string[] AcceptTypes => _request.AcceptTypes;

        public Encoding ContentEncoding => _contentEncoding;
        private readonly Encoding _contentEncoding;

        public long ContentLength => _request.ContentLength;

        public long ContentLength64 => ContentLength;

        public string ContentType => _contentType;
        private readonly string _contentType;

        public HttpCookieCollection Cookies
        {
            get
            {
                RequestCookies cookies = _request.Cookies;
                HttpCookieCollection httpCookies = new HttpCookieCollection();
                if(cookies != null)
                {
                    foreach (RequestCookie cookie in cookies)
                        httpCookies.Add(new HttpCookie(cookie.Name, cookie.Value));
                }
                return httpCookies;
            }
        }

        public bool HasEntityBody => _request.ContentLength != 0;

        public NameValueCollection Headers => _request.Headers;

        public string HttpMethod => _request.Method;

        public Stream InputStream => _request.Body;

        public bool IsSecured => _context.IsSecured;

        public bool KeepAlive => ConnectionType.KeepAlive == _request.Connection;

        public NameValueCollection QueryString => _request.QueryString;

        private Hashtable _queryAsHashtable = null;
        public Hashtable Query
        {
            get
            {
                if (_queryAsHashtable == null)
                    BuildQueryHashtable();
                return _queryAsHashtable;
            }
        }

        //faster than Query
        private Dictionary<string, string> _queryAsDictionay = null;
        public Dictionary<string,string> QueryAsDictionary
        {
            get
            {
                if (_queryAsDictionay == null)
                    BuildQueryDictionary();
                return _queryAsDictionay;
            }
        }

        private HashSet<string> _queryFlags = null;
        public HashSet<string> QueryFlags
        {
            get
            {
                if (_queryFlags == null)
                    BuildQueryDictionary();
                return _queryFlags;
            }
        }
    /// <value>
    /// POST request values, if applicable
    /// </value>
    //        public Hashtable Form { get; private set; }

        public string RawUrl => _request.Uri.AbsolutePath;

    public IPEndPoint RemoteIPEndPoint => _request.RemoteIPEndPoint;

    public IPEndPoint LocalIPEndPoint => _request.LocalIPEndPoint;

    public Uri Url => _request.Uri;

    public string UriPath => _request.UriPath;

    public string UserAgent => _userAgent;
    private readonly string _userAgent;

        public double ArrivalTS => _request.ArrivalTS;

        internal IHttpRequest IHttpRequest => _request;

        internal IHttpClientContext IHttpClientContext => _context;

        /// <summary>
        /// Internal whiteboard for handlers to store temporary stuff
        /// into.
        /// </summary>
        internal Dictionary<string, object> Whiteboard => _whiteboard;

        private readonly Dictionary<string, object> _whiteboard = new Dictionary<string, object>();

        public OSHttpRequest() {}

        public OSHttpRequest(IHttpRequest req)
        {
            _request = req;
            _context = req.Context;

            if (null != req.Headers["content-encoding"])
            {
                try
                {
                    _contentEncoding = Encoding.GetEncoding(_request.Headers["content-encoding"]);
                }
                catch
                {
                    // ignore
                }
            }

            if (null != req.Headers["content-type"])
                _contentType = _request.Headers["content-type"];
            if (null != req.Headers["user-agent"])
                _userAgent = req.Headers["user-agent"];

//            Form = new Hashtable();
//            foreach (HttpInputItem item in req.Form)
//            {
//                _log.DebugFormat("[OSHttpRequest]: Got form item {0}={1}", item.Name, item.Value);
//                Form.Add(item.Name, item.Value);
//            }
        }

        private void BuildQueryDictionary()
        {
            NameValueCollection q = _request.QueryString;
            _queryAsDictionay = new Dictionary<string, string>();
            _queryFlags = new HashSet<string>();
            for(int i = 0; i <q.Count; ++i)
            {
                try
                {
                    var name = q.GetKey(i);
                    if(!string.IsNullOrEmpty(name))
                        _queryAsDictionay[name] = q[i];
                    else
                        _queryFlags.Add(q[i]);
                }
                catch {}
            }
        }

        private void BuildQueryHashtable()
        {
            NameValueCollection q = _request.QueryString;
            _queryAsHashtable = new Hashtable();
            _queryFlags = new HashSet<string>();
            for (int i = 0; i < q.Count; ++i)
            {
                try
                {
                    var name = q.GetKey(i);
                    if (!string.IsNullOrEmpty(name))
                        _queryAsHashtable[name] = q[i];
                    else
                        _queryFlags.Add(q[i]);
                }
                catch { }
            }
        }

        public override string ToString()
        {
            StringBuilder me = new StringBuilder();
            me.Append(string.Format("OSHttpRequest: {0} {1}\n", HttpMethod, RawUrl));
            foreach (string k in Headers.AllKeys)
            {
                me.Append(string.Format("    {0}: {1}\n", k, Headers[k]));
            }
            if (null != RemoteIPEndPoint)
            {
                me.Append(string.Format("    IP: {0}\n", RemoteIPEndPoint));
            }

            return me.ToString();
        }
    }
}
