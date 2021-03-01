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

using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Framework.Capabilities
{
    /// <summary>
    /// CapsHandlers is a cap handler container but also takes
    /// care of adding and removing cap handlers to and from the
    /// supplied BaseHttpServer.
    /// </summary>
    public class CapsHandlers
    {
        private readonly Dictionary<string, IRequestHandler> _capsHandlers = new Dictionary<string, IRequestHandler>();
        private readonly ConcurrentDictionary<string, ISimpleStreamHandler> _capsSimpleHandlers = new ConcurrentDictionary<string, ISimpleStreamHandler>();
        private readonly IHttpServer _httpListener;
        private readonly string _httpListenerHostName;
        private readonly uint _httpListenerPort;
        private readonly bool _useSSL = false;

        /// <summary></summary>
        /// CapsHandlers is a cap handler container but also takes
        /// care of adding and removing cap handlers to and from the
        /// supplied BaseHttpServer.
        /// </summary>
        /// <param name="httpListener">base HTTP server</param>
        /// <param name="httpListenerHostname">host name of the HTTP server</param>
        /// <param name="httpListenerPort">HTTP port</param>
        public CapsHandlers(IHttpServer httpListener, string httpListenerHostname, uint httpListenerPort)
           {
            _httpListener = httpListener;
            _httpListenerHostName = httpListenerHostname;
            _httpListenerPort = httpListenerPort;
            if (httpListener != null && httpListener.UseSSL)
                _useSSL = true;
            else
                _useSSL = false;
        }

        /// <summary>
        /// Remove the cap handler for a capability.
        /// </summary>
        /// <param name="capsName">name of the capability of the cap
        /// handler to be removed</param>
        public void Remove(string capsName)
        {
            lock (_capsHandlers)
            {
                if(_capsHandlers.ContainsKey(capsName))
                {
                    _httpListener.RemoveStreamHandler("POST", _capsHandlers[capsName].Path);
                    _httpListener.RemoveStreamHandler("PUT", _capsHandlers[capsName].Path);
                    _httpListener.RemoveStreamHandler("GET", _capsHandlers[capsName].Path);
                    _httpListener.RemoveStreamHandler("DELETE", _capsHandlers[capsName].Path);
                    _capsHandlers.Remove(capsName);
                }
            }
            if(_capsSimpleHandlers.TryRemove(capsName, out ISimpleStreamHandler hdr))
            {
                _httpListener.RemoveSimpleStreamHandler(hdr.Path);
            }
        }

        public void AddSimpleHandler(string capName, ISimpleStreamHandler handler, bool addToListener = true)
        {
            if(ContainsCap(capName))
                Remove(capName);
            if(_capsSimpleHandlers.TryAdd(capName, handler) && addToListener)
                _httpListener.AddSimpleStreamHandler(handler);
        }

        public bool ContainsCap(string cap)
        {
            lock (_capsHandlers)
                if (_capsHandlers.ContainsKey(cap))
                    return true;
            return _capsSimpleHandlers.ContainsKey(cap);
        }

        /// <summary>
        /// The indexer allows us to treat the CapsHandlers object
        /// in an intuitive dictionary like way.
        /// </summary>
        /// <remarks>
        /// The indexer will throw an exception when you try to
        /// retrieve a cap handler for a cap that is not contained in
        /// CapsHandlers.
        /// </remarks>
        public IRequestHandler this[string idx]
        {
            get
            {
                lock (_capsHandlers)
                    return _capsHandlers[idx];
            }

            set
            {
                lock (_capsHandlers)
                {
                    if (_capsHandlers.ContainsKey(idx))
                    {
                        _httpListener.RemoveStreamHandler("POST", _capsHandlers[idx].Path);
                        _httpListener.RemoveStreamHandler("PUT", _capsHandlers[idx].Path);
                        _httpListener.RemoveStreamHandler("GET", _capsHandlers[idx].Path);
                        _httpListener.RemoveStreamHandler("DELETE", _capsHandlers[idx].Path);
                        _capsHandlers.Remove(idx);
                    }

                    if (null == value) return;

                    _capsHandlers[idx] = value;
                    _httpListener.AddStreamHandler(value);
                }
            }
        }

        /// <summary>
        /// Return the list of cap names for which this CapsHandlers
        /// object contains cap handlers.
        /// </summary>
        public string[] Caps
        {
            get
            {
                lock (_capsHandlers)
                {
                    string[] __keys = new string[_capsHandlers.Keys.Count + _capsSimpleHandlers.Keys.Count];
                    _capsHandlers.Keys.CopyTo(__keys, 0);
                    _capsSimpleHandlers.Keys.CopyTo(__keys, _capsHandlers.Keys.Count);
                    return __keys;
                }
            }
        }

        /// <summary>
        /// Return an LLSD-serializable Hashtable describing the
        /// capabilities and their handler details.
        /// </summary>
        /// <param name="excludeSeed">If true, then exclude the seed cap.</param>
        public Hashtable GetCapsDetails(bool excludeSeed, List<string> requestedCaps)
        {
            Hashtable caps = new Hashtable();

            string protocol = _useSSL ? "https://" : "http://";
            string baseUrl = protocol + _httpListenerHostName + ":" + _httpListenerPort.ToString();

            if (requestedCaps == null)
            {
                lock (_capsHandlers)
                {
                    foreach (KeyValuePair<string, ISimpleStreamHandler> kvp in _capsSimpleHandlers)
                        caps[kvp.Key] = baseUrl + kvp.Value.Path;
                    foreach (KeyValuePair<string, IRequestHandler> kvp in _capsHandlers)
                        caps[kvp.Key] = baseUrl + kvp.Value.Path;
                }
                return caps;
            }

            lock (_capsHandlers)
            {
                for(int i = 0; i < requestedCaps.Count; ++i)
                {
                    string capsName = requestedCaps[i];
                    if (excludeSeed && "SEED" == capsName)
                        continue;

                    if (_capsSimpleHandlers.TryGetValue(capsName, out ISimpleStreamHandler shdr))
                    {
                        caps[capsName] = baseUrl + shdr.Path;
                        continue;
                    }
                    if (_capsHandlers.TryGetValue(capsName, out IRequestHandler chdr))
                    {
                        caps[capsName] = baseUrl + chdr.Path;
                    }
                }
            }

            return caps;
        }

        /// <summary>
        /// Returns a copy of the dictionary of all the HTTP cap handlers
        /// </summary>
        /// <returns>
        /// The dictionary copy.  The key is the capability name, the value is the HTTP handler.
        /// </returns>
        public Dictionary<string, IRequestHandler> GetCapsHandlers()
        {
            lock (_capsHandlers)
                return new Dictionary<string, IRequestHandler>(_capsHandlers);
        }
    }
}