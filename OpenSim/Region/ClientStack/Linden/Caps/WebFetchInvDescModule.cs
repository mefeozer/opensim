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
using System.Reflection;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenSim.Capabilities.Handlers;
using OpenSim.Framework.Monitoring;

namespace OpenSim.Region.ClientStack.Linden
{
    /// <summary>
    /// This module implements both WebFetchInventoryDescendents and FetchInventoryDescendents2 capabilities.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WebFetchInvDescModule")]
    public class WebFetchInvDescModule : INonSharedRegionModule
    {
        class APollRequest
        {
            public PollServiceInventoryEventArgs thepoll;
            public UUID reqID;
            public OSHttpRequest request;
        }

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Control whether requests will be processed asynchronously.
        /// </summary>
        /// <remarks>
        /// Defaults to true.  Can currently not be changed once a region has been added to the module.
        /// </remarks>
        public bool ProcessQueuedRequestsAsync { get; }

        /// <summary>
        /// Number of inventory requests processed by this module.
        /// </summary>
        /// <remarks>
        /// It's the PollServiceRequestManager that actually sends completed requests back to the requester.
        /// </remarks>
        public static int ProcessedRequestsCount { get; set; }

        private static Stat s_queuedRequestsStat;
        private static Stat s_processedRequestsStat;

        public Scene Scene { get; private set; }

        private IInventoryService _InventoryService;
        private ILibraryService _LibraryService;

        private bool _Enabled;
        private ExpiringKey<UUID> _badRequests;

        private string _fetchInventoryDescendents2Url;
//        private string _webFetchInventoryDescendentsUrl;

        private static FetchInvDescHandler _webFetchHandler;

        private static ObjectJobEngine _workerpool = null;

        private static int _NumberScenes = 0;

        #region ISharedRegionModule Members

        public WebFetchInvDescModule() : this(true) {}

        public WebFetchInvDescModule(bool processQueuedResultsAsync)
        {
            ProcessQueuedRequestsAsync = processQueuedResultsAsync;
        }

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            _fetchInventoryDescendents2Url = config.GetString("Cap_FetchInventoryDescendents2", string.Empty);
//            _webFetchInventoryDescendentsUrl = config.GetString("Cap_WebFetchInventoryDescendents", string.Empty);

//            if (_fetchInventoryDescendents2Url != string.Empty || _webFetchInventoryDescendentsUrl != string.Empty)
            if (!string.IsNullOrEmpty(_fetchInventoryDescendents2Url))
            {
                _Enabled = true;
            }
        }

        public void AddRegion(Scene s)
        {
            if (!_Enabled)
                return;

            Scene = s;
        }

        public void RemoveRegion(Scene s)
        {
            if (!_Enabled)
                return;

            _NumberScenes--;

            Scene.EventManager.OnRegisterCaps -= RegisterCaps;

            StatsManager.DeregisterStat(s_processedRequestsStat);
            StatsManager.DeregisterStat(s_queuedRequestsStat);

            Scene = null;
        }

        public void RegionLoaded(Scene s)
        {
            if (!_Enabled)
                return;

            if (s_processedRequestsStat == null)
                s_processedRequestsStat =
                    new Stat(
                        "ProcessedFetchInventoryRequests",
                        "Number of processed fetch inventory requests",
                        "These have not necessarily yet been dispatched back to the requester.",
                        "",
                        "inventory",
                        "httpfetch",
                        StatType.Pull,
                        MeasuresOfInterest.AverageChangeOverTime,
                        stat => { stat.Value = ProcessedRequestsCount; },
                        StatVerbosity.Debug);

            if (s_queuedRequestsStat == null)
                s_queuedRequestsStat =
                    new Stat(
                        "QueuedFetchInventoryRequests",
                        "Number of fetch inventory requests queued for processing",
                        "",
                        "",
                        "inventory",
                        "httpfetch",
                        StatType.Pull,
                        MeasuresOfInterest.AverageChangeOverTime,
                        stat => { stat.Value = _workerpool.Count; },
                        StatVerbosity.Debug);

            StatsManager.RegisterStat(s_processedRequestsStat);
            StatsManager.RegisterStat(s_queuedRequestsStat);

            _InventoryService = Scene.InventoryService;
            _LibraryService = Scene.LibraryService;

            // We'll reuse the same handler for all requests.
            _webFetchHandler = new FetchInvDescHandler(_InventoryService, _LibraryService, Scene);

            Scene.EventManager.OnRegisterCaps += RegisterCaps;

            if(_badRequests == null)
                _badRequests = new ExpiringKey<UUID>(30000);

            _NumberScenes++;

            if (ProcessQueuedRequestsAsync && _workerpool == null)
                _workerpool = new ObjectJobEngine(DoInventoryRequests, "InventoryWorker",2000,2);
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            if (!_Enabled)
                return;

            if (ProcessQueuedRequestsAsync)
            {
                if (_NumberScenes <= 0 && _workerpool != null)
                {
                    _workerpool.Dispose();
                    _workerpool = null;
                    _badRequests.Dispose();
                    _badRequests = null;
                }
            }
//            _queue.Dispose();
        }

