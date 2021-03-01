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
using OpenSim.Framework;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory
{
    /// <summary>
    /// Cache root and system inventory folders to reduce number of potentially remote inventory calls and associated holdups.
    /// </summary>
    public class InventoryCache
    {
        private const int CACHE_EXPIRATION = 60000; // 1 minute

        private static readonly ExpiringCacheOS<UUID, InventoryFolderBase> _RootFolders = new ExpiringCacheOS<UUID, InventoryFolderBase>();
        private static readonly ExpiringCacheOS<UUID, Dictionary<FolderType, InventoryFolderBase>> _FolderTypes = new ExpiringCacheOS<UUID, Dictionary<FolderType, InventoryFolderBase>>();
        private static readonly ExpiringCacheOS<UUID, InventoryCollection> _Inventories = new ExpiringCacheOS<UUID, InventoryCollection>();

        public void RemoveAll(UUID userID)
        {
            _RootFolders.Remove(userID);
            _FolderTypes.Remove(userID);
            _Inventories.Remove(userID);
        }

        public void Cache(UUID userID, InventoryFolderBase root)
        {
            _RootFolders.AddOrUpdate(userID, root, CACHE_EXPIRATION);
        }

        public InventoryFolderBase GetRootFolder(UUID userID)
        {
            if (_RootFolders.TryGetValue(userID, out InventoryFolderBase root))
                return root;

            return null;
        }

        public void Cache(UUID userID, FolderType type, InventoryFolderBase folder)
        {
            if (!_FolderTypes.TryGetValue(userID, out Dictionary<FolderType, InventoryFolderBase> ff))
            {
                ff = new Dictionary<FolderType, InventoryFolderBase>();
                _FolderTypes.Add(userID, ff, CACHE_EXPIRATION);
            }

            // We need to lock here since two threads could potentially retrieve the same dictionary
            // and try to add a folder for that type simultaneously.  Dictionary<>.Add() is not described as thread-safe in the SDK
            // even if the folders are identical.
            lock (ff)
            {
                if (!ff.ContainsKey(type))
                    ff.Add(type, folder);
            }
        }

        public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        {
            if (_FolderTypes.TryGetValue(userID, out Dictionary<FolderType, InventoryFolderBase> ff))
            {
                lock (ff)
                {
                    if (ff.TryGetValue(type, out InventoryFolderBase f))
                        return f;
                }
            }

            return null;
        }

        public void Cache(UUID userID, InventoryCollection inv)
        {
            _Inventories.AddOrUpdate(userID, inv, 120);
        }

        public InventoryCollection GetFolderContent(UUID userID, UUID folderID)
        {
            InventoryCollection c;
            if (_Inventories.TryGetValue(userID, out InventoryCollection inv))
            {
                c = new InventoryCollection
                {
                    OwnerID = userID,

                    Folders = inv.Folders.FindAll(delegate (InventoryFolderBase f)
                    {
                        return f.ParentID == folderID;
                    }),
                    Items = inv.Items.FindAll(delegate (InventoryItemBase i)
                    {
                        return i.Folder == folderID;
                    })
                };
                return c;
            }
            return null;
        }

        public List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID)
        {
            if (_Inventories.TryGetValue(userID, out InventoryCollection inv))
            {
                List<InventoryItemBase> items = inv.Items.FindAll(delegate(InventoryItemBase i)
                {
                    return i.Folder == folderID;
                });
                return items;
            }
            return null;
        }
    }
}
