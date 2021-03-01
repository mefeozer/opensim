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

using log4net;
using Mono.Addins;
using System;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Grid
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionGridServicesConnector")]
    public class RegionGridServicesConnector : ISharedRegionModule, IGridService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled = false;
        private GridInfo _ThisGridInfo;

        private IGridService _LocalGridService;
        private IGridService _RemoteGridService;

        private RegionInfoCache _RegionInfoCache;

        public RegionGridServicesConnector()
        {
        }

        public RegionGridServicesConnector(IConfigSource source)
        {
            InitialiseServices(source);
        }

        #region ISharedRegionmodule

        public Type ReplaceableInterface => null;

        public string Name => "RegionGridServicesConnector";

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("GridServices", string.Empty);
                if (name == Name)
                {
                    if(InitialiseServices(source))
                    {
                        _Enabled = true;
                        if(_RemoteGridService == null)
                            _log.Info("[REGION GRID CONNECTOR]: enabled in Standalone mode");
                        else
                            _log.Info("[REGION GRID CONNECTOR]: enabled in Grid mode");
                    }
                }
            }
        }

        private bool InitialiseServices(IConfigSource source)
        {
            IConfig gridConfig = source.Configs["GridService"];
            if (gridConfig == null)
            {
                _log.Error("[REGION GRID CONNECTOR]: GridService missing from OpenSim.ini");
                return false;
            }

            string serviceDll = gridConfig.GetString("LocalServiceModule", string.Empty);
            if (string.IsNullOrWhiteSpace(serviceDll))
            {
                _log.Error("[REGION GRID CONNECTOR]: No LocalServiceModule named in section GridService");
                return false;
            }
            
            object[] args = new object[] { source };
            _LocalGridService = ServerUtils.LoadPlugin<IGridService>(serviceDll, args);

            if (_LocalGridService == null)
            {
                _log.Error("[REGION GRID CONNECTOR]: failed to load LocalServiceModule");
                return false;
            }

            string networkConnector = gridConfig.GetString("NetworkConnector", string.Empty);
            if (!string.IsNullOrWhiteSpace(networkConnector))
            {
                _RemoteGridService = ServerUtils.LoadPlugin<IGridService>(networkConnector, args);
                if (_RemoteGridService == null)
                {
                    _log.Error("[REGION GRID CONNECTOR]: failed to load NetworkConnector");
                    return false;
                }
            }

            _RegionInfoCache = new RegionInfoCache();
            return true;
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            _ThisGridInfo = null;
        }

        public void AddRegion(Scene scene)
        {
            if (_Enabled)
            {
                scene.RegisterModuleInterface<IGridService>(this);
                if(_ThisGridInfo == null)
                    _ThisGridInfo = scene.SceneGridInfo;

                GridRegion r = new GridRegion(scene.RegionInfo);
                _RegionInfoCache.CacheLocal(r);

                scene.EventManager.OnRegionUp += OnRegionUp;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (_Enabled)
            {
                _RegionInfoCache.Remove(scene.RegionInfo.ScopeID, scene.RegionInfo.RegionHandle);
                scene.EventManager.OnRegionUp -= OnRegionUp;
            }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        #endregion

        private void OnRegionUp(GridRegion region)
        {
            // This shouldn't happen
            if (region == null || !_Enabled)
                return;

            _RegionInfoCache.CacheNearNeighbour(region.ScopeID, region);
        }

        #region IGridService

        public string RegisterRegion(UUID scopeID, GridRegion regionInfo)
        {
            string msg = _LocalGridService.RegisterRegion(scopeID, regionInfo);
            if (string.IsNullOrEmpty(msg) && _RemoteGridService != null)
                return _RemoteGridService.RegisterRegion(scopeID, regionInfo);

            return msg;
        }

        public bool DeregisterRegion(UUID regionID)
        {
            if (_LocalGridService.DeregisterRegion(regionID) && _RemoteGridService != null)
                return _RemoteGridService.DeregisterRegion(regionID);

            return false;
        }

        public List<GridRegion> GetNeighbours(UUID scopeID, UUID regionID)
        {
            if(_RemoteGridService == null)
                return _LocalGridService.GetNeighbours(scopeID, regionID);
            return _RemoteGridService.GetNeighbours(scopeID, regionID);
        }

        public GridRegion GetRegionByUUID(UUID scopeID, UUID regionID)
        {
            bool inCache = false;
            GridRegion rinfo = _RegionInfoCache.Get(scopeID, regionID, out inCache);
            if (inCache)
                return rinfo;

            rinfo = _LocalGridService.GetRegionByUUID(scopeID, regionID);
            if (rinfo != null)
            {
                _RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }

            if(_RemoteGridService != null)
            {
                rinfo = _RemoteGridService.GetRegionByUUID(scopeID, regionID);
                _RegionInfoCache.Cache(scopeID, rinfo);
            }
            return rinfo;
        }

        public GridRegion GetRegionByHandle(UUID scopeID, ulong regionhandle)
        {
            bool inCache = false;
            GridRegion rinfo = _RegionInfoCache.Get(scopeID, regionhandle, out inCache);
            if (inCache)
                return rinfo;

            rinfo = _LocalGridService.GetRegionByHandle(scopeID, regionhandle);
            if (rinfo != null)
            {
                _RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }
            if(_RemoteGridService != null)
            {
                rinfo = _RemoteGridService.GetRegionByHandle(scopeID, regionhandle);
                _RegionInfoCache.Cache(scopeID, rinfo);
            }
            return rinfo;
        }

        // Get a region given its base world coordinates (in meters).
        // NOTE: this is NOT 'get a region by some point in the region'. The coordinate MUST
        //     be the base coordinate of the region.
        // The coordinates are world coords (meters), NOT region units.
        public GridRegion GetRegionByPosition(UUID scopeID, int x, int y)
        {
            bool inCache = false;
            GridRegion rinfo = _RegionInfoCache.Get(scopeID, (uint)x, (uint)y, out inCache);
            if (inCache)
                return rinfo;

            rinfo = _LocalGridService.GetRegionByPosition(scopeID, x, y);
            if (rinfo != null)
            {
                // _log.DebugFormat("[REMOTE GRID CONNECTOR]: GetRegionByPosition. Found region {0} on local. Pos=<{1},{2}>, RegionHandle={3}",
                //    rinfo.RegionName, rinfo.RegionCoordX, rinfo.RegionCoordY, rinfo.RegionHandle);
                _RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }

            if(_RemoteGridService != null)
            {
                rinfo = _RemoteGridService.GetRegionByPosition(scopeID, x, y);
                if (rinfo == null)
                {
    //                uint regionX = Util.WorldToRegionLoc((uint)x);
    //                uint regionY = Util.WorldToRegionLoc((uint)y);
    //                _log.WarnFormat("[REMOTE GRID CONNECTOR]: Requested region {0}-{1} not found", regionX, regionY);
                }
                else
                {
                    _RegionInfoCache.Cache(scopeID, rinfo);

    //                _log.DebugFormat("[REMOTE GRID CONNECTOR]: GetRegionByPosition. Added region {0} to the cache. Pos=<{1},{2}>, RegionHandle={3}",
    //                    rinfo.RegionName, rinfo.RegionCoordX, rinfo.RegionCoordY, rinfo.RegionHandle);
                }
            }
            return rinfo;
        }

        public GridRegion GetRegionByName(UUID scopeID, string name)
        {
            bool inCache = false;
            GridRegion rinfo = _RegionInfoCache.Get(scopeID, name, out inCache);
            if (inCache)
                return rinfo;

            var ruri = new RegionURI(name, _ThisGridInfo);
            return GetRegionByURI(scopeID, ruri);
        }

        public GridRegion GetRegionByURI(UUID scopeID, RegionURI uri)
        {
            GridRegion rinfo = _LocalGridService.GetRegionByURI(scopeID, uri);
            if (rinfo != null)
            {
                _RegionInfoCache.Cache(scopeID, rinfo);
                return rinfo;
            }

            if (_RemoteGridService == null || !uri.IsLocalGrid)
                return rinfo;

            if (uri.HasRegionName)
                rinfo = _RemoteGridService.GetRegionByName(scopeID, uri.RegionName);
            else
            {
                rinfo = _RemoteGridService.GetDefaultRegions(UUID.Zero)[0];
                if (rinfo == null)
                    _log.Warn("[REMOTE GRID CONNECTOR] returned null default region");
                else
                    _log.WarnFormat("[REMOTE GRID CONNECTOR] returned default region {0}", rinfo.RegionName);
            }

            _RegionInfoCache.Cache(scopeID, rinfo);
            return rinfo;
        }

        public List<GridRegion> GetRegionsByName(UUID scopeID, string name, int maxNumber)
        {
            var ruri = new RegionURI(name, _ThisGridInfo);
            return GetRegionsByURI(scopeID, ruri, maxNumber);
        }

        public List<GridRegion> GetRegionsByURI(UUID scopeID, RegionURI uri, int maxNumber)
        {
            if(!uri.IsValid)
                return null;

            List<GridRegion> rinfo = _LocalGridService.GetRegionsByURI(scopeID, uri, maxNumber);
            //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetRegionsByName {0} found {1} regions", name, rinfo.Count);

            if (_RemoteGridService == null || !uri.IsLocalGrid)
                return rinfo;

            List<GridRegion> grinfo = null;
            if (!uri.HasRegionName && (rinfo == null || rinfo.Count == 0))
            {
                List<GridRegion> grinfos = _RemoteGridService.GetDefaultRegions(scopeID);
                if (grinfos == null || grinfos.Count == 0)
                    _log.Info("[REMOTE GRID CONNECTOR] returned no default regions");
                else
                {
                    _log.InfoFormat("[REMOTE GRID CONNECTOR] returned default regions {0}, ...", grinfos[0].RegionName);
                    // only return first
                    grinfo = new List<GridRegion>() { grinfos[0] };
                }
            }
            else
                grinfo = _RemoteGridService.GetRegionsByName(scopeID, uri.RegionName, maxNumber);

            if (grinfo != null)
            {
                //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetRegionsByName {0} found {1} regions", name, grinfo.Count);
                foreach (GridRegion r in grinfo)
                {
                    _RegionInfoCache.Cache(r);
                    if (rinfo.Find(delegate (GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                        rinfo.Add(r);
                }
            }

            return rinfo;
        }

        public virtual List<GridRegion> GetRegionRange(UUID scopeID, int xmin, int xmax, int ymin, int ymax)
        {
            List<GridRegion> rinfo = _LocalGridService.GetRegionRange(scopeID, xmin, xmax, ymin, ymax);
            //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetRegionRange {0} found {1} regions", name, rinfo.Count);
            if(_RemoteGridService != null)
            {
                List<GridRegion> grinfo = _RemoteGridService.GetRegionRange(scopeID, xmin, xmax, ymin, ymax);

                if (grinfo != null)
                {
                    //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetRegionRange {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        _RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetDefaultRegions(UUID scopeID)
        {
            List<GridRegion> rinfo = _LocalGridService.GetDefaultRegions(scopeID);
            //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetDefaultRegions {0} found {1} regions", name, rinfo.Count);
            if(_RemoteGridService != null)
            {
                List<GridRegion> grinfo = _RemoteGridService.GetDefaultRegions(scopeID);

                if (grinfo != null)
                {
                    //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetDefaultRegions {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        _RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetDefaultHypergridRegions(UUID scopeID)
        {
            List<GridRegion> rinfo = _LocalGridService.GetDefaultHypergridRegions(scopeID);
            //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetDefaultHypergridRegions {0} found {1} regions", name, rinfo.Count);
            if(_RemoteGridService != null)
            {
                List<GridRegion> grinfo = _RemoteGridService.GetDefaultHypergridRegions(scopeID);

                if (grinfo != null)
                {
                    //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetDefaultHypergridRegions {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        _RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetFallbackRegions(UUID scopeID, int x, int y)
        {
            List<GridRegion> rinfo = _LocalGridService.GetFallbackRegions(scopeID, x, y);
            //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetFallbackRegions {0} found {1} regions", name, rinfo.Count);
            if(_RemoteGridService != null)
            {
                List<GridRegion> grinfo = _RemoteGridService.GetFallbackRegions(scopeID, x, y);

                if (grinfo != null)
                {
                    //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetFallbackRegions {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        _RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public List<GridRegion> GetHyperlinks(UUID scopeID)
        {
            List<GridRegion> rinfo = _LocalGridService.GetHyperlinks(scopeID);
            //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Local GetHyperlinks {0} found {1} regions", name, rinfo.Count);
            if(_RemoteGridService != null)
            {
                List<GridRegion> grinfo = _RemoteGridService.GetHyperlinks(scopeID);

                if (grinfo != null)
                {
                    //_log.DebugFormat("[REMOTE GRID CONNECTOR]: Remote GetHyperlinks {0} found {1} regions", name, grinfo.Count);
                    foreach (GridRegion r in grinfo)
                    {
                        _RegionInfoCache.Cache(r);
                        if (rinfo.Find(delegate(GridRegion gr) { return gr.RegionID == r.RegionID; }) == null)
                            rinfo.Add(r);
                    }
                }
            }
            return rinfo;
        }

        public int GetRegionFlags(UUID scopeID, UUID regionID)
        {
            int flags = _LocalGridService.GetRegionFlags(scopeID, regionID);
            if (flags == -1 && _RemoteGridService != null)
                flags = _RemoteGridService.GetRegionFlags(scopeID, regionID);

            return flags;
        }

        public Dictionary<string, object> GetExtraFeatures()
        {
            Dictionary<string, object> extraFeatures;
            extraFeatures = _LocalGridService.GetExtraFeatures();

            if (extraFeatures.Count == 0 && _RemoteGridService != null)
                extraFeatures = _RemoteGridService.GetExtraFeatures();

            return extraFeatures;
        }
        #endregion
    }
}