        public string Name => "WebFetchInvDescModule";

        public Type ReplaceableInterface => null;

        #endregion

        private class PollServiceInventoryEventArgs : PollServiceEventArgs
        {
            private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

            private readonly Dictionary<UUID, Hashtable> responses = new Dictionary<UUID, Hashtable>();
            private readonly HashSet<UUID> dropedResponses = new HashSet<UUID>();

            private readonly WebFetchInvDescModule _module;

            public PollServiceInventoryEventArgs(WebFetchInvDescModule module, string url, UUID pId) :
                base(null, url, null, null, null, null, pId, int.MaxValue)
            {
                _module = module;

                HasEvents = (requestID, y) =>
                {
                    lock (responses)
                        return responses.ContainsKey(requestID);
                };

                Drop = (requestID, y) =>
                {
                    lock (responses)
                    {
                        responses.Remove(requestID);
                        lock(dropedResponses)
                            dropedResponses.Add(requestID);
                    }
                };

                GetEvents = (requestID, y) =>
                {
                    lock (responses)
                    {
                        try
                        {
                            return responses[requestID];
                        }
                        finally
                        {
                            responses.Remove(requestID);
                        }
                    }
                };

                Request = (requestID, request) =>
                {
                    APollRequest reqinfo = new APollRequest
                    {
                        thepoll = this,
                        reqID = requestID,
                        request = request
                    };
                    _workerpool.Enqueue(reqinfo);
                    return null;
                };

                NoEvents = (x, y) =>
                {
                    Hashtable response = new Hashtable();
                    response["int_response_code"] = 500;
                    response["str_response_string"] = "Script timeout";
                    response["content_type"] = "text/plain";
                    response["keepalive"] = false;

                    return response;
                };
            }

            public void Process(APollRequest requestinfo)
            {
                if(_module == null || _module.Scene == null || _module.Scene.ShuttingDown)
                    return;

                UUID requestID = requestinfo.reqID;

                lock(responses)
                {
                    lock(dropedResponses)
                    {
                        if(dropedResponses.Contains(requestID))
                        {
                            dropedResponses.Remove(requestID);
                            return;
                        }
                    }
                }

                OSHttpResponse osresponse = new OSHttpResponse(requestinfo.request);
                _webFetchHandler.FetchInventoryDescendentsRequest(requestinfo.request, osresponse, _module._badRequests);
                requestinfo.request.InputStream.Dispose();

                lock (responses)
                {
                    lock(dropedResponses)
                    {
                        if(dropedResponses.Contains(requestID))
                        {
                            dropedResponses.Remove(requestID);
                            ProcessedRequestsCount++;
                            return;
                        }
                    }

                    Hashtable response = new Hashtable();
                    response["h"] = osresponse;
                    responses[requestID] = response;
                }
                ProcessedRequestsCount++;
            }
        }

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            RegisterFetchDescendentsCap(agentID, caps, "FetchInventoryDescendents2", _fetchInventoryDescendents2Url);
        }

        private void RegisterFetchDescendentsCap(UUID agentID, Caps caps, string capName, string url)
        {
            string capUrl;

            // disable the cap clause
            if (url == "")
            {
                return;
            }
            // handled by the simulator
            else if (url == "localhost")
            {
                capUrl = "/" + UUID.Random();

                // Register this as a poll service
                PollServiceInventoryEventArgs args = new PollServiceInventoryEventArgs(this, capUrl, agentID);
                //args.Type = PollServiceEventArgs.EventType.Inventory;

                caps.RegisterPollHandler(capName, args);
            }
            // external handler
            else
            {
                capUrl = url;
                IExternalCapsModule handler = Scene.RequestModuleInterface<IExternalCapsModule>();
                if (handler != null)
                    handler.RegisterExternalUserCapsHandler(agentID,caps,capName,capUrl);
                else
                    caps.RegisterHandler(capName, capUrl);
            }
        }

        private static void DoInventoryRequests(object o)
        {
            if(_NumberScenes <= 0)
                return;
            APollRequest poolreq = o as APollRequest;
            if (poolreq != null && poolreq.thepoll != null)
                poolreq.thepoll.Process(poolreq);
        }
    }
}
