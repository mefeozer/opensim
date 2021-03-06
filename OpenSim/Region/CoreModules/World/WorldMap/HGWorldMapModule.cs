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

using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.WorldMap;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;


namespace OpenSim.Region.CoreModules.Hypergrid
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HGWorldMapModule")]
    public class HGWorldMapModule : WorldMapModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Remember the map area that each client has been exposed to in this region
        private readonly Dictionary<UUID, List<MapBlockData>> _SeenMapBlocks = new Dictionary<UUID, List<MapBlockData>>();

        private string _MapImageServerURL = string.Empty;

        private IUserManagement _UserManagement;

        #region INonSharedRegionModule Members

        public override void Initialise(IConfigSource source)
        {
            string[] configSections = new string[] { "Map", "Startup" };
            if (Util.GetConfigVarFromSections<string>(
                source, "WorldMapModule", configSections, "WorldMap") == "HGWorldMap")
            {
                _Enabled = true;

                _MapImageServerURL = Util.GetConfigVarFromSections<string>(source, "MapTileURL", new string[] {"LoginService", "HGWorldMap", "SimulatorFeatures"});

                if (!string.IsNullOrEmpty(_MapImageServerURL))
                {
                    _MapImageServerURL = _MapImageServerURL.Trim();
                    if (!_MapImageServerURL.EndsWith("/"))
                        _MapImageServerURL = _MapImageServerURL + "/";
                }

                expireBlackListTime = Util.GetConfigVarFromSections<int>(source, "BlacklistTimeout", configSections, 10 * 60);
                expireBlackListTime *= 1000;
                _exportPrintScale =
                    Util.GetConfigVarFromSections<bool>(source, "ExportMapAddScale", configSections, _exportPrintScale);
                _exportPrintRegionName =
                    Util.GetConfigVarFromSections<bool>(source, "ExportMapAddRegionName", configSections, _exportPrintRegionName);
                _localV1MapAssets =
                    Util.GetConfigVarFromSections<bool>(source, "LocalV1MapAssets", configSections, _localV1MapAssets);
            }
        }

        public override void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            base.AddRegion(scene);

            scene.EventManager.OnClientClosed += EventManager_OnClientClosed;
        }

        public override void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            base.RegionLoaded(scene);
            ISimulatorFeaturesModule featuresModule = _scene.RequestModuleInterface<ISimulatorFeaturesModule>();

            if (featuresModule != null)
                featuresModule.OnSimulatorFeaturesRequest += OnSimulatorFeaturesRequest;

            _UserManagement = _scene.RequestModuleInterface<IUserManagement>();

        }

        public override void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            base.RemoveRegion(scene);

            scene.EventManager.OnClientClosed -= EventManager_OnClientClosed;
        }

        public override string Name => "HGWorldMap";

        #endregion

        void EventManager_OnClientClosed(UUID clientID, Scene scene)
        {
            ScenePresence sp = scene.GetScenePresence(clientID);
            if (sp != null)
            {
                if (_SeenMapBlocks.ContainsKey(clientID))
                {
                    List<MapBlockData> mapBlocks = _SeenMapBlocks[clientID];
                    foreach (MapBlockData b in mapBlocks)
                    {
                        b.Name = string.Empty;
                        // Set 'simulator is offline'. We need this because the viewer ignores SimAccess.Unknown (255)
                        b.Access = (byte)SimAccess.Down;
                    }

                    _log.DebugFormat("[HG MAP]: Resetting {0} blocks", mapBlocks.Count);
                    sp.ControllingClient.SendMapBlock(mapBlocks, 0);
                    _SeenMapBlocks.Remove(clientID);
                }
            }
        }

        protected override List<MapBlockData> GetAndSendBlocksInternal(IClientAPI remoteClient, int minX, int minY, int maxX, int maxY, uint flag)
        {
            List<MapBlockData>  mapBlocks = base.GetAndSendBlocksInternal(remoteClient, minX, minY, maxX, maxY, flag);
            if(mapBlocks.Count > 0)
            {
                lock (_SeenMapBlocks)
                {
                    if (!_SeenMapBlocks.ContainsKey(remoteClient.AgentId))
                    {
                        _SeenMapBlocks.Add(remoteClient.AgentId, mapBlocks);
                    }
                    else
                    {
                        List<MapBlockData> seen = _SeenMapBlocks[remoteClient.AgentId];
                        List<MapBlockData> newBlocks = new List<MapBlockData>();
                        foreach (MapBlockData b in mapBlocks)
                            if (seen.Find(delegate(MapBlockData bdata) { return bdata.X == b.X && bdata.Y == b.Y; }) == null)
                                newBlocks.Add(b);
                        seen.AddRange(newBlocks);
                    }
                }
            }
            return mapBlocks;
        }

        private void OnSimulatorFeaturesRequest(UUID agentID, ref OSDMap features)
        {
            if (_UserManagement != null && !string.IsNullOrEmpty(_MapImageServerURL) && !_UserManagement.IsLocalGridUser(agentID))
            {
                OSD extras;
                if (!features.TryGetValue("OpenSimExtras", out extras))
                    extras = new OSDMap();

                ((OSDMap)extras)["map-server-url"] = _MapImageServerURL;

            }
        }
    }
}
