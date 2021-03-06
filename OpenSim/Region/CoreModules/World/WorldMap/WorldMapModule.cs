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
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Reflection;

using System.Threading;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenMetaverse.StructuredData;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.CoreModules.World.Land;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.World.WorldMap
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WorldMapModule")]
    public class WorldMapModule : INonSharedRegionModule, IWorldMapModule, IDisposable
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string LogHeader = "[WORLD MAP]";

        private static readonly string DEFAULT_WORLD_MAP_EXPORT_PATH = "exportmap.jpg";

        private IMapImageGenerator _mapImageGenerator;
        private IMapImageUploadModule _mapImageServiceModule;

        protected Scene _scene;
        private ulong _regionHandle;
        private uint _regionGlobalX;
        private uint _regionGlobalY;
        private uint _regionSizeX;
        private uint _regionSizeY;
        private string _regionName;

        private byte[] myMapImageJPEG;
        protected volatile bool _Enabled = false;

        private ManualResetEvent _mapBlockRequestEvent = new ManualResetEvent(false);
        private ObjectJobEngine _mapItemsRequests;
        private readonly Dictionary<UUID, Queue<MapBlockRequestData>> _mapBlockRequests = new Dictionary<UUID, Queue<MapBlockRequestData>>();

        private readonly List<MapBlockData> cachedMapBlocks = new List<MapBlockData>();
        private ExpiringKey<string> _blacklistedurls = new ExpiringKey<string>(60000);
        private ExpiringKey<ulong> _blacklistedregions = new ExpiringKey<ulong>(60000);
        private ExpiringCacheOS<ulong, OSDMap> _cachedRegionMapItemsResponses = new ExpiringCacheOS<ulong, OSDMap>(1000);
        private readonly HashSet<UUID> _rootAgents = new HashSet<UUID>();

        private volatile bool _threadsRunning = false;

        // expire time for the blacklists in seconds
        protected int expireBlackListTime = 300; // 5 minutes
        // expire mapItems responses time in seconds. Throttles requests to regions that do answer
        private const double expireResponsesTime = 120.0; // 2 minutes ?
        //private int CacheRegionsDistance = 256;

        protected bool _exportPrintScale = false; // prints the scale of map in meters on exported map
        protected bool _exportPrintRegionName = false; // prints the region name exported map
        protected bool _localV1MapAssets = false; // keep V1 map assets only on  local cache

        ~WorldMapModule()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        bool disposed;
        public virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;

                _mapBlockRequestEvent?.Dispose();
                _blacklistedurls?.Dispose();
                _blacklistedregions?.Dispose();
                _mapItemsRequests?.Dispose();
                _cachedRegionMapItemsResponses?.Dispose();

                _mapBlockRequestEvent = null;
                _blacklistedurls = null;
                _blacklistedregions = null;
                _mapItemsRequests = null;
                _cachedRegionMapItemsResponses = null;
            }
        }

        #region INonSharedRegionModule Members
        public virtual void Initialise(IConfigSource config)
        {
            string[] configSections = new string[] { "Map", "Startup" };

            if (Util.GetConfigVarFromSections<string>(
                config, "WorldMapModule", configSections, "WorldMap") == "WorldMap")
                _Enabled = true;

            expireBlackListTime = Util.GetConfigVarFromSections<int>(config, "BlacklistTimeout", configSections, 10 * 60);
            expireBlackListTime *= 1000;
            _exportPrintScale =
                Util.GetConfigVarFromSections<bool>(config, "ExportMapAddScale", configSections, _exportPrintScale);
            _exportPrintRegionName =
                Util.GetConfigVarFromSections<bool>(config, "ExportMapAddRegionName", configSections, _exportPrintRegionName);
            _localV1MapAssets =
                Util.GetConfigVarFromSections<bool>(config, "LocalV1MapAssets", configSections, _localV1MapAssets);
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            lock (scene)
            {
                _scene = scene;
                _regionHandle = scene.RegionInfo.RegionHandle;
                _regionGlobalX = scene.RegionInfo.WorldLocX;
                _regionGlobalY = scene.RegionInfo.WorldLocY;
                _regionSizeX = scene.RegionInfo.RegionSizeX;
                _regionSizeY = scene.RegionInfo.RegionSizeX;
                _regionName = scene.RegionInfo.RegionName;

                _scene.RegisterModuleInterface<IWorldMapModule>(this);

                _scene.AddCommand(
                    "Regions", this, "export-map",
                    "export-map [<path>]",
                    "Save an image of the world map", HandleExportWorldMapConsoleCommand);

                _scene.AddCommand(
                    "Regions", this, "generate map",
                    "generate map",
                    "Generates and stores a new maptile.", HandleGenerateMapConsoleCommand);

                AddHandlers();
            }
        }

        public virtual void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            lock (_scene)
            {
                _Enabled = false;
                RemoveHandlers();
                _scene = null;
            }
        }

        public virtual void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            _mapImageGenerator = _scene.RequestModuleInterface<IMapImageGenerator>();
            _mapImageServiceModule = _scene.RequestModuleInterface<IMapImageUploadModule>();
        }

        public virtual void Close()
        {
            Dispose();
        }

        public Type ReplaceableInterface => null;

        public virtual string Name => "WorldMapModule";

        #endregion

        // this has to be called with a lock on _scene
        protected virtual void AddHandlers()
        {
            myMapImageJPEG = new byte[0];

            string regionimage = "regionImage" + _scene.RegionInfo.RegionID.ToString();
            regionimage = regionimage.Replace("-", "");
            _log.Info("[WORLD MAP]: JPEG Map location: " + _scene.RegionInfo.ServerURI + "index.php?method=" + regionimage);

            MainServer.Instance.AddIndexPHPMethodHandler(regionimage, OnHTTPGetMapImage);
            MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler(
                "/MAP/MapItems/" + _regionHandle.ToString(), HandleRemoteMapItemRequest));

            _scene.EventManager.OnRegisterCaps += OnRegisterCaps;
            _scene.EventManager.OnNewClient += OnNewClient;
            _scene.EventManager.OnClientClosed += ClientLoggedOut;
            _scene.EventManager.OnMakeChildAgent += MakeChildAgent;
            _scene.EventManager.OnMakeRootAgent += MakeRootAgent;
            _scene.EventManager.OnRegionUp += OnRegionUp;

            StartThreads();
        }

        // this has to be called with a lock on _scene
        protected virtual void RemoveHandlers()
        {
            StopThreads();

            _scene.EventManager.OnRegionUp -= OnRegionUp;
            _scene.EventManager.OnMakeRootAgent -= MakeRootAgent;
            _scene.EventManager.OnMakeChildAgent -= MakeChildAgent;
            _scene.EventManager.OnClientClosed -= ClientLoggedOut;
            _scene.EventManager.OnNewClient -= OnNewClient;
            _scene.EventManager.OnRegisterCaps -= OnRegisterCaps;

            _scene.UnregisterModuleInterface<IWorldMapModule>(this);

            MainServer.Instance.RemoveSimpleStreamHandler("/MAP/MapItems/" + _scene.RegionInfo.RegionHandle.ToString());
            string regionimage = "regionImage" + _scene.RegionInfo.RegionID.ToString();
            regionimage = regionimage.Replace("-", "");
            MainServer.Instance.RemoveIndexPHPMethodHandler(regionimage);
        }

        public void OnRegisterCaps(UUID agentID, Caps caps)
        {
            //_log.DebugFormat("[WORLD MAP]: OnRegisterCaps: agentID {0} caps {1}", agentID, caps);
            caps.RegisterSimpleHandler("MapLayer", new SimpleStreamHandler("/" + UUID.Random(), MapLayerRequest));
        }

        /// <summary>
        /// Callback for a map layer request
        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void MapLayerRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            if(request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            LLSDMapLayerResponse mapResponse = new LLSDMapLayerResponse();
            mapResponse.LayerData.Array.Add(GetOSDMapLayerResponse());
            response.RawBuffer = System.Text.Encoding.UTF8.GetBytes(LLSDHelpers.SerialiseLLSDReply(mapResponse));
            response.StatusCode = (int)HttpStatusCode.OK;
        }

         /// <summary>
        ///
        /// </summary>
        /// <returns></returns>
        protected static OSDMapLayer GetOSDMapLayerResponse()
        {
            // not sure about this.... 2048 or master 5000 and hack above?

            OSDMapLayer mapLayer = new OSDMapLayer
            {
                Right = 30000,
                Top = 30000,
                ImageID = new UUID("00000000-0000-1111-9999-000000000006")
            };

            return mapLayer;
        }
        #region EventHandlers

        /// <summary>
        /// Registered for event
        /// </summary>
        /// <param name="client"></param>
        private void OnNewClient(IClientAPI client)
        {
            client.OnRequestMapBlocks += RequestMapBlocks;
            client.OnMapItemRequest += HandleMapItemRequest;
        }

        /// <summary>
        /// Client logged out, check to see if there are any more root agents in the simulator
        /// If not, stop the mapItemRequest Thread
        /// Event handler
        /// </summary>
        /// <param name="AgentId">AgentID that logged out</param>
        private void ClientLoggedOut(UUID AgentId, Scene scene)
        {
            lock (_rootAgents)
            {
                _rootAgents.Remove(AgentId);
            }
            lock (_mapBlockRequestEvent)
            {
                _mapBlockRequests.Remove(AgentId);
            }
        }
        #endregion

        /// <summary>
        /// Starts the MapItemRequest Thread
        /// Note that this only gets started when there are actually agents in the region
        /// Additionally, it gets stopped when there are none.
        /// </summary>
        /// <param name="o"></param>
        private void StartThreads()
        {
            if (!_threadsRunning)
            {
                _threadsRunning = true;
                _mapItemsRequests = new ObjectJobEngine(MapItemsprocess,string.Format("MapItems ({0})", _regionName));
                WorkManager.StartThread(MapBlocksProcess, string.Format("MapBlocks ({0})", _regionName));
            }
        }

        /// <summary>
        /// Enqueues a 'stop thread' MapRequestState.  Causes the MapItemRequest thread to end
        /// </summary>
        private void StopThreads()
        {
            _threadsRunning = false;
            _mapBlockRequestEvent.Set();
            _mapItemsRequests.Dispose();
        }

        public virtual void HandleMapItemRequest(IClientAPI remoteClient, uint flags,
            uint EstateID, bool godlike, uint itemtype, ulong regionhandle)
        {
            // _log.DebugFormat("[WORLD MAP]: Handle MapItem request {0} {1}", regionhandle, itemtype);

            lock (_rootAgents)
            {
                if (!_rootAgents.Contains(remoteClient.AgentId))
                    return;
            }

            // local or remote request?
            if (regionhandle != 0 && regionhandle != _regionHandle)
            {
                Util.RegionHandleToWorldLoc(regionhandle, out uint x, out uint y);
                if( x < _regionGlobalX || y < _regionGlobalY ||
                    x >= _regionGlobalX + _regionSizeX || y >= _regionGlobalY + _regionSizeY)
                {
                    RequestMapItems(remoteClient.AgentId, flags, EstateID, godlike, itemtype, regionhandle);
                    return;
                }
            }

            // its about this region...

            List<mapItemReply> mapitems = new List<mapItemReply>();
            mapItemReply mapitem = new mapItemReply();

            // viewers only ask for green dots to each region now
            // except at login with regionhandle 0
            // possible on some other rare ocasions
            // use previous hack of sending all items with the green dots

            bool adultRegion;

            int tc = Environment.TickCount;
            string hash = Util.Md5Hash(_regionName + tc.ToString());

            if (regionhandle == 0)
            {
                switch (itemtype)
                {
                    case (int)GridItemType.AgentLocations:
                        // Service 6 right now (MAP_ITEM_AGENTS_LOCATION; green dots)

                        if (_scene.GetRootAgentCount() <= 1) //own position is not sent
                        {
                            mapitem = new mapItemReply(
                                        _regionGlobalX + 1,
                                        _regionGlobalY + 1,
                                        UUID.Zero,
                                        hash,
                                        0, 0);
                            mapitems.Add(mapitem);
                        }
                        else
                        {
                            _scene.ForEachRootScenePresence(delegate (ScenePresence sp)
                            {
                                // Don't send a green dot for yourself
                                if (sp.UUID != remoteClient.AgentId)
                                {
                                    if (sp.IsNPC || sp.IsDeleted || sp.IsInTransit)
                                        return;

                                    mapitem = new mapItemReply(
                                        _regionGlobalX + (uint)sp.AbsolutePosition.X,
                                        _regionGlobalY + (uint)sp.AbsolutePosition.Y,
                                        UUID.Zero,
                                        hash,
                                        1, 0);
                                    mapitems.Add(mapitem);
                                }
                            });
                        }
                        remoteClient.SendMapItemReply(mapitems.ToArray(), itemtype, flags);
                        break;

                    case (int)GridItemType.Telehub:
                        // Service 1 (MAP_ITEM_TELEHUB)

                        SceneObjectGroup sog = _scene.GetSceneObjectGroup(_scene.RegionInfo.RegionSettings.TelehubObject);
                        if (sog != null)
                        {
                            mapitem = new mapItemReply(
                                            _regionGlobalX + (uint)sog.AbsolutePosition.X,
                                            _regionGlobalY + (uint)sog.AbsolutePosition.Y,
                                            UUID.Zero,
                                            sog.Name,
                                            0,  // color (not used)
                                            0   // 0 = telehub / 1 = infohub
                                            );
                            mapitems.Add(mapitem);
                            remoteClient.SendMapItemReply(mapitems.ToArray(), itemtype, flags);
                        }
                        break;

                    case (int)GridItemType.AdultLandForSale:
                    case (int)GridItemType.LandForSale:

                        // Service 7 (MAP_ITEM_LAND_FOR_SALE)
                        adultRegion = _scene.RegionInfo.RegionSettings.Maturity == 2;
                        if (adultRegion)
                        {
                            if (itemtype == (int)GridItemType.LandForSale)
                                break;
                        }
                        else
                        {
                            if (itemtype == (int)GridItemType.AdultLandForSale)
                                break;
                        }

                        // Parcels
                        ILandChannel landChannel = _scene.LandChannel;
                        List<ILandObject> parcels = landChannel.AllParcels();

                        if (parcels != null && parcels.Count >= 1)
                        {
                            foreach (ILandObject parcel_interface in parcels)
                            {
                                // Play it safe
                                if (!(parcel_interface is LandObject))
                                    continue;

                                LandObject land = (LandObject)parcel_interface;
                                LandData parcel = land.LandData;

                                // Show land for sale
                                if ((parcel.Flags & (uint)ParcelFlags.ForSale) == (uint)ParcelFlags.ForSale)
                                {
                                    float x = land.CenterPoint.X + _regionGlobalX;
                                    float y = land.CenterPoint.Y + _regionGlobalY;
                                    mapitem = new mapItemReply(
                                                (uint)x, (uint)y,
                                                parcel.GlobalID,
                                                parcel.Name,
                                                parcel.Area,
                                                parcel.SalePrice
                                    );
                                    mapitems.Add(mapitem);
                                }
                            }
                        }
                        remoteClient.SendMapItemReply(mapitems.ToArray(), itemtype, flags);
                        break;

                    case (uint)GridItemType.PgEvent:
                    case (uint)GridItemType.MatureEvent:
                    case (uint)GridItemType.AdultEvent:
                    case (uint)GridItemType.Classified:
                    case (uint)GridItemType.Popular:
                        // TODO
                        // just dont not cry about them
                        break;

                    default:
                        // unkown map item type
                        _log.DebugFormat("[WORLD MAP]: Unknown MapItem type {0}", itemtype);
                        break;
                }
            }
            else
            {
                // send all items till we get a better fix

                // Service 6 right now (MAP_ITEM_AGENTS_LOCATION; green dots)

                if (_scene.GetRootAgentCount() <= 1) // own is not sent
                {
                    mapitem = new mapItemReply(
                                _regionGlobalX + 1,
                                _regionGlobalY + 1,
                                UUID.Zero,
                                hash,
                                0, 0);
                    mapitems.Add(mapitem);
                }
                else
                {
                    _scene.ForEachRootScenePresence(delegate (ScenePresence sp)
                    {
                        // Don't send a green dot for yourself
                        if (sp.UUID != remoteClient.AgentId)
                        {
                            if (sp.IsNPC || sp.IsDeleted || sp.IsInTransit)
                                return;

                            mapitem = new mapItemReply(
                                _regionGlobalX + (uint)sp.AbsolutePosition.X,
                                _regionGlobalY + (uint)sp.AbsolutePosition.Y,
                                UUID.Zero,
                                hash,
                                1, 0);
                            mapitems.Add(mapitem);
                        }
                    });
                }
                remoteClient.SendMapItemReply(mapitems.ToArray(), 6, flags);
                mapitems.Clear();

                // Service 1 (MAP_ITEM_TELEHUB)

                SceneObjectGroup sog = _scene.GetSceneObjectGroup(_scene.RegionInfo.RegionSettings.TelehubObject);
                if (sog != null)
                {
                    mapitem = new mapItemReply(
                                    _regionGlobalX + (uint)sog.AbsolutePosition.X,
                                    _regionGlobalY + (uint)sog.AbsolutePosition.Y,
                                    UUID.Zero,
                                    sog.Name,
                                    0,  // color (not used)
                                    0   // 0 = telehub / 1 = infohub
                                    );
                    mapitems.Add(mapitem);
                    remoteClient.SendMapItemReply(mapitems.ToArray(), 1, flags);
                    mapitems.Clear();
                }

                // Service 7 (MAP_ITEM_LAND_FOR_SALE)

                uint its = 7;
                if (_scene.RegionInfo.RegionSettings.Maturity == 2)
                    its = 10;

                // Parcels
                ILandChannel landChannel = _scene.LandChannel;
                List<ILandObject> parcels = landChannel.AllParcels();

                if (parcels != null && parcels.Count >= 1)
                {
                    foreach (ILandObject parcel_interface in parcels)
                    {
                        // Play it safe
                        if (!(parcel_interface is LandObject))
                            continue;

                        LandObject land = (LandObject)parcel_interface;
                        LandData parcel = land.LandData;

                        // Show land for sale
                        if ((parcel.Flags & (uint)ParcelFlags.ForSale) == (uint)ParcelFlags.ForSale)
                        {
                            float x = land.CenterPoint.X + _regionGlobalX;
                            float y = land.CenterPoint.Y + _regionGlobalY;
                            mapitem = new mapItemReply(
                                        (uint)x, (uint)y,
                                        parcel.GlobalID,
                                        parcel.Name,
                                        parcel.Area,
                                        parcel.SalePrice
                            );
                            mapitems.Add(mapitem);
                        }
                    }
                    if(mapitems.Count >0)
                        remoteClient.SendMapItemReply(mapitems.ToArray(), its, flags);
                    mapitems.Clear();
                }
            }
        }

        private int nAsyncRequests = 0;
        /// <summary>
        /// Processing thread main() loop for doing remote mapitem requests
        /// </summary>
        public void MapItemsprocess(object o)
        {
            if (_scene == null || !_threadsRunning)
                return;

            const int MAX_ASYNC_REQUESTS = 5;
            ScenePresence av = null;
            MapRequestState st = o as MapRequestState;

            if (st == null || st.agentID == UUID.Zero)
                return;

            if (_blacklistedregions.ContainsKey(st.regionhandle))
                return;
            if (!_scene.TryGetScenePresence(st.agentID, out av))
                return;
            if (av == null || av.IsChildAgent || av.IsDeleted || av.IsInTransit)
                return;

            try
            {
                if (_cachedRegionMapItemsResponses.TryGetValue(st.regionhandle, out OSDMap responseMap))
                {
                    if (responseMap != null)
                    {
                        if (responseMap.ContainsKey(st.itemtype.ToString()))
                        {
                            List<mapItemReply> returnitems = new List<mapItemReply>();
                            OSDArray itemarray = (OSDArray)responseMap[st.itemtype.ToString()];
                            for (int i = 0; i < itemarray.Count; i++)
                            {
                                OSDMap mapitem = (OSDMap)itemarray[i];
                                mapItemReply mi = new mapItemReply
                                {
                                    x = (uint)mapitem["X"].AsInteger(),
                                    y = (uint)mapitem["Y"].AsInteger(),
                                    id = mapitem["ID"].AsUUID(),
                                    Extra = mapitem["Extra"].AsInteger(),
                                    Extra2 = mapitem["Extra2"].AsInteger(),
                                    name = mapitem["Name"].AsString()
                                };
                                returnitems.Add(mi);
                            }
                            av.ControllingClient.SendMapItemReply(returnitems.ToArray(), st.itemtype, st.flags & 0xffff);
                        }
                    }
                    else
                    {
                        _mapItemsRequests.Enqueue(st);
                        if (_mapItemsRequests.Count < 3)
                            Thread.Sleep(100);
                    }
                }
                else
                {
                    _cachedRegionMapItemsResponses.AddOrUpdate(st.regionhandle, null, expireResponsesTime); //  a bit more time for the access

                    // nothig for region, fire a request
                    Interlocked.Increment(ref nAsyncRequests);
                    MapRequestState rst = st;
                    Util.FireAndForget(x =>
                    {
                        RequestMapItemsAsync(rst);
                    });
                }

                while (nAsyncRequests >= MAX_ASYNC_REQUESTS) // hit the break
                {
                    Thread.Sleep(100);
                    if (_scene == null || !_threadsRunning)
                        break;
                }
            }
            catch { }
        }

        /// <summary>
        /// Enqueue the MapItem request for remote processing
        /// </summary>
        /// <param name="id">Agent ID that we are making this request on behalf</param>
        /// <param name="flags">passed in from packet</param>
        /// <param name="EstateID">passed in from packet</param>
        /// <param name="godlike">passed in from packet</param>
        /// <param name="itemtype">passed in from packet</param>
        /// <param name="regionhandle">Region we're looking up</param>
        public void RequestMapItems(UUID id, uint flags, uint EstateID, bool godlike, uint itemtype, ulong regionhandle)
        {
            if(!_threadsRunning)
                return;

            MapRequestState st = new MapRequestState
            {
                agentID = id,
                flags = flags,
                EstateID = EstateID,
                godlike = godlike,
                itemtype = itemtype,
                regionhandle = regionhandle
            };
            _mapItemsRequests.Enqueue(st);
        }

        private static readonly uint[] itemTypesForcedSend = new uint[] { 6, 1, 7, 10 }; // green dots, infohub, land sells

        /// <summary>
        /// Does the actual remote mapitem request
        /// This should be called from an asynchronous thread
        /// Request failures get blacklisted until region restart so we don't
        /// continue to spend resources trying to contact regions that are down.
        /// </summary>
        /// <param name="httpserver">blank string, we discover this in the process</param>
        /// <param name="id">Agent ID that we are making this request on behalf</param>
        /// <param name="flags">passed in from packet</param>
        /// <param name="EstateID">passed in from packet</param>
        /// <param name="godlike">passed in from packet</param>
        /// <param name="itemtype">passed in from packet</param>
        /// <param name="regionhandle">Region we're looking up</param>
        /// <returns></returns>
        private void RequestMapItemsAsync(MapRequestState requestState)
        {
            // _log.DebugFormat("[WORLDMAP]: RequestMapItemsAsync; region handle: {0} {1}", regionhandle, itemtype);

            ulong regionhandle = requestState.regionhandle;
            if (_blacklistedregions.ContainsKey(regionhandle))
            {
                _cachedRegionMapItemsResponses.Remove(regionhandle);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            UUID agentID = requestState.agentID;
            if (agentID == UUID.Zero || !_scene.TryGetScenePresence(agentID, out ScenePresence sp))
            {
                _cachedRegionMapItemsResponses.Remove(regionhandle);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            GridRegion mreg = _scene.GridService.GetRegionByHandle(_scene.RegionInfo.ScopeID, regionhandle);
            if (mreg == null)
            {
                // Can't find the http server or its blocked
                _blacklistedregions.Add(regionhandle, expireBlackListTime);
                _cachedRegionMapItemsResponses.Remove(regionhandle);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            if (!_threadsRunning)
                return;

            string serverURI = mreg.ServerURI;
            if(WebUtil.GlobalExpiringBadURLs.ContainsKey(serverURI))
            {
                _blacklistedregions.Add(regionhandle, expireBlackListTime);
                _cachedRegionMapItemsResponses.Remove(regionhandle);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            string httpserver = serverURI + "MAP/MapItems/" + regionhandle.ToString();
            if (_blacklistedurls.ContainsKey(httpserver))
            {
                _blacklistedregions.Add(regionhandle, expireBlackListTime);
                _cachedRegionMapItemsResponses.Remove(regionhandle);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            if (!_threadsRunning)
                return;

            WebRequest mapitemsrequest = null;
            try
            {
                mapitemsrequest = WebRequest.Create(httpserver);
            }
            catch (Exception e)
            {
                WebUtil.GlobalExpiringBadURLs.Add(serverURI, 120000);
                _blacklistedregions.Add(regionhandle, expireBlackListTime);
                _cachedRegionMapItemsResponses.Remove(regionhandle);
                _log.DebugFormat("[WORLD MAP]: Access to {0} failed with {1}", httpserver, e);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            UUID requestID = UUID.Random();

            mapitemsrequest.Method = "GET";
            mapitemsrequest.ContentType = "application/xml+llsd";

            string response_mapItems_reply = null;

            // get the response
            try
            {
                using (WebResponse webResponse = mapitemsrequest.GetResponse())
                {
                    using (StreamReader sr = new StreamReader(webResponse.GetResponseStream()))
                        response_mapItems_reply = sr.ReadToEnd().Trim();
                }
            }
            catch (WebException)
            {
                WebUtil.GlobalExpiringBadURLs.Add(serverURI, 60000);
                _blacklistedurls.Add(httpserver, expireBlackListTime);
                _blacklistedregions.Add(regionhandle, expireBlackListTime);
                _cachedRegionMapItemsResponses.Remove(regionhandle);

                _log.WarnFormat("[WORLD MAP]: Blacklisted url {0}", httpserver);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }
            catch
            {
                _log.DebugFormat("[WORLD MAP]: RequestMapItems failed for {0}", httpserver);
                _blacklistedregions.Add(regionhandle, expireBlackListTime);
                _cachedRegionMapItemsResponses.Remove(regionhandle);
                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            if (!_threadsRunning)
                return;

            OSDMap responseMap = null;
            try
            {
                responseMap = (OSDMap)OSDParser.DeserializeLLSDXml(response_mapItems_reply);
            }
            catch (Exception ex)
            {
                _log.InfoFormat("[WORLD MAP]: exception on parse of RequestMapItems reply from {0}: {1}", httpserver, ex.Message);
                _blacklistedregions.Add(regionhandle, expireBlackListTime);
                _cachedRegionMapItemsResponses.Remove(regionhandle);

                Interlocked.Decrement(ref nAsyncRequests);
                return;
            }

            if (!_threadsRunning)
                return;

            _cachedRegionMapItemsResponses.AddOrUpdate(regionhandle, responseMap, expireResponsesTime);

            uint flags = requestState.flags & 0xffff;
            if(_scene.TryGetScenePresence(agentID, out ScenePresence av) &&
                    av != null && !av.IsChildAgent && !av.IsDeleted && !av.IsInTransit)
            {
                // send all the items or viewers will never ask for them, except green dots
                foreach (uint itfs in itemTypesForcedSend)
                {
                    if (responseMap.ContainsKey(itfs.ToString()))
                    {
                        List<mapItemReply> returnitems = new List<mapItemReply>();
                        OSDArray itemarray = (OSDArray)responseMap[itfs.ToString()];
                        for (int i = 0; i < itemarray.Count; i++)
                        {
                            if (!_threadsRunning)
                                return;

                            OSDMap mapitem = (OSDMap)itemarray[i];
                            mapItemReply mi = new mapItemReply
                            {
                                x = (uint)mapitem["X"].AsInteger(),
                                y = (uint)mapitem["Y"].AsInteger(),
                                id = mapitem["ID"].AsUUID(),
                                Extra = mapitem["Extra"].AsInteger(),
                                Extra2 = mapitem["Extra2"].AsInteger(),
                                name = mapitem["Name"].AsString()
                            };
                            returnitems.Add(mi);
                        }
                        av.ControllingClient.SendMapItemReply(returnitems.ToArray(), itfs, flags);
                    }
                }
            }

            Interlocked.Decrement(ref nAsyncRequests);
        }


        private const double SPAMBLOCKTIMEms = 300000; // 5 minutes
        private readonly Dictionary<UUID,double> spamBlocked = new Dictionary<UUID,double>();

        /// <summary>
        /// Requests map blocks in area of minX, maxX, minY, MaxY in world cordinates
        /// </summary>
        /// <param name="minX"></param>
        /// <param name="minY"></param>
        /// <param name="maxX"></param>
        /// <param name="maxY"></param>
        public void RequestMapBlocks(IClientAPI remoteClient, int minX, int minY, int maxX, int maxY, uint flag)
        {
            // anti spam because of FireStorm 4.7.7 absurd request repeat rates
            // possible others

            double now = Util.GetTimeStampMS();
            UUID agentID = remoteClient.AgentId;

            lock (_mapBlockRequestEvent)
            {
                if(spamBlocked.ContainsKey(agentID))
                {
                    if(spamBlocked[agentID] < now &&
                            (!_mapBlockRequests.ContainsKey(agentID) ||
                            _mapBlockRequests[agentID].Count == 0 ))
                    {
                        spamBlocked.Remove(agentID);
                        _log.DebugFormat("[WoldMapModule] RequestMapBlocks release spammer {0}", agentID);
                    }
                    else
                        return;
                }
                else
                {
                // ugly slow expire spammers
                    if(spamBlocked.Count > 0)
                    {
                        UUID k = UUID.Zero;
                        bool expireone = false;
                        foreach(UUID k2 in spamBlocked.Keys)
                        {
                            if(spamBlocked[k2] < now &&
                                (!_mapBlockRequests.ContainsKey(k2) ||
                                _mapBlockRequests[k2].Count == 0 ))
                            {
                                _log.DebugFormat("[WoldMapModule] RequestMapBlocks release spammer {0}", k2);
                                k = k2;
                                expireone = true;
                            }
                        break; // doing one at a time
                        }
                    if(expireone)
                        spamBlocked.Remove(k);
                    }
                }

//                _log.DebugFormat("[WoldMapModule] RequestMapBlocks {0}={1}={2}={3} {4}", minX, minY, maxX, maxY, flag);

                MapBlockRequestData req = new MapBlockRequestData()
                {
                    client = remoteClient,
                    minX = minX,
                    maxX = maxX,
                    minY = minY,
                    maxY = maxY,
                    flags = flag
                };

                Queue<MapBlockRequestData> agentq; 
                if(!_mapBlockRequests.TryGetValue(agentID, out agentq))
                {
                    agentq = new Queue<MapBlockRequestData>();
                    _mapBlockRequests[agentID] = agentq;
                }
                if(agentq.Count < 150 )
                    agentq.Enqueue(req);
                else
                {
                    spamBlocked[agentID] = now + SPAMBLOCKTIMEms;
                    _log.DebugFormat("[WoldMapModule] RequestMapBlocks blocking spammer {0} for {1} s",agentID, SPAMBLOCKTIMEms/1000.0);
                }
                _mapBlockRequestEvent.Set();
            }
        }

        protected void MapBlocksProcess()
        {
            List<MapBlockRequestData> thisRunData = new List<MapBlockRequestData>();
            List<UUID> toRemove = new List<UUID>();
            try
            {
                while (true)
                {
                    while(!_mapBlockRequestEvent.WaitOne(4900))
                    {
                        Watchdog.UpdateThread();
                        if (_scene == null || !_threadsRunning)
                        {
                            Watchdog.RemoveThread();
                            return;
                        }
                    }
                    Watchdog.UpdateThread();
                    if (_scene == null || !_threadsRunning)
                        break;

                    lock (_mapBlockRequestEvent)
                    {
                        int total = 0;
                        foreach (KeyValuePair<UUID, Queue<MapBlockRequestData>> kvp in _mapBlockRequests)
                        {
                            if (kvp.Value.Count > 0)
                            {
                                thisRunData.Add(kvp.Value.Dequeue());
                                total += kvp.Value.Count;
                            }
                            else
                                toRemove.Add(kvp.Key);
                        }

                        if (_scene == null || !_threadsRunning)
                            break;

                        if (total == 0)
                            _mapBlockRequestEvent.Reset();
                    }

                    if (toRemove.Count > 0)
                    {
                        foreach (UUID u in toRemove)
                            _mapBlockRequests.Remove(u);
                        toRemove.Clear();
                    }

                    if (thisRunData.Count > 0)
                    {
                        foreach (MapBlockRequestData req in thisRunData)
                        {
                            GetAndSendBlocksInternal(req.client, req.minX, req.minY, req.maxX, req.maxY, req.flags);
                            if (_scene == null || !_threadsRunning)
                                break;
                            Watchdog.UpdateThread();
                        }
                        thisRunData.Clear();
                    }

                    if (_scene == null || !_threadsRunning)
                        break;
                    Thread.Sleep(50);
                }
            }
            catch { }
            Watchdog.RemoveThread();
        }

        protected virtual List<MapBlockData> GetAndSendBlocksInternal(IClientAPI remoteClient, int minX, int minY, int maxX, int maxY, uint flag)
        {
            List<MapBlockData> mapBlocks = new List<MapBlockData>();
            List<GridRegion> regions = _scene.GridService.GetRegionRange(_scene.RegionInfo.ScopeID,
                minX * (int)Constants.RegionSize,
                maxX * (int)Constants.RegionSize,
                minY * (int)Constants.RegionSize,
                maxY * (int)Constants.RegionSize);

            // only send a negative answer for a single region request
            // corresponding to a click on the map. Current viewers
            // keep displaying "loading.." without this
            if (regions.Count == 0)
            {
                if((flag & 0x10000) != 0 && minX == maxX && minY == maxY)
                {
                    MapBlockData block = new MapBlockData
                    {
                        X = (ushort)minX,
                        Y = (ushort)minY,
                        MapImageId = UUID.Zero,
                        Access = (byte)SimAccess.NonExistent
                    };
                    mapBlocks.Add(block);
                    remoteClient.SendMapBlock(mapBlocks, flag & 0xffff);
                }
                return mapBlocks;
            }

            List<MapBlockData> allBlocks = new List<MapBlockData>();
            flag &= 0xffff;

            foreach (GridRegion r in regions)
            {
                if (r == null)
                    continue;
                MapBlockData block = new MapBlockData();
                MapBlockFromGridRegion(block, r, flag);
                mapBlocks.Add(block);
                allBlocks.Add(block);

                if (mapBlocks.Count >= 10)
                {
                    remoteClient.SendMapBlock(mapBlocks, flag);
                    mapBlocks.Clear();
                    Thread.Sleep(50);
                }
                if (_scene == null || !_threadsRunning)
                    return allBlocks;
            }
            if (mapBlocks.Count > 0)
                remoteClient.SendMapBlock(mapBlocks, flag);

            return allBlocks;
        }

        public void MapBlockFromGridRegion(MapBlockData block, GridRegion r, uint flag)
        {
            if (r == null)
            {
                // we should not get here ??
//                block.Access = (byte)SimAccess.Down; this is for a grid reply on r
                block.Access = (byte)SimAccess.NonExistent;
                block.MapImageId = UUID.Zero;
                return;
            }

            block.Access = r.Access;
            switch (flag)
            {
                case 0:
                    block.MapImageId = r.TerrainImage;
                    break;
                case 2:
                    block.MapImageId = r.ParcelImage;
                    break;
                default:
                    block.MapImageId = UUID.Zero;
                    break;
            }
            block.Name = r.RegionName;
            block.X = (ushort)(r.RegionLocX / Constants.RegionSize);
            block.Y = (ushort)(r.RegionLocY / Constants.RegionSize);
            block.SizeX = (ushort)r.RegionSizeX;
            block.SizeY = (ushort)r.RegionSizeY;

        }

        public Hashtable OnHTTPThrottled(Hashtable keysvals)
        {
            Hashtable reply = new Hashtable();
            int statuscode = 500;
            reply["str_response_string"] = "";
            reply["int_response_code"] = statuscode;
            reply["content_type"] = "text/plain";
            return reply;
        }

        public void OnHTTPGetMapImage(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;
            if (request.HttpMethod != "GET" || _scene.RegionInfo.RegionSettings.TerrainImageID == UUID.Zero)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            byte[] jpeg = null;
            _log.Debug("[WORLD MAP]: Sending map image jpeg");

            if (myMapImageJPEG.Length == 0)
            {
                MemoryStream imgstream = null;
                Bitmap mapTexture = new Bitmap(1, 1);
                ManagedImage managedImage;
                Image image = mapTexture;

                try
                {
                    // Taking our jpeg2000 data, decoding it, then saving it to a byte array with regular jpeg data

                    imgstream = new MemoryStream();

                    // non-async because we know we have the asset immediately.
                    AssetBase mapasset = _scene.AssetService.Get(_scene.RegionInfo.RegionSettings.TerrainImageID.ToString());
                    if(mapasset == null || mapasset.Data == null || mapasset.Data.Length == 0)
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        return;
                    }

                    // Decode image to System.Drawing.Image
                    if (OpenJPEG.DecodeToImage(mapasset.Data, out managedImage, out image))
                    {
                        // Save to bitmap
                        mapTexture = new Bitmap(image);

                        EncoderParameters myEncoderParameters = new EncoderParameters();
                        myEncoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, 95L);

                        // Save bitmap to stream
                        mapTexture.Save(imgstream, GetEncoderInfo("image/jpeg"), myEncoderParameters);

                        // Write the stream to a byte array for output
                        jpeg = imgstream.ToArray();
                        myMapImageJPEG = jpeg;
                    }
                }
                catch (Exception e)
                {
                    // Dummy!
                    _log.Warn("[WORLD MAP]: Unable to generate Map image" + e.Message);
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }
                finally
                {
                    // Reclaim memory, these are unmanaged resources
                    // If we encountered an exception, one or more of these will be null
                    if (mapTexture != null)
                        mapTexture.Dispose();

                    if (image != null)
                        image.Dispose();

                    if (imgstream != null)
                        imgstream.Dispose();
                }
            }
            else
            {
                // Use cached version so we don't have to loose our mind
                jpeg = myMapImageJPEG;
            }
            if(jpeg == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.RawBuffer = jpeg;
            //response.RawBuffer = Convert.ToBase64String(jpeg);
            response.ContentType = "image/jpeg";
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        // From msdn
        private static ImageCodecInfo GetEncoderInfo(string mimeType)
        {
            ImageCodecInfo[] encoders;
            encoders = ImageCodecInfo.GetImageEncoders();
            for (int j = 0; j < encoders.Length; ++j)
            {
                if (encoders[j].MimeType == mimeType)
                    return encoders[j];
            }
            return null;
        }

        /// <summary>
        /// Export the world map
        /// </summary>
        /// <param name="fileName"></param>
        public void HandleExportWorldMapConsoleCommand(string module, string[] cmdparams)
        {
            if (_scene.ConsoleScene() == null)
            {
                // FIXME: If console region is root then this will be printed by every module.  Currently, there is no
                // way to prevent this, short of making the entire module shared (which is complete overkill).
                // One possibility is to return a bool to signal whether the module has completely handled the command
                _log.InfoFormat("[WORLD MAP]: Please change to a specific region in order to export its world map");
                return;
            }

            if (_scene.ConsoleScene() != _scene)
                return;

            string exportPath;

            if (cmdparams.Length > 1)
                exportPath = cmdparams[1];
            else
                exportPath = DEFAULT_WORLD_MAP_EXPORT_PATH;

            _log.InfoFormat(
                "[WORLD MAP]: Exporting world map for {0} to {1}", _regionName, exportPath);

            // assumed this is 1m less than next grid line
            int regionsView = (int)_scene.MaxRegionViewDistance;

            int regionSizeX = (int)_regionSizeX;
            int regionSizeY = (int)_regionSizeY;

            int regionX = (int)_regionGlobalX;
            int regionY = (int)_regionGlobalY;

            int startX = regionX - regionsView;
            int startY = regionY - regionsView;

            int endX = regionX + regionSizeX + regionsView;
            int endY = regionY + regionSizeY + regionsView;

            int spanX = endX - startX + 2;
            int spanY = endY - startY + 2;

            Bitmap mapTexture = new Bitmap(spanX, spanY);
            ImageAttributes gatrib = new ImageAttributes();
            gatrib.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);

            Graphics g = Graphics.FromImage(mapTexture);           
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;

            SolidBrush sea = new SolidBrush(Color.DarkBlue);
            g.FillRectangle(sea, 0, 0, spanX, spanY);
            sea.Dispose();

            Font drawFont = new Font("Arial", 32);
            SolidBrush drawBrush = new SolidBrush(Color.White);

            List<GridRegion> regions = _scene.GridService.GetRegionRange(_scene.RegionInfo.ScopeID,
                    startX, startY, endX, endY);

            startX--;
            startY--;

            bool doneLocal = false;
            string filename = "MAP-" + _scene.RegionInfo.RegionID.ToString() + ".png";
            try
            {
                using(Image localMap = Bitmap.FromFile(filename))
                {
                    int x = regionX - startX;
                    int y = regionY - startY;
                    int sx = regionSizeX;
                    int sy = regionSizeY;
                    // y origin is top
                    g.DrawImage(localMap,new Rectangle(x, spanY - y - sy, sx, sy),
                                0, 0, localMap.Width, localMap.Height, GraphicsUnit.Pixel, gatrib);

                    if(_exportPrintRegionName)
                    {
                        SizeF stringSize = g.MeasureString(_regionName, drawFont);
                        g.DrawString(_regionName, drawFont, drawBrush, x + 30, spanY - y - 30 - stringSize.Height);
                    }
                }
                doneLocal = true;
            }
            catch {}

            if(regions.Count > 0)
            {
                ManagedImage managedImage = null;
                Image image = null;

                foreach(GridRegion r in regions)
                {
                    if(r.TerrainImage == UUID.Zero)
                        continue;

                    if(doneLocal && r.RegionHandle == _regionHandle)
                        continue;

                    AssetBase texAsset = _scene.AssetService.Get(r.TerrainImage.ToString());
                    if(texAsset == null)
                        continue;

                    if(OpenJPEG.DecodeToImage(texAsset.Data, out managedImage, out image))
                    {
                        int x = r.RegionLocX - startX;
                        int y = r.RegionLocY - startY;
                        int sx = r.RegionSizeX;
                        int sy = r.RegionSizeY;
                        // y origin is top
                        g.DrawImage(image,new Rectangle(x, spanY - y - sy, sx, sy),
                                0, 0, image.Width, image.Height, GraphicsUnit.Pixel, gatrib);

                        if(_exportPrintRegionName && r.RegionHandle == _regionHandle)
                        {
                            SizeF stringSize = g.MeasureString(r.RegionName, drawFont);
                            g.DrawString(r.RegionName, drawFont, drawBrush, x + 30, spanY - y - 30 - stringSize.Height);
                        }
                    }
                }

                if(image != null)
                    image.Dispose();

            }

            if(_exportPrintScale)
            {
                string drawString = string.Format("{0}m x {1}m", spanX, spanY);
                g.DrawString(drawString, drawFont, drawBrush, 30, 30);
            }

            drawBrush.Dispose();
            drawFont.Dispose();
            gatrib.Dispose();
            g.Dispose();

            mapTexture.Save(exportPath, ImageFormat.Jpeg);
            mapTexture.Dispose();

            _log.InfoFormat(
                "[WORLD MAP]: Successfully exported world map for {0} to {1}",
                _regionName, exportPath);
        }

        public void HandleGenerateMapConsoleCommand(string module, string[] cmdparams)
        {
            Scene consoleScene = _scene.ConsoleScene();

            if (consoleScene != null && consoleScene != _scene)
                return;

            _scene.RegenerateMaptileAndReregister(this, null);
        }

        public void HandleRemoteMapItemRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            // Service 6 (MAP_ITEM_AGENTS_LOCATION; green dots)

            OSDMap responsemap = new OSDMap();
            int tc = Environment.TickCount;
            OSD osdhash = OSD.FromString(Util.Md5Hash(_regionName + tc.ToString()));

            if (_scene.GetRootAgentCount() == 0)
            {
                OSDMap responsemapdata = new OSDMap();
                responsemapdata["X"] = OSD.FromInteger((int)(_regionGlobalX + 1));
                responsemapdata["Y"] = OSD.FromInteger((int)(_regionGlobalY + 1));
                responsemapdata["ID"] = OSD.FromUUID(UUID.Zero);
                responsemapdata["Name"] = osdhash;
                responsemapdata["Extra"] = OSD.FromInteger(0);
                responsemapdata["Extra2"] = OSD.FromInteger(0);
                OSDArray responsearr = new OSDArray();
                responsearr.Add(responsemapdata);

                responsemap["6"] = responsearr;
            }
            else
            {
                OSDArray responsearr = new OSDArray(); // Don't preallocate. MT (_scene.GetRootAgentCount());
                _scene.ForEachRootScenePresence(delegate (ScenePresence sp)
                {
                    if (sp.IsNPC || sp.IsDeleted || sp.IsInTransit)
                        return;
                    OSDMap responsemapdata = new OSDMap();
                    responsemapdata["X"] = OSD.FromInteger((int)(_regionGlobalX + sp.AbsolutePosition.X));
                    responsemapdata["Y"] = OSD.FromInteger((int)(_regionGlobalY + sp.AbsolutePosition.Y));
                    responsemapdata["ID"] = OSD.FromUUID(UUID.Zero);
                    responsemapdata["Name"] = osdhash;
                    responsemapdata["Extra"] = OSD.FromInteger(1);
                    responsemapdata["Extra2"] = OSD.FromInteger(0);
                    responsearr.Add(responsemapdata);
                });
                responsemap["6"] = responsearr;
            }

            // Service 7/10 (MAP_ITEM_LAND_FOR_SALE/ADULT)

            ILandChannel landChannel = _scene.LandChannel;
            List<ILandObject> parcels = landChannel.AllParcels();

            if (parcels != null && parcels.Count >= 0)
            {
                OSDArray responsearr = new OSDArray(parcels.Count);
                foreach (ILandObject parcel_interface in parcels)
                {
                    // Play it safe
                    if (!(parcel_interface is LandObject))
                        continue;

                    LandObject land = (LandObject)parcel_interface;
                    LandData parcel = land.LandData;

                    // Show land for sale
                    if ((parcel.Flags & (uint)ParcelFlags.ForSale) == (uint)ParcelFlags.ForSale)
                    {
                        float x = _regionGlobalX + land.CenterPoint.X;
                        float y = _regionGlobalY + land.CenterPoint.Y;

                        OSDMap responsemapdata = new OSDMap();
                        responsemapdata["X"] = OSD.FromInteger((int)x);
                        responsemapdata["Y"] = OSD.FromInteger((int)y);
                        // responsemapdata["Z"] = OSD.FromInteger((int)_scene.GetGroundHeight(x,y));
                        responsemapdata["ID"] = OSD.FromUUID(land.FakeID);
                        responsemapdata["Name"] = OSD.FromString(parcel.Name);
                        responsemapdata["Extra"] = OSD.FromInteger(parcel.Area);
                        responsemapdata["Extra2"] = OSD.FromInteger(parcel.SalePrice);
                        responsearr.Add(responsemapdata);
                    }
                }

                if(responsearr.Count > 0)
                {
                    if(_scene.RegionInfo.RegionSettings.Maturity == 2)
                        responsemap["10"] = responsearr;
                    else
                    responsemap["7"] = responsearr;
                }
            }

            if (_scene.RegionInfo.RegionSettings.TelehubObject != UUID.Zero)
            {
                SceneObjectGroup sog = _scene.GetSceneObjectGroup(_scene.RegionInfo.RegionSettings.TelehubObject);
                if (sog != null)
                {
                    OSDArray responsearr = new OSDArray();
                    OSDMap responsemapdata = new OSDMap();
                    responsemapdata["X"] = OSD.FromInteger((int)(_regionGlobalX + sog.AbsolutePosition.X));
                    responsemapdata["Y"] = OSD.FromInteger((int)(_regionGlobalY + sog.AbsolutePosition.Y));
                    // responsemapdata["Z"] = OSD.FromInteger((int)_scene.GetGroundHeight(x,y));
                    responsemapdata["ID"] = OSD.FromUUID(sog.UUID);
                    responsemapdata["Name"] = OSD.FromString(sog.Name);
                    responsemapdata["Extra"] = OSD.FromInteger(0); // color (unused)
                    responsemapdata["Extra2"] = OSD.FromInteger(0); // 0 = telehub / 1 = infohub
                    responsearr.Add(responsemapdata);

                    responsemap["1"] = responsearr;
                }
            }

            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(responsemap);
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        public void GenerateMaptile()
        {
            // Cannot create a map for a nonexistent heightmap
            if (_scene.Heightmap == null)
                return;

            if (_mapImageGenerator == null)
            {
                Console.WriteLine("No map image generator available for {0}", _scene.Name);
                return;
            }
            _log.DebugFormat("[WORLD MAP]: Generating map image for {0}", _scene.Name);

            using (Bitmap mapbmp = _mapImageGenerator.CreateMapTile())
            {
                GenerateMaptile(mapbmp);

                if (_mapImageServiceModule != null)
                    _mapImageServiceModule.UploadMapTile(_scene, mapbmp);
            }
        }

        public void DeregisterMap()
        {
            //if (_mapImageServiceModule != null)
            //    _mapImageServiceModule.RemoveMapTiles(_scene);
        }

        private void GenerateMaptile(Bitmap mapbmp)
        {
            bool needRegionSave = false;

            // remove old assets
            UUID lastID = _scene.RegionInfo.RegionSettings.TerrainImageID;
            if (lastID != UUID.Zero)
            {
                _scene.AssetService.Delete(lastID.ToString());
                _scene.RegionInfo.RegionSettings.TerrainImageID = UUID.Zero;
                myMapImageJPEG = new byte[0];
                needRegionSave = true;
            }

            lastID = _scene.RegionInfo.RegionSettings.ParcelImageID;
            if (lastID != UUID.Zero)
            {
                _scene.AssetService.Delete(lastID.ToString());
                _scene.RegionInfo.RegionSettings.ParcelImageID = UUID.Zero;
                needRegionSave = true;
            }

            if(mapbmp != null)
            {
                try
                {
                    byte[] data;

                    // if large region limit its size since new viewers will not use it
                    // but it is still usable for ossl
                    if(_scene.RegionInfo.RegionSizeX > Constants.RegionSize ||
                            _scene.RegionInfo.RegionSizeY > Constants.RegionSize)
                    {
                        int bx = mapbmp.Width;
                        int by = mapbmp.Height;
                        int mb = bx;
                        if(mb < by)
                            mb = by;
                        if(mb > Constants.RegionSize && mb > 0)
                        {
                            float scale = Constants.RegionSize / (float)mb;
                            using(Bitmap scaledbmp = Util.ResizeImageSolid(mapbmp, (int)(bx * scale), (int)(by * scale)))
                                data = OpenJPEG.EncodeFromImage(scaledbmp, true);
                        }
                        else
                            data = OpenJPEG.EncodeFromImage(mapbmp, true);
                    }
                    else
                        data = OpenJPEG.EncodeFromImage(mapbmp, true);

                    if (data != null && data.Length > 0)
                    {
                        UUID terrainImageID = UUID.Random();

                        AssetBase asset = new AssetBase(
                            terrainImageID,
                            "terrainImage_" + _scene.RegionInfo.RegionID.ToString(),
                            (sbyte)AssetType.Texture,
                            _scene.RegionInfo.RegionID.ToString())
                        {
                            Data = data,
                            Description = _regionName,
                            Local = _localV1MapAssets,
                            Temporary = false,
                            Flags = AssetFlags.Maptile
                        };

                        // Store the new one
                        _log.DebugFormat("[WORLD MAP]: Storing map image {0} for {1}", asset.ID, _regionName);

                        _scene.AssetService.Store(asset);

                        _scene.RegionInfo.RegionSettings.TerrainImageID = terrainImageID;
                        needRegionSave = true;
                    }
                }
                catch (Exception e)
                {
                    _log.Error("[WORLD MAP]: Failed generating terrain map: " + e);
                }
            }

            // V2/3 still seem to need this, or we are missing something somewhere
            byte[] overlay = GenerateOverlay();
            if (overlay != null)
            {
                UUID parcelImageID = UUID.Random();

                AssetBase parcels = new AssetBase(
                    parcelImageID,
                    "parcelImage_" + _scene.RegionInfo.RegionID.ToString(),
                    (sbyte)AssetType.Texture,
                    _scene.RegionInfo.RegionID.ToString())
                {
                    Data = overlay,
                    Description = _regionName,
                    Temporary = false,
                    Local = _localV1MapAssets,
                    Flags = AssetFlags.Maptile
                };

                _scene.AssetService.Store(parcels);

                _scene.RegionInfo.RegionSettings.ParcelImageID = parcelImageID;
                needRegionSave = true;
            }

            if (needRegionSave)
                _scene.RegionInfo.RegionSettings.Save();
        }

        private void MakeRootAgent(ScenePresence avatar)
        {
            lock (_rootAgents)
            {
                if (!_rootAgents.Contains(avatar.UUID))
                {
                    _rootAgents.Add(avatar.UUID);
                }
            }
        }

        private void MakeChildAgent(ScenePresence avatar)
        {
            lock (_rootAgents)
            {
                _rootAgents.Remove(avatar.UUID);
            }

            lock (_mapBlockRequestEvent)
            {
                if (_mapBlockRequests.ContainsKey(avatar.UUID))
                    _mapBlockRequests.Remove(avatar.UUID);
            }
        }

        public void OnRegionUp(GridRegion otherRegion)
        {
            ulong regionhandle = otherRegion.RegionHandle;
            string httpserver = otherRegion.ServerURI + "MAP/MapItems/" + regionhandle.ToString();

             _blacklistedregions.Remove(regionhandle);
             _blacklistedurls.Remove(httpserver);
        }

        private byte[] GenerateOverlay()
        {
            int landTileSize = LandManagementModule.LandUnit;

            // These need to be ints for bitmap generation
            int regionSizeX = (int)_scene.RegionInfo.RegionSizeX;
            int regionLandTilesX = regionSizeX / landTileSize;

            int regionSizeY = (int)_scene.RegionInfo.RegionSizeY;
            int regionLandTilesY = regionSizeY / landTileSize;

            bool landForSale = false;
            ILandObject land;

            // scan terrain avoiding potencial merges of large bitmaps
            //TODO  create the sell bitmap at landchannel / landmaster ?
            // and auction also, still not suported

            bool[,] saleBitmap = new bool[regionLandTilesX, regionLandTilesY];
            for (int x = 0, xx = 0; x < regionLandTilesX; x++ ,xx += landTileSize)
            {
                for (int y = 0, yy = 0; y < regionLandTilesY; y++, yy += landTileSize)
                {
                    land = _scene.LandChannel.GetLandObject(xx, yy);
                    if (land != null && (land.LandData.Flags & (uint)ParcelFlags.ForSale) != 0)
                    {
                        saleBitmap[x, y] = true;
                        landForSale = true;
                    }
                    else
                        saleBitmap[x, y] = false;
                }
            }

            if (!landForSale)
            {
                _log.DebugFormat("[WORLD MAP]: Region {0} has no parcels for sale, not generating overlay", _regionName);
                return null;
            }

            _log.DebugFormat("[WORLD MAP]: Region {0} has parcels for sale, generating overlay", _regionName);

            using (Bitmap overlay = new Bitmap(regionSizeX, regionSizeY))
            {
                Color background = Color.FromArgb(0, 0, 0, 0);

                using (Graphics g = Graphics.FromImage(overlay))
                {
                    using (SolidBrush transparent = new SolidBrush(background))
                        g.FillRectangle(transparent, 0, 0, regionSizeX, regionSizeY);

                    // make it a bit transparent
                    using (SolidBrush yellow = new SolidBrush(Color.FromArgb(192, 249, 223, 9)))
                    {
                        for (int x = 0; x < regionLandTilesX; x++)
                        {
                            for (int y = 0; y < regionLandTilesY; y++)
                            {
                                if (saleBitmap[x, y])
                                    g.FillRectangle(
                                        yellow,
                                        x * landTileSize,
                                        regionSizeX - landTileSize - y * landTileSize,
                                        landTileSize,
                                        landTileSize);
                            }
                        }
                    }
                }

                try
                {
                    return OpenJPEG.EncodeFromImage(overlay, false);
                }
                catch (Exception e)
                {
                    _log.DebugFormat("[WORLD MAP]: Error creating parcel overlay: " + e.ToString());
                }
            }

            return null;
        }
    }

    public class MapRequestState
    {
        public UUID agentID;
        public uint flags;
        public uint EstateID;
        public bool godlike;
        public uint itemtype;
        public ulong regionhandle;
    }

    public struct MapBlockRequestData
    {
        public IClientAPI client;
        public int minX;
        public int minY;
        public int maxX;
        public int maxY;
        public uint flags;
    }
}
