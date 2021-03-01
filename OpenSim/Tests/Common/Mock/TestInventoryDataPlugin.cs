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
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Data;

namespace OpenSim.Tests.Common
{
    /// <summary>
    /// In memory inventory data plugin for test purposes.  Could be another dll when properly filled out and when the
    /// mono addin plugin system starts co-operating with the unit test system.  Currently no locking since unit
    /// tests are single threaded.
    /// </summary>
    public class TestInventoryDataPlugin : IInventoryDataPlugin
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <value>
        /// Inventory folders
        /// </value>
        private readonly Dictionary<UUID, InventoryFolderBase> _folders = new Dictionary<UUID, InventoryFolderBase>();

        //// <value>
        /// Inventory items
        /// </value>
        private readonly Dictionary<UUID, InventoryItemBase> _items = new Dictionary<UUID, InventoryItemBase>();

        /// <value>
        /// User root folders
        /// </value>
        private readonly Dictionary<UUID, InventoryFolderBase> _rootFolders = new Dictionary<UUID, InventoryFolderBase>();

        public string Version => "0";
        public string Name => "TestInventoryDataPlugin";

        public void Initialise() {}
        public void Initialise(string connect) {}
        public void Dispose() {}

        public List<InventoryFolderBase> getFolderHierarchy(UUID parentID)
        {
            List<InventoryFolderBase> folders = new List<InventoryFolderBase>();

            foreach (InventoryFolderBase folder in _folders.Values)
            {
                if (folder.ParentID == parentID)
                {
                    folders.AddRange(getFolderHierarchy(folder.ID));
                    folders.Add(folder);
                }
            }

            return folders;
        }

        public List<InventoryItemBase> getInventoryInFolder(UUID folderID)
        {
//            InventoryFolderBase folder = _folders[folderID];

//            _log.DebugFormat("[MOCK INV DB]: Getting items in folder {0} {1}", folder.Name, folder.ID);

            List<InventoryItemBase> items = new List<InventoryItemBase>();

            foreach (InventoryItemBase item in _items.Values)
            {
                if (item.Folder == folderID)
                {
//                    _log.DebugFormat("[MOCK INV DB]: getInventoryInFolder() adding item {0}", item.Name);
                    items.Add(item);
                }
            }

            return items;
        }

        public List<InventoryFolderBase> getUserRootFolders(UUID user) { return null; }

        public InventoryFolderBase getUserRootFolder(UUID user)
        {
//            _log.DebugFormat("[MOCK INV DB]: Looking for root folder for {0}", user);

            InventoryFolderBase folder = null;
            _rootFolders.TryGetValue(user, out folder);

            return folder;
        }

        public List<InventoryFolderBase> getInventoryFolders(UUID parentID)
        {
//            InventoryFolderBase parentFolder = _folders[parentID];

//            _log.DebugFormat("[MOCK INV DB]: Getting folders in folder {0} {1}", parentFolder.Name, parentFolder.ID);

            List<InventoryFolderBase> folders = new List<InventoryFolderBase>();

            foreach (InventoryFolderBase folder in _folders.Values)
            {
                if (folder.ParentID == parentID)
                {
//                    _log.DebugFormat(
//                        "[MOCK INV DB]: Found folder {0} {1} in {2} {3}",
//                        folder.Name, folder.ID, parentFolder.Name, parentFolder.ID);

                    folders.Add(folder);
                }
            }

            return folders;
        }

        public InventoryFolderBase getInventoryFolder(UUID folderId)
        {
            InventoryFolderBase folder = null;
            _folders.TryGetValue(folderId, out folder);

            return folder;
        }

        public InventoryFolderBase queryInventoryFolder(UUID folderID)
        {
            return getInventoryFolder(folderID);
        }

        public void addInventoryFolder(InventoryFolderBase folder)
        {
//            _log.DebugFormat(
//                "[MOCK INV DB]: Adding inventory folder {0} {1} type {2}",
//                folder.Name, folder.ID, (AssetType)folder.Type);

            _folders[folder.ID] = folder;

            if (folder.ParentID == UUID.Zero)
            {
//                _log.DebugFormat(
//                    "[MOCK INV DB]: Adding root folder {0} {1} for {2}", folder.Name, folder.ID, folder.Owner);
                _rootFolders[folder.Owner] = folder;
            }
        }

        public void updateInventoryFolder(InventoryFolderBase folder)
        {
            _folders[folder.ID] = folder;
        }

        public void moveInventoryFolder(InventoryFolderBase folder)
        {
            // Simple replace
            updateInventoryFolder(folder);
        }

        public void deleteInventoryFolder(UUID folderId)
        {
            if (_folders.ContainsKey(folderId))
                _folders.Remove(folderId);
        }

        public void addInventoryItem(InventoryItemBase item)
        {
            InventoryFolderBase folder = _folders[item.Folder];

//            _log.DebugFormat(
//                "[MOCK INV DB]: Adding inventory item {0} {1} in {2} {3}", item.Name, item.ID, folder.Name, folder.ID);

            _items[item.ID] = item;
        }

        public void updateInventoryItem(InventoryItemBase item) { addInventoryItem(item); }

        public void deleteInventoryItem(UUID itemId)
        {
            if (_items.ContainsKey(itemId))
                _items.Remove(itemId);
        }

        public InventoryItemBase getInventoryItem(UUID itemId)
        {
            if (_items.ContainsKey(itemId))
                return _items[itemId];
            else
                return null;
        }

        public InventoryItemBase queryInventoryItem(UUID item)
        {
            return null;
        }

        public List<InventoryItemBase> fetchActiveGestures(UUID avatarID) { return null; }
    }
}
