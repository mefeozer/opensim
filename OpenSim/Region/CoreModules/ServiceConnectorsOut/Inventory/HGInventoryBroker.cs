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
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using OpenSim.Framework;

using OpenSim.Server.Base;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HGInventoryBroker")]
    public class HGInventoryBroker : ISharedRegionModule, IInventoryService
    {
        private static readonly ILog _log = LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private static bool _Enabled = false;

        private const int CONNECTORS_CACHE_EXPIRE = 60000; // 1 minute

        private readonly List<Scene> _Scenes = new List<Scene>();

        private IInventoryService _LocalGridInventoryService;
        private readonly ExpiringCacheOS<string, IInventoryService> _connectors = new ExpiringCacheOS<string, IInventoryService>();

        // A cache of userIDs --> ServiceURLs, for HGBroker only
        protected readonly ConcurrentDictionary<UUID, string> _InventoryURLs = new ConcurrentDictionary<UUID, string>();
        private readonly InventoryCache _Cache = new InventoryCache();

        /// <summary>
        /// Used to serialize inventory requests.
        /// </summary>
        private readonly object _Lock = new object();

        protected IUserManagement _UserManagement;
        protected IUserManagement UserManagementModule
        {
            get
            {
                if (_UserManagement == null)
                {
                    _UserManagement = _Scenes[0].RequestModuleInterface<IUserManagement>();

                    if (_UserManagement == null)
                        _log.ErrorFormat(
                            "[HG INVENTORY CONNECTOR]: Could not retrieve IUserManagement module from {0}",
                            _Scenes[0].RegionInfo.RegionName);
                }

                return _UserManagement;
            }
        }

        public Type ReplaceableInterface => null;

        public string Name => "HGInventoryBroker";

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("InventoryServices", "");
                if (name == Name)
                {
                    IConfig inventoryConfig = source.Configs["InventoryService"];
                    if (inventoryConfig == null)
                    {
                        _log.Error("[HG INVENTORY CONNECTOR]: InventoryService missing from OpenSim.ini");
                        return;
                    }

                    string localDll = inventoryConfig.GetString("LocalGridInventoryService",
                            string.Empty);
 
                    if (string.IsNullOrEmpty(localDll))
                    {
                        _log.Error("[HG INVENTORY CONNECTOR]: No LocalGridInventoryService named in section InventoryService");
                        //return;
                        throw new Exception("Unable to proceed. Please make sure your ini files in config-include are updated according to .example's");
                    }

                    object[] args = new object[] { source };
                    _LocalGridInventoryService =
                            ServerUtils.LoadPlugin<IInventoryService>(localDll,
                            args);

                    if (_LocalGridInventoryService == null)
                    {
                        _log.Error("[HG INVENTORY CONNECTOR]: Can't load local inventory service");
                        return;
                    }

                    _Enabled = true;
                    _log.InfoFormat("[HG INVENTORY CONNECTOR]: HG inventory broker enabled with inner connector of type {0}", _LocalGridInventoryService.GetType());
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _Scenes.Add(scene);

            scene.RegisterModuleInterface<IInventoryService>(this);

            if (_Scenes.Count == 1)
            {
                // FIXME: The local connector needs the scene to extract the UserManager.  However, it's not enabled so
                // we can't just add the region.  But this approach is super-messy.
                if (_LocalGridInventoryService is RemoteXInventoryServicesConnector)
                {
                    _log.DebugFormat(
                        "[HG INVENTORY BROKER]: Manually setting scene in RemoteXInventoryServicesConnector to {0}",
                        scene.RegionInfo.RegionName);

                    ((RemoteXInventoryServicesConnector)_LocalGridInventoryService).Scene = scene;
                }
                else if (_LocalGridInventoryService is LocalInventoryServicesConnector)
                {
                    _log.DebugFormat(
                        "[HG INVENTORY BROKER]: Manually setting scene in LocalInventoryServicesConnector to {0}",
                        scene.RegionInfo.RegionName);

                    ((LocalInventoryServicesConnector)_LocalGridInventoryService).Scene = scene;
                }
            }
            scene.EventManager.OnClientClosed += OnClientClosed;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _Scenes.Remove(scene);
            scene.EventManager.OnClientClosed -= OnClientClosed;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            _log.InfoFormat("[HG INVENTORY CONNECTOR]: Enabled HG inventory for region {0}", scene.RegionInfo.RegionName);

        }

        #region URL Cache

        void OnClientClosed(UUID clientID, Scene scene)
        {
            foreach (Scene s in _Scenes)
            {
                if(s.TryGetScenePresence(clientID, out ScenePresence sp) && !sp.IsChildAgent && sp.ControllingClient != null && sp.ControllingClient.IsActive)
                {
                    //_log.DebugFormat("[HG INVENTORY CACHE]: OnClientClosed in {0}, but user {1} still in sim. Keeping inventoryURL in cache",
                    //        scene.RegionInfo.RegionName, clientID);
                    return;
                }
            }

            _InventoryURLs.TryRemove(clientID, out string dummy);
            _Cache.RemoveAll(clientID);
        }

        /// <summary>
        /// Gets the user's inventory URL from its serviceURLs, if the user is foreign,
        /// and sticks it in the cache
        /// </summary>
        /// <param name="userID"></param>
        private string CacheInventoryServiceURL(UUID userID)
        {
            if (UserManagementModule != null && !UserManagementModule.IsLocalGridUser(userID))
            {
                // The user is not local; let's cache its service URL
                string inventoryURL = string.Empty;
                ScenePresence sp = null;
                foreach (Scene scene in _Scenes)
                {
                    scene.TryGetScenePresence(userID, out sp);
                    if (sp != null)
                    {
                        AgentCircuitData aCircuit = scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
                        if (aCircuit == null)
                            return null;
                        if (aCircuit.ServiceURLs == null)
                            return null;

                        if (aCircuit.ServiceURLs.ContainsKey("InventoryServerURI"))
                        {
                            inventoryURL = aCircuit.ServiceURLs["InventoryServerURI"].ToString();
                            if (inventoryURL != null && !string.IsNullOrEmpty(inventoryURL))
                            {
                                inventoryURL = inventoryURL.Trim(new char[] { '/' });
                                _InventoryURLs[userID] = inventoryURL;
                                _log.DebugFormat("[HG INVENTORY CONNECTOR]: Added {0} to the cache of inventory URLs", inventoryURL);
                                return inventoryURL;
                            }
                        }
//                        else
//                        {
//                            _log.DebugFormat("[HG INVENTORY CONNECTOR]: User {0} does not have InventoryServerURI. OH NOES!", userID);
//                            return;
//                        }
                    }
                }
                if (sp == null)
                {
                    inventoryURL = UserManagementModule.GetUserServerURL(userID, "InventoryServerURI");
                    if (!string.IsNullOrEmpty(inventoryURL))
                    {
                        inventoryURL = inventoryURL.Trim(new char[] { '/' });
                        _InventoryURLs[userID] = inventoryURL;
                        _log.DebugFormat("[HG INVENTORY CONNECTOR]: Added {0} to the cache of inventory URLs", inventoryURL);
                        return inventoryURL;
                    }
                }
            }
            return null;
        }

        public string GetInventoryServiceURL(UUID userID)
        {
            if (_InventoryURLs.TryGetValue(userID, out string value))
                return value;

             return CacheInventoryServiceURL(userID);
        }

        #endregion

        #region IInventoryService

        public bool CreateUserInventory(UUID userID)
        {
            lock (_Lock)
                return _LocalGridInventoryService.CreateUserInventory(userID);
        }

        public List<InventoryFolderBase> GetInventorySkeleton(UUID userID)
        {
            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetInventorySkeleton(userID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetInventorySkeleton(userID);
        }

        public InventoryFolderBase GetRootFolder(UUID userID)
        {
            //_log.DebugFormat("[HG INVENTORY CONNECTOR]: GetRootFolder for {0}", userID);
            InventoryFolderBase root = _Cache.GetRootFolder(userID);
            if (root != null)
                return root;

            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetRootFolder(userID);

            IInventoryService connector = GetConnector(invURL);

            root = connector.GetRootFolder(userID);

            _Cache.Cache(userID, root);

            return root;
        }

        public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        {
            //_log.DebugFormat("[HG INVENTORY CONNECTOR]: GetFolderForType {0} type {1}", userID, type);
            InventoryFolderBase f = _Cache.GetFolderForType(userID, type);
            if (f != null)
                return f;

            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetFolderForType(userID, type);

            IInventoryService connector = GetConnector(invURL);

            f = connector.GetFolderForType(userID, type);

            _Cache.Cache(userID, type, f);

            return f;
        }

        public InventoryCollection GetFolderContent(UUID userID, UUID folderID)
        {
            //_log.Debug("[HG INVENTORY CONNECTOR]: GetFolderContent " + folderID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetFolderContent(userID, folderID);

            InventoryCollection c = _Cache.GetFolderContent(userID, folderID);
            if (c != null)
            {
                _log.Debug("[HG INVENTORY CONNECTOR]: GetFolderContent found content in cache " + folderID);
                return c;
            }

            IInventoryService connector = GetConnector(invURL);

            return connector.GetFolderContent(userID, folderID);
        }

        public InventoryCollection[] GetMultipleFoldersContent(UUID userID, UUID[] folderIDs)
        {
            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetMultipleFoldersContent(userID, folderIDs);

            else
            {
                InventoryCollection[] coll = new InventoryCollection[folderIDs.Length];
                int i = 0;
                foreach (UUID fid in folderIDs)
                    coll[i++] = GetFolderContent(userID, fid);

                return coll;
            }
        }

        public List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID)
        {
            //_log.Debug("[HG INVENTORY CONNECTOR]: GetFolderItems " + folderID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetFolderItems(userID, folderID);

            List<InventoryItemBase> items = _Cache.GetFolderItems(userID, folderID);
            if (items != null)
            {
                _log.Debug("[HG INVENTORY CONNECTOR]: GetFolderItems found items in cache " + folderID);
                return items;
            }

            IInventoryService connector = GetConnector(invURL);

            return connector.GetFolderItems(userID, folderID);
        }

        public bool AddFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //_log.Debug("[HG INVENTORY CONNECTOR]: AddFolder " + folder.ID);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.AddFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.AddFolder(folder);
        }

        public bool UpdateFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //_log.Debug("[HG INVENTORY CONNECTOR]: UpdateFolder " + folder.ID);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.UpdateFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.UpdateFolder(folder);
        }

        public bool DeleteFolders(UUID ownerID, List<UUID> folderIDs)
        {
            if (folderIDs == null)
                return false;
            if (folderIDs.Count == 0)
                return false;

            //_log.Debug("[HG INVENTORY CONNECTOR]: DeleteFolders for " + ownerID);

            string invURL = GetInventoryServiceURL(ownerID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.DeleteFolders(ownerID, folderIDs);

            IInventoryService connector = GetConnector(invURL);

            return connector.DeleteFolders(ownerID, folderIDs);
        }

        public bool MoveFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //_log.Debug("[HG INVENTORY CONNECTOR]: MoveFolder for " + folder.Owner);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.MoveFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.MoveFolder(folder);
        }

        public bool PurgeFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //_log.Debug("[HG INVENTORY CONNECTOR]: PurgeFolder for " + folder.Owner);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.PurgeFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.PurgeFolder(folder);
        }

        public bool AddItem(InventoryItemBase item)
        {
            if (item == null)
                return false;

            //_log.Debug("[HG INVENTORY CONNECTOR]: AddItem " + item.ID);

            string invURL = GetInventoryServiceURL(item.Owner);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.AddItem(item);

            IInventoryService connector = GetConnector(invURL);

            return connector.AddItem(item);
        }

        public bool UpdateItem(InventoryItemBase item)
        {
            if (item == null)
                return false;

            //_log.Debug("[HG INVENTORY CONNECTOR]: UpdateItem " + item.ID);

            string invURL = GetInventoryServiceURL(item.Owner);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.UpdateItem(item);

            IInventoryService connector = GetConnector(invURL);

            return connector.UpdateItem(item);
        }

        public bool MoveItems(UUID ownerID, List<InventoryItemBase> items)
        {
            if (items == null)
                return false;
            if (items.Count == 0)
                return true;

            //_log.Debug("[HG INVENTORY CONNECTOR]: MoveItems for " + ownerID);

            string invURL = GetInventoryServiceURL(ownerID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.MoveItems(ownerID, items);

            IInventoryService connector = GetConnector(invURL);

            return connector.MoveItems(ownerID, items);
        }

        public bool DeleteItems(UUID ownerID, List<UUID> itemIDs)
        {
            //_log.DebugFormat("[HG INVENTORY CONNECTOR]: Delete {0} items for user {1}", itemIDs.Count, ownerID);

            if (itemIDs == null)
                return false;
            if (itemIDs.Count == 0)
                return true;

            string invURL = GetInventoryServiceURL(ownerID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.DeleteItems(ownerID, itemIDs);

            IInventoryService connector = GetConnector(invURL);

            return connector.DeleteItems(ownerID, itemIDs);
        }

        public InventoryItemBase GetItem(UUID principalID, UUID itemID)
        {
            //_log.Debug("[HG INVENTORY CONNECTOR]: GetItem " + item.ID);

            string invURL = GetInventoryServiceURL(principalID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetItem(principalID, itemID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetItem(principalID, itemID);
        }

        public InventoryItemBase[] GetMultipleItems(UUID userID, UUID[] itemIDs)
        {
            if (itemIDs == null)
                return new InventoryItemBase[0];
            //_log.Debug("[HG INVENTORY CONNECTOR]: GetItem " + item.ID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetMultipleItems(userID, itemIDs);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetMultipleItems(userID, itemIDs);
        }

        public InventoryFolderBase GetFolder(UUID principalID, UUID folderID)
        {
            //_log.Debug("[HG INVENTORY CONNECTOR]: GetFolder " + folder.ID);

            string invURL = GetInventoryServiceURL(principalID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetFolder(principalID, folderID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetFolder(principalID, folderID);
        }

        public bool HasInventoryForUser(UUID userID)
        {
            return false;
        }

        public List<InventoryItemBase> GetActiveGestures(UUID userId)
        {
            return new List<InventoryItemBase>();
        }

        public int GetAssetPermissions(UUID userID, UUID assetID)
        {
            //_log.Debug("[HG INVENTORY CONNECTOR]: GetAssetPermissions " + assetID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (_Lock)
                    return _LocalGridInventoryService.GetAssetPermissions(userID, assetID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetAssetPermissions(userID, assetID);
        }

        #endregion

        private IInventoryService GetConnector(string url)
        {
            IInventoryService connector = null;
            lock (_connectors)
            {
                if (!_connectors.TryGetValue(url, out connector))
                {
                    // Still not as flexible as I would like this to be,
                    // but good enough for now
                    RemoteXInventoryServicesConnector rxisc = new RemoteXInventoryServicesConnector(url)
                    {
                        Scene = _Scenes[0]
                    };
                    connector = rxisc;
                }
                if (connector != null)
                    _connectors.AddOrUpdate(url, connector, CONNECTORS_CACHE_EXPIRE);
            }
            return connector;
        }
    }
}
