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

using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Capabilities.Handlers;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using System;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    /// <summary>
    /// This module implements both WebFetchInventoryDescendents and FetchInventoryDescendents2 capabilities.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "FetchInventory2Module")]
    public class FetchInventory2Module : ISharedRegionModule
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public bool Enabled { get; private set; }

        private int _nScenes;

        private IInventoryService _inventoryService = null;
        private ILibraryService _LibraryService = null;
        private string _fetchInventory2Url;
        private ExpiringKey<UUID> _badRequests;

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            _fetchInventory2Url = config.GetString("Cap_FetchInventory2", string.Empty);

            if (!string.IsNullOrEmpty(_fetchInventory2Url))
                Enabled = true;
        }

        public void AddRegion(Scene s)
        {
        }

        public void RemoveRegion(Scene s)
        {
            if (!Enabled)
                return;

            s.EventManager.OnRegisterCaps -= RegisterCaps;
            --_nScenes;
            if(_nScenes <= 0)
            {
                _inventoryService = null;
                _LibraryService = null;
                _badRequests.Dispose();
                _badRequests = null;
            }
        }

        public void RegionLoaded(Scene s)
        {
            if (!Enabled)
                return;

            if (_inventoryService == null)
                _inventoryService = s.InventoryService;
            if(_LibraryService == null)
                _LibraryService = s.LibraryService;

            if(_badRequests == null)
                _badRequests = new ExpiringKey<UUID>(30000);

            if (_inventoryService != null)
            {
                s.EventManager.OnRegisterCaps += RegisterCaps;
                ++_nScenes;
            }
        }

        public void PostInitialise() {}

        public void Close() {}

        public string Name => "FetchInventory2Module";

        public Type ReplaceableInterface => null;

        #endregion

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            if (_fetchInventory2Url == "localhost")
            {
                FetchInventory2Handler fetchHandler = new FetchInventory2Handler(_inventoryService, agentID);
                caps.RegisterSimpleHandler("FetchInventory2",
                    new SimpleOSDMapHandler("POST", "/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
                    {
                        fetchHandler.FetchInventorySimpleRequest(httpRequest, httpResponse, map, _badRequests);
                    }
                 ));
            }
            else
            {
                caps.RegisterHandler("FetchInventory2", _fetchInventory2Url);
            }

//            _log.DebugFormat(
//                "[FETCH INVENTORY2 MODULE]: Registered capability {0} at {1} in region {2} for {3}",
//                capName, capUrl, _scene.RegionInfo.RegionName, agentID);
        }
    }
}
