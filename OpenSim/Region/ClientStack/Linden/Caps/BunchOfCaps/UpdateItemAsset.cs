using System;
using System.Collections;
using System.Net;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;
using OpenSim.Framework.Capabilities;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Servers.HttpServer;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

namespace OpenSim.Region.ClientStack.Linden
{
    public delegate UUID UpdateItem(UUID itemID, UUID objectID, byte[] data);
    public delegate UUID ItemUpdatedCallback(UUID userID, UUID itemID, UUID objectID, byte[] data);

    public partial class BunchOfCaps
    {
        public void UpdateNotecardItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
        {
            UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.Notecard);
        }

        public void UpdateAnimSetItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
        {
            //UpdateInventoryItemAsset(httpRequest, httpResponse, map, CustomInventoryType.AnimationSet);
        }

        public void UpdateScriptItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
        {
            UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.LSLText);
        }

        public void UpdateSettingsItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
        {
            UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.Settings);
        }

        public void UpdateGestureItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
        {
            UpdateInventoryItemAsset(httpRequest, httpResponse, map, (byte)AssetType.Gesture);
        }

        private void UpdateInventoryItemAsset(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map, byte atype, bool taskSript = false)
        {
            _log.Debug("[CAPS]: UpdateInventoryItemAsset Request in region: " + _regionName + "\n");

            httpResponse.StatusCode = (int)HttpStatusCode.OK;

            UUID itemID = UUID.Zero;
            UUID objectID = UUID.Zero;

            try
            {
                if (map.TryGetValue("ite_id", out OSD itmp))
                    itemID = itmp;
                if (map.TryGetValue("task_id", out OSD tmp))
                    objectID = tmp;
            }
            catch { }

            if (itemID == UUID.Zero)
            {
                LLSDAssetUploadError error = new LLSDAssetUploadError
                {
                    message = "failed to recode request",
                    identifier = UUID.Zero
                };
                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                return;
            }

            if (objectID != UUID.Zero)
            {
                SceneObjectPart sop = _Scene.GetSceneObjectPart(objectID);
                if (sop == null)
                {
                    LLSDAssetUploadError error = new LLSDAssetUploadError
                    {
                        message = "object not found",
                        identifier = UUID.Zero
                    };
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                    return;
                }

                if (!_Scene.Permissions.CanEditObjectInventory(objectID, _AgentID))
                {
                    LLSDAssetUploadError error = new LLSDAssetUploadError
                    {
                        message = "No permissions to edit objec",
                        identifier = UUID.Zero
                    };
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                    return;
                }
            }

            string uploaderPath = GetNewCapPath();

            string protocol = _HostCapsObj.SSLCaps ? "https://" : "http://";
            string uploaderURL = protocol + _HostCapsObj.HostName + ":" + _HostCapsObj.Port.ToString() + uploaderPath;
            LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse
            {
                uploader = uploaderURL,
                state = "upload"
            };

            ItemUpdater uploader = new ItemUpdater(itemID, objectID, atype, uploaderPath, _HostCapsObj.HttpListener, _dumpAssetsToFile)
            {
                _remoteAdress = httpRequest.RemoteIPEndPoint.Address
            };

            uploader.OnUpLoad += ItemUpdated;

            var uploaderHandler = new SimpleBinaryHandler("POST", uploaderPath, uploader.process)
            {
                MaxDataSize = 10000000 // change per asset type?
            };

            _HostCapsObj.HttpListener.AddSimpleStreamHandler(uploaderHandler);

            // _log.InfoFormat("[CAPS]: UpdateAgentInventoryAsset response: {0}",
            //                             LLSDHelpers.SerialiseLLSDReply(uploadResponse)));

            httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(uploadResponse));
        }

        /// <summary>
        /// Called when new asset data for an inventory item update has been uploaded.
        /// </summary>
        /// <param name="itemID">Item to update</param>
        /// <param name="data">New asset data</param>
        /// <returns></returns>
        public UUID ItemUpdated(UUID itemID, UUID objectID, byte[] data)
        {
            if (ItemUpdatedCall != null)
            {
                return ItemUpdatedCall(_HostCapsObj.AgentID, itemID, objectID, data);
            }
            return UUID.Zero;
        }

        /// <summary>
        /// Called by the script task update handler.  Provides a URL to which the client can upload a new asset.
        /// </summary>
        /// <param name="httpRequest">HTTP request header object</param>
        /// <param name="httpResponse">HTTP response header object</param>
        /// <returns></returns>
        public void UpdateScriptTaskInventory(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, OSDMap map)
        {
            httpResponse.StatusCode = (int)HttpStatusCode.OK;

            try
            {
                //_log.Debug("[CAPS]: ScriptTaskInventory Request in region: " + _regionName);
                //_log.DebugFormat("[CAPS]: request: {0}, path: {1}, param: {2}", request, path, param);

                UUID itemID = UUID.Zero;
                UUID objectID = UUID.Zero;
                bool is_script_running = false;
                OSD tmp;
                try
                {
                    if (map.TryGetValue("ite_id", out tmp))
                        itemID = tmp;
                    if (map.TryGetValue("task_id", out tmp))
                        objectID = tmp;
                    if (map.TryGetValue("is_script_running", out tmp))
                        is_script_running = tmp;
                }
                catch { }

                if (itemID == UUID.Zero || objectID == UUID.Zero)
                {
                    LLSDAssetUploadError error = new LLSDAssetUploadError
                    {
                        message = "failed to recode request",
                        identifier = UUID.Zero
                    };
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                    return;
                }

                SceneObjectPart sop = _Scene.GetSceneObjectPart(objectID);
                if (sop == null)
                {
                    LLSDAssetUploadError error = new LLSDAssetUploadError
                    {
                        message = "object not found",
                        identifier = UUID.Zero
                    };
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                    return;
                }

                if (!_Scene.Permissions.CanEditObjectInventory(objectID, _AgentID))
                {
                    LLSDAssetUploadError error = new LLSDAssetUploadError
                    {
                        message = "No permissions to edit objec",
                        identifier = UUID.Zero
                    };
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                    return;
                }

                if (!_Scene.Permissions.CanEditScript(itemID, objectID, _AgentID))
                {
                    LLSDAssetUploadError error = new LLSDAssetUploadError
                    {
                        message = "No permissions to edit script",
                        identifier = UUID.Zero
                    };
                    httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                    return;
                }

                string uploaderPath = GetNewCapPath();
                string protocol = _HostCapsObj.SSLCaps ? "https://" : "http://";
                string uploaderURL = protocol + _HostCapsObj.HostName + ":" + _HostCapsObj.Port.ToString() + uploaderPath;
                LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse
                {
                    uploader = uploaderURL,
                    state = "upload"
                };

                TaskInventoryScriptUpdater uploader = new TaskInventoryScriptUpdater(itemID, objectID, is_script_running,
                        uploaderPath, _HostCapsObj.HttpListener, httpRequest.RemoteIPEndPoint.Address, _dumpAssetsToFile);
                uploader.OnUpLoad += TaskScriptUpdated;

                var uploaderHandler = new SimpleBinaryHandler("POST", uploaderPath, uploader.process)
                {
                    MaxDataSize = 10000000 // change per asset type?
                };

                _HostCapsObj.HttpListener.AddSimpleStreamHandler(uploaderHandler);

                // _log.InfoFormat("[CAPS]: " +
                //    "ScriptTaskInventory response: {0}",
                //       LLSDHelpers.SerialiseLLSDReply(uploadResponse)));

                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(uploadResponse));
            }
            catch (Exception e)
            {
                _log.Error("[UpdateScriptTaskInventory]: " + e.ToString());
            }
        }
        /// <summary>
        /// Called when new asset data for an agent inventory item update has been uploaded.
        /// </summary>
        /// <param name="itemID">Item to update</param>
        /// <param name="primID">Prim containing item to update</param>
        /// <param name="isScriptRunning">Signals whether the script to update is currently running</param>
        /// <param name="data">New asset data</param>
        public void TaskScriptUpdated(UUID itemID, UUID primID, bool isScriptRunning, byte[] data, ref ArrayList errors)
        {
            if (TaskScriptUpdatedCall != null)
            {
                ArrayList e = TaskScriptUpdatedCall(_HostCapsObj.AgentID, itemID, primID, isScriptRunning, data);
                foreach (object item in e)
                    errors.Add(item);
            }
        }

        static public bool ValidateAssetData(byte assetType, byte[] data)
        {
            return true;
        }

        /// <summary>
        /// This class is a callback invoked when a client sends asset data to
        /// an agent inventory notecard update url
        /// </summary>
        public class ItemUpdater : ExpiringCapBase
        {
            public event UpdateItem OnUpLoad = null;
            private readonly UUID _inventoryItemID;
            private readonly UUID _objectID;
            private readonly bool _dumpAssetToFile;
            public IPAddress _remoteAdress;
            private readonly byte _assetType;

            public ItemUpdater(UUID inventoryItem, UUID objectid, byte aType, string path, IHttpServer httpServer, bool dumpAssetToFile):
                base(httpServer, path)
            {
                _dumpAssetToFile = dumpAssetToFile;

                _inventoryItemID = inventoryItem;
                _objectID = objectid;
                _httpListener = httpServer;
                _assetType = aType;

                Start(30000);
            }

            /// <summary>
            /// Handle raw uploaded asset data.
            /// </summary>
            /// <param name="data"></param>
            /// <param name="path"></param>
            /// <param name="param"></param>
            /// <returns></returns>
            public void process(IOSHttpRequest request, IOSHttpResponse response, byte[] data)
            {
                Stop();

                if (!request.RemoteIPEndPoint.Address.Equals(_remoteAdress))
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return;
                }

                string res = string.Empty;

                if (OnUpLoad == null)
                {
                    response.StatusCode = (int)HttpStatusCode.Gone;
                    return;
                }

                if (!BunchOfCaps.ValidateAssetData(_assetType, data))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                UUID assetID = OnUpLoad(_inventoryItemID, _objectID, data);

                if (assetID == UUID.Zero)
                {
                    LLSDAssetUploadError uperror = new LLSDAssetUploadError
                    {
                        message = "Failed to update inventory item asset",
                        identifier = _inventoryItemID
                    };
                    res = LLSDHelpers.SerialiseLLSDReply(uperror);
                }
                else
                {
                    LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete
                    {
                        new_asset = assetID.ToString(),
                        new_inventory_item = _inventoryItemID,
                        state = "complete"
                    };
                    res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);
                }

                if (_dumpAssetToFile)
                {
                    Util.SaveAssetToFile("updateditem" + Util.RandomClass.Next(1, 1000) + ".dat", data);
                }

                response.StatusCode = (int)HttpStatusCode.OK;
                response.RawBuffer = Util.UTF8NBGetbytes(res);
            }
        }

        /// <summary>
        /// This class is a callback invoked when a client sends asset data to
        /// a task inventory script update url
        /// </summary>
        public class TaskInventoryScriptUpdater : ExpiringCapBase
        {
            public event UpdateTaskScript OnUpLoad;
            private readonly UUID _inventoryItemID;
            private readonly UUID _primID;
            private readonly bool _isScriptRunning;
            private readonly bool _dumpAssetToFile;
            public IPAddress _remoteAddress;

            public TaskInventoryScriptUpdater(UUID inventoryItemID, UUID primID, bool isScriptRunning,
                                                string path, IHttpServer httpServer, IPAddress address,
                                                bool dumpAssetToFile) : base(httpServer, path)
            {
                _dumpAssetToFile = dumpAssetToFile;
                _inventoryItemID = inventoryItemID;
                _primID = primID;
                _isScriptRunning = isScriptRunning;
                _remoteAddress = address;
                Start(30000);
            }

            /// <summary>
            ///
            /// </summary>
            /// <param name="data"></param>
            /// <param name="path"></param>
            /// <param name="param"></param>
            /// <returns></returns>
            public void process(IOSHttpRequest request, IOSHttpResponse response, byte[] data)
            {
                Stop();

                if (!request.RemoteIPEndPoint.Address.Equals(_remoteAddress))
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    return;
                }

                if (OnUpLoad == null)
                {
                    response.StatusCode = (int)HttpStatusCode.Gone;
                    return;
                }

                if (!BunchOfCaps.ValidateAssetData((byte)AssetType.LSLText, data))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                response.StatusCode = (int)HttpStatusCode.OK;

                try
                {
                    string res = string.Empty;
                    LLSDTaskScriptUploadComplete uploadComplete = new LLSDTaskScriptUploadComplete();

                    ArrayList errors = new ArrayList();
                    OnUpLoad?.Invoke(_inventoryItemID, _primID, _isScriptRunning, data, ref errors);

                    uploadComplete.new_asset = _inventoryItemID;
                    uploadComplete.compiled = errors.Count > 0 ? false : true;
                    uploadComplete.state = "complete";
                    uploadComplete.errors = new OpenSim.Framework.Capabilities.OSDArray
                    {
                        Array = errors
                    };

                    res = LLSDHelpers.SerialiseLLSDReply(uploadComplete);

                    if (_dumpAssetToFile)
                    {
                        Util.SaveAssetToFile("updatedtaskscript" + Util.RandomClass.Next(1, 1000) + ".dat", data);
                    }

                    // _log.InfoFormat("[CAPS]: TaskInventoryScriptUpdater.uploaderCaps res: {0}", res);
                    response.RawBuffer = Util.UTF8NBGetbytes(res);
                }
                catch
                {
                    LLSDAssetUploadError error = new LLSDAssetUploadError
                    {
                        message = "could not compile script",
                        identifier = UUID.Zero
                    };
                    response.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(error));
                    return;
                }
            }
        }
    }
}