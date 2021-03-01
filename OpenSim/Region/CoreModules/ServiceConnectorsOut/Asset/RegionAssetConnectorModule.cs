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
using Nini.Config;
using System;
using System.Reflection;
using System.Collections.Generic;

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;


namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionAssetConnector")]
    public class RegionAssetConnector : ISharedRegionModule, IAssetService
    {
        private static readonly ILog _log = LogManager.GetLogger( MethodBase.GetCurrentMethod().DeclaringType);

        private delegate void AssetRetrievedEx(AssetBase asset);
        private bool _Enabled = false;

        private Scene _aScene;

        private IAssetCache _Cache;
        private IAssetService _localConnector;
        private IAssetService _HGConnector;
        private AssetPermissions _AssetPerms;

        //const int MAXSENDRETRIESLEN = 30;
        //private List<AssetBase>[] _sendRetries;
        //private List<string>[] _sendCachedRetries;
        //private System.Timers.Timer _retryTimer;

        //private int _retryCounter;
        //private bool _inRetries;

        private readonly Dictionary<string, List<AssetRetrievedEx>> _AssetHandlers = new Dictionary<string, List<AssetRetrievedEx>>();

        private ObjectJobEngine _requestQueue;

        public Type ReplaceableInterface => null;

        public string Name => "RegionAssetConnector";

        public RegionAssetConnector() {}

        public RegionAssetConnector(IConfigSource config)
        {
            Initialise(config);
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("AssetServices", "");
                if (name == Name)
                {
                    IConfig assetConfig = source.Configs["AssetService"];
                    if (assetConfig == null)
                    {
                        _log.Error("[REGIONASSETCONNECTOR]: AssetService missing from configuration files");
                        throw new Exception("Region asset connector init error");
                    }

                    string localGridConnector = assetConfig.GetString("LocalGridAssetService", string.Empty);
                    if(string.IsNullOrEmpty(localGridConnector))
                    {
                        _log.Error("[REGIONASSETCONNECTOR]: LocalGridAssetService missing from configuration files");
                        throw new Exception("Region asset connector init error");
                    }

                    object[] args = new object[] { source };

                    _localConnector = ServerUtils.LoadPlugin<IAssetService>(localGridConnector, args);
                    if (_localConnector == null)
                    {
                        _log.Error("[REGIONASSETCONNECTOR]: Fail to load local asset service " + localGridConnector);
                        throw new Exception("Region asset connector init error");
                    }

                    string HGConnector = assetConfig.GetString("HypergridAssetService", string.Empty);
                    if(!string.IsNullOrEmpty(HGConnector))
                    {
                        _HGConnector = ServerUtils.LoadPlugin<IAssetService>(HGConnector, args);
                        if (_HGConnector == null)
                        {
                            _log.Error("[REGIONASSETCONNECTOR]: Fail to load HG asset service " + HGConnector);
                            throw new Exception("Region asset connector init error");
                        }
                        IConfig hgConfig = source.Configs["HGAssetService"];
                        if (hgConfig != null)
                            _AssetPerms = new AssetPermissions(hgConfig);
                    }

                    _requestQueue = new ObjectJobEngine(AssetRequestProcessor, "GetAssetsWorkers", 2000, 2);
                    _Enabled = true;
                    _log.Info("[REGIONASSETCONNECTOR]: enabled");
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
            if (!_Enabled)
                return;

            _requestQueue.Dispose();
            _requestQueue = null;
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _aScene = scene;
            _aScene.RegisterModuleInterface<IAssetService>(this);
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            if (_Cache == null)
            {
                _Cache = scene.RequestModuleInterface<IAssetCache>();

                if (!(_Cache is ISharedRegionModule))
                    _Cache = null;
            }

            if(_HGConnector == null)
            {
                if (_Cache != null)
                    _log.InfoFormat("[REGIONASSETCONNECTOR]: active with cache for region {0}", scene.RegionInfo.RegionName);
                else
                    _log.InfoFormat("[REGIONASSETCONNECTOR]: active  without cache for region {0}", scene.RegionInfo.RegionName);
            }
            else
            {
                if (_Cache != null)
                    _log.InfoFormat("[REGIONASSETCONNECTOR]: active with HG and cache for region {0}", scene.RegionInfo.RegionName);
                else
                    _log.InfoFormat("[REGIONASSETCONNECTOR]: active with HG and without cache for region {0}", scene.RegionInfo.RegionName);
            }
        }


        private bool IsHG(string id)
        {
            return id.Length > 0 && (id[0] == 'h' || id[0] == 'H');
        }

        public AssetBase GetCached(string id)
        {
            AssetBase asset = null;
            if (_Cache != null)
                _Cache.Get(id, out asset);
            return asset;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private AssetBase GetFromLocal(string id)
        {
            return _localConnector.Get(id);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private AssetBase GetFromForeign(string id, string ForeignAssetService)
        {
            if (_HGConnector == null || string.IsNullOrEmpty(ForeignAssetService))
                return null;
            return _HGConnector.Get(id , ForeignAssetService, true);
        }

        public AssetBase GetForeign(string id)
        {
            int type = Util.ParseForeignAssetID(id, out string uri, out string uuidstr);
            if (type < 0)
                return null;

            AssetBase asset = null;
            if (_Cache != null)
            {
                 asset = _Cache.GetCached(uuidstr);
                if(asset != null)
                    return asset;
            }

            asset = GetFromLocal(uuidstr);
            if (asset != null || type == 0)
                return asset;
            return GetFromForeign(uuidstr, uri);
        }

        public AssetBase Get(string id)
        {
            //_log.DebugFormat("[HG ASSET CONNECTOR]: Get {0}", id);
            AssetBase asset = null;
            if (IsHG(id))
            {
                asset = GetForeign(id);
                if (asset != null)
                {
                    // Now store it locally, if allowed
                    if (_AssetPerms != null && !_AssetPerms.AllowedImport(asset.Type))
                        return null;
                    Store(asset);
                }
            }
            else
            {
                if (_Cache != null)
                {
                    if(!_Cache.Get(id, out asset))
                        return null;
                    if (asset != null)
                        return asset;
                }
                asset = GetFromLocal(id);
                if(_Cache != null)
                {
                    if(asset == null)
                        _Cache.CacheNegative(id);
                    else
                        _Cache.Cache(asset);
                }
            }
            return asset;
        }

        public AssetBase Get(string id, string ForeignAssetService, bool StoreOnLocalGrid)
        {
            // assumes id and ForeignAssetService are valid and resolved
            AssetBase asset = null;
            if (_Cache != null)
            {
                asset = _Cache.GetCached(id);
                if (asset != null)
                    return asset;
            }

            asset = GetFromLocal(id);
            if (asset == null)
            {
                asset = GetFromForeign(id, ForeignAssetService);
                if (asset != null)
                {
                    if (_AssetPerms != null && !_AssetPerms.AllowedImport(asset.Type))
                    {
                        if (_Cache != null)
                            _Cache.CacheNegative(id);
                        return null;
                    }
                    if(StoreOnLocalGrid)
                        Store(asset);
                    else if (_Cache != null)
                        _Cache.Cache(asset);
                }
                else if (_Cache != null)
                    _Cache.CacheNegative(id);
            }
            else if (_Cache != null)
                _Cache.Cache(asset);

            return asset;
        }

        public AssetMetadata GetMetadata(string id)
        {
            AssetBase asset = Get(id);
            if (asset != null)
                return asset.Metadata;
            return null;
        }

        public byte[] GetData(string id)
        {
            AssetBase asset = Get(id);
            if (asset != null)
                return asset.Data;
            return null;
        }

        public virtual bool Get(string id, object sender, AssetRetrieved callBack)
        {
            AssetBase asset = null;
            if (_Cache != null)
            {
                if (!_Cache.Get(id, out asset))
                    return false;
            }

            if (asset == null)
            {
                lock (_AssetHandlers)
                {
                    AssetRetrievedEx handlerEx = new AssetRetrievedEx(delegate (AssetBase _asset) { callBack(id, sender, _asset); });

                    List<AssetRetrievedEx> handlers;
                    if (_AssetHandlers.TryGetValue(id, out handlers))
                    {
                        // Someone else is already loading this asset. It will notify our handler when done.
                        handlers.Add(handlerEx);
                        return true;
                    }

                    handlers = new List<AssetRetrievedEx>();
                    handlers.Add(handlerEx);

                    _AssetHandlers.Add(id, handlers);
                    _requestQueue.Enqueue(id);
                }
            }
            else
            {
                if (asset != null && (asset.Data == null || asset.Data.Length == 0))
                    asset = null;
                callBack(id, sender, asset);
            }
            return true;
        }

        private void AssetRequestProcessor(object o)
        {
            string id = o as string;
            if(id == null)
                return;

            try
            {
                AssetBase a = Get(id);
                List<AssetRetrievedEx> handlers;
                lock (_AssetHandlers)
                {
                    handlers = _AssetHandlers[id];
                    _AssetHandlers.Remove(id);
                }

                if (handlers != null)
                {
                    Util.FireAndForget(x =>
                    {
                        foreach (AssetRetrievedEx h in handlers)
                        {
                            try
                            {
                                h.Invoke(a);
                            }
                            catch { }
                        }
                        handlers.Clear();
                    });
                }
            }
            catch { }
        }

        public bool[] AssetsExist(string[] ids)
        {
            int numHG = 0;
            foreach (string id in ids)
            {
                if (IsHG(id))
                    ++numHG;
            }
            if(numHG == 0)
                return _localConnector.AssetsExist(ids);
            else if (_HGConnector != null)
                return _HGConnector.AssetsExist(ids);
            return null;
        }

        private readonly string stringUUIDZero = UUID.Zero.ToString();
        public string Store(AssetBase asset)
        {
            string id;
            if (IsHG(asset.ID))
            {
                if (asset.Local || asset.Temporary)
                    return null;

                id = StoreForeign(asset);
                if (_Cache != null)
                {
                    if (!string.IsNullOrEmpty(id) && !id.Equals(stringUUIDZero))
                        _Cache.Cache(asset);
                }
                return id;
            }

            if (_Cache != null)
            {
                 _Cache.Cache(asset);
                if (asset.Local || asset.Temporary)
                    return asset.ID;
            }

            id = StoreLocal(asset);

            if (string.IsNullOrEmpty(id))
                return string.Empty;

            return id;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private string StoreForeign(AssetBase asset)
        {
            if (_HGConnector == null)
                return string.Empty;
            if (_AssetPerms != null && !_AssetPerms.AllowedExport(asset.Type))
                return string.Empty;
            return _HGConnector.Store(asset);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private string StoreLocal(AssetBase asset)
        {
            return _localConnector.Store(asset);
        }

        public bool UpdateContent(string id, byte[] data)
        {
            if (IsHG(id))
                return false;
            return _localConnector.UpdateContent(id, data);
        }

        public bool Delete(string id)
        {
            if (IsHG(id))
                return false;

            return _localConnector.Delete(id);
        }
    }
}
