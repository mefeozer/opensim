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
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.Inventory
{
    public class InventoryServiceInConnector : ServiceConnector
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected IInventoryService _InventoryService;

        private readonly bool _doLookup = false;

        //private static readonly int INVENTORY_DEFAULT_SESSION_TIME = 30; // secs
        //private AuthedSessionCache _session_cache = new AuthedSessionCache(INVENTORY_DEFAULT_SESSION_TIME);

        private readonly string _userserver_url;
        protected string _ConfigName = "InventoryService";

        public InventoryServiceInConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            if (!string.IsNullOrEmpty(configName))
                _ConfigName = configName;

            IConfig serverConfig = config.Configs[_ConfigName];
            if (serverConfig == null)
                throw new Exception(string.Format("No section '{0}' in config file", _ConfigName));

            string inventoryService = serverConfig.GetString("LocalServiceModule",
                    string.Empty);

            if (string.IsNullOrEmpty(inventoryService))
                throw new Exception("No LocalServiceModule in config file");

            object[] args = new object[] { config };
            _InventoryService =
                    ServerUtils.LoadPlugin<IInventoryService>(inventoryService, args);

            _userserver_url = serverConfig.GetString("UserServerURI", string.Empty);
            _doLookup = serverConfig.GetBoolean("SessionAuthentication", false);

            AddHttpHandlers(server);
            _log.Debug("[INVENTORY HANDLER]: handlers initialized");
        }

        protected virtual void AddHttpHandlers(IHttpServer _httpServer)
        {
            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, List<InventoryFolderBase>>(
                "POST", "/SystemFolders/", GetSystemFolders, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, InventoryCollection>(
                "POST", "/GetFolderContent/", GetFolderContent, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/UpdateFolder/", _InventoryService.UpdateFolder, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/MoveFolder/", _InventoryService.MoveFolder, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/PurgeFolder/", _InventoryService.PurgeFolder, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<List<Guid>, bool>(
                    "POST", "/DeleteFolders/", DeleteFolders, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<List<Guid>, bool>(
                    "POST", "/DeleteItem/", DeleteItems, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, InventoryItemBase>(
                    "POST", "/QueryItem/", GetItem, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, InventoryFolderBase>(
                    "POST", "/QueryFolder/", GetFolder, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<Guid, bool>(
                    "POST", "/CreateInventory/", CreateUsersInventory, CheckTrustSource));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/NewFolder/", _InventoryService.AddFolder, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryFolderBase, bool>(
                    "POST", "/CreateFolder/", _InventoryService.AddFolder, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<InventoryItemBase, bool>(
                    "POST", "/NewItem/", _InventoryService.AddItem, CheckAuthSession));

            _httpServer.AddStreamHandler(
             new RestDeserialiseTrustedHandler<InventoryItemBase, bool>(
                 "POST", "/AddNewItem/", _InventoryService.AddItem, CheckTrustSource));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<Guid, List<InventoryItemBase>>(
                    "POST", "/GetItems/", GetFolderItems, CheckAuthSession));

            _httpServer.AddStreamHandler(
                new RestDeserialiseSecureHandler<List<InventoryItemBase>, bool>(
                    "POST", "/MoveItems/", MoveItems, CheckAuthSession));

            _httpServer.AddStreamHandler(new InventoryServerMoveItemsHandler(_InventoryService));


            // for persistent active gestures
            _httpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<Guid, List<InventoryItemBase>>
                    ("POST", "/ActiveGestures/", GetActiveGestures, CheckTrustSource));

            // WARNING: Root folders no longer just delivers the root and immediate child folders (e.g
            // system folders such as Objects, Textures), but it now returns the entire inventory skeleton.
            // It would have been better to rename this request, but complexities in the BaseHttpServer
            // (e.g. any http request not found is automatically treated as an xmlrpc request) make it easier
            // to do this for now.
            _httpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<Guid, List<InventoryFolderBase>>
                    ("POST", "/RootFolders/", GetInventorySkeleton, CheckTrustSource));

            _httpServer.AddStreamHandler(
                new RestDeserialiseTrustedHandler<InventoryItemBase, int>
                ("POST", "/AssetPermissions/", GetAssetPermissions, CheckTrustSource));

        }

        #region Wrappers for converting the Guid parameter

        public List<InventoryFolderBase> GetSystemFolders(Guid guid)
        {
            UUID userID = new UUID(guid);
            return new List<InventoryFolderBase>(GetSystemFolders(userID).Values);
        }

        // This shouldn't be here, it should be in the inventory service.
        // But I don't want to deal with types and dependencies for now.
        private Dictionary<AssetType, InventoryFolderBase> GetSystemFolders(UUID userID)
        {
            InventoryFolderBase root = _InventoryService.GetRootFolder(userID);
            if (root != null)
            {
                InventoryCollection content = _InventoryService.GetFolderContent(userID, root.ID);
                if (content != null)
                {
                    Dictionary<AssetType, InventoryFolderBase> folders = new Dictionary<AssetType, InventoryFolderBase>();
                    foreach (InventoryFolderBase folder in content.Folders)
                    {
                        if (folder.Type != (short)AssetType.Folder && folder.Type != (short)AssetType.Unknown)
                            folders[(AssetType)folder.Type] = folder;
                    }
                    // Put the root folder there, as type Folder
                    folders[AssetType.Folder] = root;
                    return folders;
                }
            }
            _log.WarnFormat("[INVENTORY SERVICE]: System folders for {0} not found", userID);
            return new Dictionary<AssetType, InventoryFolderBase>();
        }

        public InventoryItemBase GetItem(Guid guid)
        {
            return _InventoryService.GetItem(UUID.Zero, new UUID(guid));
        }

        public InventoryFolderBase GetFolder(Guid guid)
        {
            return _InventoryService.GetFolder(UUID.Zero, new UUID(guid));
        }

        public InventoryCollection GetFolderContent(Guid guid)
        {
            return _InventoryService.GetFolderContent(UUID.Zero, new UUID(guid));
        }

        public List<InventoryItemBase> GetFolderItems(Guid folderID)
        {
            List<InventoryItemBase> allItems = new List<InventoryItemBase>();

            // TODO: UUID.Zero is passed as the userID here, making the old assumption that the OpenSim
            // inventory server only has a single inventory database and not per-user inventory databases.
            // This could be changed but it requirs a bit of hackery to pass another parameter into this
            // callback
            List<InventoryItemBase> items = _InventoryService.GetFolderItems(UUID.Zero, new UUID(folderID));

            if (items != null)
            {
                allItems.InsertRange(0, items);
            }
            return allItems;
        }

        public bool CreateUsersInventory(Guid rawUserID)
        {
            UUID userID = new UUID(rawUserID);


            return _InventoryService.CreateUserInventory(userID);
        }

        public List<InventoryItemBase> GetActiveGestures(Guid rawUserID)
        {
            UUID userID = new UUID(rawUserID);

            return _InventoryService.GetActiveGestures(userID);
        }

        public List<InventoryFolderBase> GetInventorySkeleton(Guid rawUserID)
        {
            UUID userID = new UUID(rawUserID);
            return _InventoryService.GetInventorySkeleton(userID);
        }

        public int GetAssetPermissions(InventoryItemBase item)
        {
            return _InventoryService.GetAssetPermissions(item.Owner, item.AssetID);
        }

        public bool DeleteFolders(List<Guid> items)
        {
            List<UUID> uuids = new List<UUID>();
            foreach (Guid g in items)
                uuids.Add(new UUID(g));
            // oops we lost the user info here. Bad bad handlers
            return _InventoryService.DeleteFolders(UUID.Zero, uuids);
        }

        public bool DeleteItems(List<Guid> items)
        {
            List<UUID> uuids = new List<UUID>();
            foreach (Guid g in items)
                uuids.Add(new UUID(g));
            // oops we lost the user info here. Bad bad handlers
            return _InventoryService.DeleteItems(UUID.Zero, uuids);
        }

        public bool MoveItems(List<InventoryItemBase> items)
        {
            // oops we lost the user info here. Bad bad handlers
            // let's peek at one item
            UUID ownerID = UUID.Zero;
            if (items.Count > 0)
                ownerID = items[0].Owner;
            return _InventoryService.MoveItems(ownerID, items);
        }
        #endregion

        /// <summary>
        /// Check that the source of an inventory request is one that we trust.
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        public bool CheckTrustSource(IPEndPoint peer)
        {
            if (_doLookup)
            {
                _log.InfoFormat("[INVENTORY IN CONNECTOR]: Checking trusted source {0}", peer);
                UriBuilder ub = new UriBuilder(_userserver_url);
                IPAddress[] uaddrs = Dns.GetHostAddresses(ub.Host);
                foreach (IPAddress uaddr in uaddrs)
                {
                    if (uaddr.Equals(peer.Address))
                    {
                        return true;
                    }
                }

                _log.WarnFormat(
                    "[INVENTORY IN CONNECTOR]: Rejecting request since source {0} was not in the list of trusted sources",
                    peer);

                return false;
            }
            else
            {
                return true;
            }
        }

        /// <summary>
        /// Check that the source of an inventory request for a particular agent is a current session belonging to
        /// that agent.
        /// </summary>
        /// <param name="session_id"></param>
        /// <param name="avatar_id"></param>
        /// <returns></returns>
        public virtual bool CheckAuthSession(string session_id, string avatar_id)
        {
            return true;
        }

    }
}
