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
using System.Reflection;
using System.Threading;
using log4net;
using OpenMetaverse;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;

// using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Framework.Capabilities
{
    /// <summary>
    /// XXX Probably not a particularly nice way of allow us to get the scene presence from the scene (chiefly so that
    /// we can popup a message on the user's client if the inventory service has permanently failed).  But I didn't want
    /// to just pass the whole Scene into CAPS.
    /// </summary>
    public delegate IClientAPI GetClientDelegate(UUID agentID);

    public class Caps : IDisposable
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string _httpListenerHostName;
        private readonly uint _httpListenPort;

        /// <summary>
        /// This is the uuid portion of every CAPS path.  It is used to make capability urls private to the requester.
        /// </summary>
        private readonly string _capsObjectPath;
        public string CapsObjectPath => _capsObjectPath;

        private readonly CapsHandlers _capsHandlers;

        private readonly ConcurrentDictionary<string, PollServiceEventArgs> _pollServiceHandlers
            = new ConcurrentDictionary<string, PollServiceEventArgs>();

        private readonly Dictionary<string, string> _externalCapsHandlers = new Dictionary<string, string>();

        private readonly IHttpServer _httpListener;
        private readonly UUID _agentID;
        private readonly string _regionName;
        private ManualResetEvent _capsActive = new ManualResetEvent(false);

        public UUID AgentID => _agentID;

        public string RegionName => _regionName;

        public string HostName => _httpListenerHostName;

        public uint Port => _httpListenPort;

        public IHttpServer HttpListener => _httpListener;

        public bool SSLCaps => _httpListener.UseSSL;

        public string SSLCommonName => _httpListener.SSLCommonName;

        public CapsHandlers CapsHandlers => _capsHandlers;

        public Dictionary<string, string> ExternalCapsHandlers => _externalCapsHandlers;

        [Flags]
        public enum CapsFlags:uint
        {
            None =          0,
            SentSeeds =     1,

            ObjectAnim =    0x100,
            WLEnv =         0x200,
            AdvEnv =        0x400
        }

        public CapsFlags Flags { get; set;}

        public Caps(IHttpServer httpServer, string httpListen, uint httpPort, string capsPath,
                    UUID agent, string regionName)
        {
            _capsObjectPath = capsPath;
            _httpListener = httpServer;
            _httpListenerHostName = httpListen;

            _httpListenPort = httpPort;

            if (httpServer != null && httpServer.UseSSL)
            {
                _httpListenPort = httpServer.SSLPort;
                httpListen = httpServer.SSLCommonName;
                httpPort = httpServer.SSLPort;
            }

            _agentID = agent;
            _capsHandlers = new CapsHandlers(httpServer, httpListen, httpPort);
            _regionName = regionName;
            Flags = CapsFlags.None;
            _capsActive.Reset();
        }

        ~Caps()
        {
            Dispose(false);
        }
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            Flags = CapsFlags.None;
            if (_capsActive != null)
            {
                DeregisterHandlers();
                _capsActive.Dispose();
                _capsActive = null;
            }
        }

        /// <summary>
        /// Register a handler.  This allows modules to register handlers.
        /// </summary>
        /// <param name="capName"></param>
        /// <param name="handler"></param>
        public void RegisterHandler(string capName, IRequestHandler handler)
        {
            //_log.DebugFormat("[CAPS]: Registering handler for \"{0}\": path {1}", capName, handler.Path);
            _capsHandlers[capName] = handler;
        }

        public void RegisterSimpleHandler(string capName, ISimpleStreamHandler handler, bool addToListener = true)
        {
            //_log.DebugFormat("[CAPS]: Registering handler for \"{0}\": path {1}", capName, handler.Path);
            _capsHandlers.AddSimpleHandler(capName, handler, addToListener);
        }

        public void RegisterPollHandler(string capName, PollServiceEventArgs pollServiceHandler)
        {
//            _log.DebugFormat(
//                "[CAPS]: Registering handler with name {0}, url {1} for {2}",
//                capName, pollServiceHandler.Url, _agentID, _regionName);

            if(!_pollServiceHandlers.TryAdd(capName, pollServiceHandler))
            {
                _log.ErrorFormat(
                    "[CAPS]: Handler with name {0} already registered (ulr {1}, agent {2}, region {3}",
                    capName, pollServiceHandler.Url, _agentID, _regionName);
                return;
            }

            _httpListener.AddPollServiceHTTPHandler(pollServiceHandler);

//            uint port = (MainServer.Instance == null) ? 0 : MainServer.Instance.Port;
//            string protocol = "http";
//            string hostName = _httpListenerHostName;
//
//            if (MainServer.Instance.UseSSL)
//            {
//                hostName = MainServer.Instance.SSLCommonName;
//                port = MainServer.Instance.SSLPort;
//                protocol = "https";
//            }

//            RegisterHandler(
//                capName, String.Format("{0}://{1}:{2}{3}", protocol, hostName, port, pollServiceHandler.Url));
        }

        /// <summary>
        /// Register an external handler. The service for this capability is somewhere else
        /// given by the URL.
        /// </summary>
        /// <param name="capsName"></param>
        /// <param name="url"></param>
        public void RegisterHandler(string capsName, string url)
        {
            _externalCapsHandlers.Add(capsName, url);
        }

        /// <summary>
        /// Remove all CAPS service handlers.
        /// </summary>
        public void DeregisterHandlers()
        {
            foreach (string capsName in _capsHandlers.Caps)
            {
                _capsHandlers.Remove(capsName);
            }

            foreach (PollServiceEventArgs handler in _pollServiceHandlers.Values)
            {
                _httpListener.RemovePollServiceHTTPHandler(handler.Url);
            }
            _pollServiceHandlers.Clear();
        }

        public bool TryGetPollHandler(string name, out PollServiceEventArgs pollHandler)
        {
            return _pollServiceHandlers.TryGetValue(name, out pollHandler);
        }

        public Dictionary<string, PollServiceEventArgs> GetPollHandlers()
        {
            return new Dictionary<string, PollServiceEventArgs>(_pollServiceHandlers);
        }

        /// <summary>
        /// Return an LLSD-serializable Hashtable describing the
        /// capabilities and their handler details.
        /// </summary>
        /// <param name="excludeSeed">If true, then exclude the seed cap.</param>
        public Hashtable GetCapsDetails(bool excludeSeed, List<string> requestedCaps)
        {
            Hashtable caps = CapsHandlers.GetCapsDetails(excludeSeed, requestedCaps);

            lock (_pollServiceHandlers)
            {
                foreach (KeyValuePair <string, PollServiceEventArgs> kvp in _pollServiceHandlers)
                {
                    if (!requestedCaps.Contains(kvp.Key))
                        continue;

                        string hostName = _httpListenerHostName;
                        uint port = MainServer.Instance == null ? 0 : MainServer.Instance.Port;
                        string protocol = "http";

                        if (MainServer.Instance.UseSSL)
                        {
                            hostName = MainServer.Instance.SSLCommonName;
                            port = MainServer.Instance.SSLPort;
                            protocol = "https";
                        }
                        caps[kvp.Key] = string.Format("{0}://{1}:{2}{3}", protocol, hostName, port, kvp.Value.Url);
                }
            }

            // Add the external too
            foreach (KeyValuePair<string, string> kvp in ExternalCapsHandlers)
            {
                if (!requestedCaps.Contains(kvp.Key))
                    continue;

                caps[kvp.Key] = kvp.Value;
            }


            return caps;
        }

        public void Activate()
        {
            _capsActive.Set();
        }

        public bool WaitForActivation()
        {
            // Wait for 30s. If that elapses, return false and run without caps
            return _capsActive.WaitOne(120000);
        }
    }
}