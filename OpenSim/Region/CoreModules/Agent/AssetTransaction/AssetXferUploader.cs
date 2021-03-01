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
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.CoreModules.Agent.AssetTransaction
{
    public class AssetXferUploader
    {

        private readonly List<UUID> defaultIDs = new List<UUID> {
                // Viewer's notion of the default texture
                new UUID("5748decc-f629-461c-9a36-a35a221fe21f"), // others == default blank
                new UUID("7ca39b4c-bd19-4699-aff7-f93fd03d3e7b"), // hair
                new UUID("6522e74d-1660-4e7f-b601-6f48c1659a77"), // eyes
                new UUID("c228d1cf-4b5d-4ba8-84f4-899a0796aa97"), // skin
                new UUID("8dcd4a48-2d37-4909-9f78-f7a9eb4ef903"), // transparency for alpha
                // opensim assets textures possibly obsolete now
                new UUID("00000000-0000-1111-9999-000000000010"),
                new UUID("00000000-0000-1111-9999-000000000011"),
                new UUID("00000000-0000-1111-9999-000000000012"),
                // other transparency defined in assets
                new UUID("3a367d1c-bef1-6d43-7595-e88c1e3aadb3"),
                new UUID("1578a2b1-5179-4b53-b618-fe00ca5a5594"),
                };

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Upload state.
        /// </summary>
        /// <remarks>
        /// New -> Uploading -> Complete
        /// </remarks>
        private enum UploadState
        {
            New,
            Uploading,
            Complete
        }

        /// <summary>
        /// Reference to the object that holds this uploader.  Used to remove ourselves from it's list if we
        /// are performing a delayed update.
        /// </summary>
        readonly AgentAssetTransactions _transactions;

        private UploadState _uploadState = UploadState.New;

        private readonly AssetBase _asset;
        private UUID InventFolder = UUID.Zero;
        private sbyte invType = 0;

        private bool _createItem;
        private uint _createItemCallback;

        private bool _updateItem;
        private InventoryItemBase _updateItemData;

        private bool _updateTaskItem;
        private TaskInventoryItem _updateTaskItemData;

        private string _description = string.Empty;
        private readonly bool _dumpAssetToFile;
        private string _name = string.Empty;
//        private bool _storeLocal;
        private uint nextPerm = 0;
        private IClientAPI ourClient;

        private readonly UUID _transactionID;

        private sbyte type = 0;
        private byte wearableType = 0;
        private byte[] _oldData = null;
        public ulong XferID;
        private readonly Scene _Scene;

        /// <summary>
        /// AssetXferUploader constructor
        /// </summary>
        /// <param name='transactions'>/param>
        /// <param name='scene'></param>
        /// <param name='transactionID'></param>
        /// <param name='dumpAssetToFile'>
        /// If true then when the asset is uploaded it is dumped to a file with the format
        /// String.Format("{6}_{7}_{0:d2}{1:d2}{2:d2}_{3:d2}{4:d2}{5:d2}.dat",
        ///   now.Year, now.Month, now.Day, now.Hour, now.Minute,
        ///   now.Second, _asset.Name, _asset.Type);
        /// for debugging purposes.
        /// </param>
        public AssetXferUploader(
            AgentAssetTransactions transactions, Scene scene, UUID transactionID, bool dumpAssetToFile)
        {
            _asset = new AssetBase();

            _transactions = transactions;
            _transactionID = transactionID;
            _Scene = scene;
            _dumpAssetToFile = dumpAssetToFile;
        }

        /// <summary>
        /// Process transfer data received from the client.
        /// </summary>
        /// <param name="xferID"></param>
        /// <param name="packetID"></param>
        /// <param name="data"></param>
        /// <returns>True if the transfer is complete, false otherwise or if the xferID was not valid</returns>
        public bool HandleXferPacket(ulong xferID, uint packetID, byte[] data)
        {
//            _log.DebugFormat(
//                "[ASSET XFER UPLOADER]: Received packet {0} for xfer {1} (data length {2})",
//                packetID, xferID, data.Length);

            if (XferID == xferID)
            {
                lock (this)
                {
                    int assetLength = _asset.Data.Length;
                    int dataLength = data.Length;

                    if (_asset.Data.Length > 1)
                    {
                        byte[] destinationArray = new byte[assetLength + dataLength];
                        Array.Copy(_asset.Data, 0, destinationArray, 0, assetLength);
                        Array.Copy(data, 0, destinationArray, assetLength, dataLength);
                        _asset.Data = destinationArray;
                    }
                    else
                    {
                        if (dataLength > 4)
                        {
                            byte[] buffer2 = new byte[dataLength - 4];
                            Array.Copy(data, 4, buffer2, 0, dataLength - 4);
                            _asset.Data = buffer2;
                        }
                    }
                }

                ourClient.SendConfirmXfer(xferID, packetID);

                if ((packetID & 0x80000000) != 0)
                {
                    SendCompleteMessage();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Start asset transfer from the client
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="assetID"></param>
        /// <param name="transaction"></param>
        /// <param name="type"></param>
        /// <param name="data">
        /// Optional data.  If present then the asset is created immediately with this data
        /// rather than requesting an upload from the client.  The data must be longer than 2 bytes.
        /// </param>
        /// <param name="storeLocal"></param>
        /// <param name="tempFile"></param>
        public void StartUpload(
            IClientAPI remoteClient, UUID assetID, UUID transaction, sbyte type, byte[] data, bool storeLocal,
            bool tempFile)
        {
//            _log.DebugFormat(
//                "[ASSET XFER UPLOADER]: Initialised xfer from {0}, asset {1}, transaction {2}, type {3}, storeLocal {4}, tempFile {5}, already received data length {6}",
//                remoteClient.Name, assetID, transaction, type, storeLocal, tempFile, data.Length);

            lock (this)
            {
                if (_uploadState != UploadState.New)
                {
                    _log.WarnFormat(
                        "[ASSET XFER UPLOADER]: Tried to start upload of asset {0}, transaction {1} for {2} but this is already in state {3}.  Aborting.",
                        assetID, transaction, remoteClient.Name, _uploadState);

                    return;
                }

                _uploadState = UploadState.Uploading;
            }

            ourClient = remoteClient;

            _asset.FullID = assetID;
            _asset.Type = type;
            _asset.CreatorID = remoteClient.AgentId.ToString();
            _asset.Data = data;
            _asset.Local = storeLocal;
            _asset.Temporary = tempFile;

//            _storeLocal = storeLocal;

            if (_asset.Data.Length > 2)
            {
                SendCompleteMessage();
            }
            else
            {
                RequestStartXfer();
            }
        }

        protected void RequestStartXfer()
        {
            XferID = Util.GetNextXferID();

//            _log.DebugFormat(
//                "[ASSET XFER UPLOADER]: Requesting Xfer of asset {0}, type {1}, transfer id {2} from {3}",
//                _asset.FullID, _asset.Type, XferID, ourClient.Name);

            ourClient.SendXferRequest(XferID, _asset.Type, _asset.FullID, 0, new byte[0]);
        }

        protected void SendCompleteMessage()
        {
            // We must lock in order to avoid a race with a separate thread dealing with an inventory item or create
            // message from other client UDP.
            lock (this)
            {
                _uploadState = UploadState.Complete;

                bool sucess = true;                
                if (_createItem)
                {
                    sucess = CompleteCreateItem(_createItemCallback);
                }
                else if (_updateItem)
                {
                    sucess = CompleteItemUpdate(_updateItemData);
                }
                else if (_updateTaskItem)
                {
                    sucess = CompleteTaskItemUpdate(_updateTaskItemData);
                }
                else if (_asset.Local)
                {
                    _Scene.AssetService.Store(_asset);
                }
                ourClient.SendAssetUploadCompleteMessage(_asset.Type, sucess, _asset.FullID);
            }

            _log.DebugFormat(
                "[ASSET XFER UPLOADER]: Uploaded asset {0} for transaction {1}",
                _asset.FullID, _transactionID);

            if (_dumpAssetToFile)
            {
                DateTime now = DateTime.Now;
                string filename =
                        string.Format("{6}_{7}_{0:d2}{1:d2}{2:d2}_{3:d2}{4:d2}{5:d2}.dat",
                        now.Year, now.Month, now.Day, now.Hour, now.Minute,
                        now.Second, _asset.Name, _asset.Type);
                SaveAssetToFile(filename, _asset.Data);
            }
        }

        private void SaveAssetToFile(string filename, byte[] data)
        {
            string assetPath = "UserAssets";
            if (!Directory.Exists(assetPath))
            {
                Directory.CreateDirectory(assetPath);
            }
            FileStream fs = File.Create(Path.Combine(assetPath, filename));
            BinaryWriter bw = new BinaryWriter(fs);
            bw.Write(data);
            bw.Close();
            fs.Close();
        }

        public void RequestCreateInventoryItem(IClientAPI remoteClient,
                UUID folderID, uint callbackID,
                string description, string name, sbyte invType,
                sbyte type, byte wearableType, uint nextOwnerMask)
        {
            InventFolder = folderID;
            _name = name;
            _description = description;
            this.type = type;
            this.invType = invType;
            this.wearableType = wearableType;
            nextPerm = nextOwnerMask;
            _asset.Name = name;
            _asset.Description = description;
            _asset.Type = type;

            // We must lock to avoid a race with a separate thread uploading the asset.
            lock (this)
            {
                if (_uploadState == UploadState.Complete)
                {
                    CompleteCreateItem(callbackID);
                }
                else
                {
                    _createItem = true; //set flag so the inventory item is created when upload is complete
                    _createItemCallback = callbackID;
                }
            }
        }

        public void RequestUpdateInventoryItem(IClientAPI remoteClient, InventoryItemBase item)
        {
            // We must lock to avoid a race with a separate thread uploading the asset.
            lock (this)
            {
                _asset.Name = item.Name;
                _asset.Description = item.Description;
                _asset.Type = (sbyte)item.AssetType;

                // remove redundante _Scene.InventoryService.UpdateItem
                // if uploadState == UploadState.Complete)
//                if (_asset.FullID != UUID.Zero)
//                {
                    // We must always store the item at this point even if the asset hasn't finished uploading, in order
                    // to avoid a race condition when the appearance module retrieves the item to set the asset id in
                    // the AvatarAppearance structure.
//                    item.AssetID = _asset.FullID;
//                    _Scene.InventoryService.UpdateItem(item);
//                }

                if (_uploadState == UploadState.Complete)
                {
                    CompleteItemUpdate(item);
                }
                else
                {
                    // do it here to avoid the eventual race condition
                    if (_asset.FullID != UUID.Zero)
                    {
                        // We must always store the item at this point even if the asset hasn't finished uploading, in order
                        // to avoid a race condition when the appearance module retrieves the item to set the asset id in
                        // the AvatarAppearance structure.
                        item.AssetID = _asset.FullID;
                        _Scene.InventoryService.UpdateItem(item);
                    }


                    //                    _log.DebugFormat(
                    //                        "[ASSET XFER UPLOADER]: Holding update inventory item request {0} for {1} pending completion of asset xfer for transaction {2}",
                    //                        item.Name, remoteClient.Name, transactionID);

                    _updateItem = true;
                    _updateItemData = item;
                }
            }
        }

        public void RequestUpdateTaskInventoryItem(IClientAPI remoteClient, TaskInventoryItem taskItem)
        {
            // We must lock to avoid a race with a separate thread uploading the asset.
            lock (this)
            {
                _asset.Name = taskItem.Name;
                _asset.Description = taskItem.Description;
                _asset.Type = (sbyte)taskItem.Type;
                taskItem.AssetID = _asset.FullID;

                if (_uploadState == UploadState.Complete)
                {
                    CompleteTaskItemUpdate(taskItem);
                }
                else
                {
                    _updateTaskItem = true;
                    _updateTaskItemData = taskItem;
                }
            }
        }

        /// <summary>
        /// Store the asset for the given item when it has been uploaded.
        /// </summary>
        /// <param name="item"></param>
        private bool CompleteItemUpdate(InventoryItemBase item)
        {
//            _log.DebugFormat(
//                "[ASSET XFER UPLOADER]: Storing asset {0} for earlier item update for {1} for {2}",
//                _asset.FullID, item.Name, ourClient.Name);

            uint perms = ValidateAssets();
            if(perms == 0)
            {
                string error = string.Format("Not enough permissions on asset(s) referenced by item '{0}', update failed", item.Name);
                ourClient.SendAlertMessage(error);
                _transactions.RemoveXferUploader(_transactionID);
                ourClient.SendBulkUpdateInventory(item); // invalid the change item on viewer cache
            }
            else
            {
                _Scene.AssetService.Store(_asset);
                if (_asset.FullID != UUID.Zero)
                {
                    item.AssetID = _asset.FullID;
                    _Scene.InventoryService.UpdateItem(item);
                }
                ourClient.SendInventoryItemCreateUpdate(item, _transactionID, 0);           
                _transactions.RemoveXferUploader(_transactionID);
                _Scene.EventManager.TriggerOnNewInventoryItemUploadComplete(item, 0);
            }

            return perms != 0;
        }

        /// <summary>
        /// Store the asset for the given task item when it has been uploaded.
        /// </summary>
        /// <param name="taskItem"></param>
        private bool CompleteTaskItemUpdate(TaskInventoryItem taskItem)
        {
//            _log.DebugFormat(
//                "[ASSET XFER UPLOADER]: Storing asset {0} for earlier task item update for {1} for {2}",
//                _asset.FullID, taskItem.Name, ourClient.Name);

            if(ValidateAssets() == 0)
            {
                _transactions.RemoveXferUploader(_transactionID);
                string error = string.Format("Not enough permissions on asset(s) referenced by task item '{0}', update failed", taskItem.Name);
                ourClient.SendAlertMessage(error);
                // force old asset to viewers ??
                return false;
            }

            _Scene.AssetService.Store(_asset);
            _transactions.RemoveXferUploader(_transactionID);
            return true;
        }

        private bool CompleteCreateItem(uint callbackID)
        {
            if(ValidateAssets() == 0)
            {
                _transactions.RemoveXferUploader(_transactionID);
                string error = string.Format("Not enough permissions on asset(s) referenced by item '{0}', creation failed", _name);
                ourClient.SendAlertMessage(error);
                return false;
            }

            _Scene.AssetService.Store(_asset);

            InventoryItemBase item = new InventoryItemBase
            {
                Owner = ourClient.AgentId,
                CreatorId = ourClient.AgentId.ToString(),
                ID = UUID.Random(),
                AssetID = _asset.FullID,
                Description = _description,
                Name = _name,
                AssetType = type,
                InvType = invType,
                Folder = InventFolder,
                BasePermissions = (uint)(PermissionMask.All | PermissionMask.Export)
            };
            item.CurrentPermissions = item.BasePermissions;
            item.GroupPermissions=0;
            item.EveryOnePermissions=0;
            item.NextPermissions = nextPerm;
            item.Flags = (uint) wearableType;
            item.CreationDate = Util.UnixTimeSinceEpoch();

            _log.DebugFormat("[XFER]: Created item {0} with asset {1}",
                    item.ID, item.AssetID);

            if (_Scene.AddInventoryItem(item))
                ourClient.SendInventoryItemCreateUpdate(item, _transactionID, callbackID);
            else
                ourClient.SendAlertMessage("Unable to create inventory item");

            _transactions.RemoveXferUploader(_transactionID);
            return true;
        }

        private uint ValidateAssets()
        {
            uint retPerms = 0x7fffffff;
//            if(_Scene.Permissions.BypassPermissions())
//                return retPerms;

            if (_asset.Type == (sbyte)CustomAssetType.AnimationSet)
            {

                AnimationSet animSet = new AnimationSet(_asset.Data);

                retPerms &= animSet.Validate(x => {
                    const uint required = (uint)(PermissionMask.Transfer | PermissionMask.Copy);
                    uint perms = (uint)_Scene.InventoryService.GetAssetPermissions(ourClient.AgentId, x);
                    // currrent yes/no rule
                    if ((perms & required) != required)
                        return 0;
                    return perms;
                    });

                return retPerms;
            }

            if (_asset.Type == (sbyte)AssetType.Clothing ||
                _asset.Type == (sbyte)AssetType.Bodypart)
            {
                const uint texturesfullPermMask = (uint)(PermissionMask.Modify | PermissionMask.Transfer | PermissionMask.Copy);
                string content = System.Text.Encoding.ASCII.GetString(_asset.Data);
                string[] lines = content.Split(new char[] {'\n'});

                // on current requiriment of full rigths assume old assets where accepted
                Dictionary<int, UUID> allowed = ExtractTexturesFromOldData();

                int textures = 0;

                foreach (string line in lines)
                {
                    try
                    {
                        if (line.StartsWith("textures "))
                            textures = Convert.ToInt32(line.Substring(9));

                        else if (textures > 0)
                        {
                            string[] parts = line.Split(new char[] {' '});

                            UUID tx = new UUID(parts[1]);
                            int id = Convert.ToInt32(parts[0]);

                            if (defaultIDs.Contains(tx) || tx == UUID.Zero ||
                                allowed.ContainsKey(id) && allowed[id] == tx)
                            {
                                continue;
                            }
                            else
                            {
                                uint perms = (uint)_Scene.InventoryService.GetAssetPermissions(ourClient.AgentId, tx);

                                if ((perms & texturesfullPermMask) != texturesfullPermMask)
                                {
                                    _log.ErrorFormat("[ASSET UPLOADER]: REJECTED update with texture {0} from {1} because they do not own the texture", tx, ourClient.AgentId);
                                    return 0;
                                }
                                else
                                {
                                    retPerms &= perms;
                                }
                            }
                            textures--;
                        }
                    }
                    catch
                    {
                        // If it's malformed, skip it
                    }
                }
            }
            return retPerms;
        }

/* not in use
        /// <summary>
        /// Get the asset data uploaded in this transfer.
        /// </summary>
        /// <returns>null if the asset has not finished uploading</returns>
        public AssetBase GetAssetData()
        {
            if (_uploadState == UploadState.Complete)
            {
                ValidateAssets();
                return _asset;
            }

            return null;
        }
*/
        public void SetOldData(byte[] d)
        {
            _oldData = d;
        }

        private Dictionary<int,UUID> ExtractTexturesFromOldData()
        {
            Dictionary<int,UUID> result = new Dictionary<int,UUID>();
            if (_oldData == null)
                return result;

            string content = System.Text.Encoding.ASCII.GetString(_oldData);
            string[] lines = content.Split(new char[] {'\n'});

            int textures = 0;

            foreach (string line in lines)
            {
                try
                {
                    if (line.StartsWith("textures "))
                    {
                        textures = Convert.ToInt32(line.Substring(9));
                    }
                    else if (textures > 0)
                    {
                        string[] parts = line.Split(new char[] {' '});

                        UUID tx = new UUID(parts[1]);
                        int id = Convert.ToInt32(parts[0]);
                        result[id] = tx;
                        textures--;
                    }
                }
                catch
                {
                    // If it's malformed, skip it
                }
            }

            return result;
        }
    }
}
