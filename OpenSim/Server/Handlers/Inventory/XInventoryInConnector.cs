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
using System.Reflection;
using System.Xml;
using System.Collections.Generic;
using System.IO;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.ServiceAuth;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using log4net;
using OpenMetaverse;

namespace OpenSim.Server.Handlers.Inventory
{
    public class XInventoryInConnector : ServiceConnector
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IInventoryService _InventoryService;
        private readonly string _ConfigName = "InventoryService";

        public XInventoryInConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            if (!string.IsNullOrEmpty(configName))
                _ConfigName = configName;

            _log.DebugFormat("[XInventoryInConnector]: Starting with config name {0}", _ConfigName);

            IConfig serverConfig = config.Configs[_ConfigName];
            if (serverConfig == null)
                throw new Exception(string.Format("No section '{0}' in config file", _ConfigName));

            string inventoryService = serverConfig.GetString("LocalServiceModule",
                    string.Empty);

            if (string.IsNullOrEmpty(inventoryService))
                throw new Exception("No InventoryService in config file");

            object[] args = new object[] { config, _ConfigName };
            _InventoryService =
                    ServerUtils.LoadPlugin<IInventoryService>(inventoryService, args);

            IServiceAuth auth = ServiceAuth.Create(config, _ConfigName);

            server.AddStreamHandler(new XInventoryConnectorPostHandler(_InventoryService, auth));
        }
    }

    public class XInventoryConnectorPostHandler : BaseStreamHandler
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IInventoryService _InventoryService;

        public XInventoryConnectorPostHandler(IInventoryService service, IServiceAuth auth) :
                base("POST", "/xinventory", auth)
        {
            _InventoryService = service;
        }

        protected override byte[] ProcessRequest(string path, Stream requestData,
                IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
        {
            string body;
            using(StreamReader sr = new StreamReader(requestData))
                body = sr.ReadToEnd();
            body = body.Trim();

            //_log.DebugFormat("[XXX]: query String: {0}", body);

            try
            {
                Dictionary<string, object> request =
                        ServerUtils.ParseQueryString(body);

                if (!request.ContainsKey("METHOD"))
                    return FailureResult();

                string method = request["METHOD"].ToString();
                request.Remove("METHOD");

                switch (method)
                {
                    case "CREATEUSERINVENTORY":
                        return HandleCreateUserInventory(request);
                    case "GETINVENTORYSKELETON":
                        return HandleGetInventorySkeleton(request);
                    case "GETROOTFOLDER":
                        return HandleGetRootFolder(request);
                    case "GETFOLDERFORTYPE":
                        return HandleGetFolderForType(request);
                    case "GETFOLDERCONTENT":
                        return HandleGetFolderContent(request);
                    case "GETMULTIPLEFOLDERSCONTENT":
                        return HandleGetMultipleFoldersContent(request);
                    case "GETFOLDERITEMS":
                        return HandleGetFolderItems(request);
                    case "ADDFOLDER":
                        return HandleAddFolder(request);
                    case "UPDATEFOLDER":
                        return HandleUpdateFolder(request);
                    case "MOVEFOLDER":
                        return HandleMoveFolder(request);
                    case "DELETEFOLDERS":
                        return HandleDeleteFolders(request);
                    case "PURGEFOLDER":
                        return HandlePurgeFolder(request);
                    case "ADDITEM":
                        return HandleAddItem(request);
                    case "UPDATEITEM":
                        return HandleUpdateItem(request);
                    case "MOVEITEMS":
                        return HandleMoveItems(request);
                    case "DELETEITEMS":
                        return HandleDeleteItems(request);
                    case "GETITEM":
                        return HandleGetItem(request);
                    case "GETMULTIPLEITEMS":
                        return HandleGetMultipleItems(request);
                    case "GETFOLDER":
                        return HandleGetFolder(request);
                    case "GETACTIVEGESTURES":
                        return HandleGetActiveGestures(request);
                    case "GETASSETPERMISSIONS":
                        return HandleGetAssetPermissions(request);
                }
                _log.DebugFormat("[XINVENTORY HANDLER]: unknown method request: {0}", method);
            }
            catch (Exception e)
            {
                _log.Error(string.Format("[XINVENTORY HANDLER]: Exception {0} ", e.Message), e);
            }

            return FailureResult();
        }

        private byte[] FailureResult()
        {
            return BoolResult(false);
        }

        private byte[] SuccessResult()
        {
            return BoolResult(true);
        }

        private byte[] BoolResult(bool value)
        {
            XmlDocument doc = new XmlDocument();

            XmlNode xmlnode = doc.CreateNode(XmlNodeType.XmlDeclaration,
                    "", "");

            doc.AppendChild(xmlnode);

            XmlElement rootElement = doc.CreateElement("", "ServerResponse",
                    "");

            doc.AppendChild(rootElement);

            XmlElement result = doc.CreateElement("", "RESULT", "");
            result.AppendChild(doc.CreateTextNode(value.ToString()));

            rootElement.AppendChild(result);

            return Util.DocToBytes(doc);
        }

        byte[] HandleCreateUserInventory(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();

            if (!request.ContainsKey("PRINCIPAL"))
                return FailureResult();

            if (_InventoryService.CreateUserInventory(new UUID(request["PRINCIPAL"].ToString())))
                result["RESULT"] = "True";
            else
                result["RESULT"] = "False";

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetInventorySkeleton(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();

            if (!request.ContainsKey("PRINCIPAL"))
                return FailureResult();


            List<InventoryFolderBase> folders = _InventoryService.GetInventorySkeleton(new UUID(request["PRINCIPAL"].ToString()));

            Dictionary<string, object> sfolders = new Dictionary<string, object>();
            if (folders != null)
            {
                int i = 0;
                foreach (InventoryFolderBase f in folders)
                {
                    sfolders["folder_" + i.ToString()] = EncodeFolder(f);
                    i++;
                }
            }
            result["FOLDERS"] = sfolders;

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetRootFolder(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();

            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            InventoryFolderBase rfolder = _InventoryService.GetRootFolder(principal);
            if (rfolder != null)
                result["folder"] = EncodeFolder(rfolder);

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetFolderForType(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            int type = 0;
            int.TryParse(request["TYPE"].ToString(), out type);
            InventoryFolderBase folder = _InventoryService.GetFolderForType(principal, (FolderType)type);
            if (folder != null)
                result["folder"] = EncodeFolder(folder);

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetFolderContent(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            UUID folderID = UUID.Zero;
            UUID.TryParse(request["FOLDER"].ToString(), out folderID);

            InventoryCollection icoll = _InventoryService.GetFolderContent(principal, folderID);
            if (icoll != null)
            {
                result["FID"] = icoll.FolderID.ToString();
                result["VERSION"] = icoll.Version.ToString();
                Dictionary<string, object> folders = new Dictionary<string, object>();
                int i = 0;
                if (icoll.Folders != null)
                {
                    foreach (InventoryFolderBase f in icoll.Folders)
                    {
                        folders["folder_" + i.ToString()] = EncodeFolder(f);
                        i++;
                    }
                    result["FOLDERS"] = folders;
                }
                if (icoll.Items != null)
                {
                    i = 0;
                    Dictionary<string, object> items = new Dictionary<string, object>();
                    foreach (InventoryItemBase it in icoll.Items)
                    {
                        items["ite_" + i.ToString()] = EncodeItem(it);
                        i++;
                    }
                    result["ITEMS"] = items;
                }
            }

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetMultipleFoldersContent(Dictionary<string, object> request)
        {
            Dictionary<string, object> resultSet = new Dictionary<string, object>();
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            string folderIDstr = request["FOLDERS"].ToString();
            int count = 0;
            int.TryParse(request["COUNT"].ToString(), out count);

            UUID[] fids = new UUID[count];
            string[] uuids = folderIDstr.Split(',');
            int i = 0;
            foreach (string id in uuids)
            {
                UUID fid = UUID.Zero;
                if (UUID.TryParse(id, out fid))
                    fids[i] = fid;
                i += 1;
            }

            count = 0;
            InventoryCollection[] icollList = _InventoryService.GetMultipleFoldersContent(principal, fids);
            if (icollList != null && icollList.Length > 0)
            {
                foreach (InventoryCollection icoll in icollList)
                {
                    Dictionary<string, object> result = new Dictionary<string, object>();
                    result["FID"] = icoll.FolderID.ToString();
                    result["VERSION"] = icoll.Version.ToString();
                    result["OWNER"] = icoll.OwnerID.ToString();
                    Dictionary<string, object> folders = new Dictionary<string, object>();
                    i = 0;
                    if (icoll.Folders != null)
                    {
                        foreach (InventoryFolderBase f in icoll.Folders)
                        {
                            folders["folder_" + i.ToString()] = EncodeFolder(f);
                            i++;
                        }
                        result["FOLDERS"] = folders;
                    }
                    i = 0;
                    if (icoll.Items != null)
                    {
                        Dictionary<string, object> items = new Dictionary<string, object>();
                        foreach (InventoryItemBase it in icoll.Items)
                        {
                            items["ite_" + i.ToString()] = EncodeItem(it);
                            i++;
                        }
                        result["ITEMS"] = items;
                    }

                    resultSet["F_" + fids[count++]] = result;
                    //_log.DebugFormat("[XXX]: Sending {0} {1}", fids[count-1], icoll.FolderID);
                }
            }

            string xmlString = ServerUtils.BuildXmlResponse(resultSet);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetFolderItems(Dictionary<string, object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            UUID folderID = UUID.Zero;
            UUID.TryParse(request["FOLDER"].ToString(), out folderID);

            List<InventoryItemBase> items = _InventoryService.GetFolderItems(principal, folderID);
            Dictionary<string, object> sitems = new Dictionary<string, object>();

            if (items != null)
            {
                int i = 0;
                foreach (InventoryItemBase item in items)
                {
                    sitems["ite_" + i.ToString()] = EncodeItem(item);
                    i++;
                }
            }
            result["ITEMS"] = sitems;

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleAddFolder(Dictionary<string,object> request)
        {
            InventoryFolderBase folder = BuildFolder(request);

            if (_InventoryService.AddFolder(folder))
                return SuccessResult();
            else
                return FailureResult();
        }

        byte[] HandleUpdateFolder(Dictionary<string,object> request)
        {
            InventoryFolderBase folder = BuildFolder(request);

            if (_InventoryService.UpdateFolder(folder))
                return SuccessResult();
            else
                return FailureResult();
        }

        byte[] HandleMoveFolder(Dictionary<string,object> request)
        {
            UUID parentID = UUID.Zero;
            UUID.TryParse(request["ParentID"].ToString(), out parentID);
            UUID folderID = UUID.Zero;
            UUID.TryParse(request["ID"].ToString(), out folderID);
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);

            InventoryFolderBase folder = new InventoryFolderBase(folderID, "", principal, parentID);
            if (_InventoryService.MoveFolder(folder))
                return SuccessResult();
            else
                return FailureResult();

        }

        byte[] HandleDeleteFolders(Dictionary<string,object> request)
        {
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            List<string> slist = (List<string>)request["FOLDERS"];
            List<UUID> uuids = new List<UUID>();
            foreach (string s in slist)
            {
                UUID u = UUID.Zero;
                if (UUID.TryParse(s, out u))
                    uuids.Add(u);
            }

            if (_InventoryService.DeleteFolders(principal, uuids))
                return SuccessResult();
            else
                return
                    FailureResult();
        }

        byte[] HandlePurgeFolder(Dictionary<string,object> request)
        {
            UUID folderID = UUID.Zero;
            UUID.TryParse(request["ID"].ToString(), out folderID);

            InventoryFolderBase folder = new InventoryFolderBase(folderID);
            if (_InventoryService.PurgeFolder(folder))
                return SuccessResult();
            else
                return FailureResult();
        }

        byte[] HandleAddItem(Dictionary<string,object> request)
        {
            InventoryItemBase item = BuildItem(request);

            if (_InventoryService.AddItem(item))
                return SuccessResult();
            else
                return FailureResult();
        }

        byte[] HandleUpdateItem(Dictionary<string,object> request)
        {
            InventoryItemBase item = BuildItem(request);

            if (_InventoryService.UpdateItem(item))
                return SuccessResult();
            else
                return FailureResult();
        }

        byte[] HandleMoveItems(Dictionary<string,object> request)
        {
            List<string> idlist = (List<string>)request["IDLIST"];
            List<string> destlist = (List<string>)request["DESTLIST"];
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);

            List<InventoryItemBase> items = new List<InventoryItemBase>();
            int n = 0;
            try
            {
                foreach (string s in idlist)
                {
                    UUID u = UUID.Zero;
                    if (UUID.TryParse(s, out u))
                    {
                        UUID fid = UUID.Zero;
                        if (UUID.TryParse(destlist[n++], out fid))
                        {
                            InventoryItemBase item = new InventoryItemBase(u, principal)
                            {
                                Folder = fid
                            };
                            items.Add(item);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _log.DebugFormat("[XINVENTORY IN CONNECTOR]: Exception in HandleMoveItems: {0}", e.Message);
                return FailureResult();
            }

            if (_InventoryService.MoveItems(principal, items))
                return SuccessResult();
            else
                return FailureResult();
        }

        byte[] HandleDeleteItems(Dictionary<string,object> request)
        {
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            List<string> slist = (List<string>)request["ITEMS"];
            List<UUID> uuids = new List<UUID>();
            foreach (string s in slist)
            {
                UUID u = UUID.Zero;
                if (UUID.TryParse(s, out u))
                    uuids.Add(u);
            }

            if (_InventoryService.DeleteItems(principal, uuids))
                return SuccessResult();
            else
                return
                    FailureResult();
        }

        byte[] HandleGetItem(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();
            UUID id = UUID.Zero;
            UUID.TryParse(request["ID"].ToString(), out id);
            UUID user = UUID.Zero;
            if (request.ContainsKey("PRINCIPAL"))
                UUID.TryParse(request["PRINCIPAL"].ToString(), out user);

            InventoryItemBase item = _InventoryService.GetItem(user, id);
            if (item != null)
                result["item"] = EncodeItem(item);

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetMultipleItems(Dictionary<string, object> request)
        {
            Dictionary<string, object> resultSet = new Dictionary<string, object>();
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            string itemIDstr = request["ITEMS"].ToString();
            int count = 0;
            int.TryParse(request["COUNT"].ToString(), out count);

            UUID[] fids = new UUID[count];
            string[] uuids = itemIDstr.Split(',');
            int i = 0;
            foreach (string id in uuids)
            {
                UUID fid = UUID.Zero;
                if (UUID.TryParse(id, out fid))
                    fids[i] = fid;
                i += 1;
            }

            InventoryItemBase[] itemsList = _InventoryService.GetMultipleItems(principal, fids);
            if (itemsList != null && itemsList.Length > 0)
            {
                count = 0;
                foreach (InventoryItemBase item in itemsList)
                    resultSet["ite_" + count++] = item == null ? (object)"NULL" : EncodeItem(item);
            }

            string xmlString = ServerUtils.BuildXmlResponse(resultSet);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetFolder(Dictionary<string,object> request)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            UUID id = UUID.Zero;
            UUID.TryParse(request["ID"].ToString(), out id);
            UUID user = UUID.Zero;
            if (request.ContainsKey("PRINCIPAL"))
                UUID.TryParse(request["PRINCIPAL"].ToString(), out user);

            InventoryFolderBase folder = _InventoryService.GetFolder(user, id);
            if (folder != null)
                result["folder"] = EncodeFolder(folder);

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetActiveGestures(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);

            List<InventoryItemBase> gestures = _InventoryService.GetActiveGestures(principal);
            Dictionary<string, object> items = new Dictionary<string, object>();
            if (gestures != null)
            {
                int i = 0;
                foreach (InventoryItemBase item in gestures)
                {
                    items["ite_" + i.ToString()] = EncodeItem(item);
                    i++;
                }
            }
            result["ITEMS"] = items;

            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        byte[] HandleGetAssetPermissions(Dictionary<string,object> request)
        {
            Dictionary<string,object> result = new Dictionary<string,object>();
            UUID principal = UUID.Zero;
            UUID.TryParse(request["PRINCIPAL"].ToString(), out principal);
            UUID assetID = UUID.Zero;
            UUID.TryParse(request["ASSET"].ToString(), out assetID);

            int perms = _InventoryService.GetAssetPermissions(principal, assetID);

            result["RESULT"] = perms.ToString();
            string xmlString = ServerUtils.BuildXmlResponse(result);

            //_log.DebugFormat("[XXX]: resp string: {0}", xmlString);
            return Util.UTF8NoBomEncoding.GetBytes(xmlString);
        }

        private Dictionary<string, object> EncodeFolder(InventoryFolderBase f)
        {
            Dictionary<string, object> ret = new Dictionary<string, object>();

            ret["ParentID"] = f.ParentID.ToString();
            ret["Type"] = f.Type.ToString();
            ret["Version"] = f.Version.ToString();
            ret["Name"] = f.Name;
            ret["Owner"] = f.Owner.ToString();
            ret["ID"] = f.ID.ToString();

            return ret;
        }

        private Dictionary<string, object> EncodeItem(InventoryItemBase item)
        {
            Dictionary<string, object> ret = new Dictionary<string, object>();

            ret["AssetID"] = item.AssetID.ToString();
            ret["AssetType"] = item.AssetType.ToString();
            ret["BasePermissions"] = item.BasePermissions.ToString();
            ret["CreationDate"] = item.CreationDate.ToString();
            if (item.CreatorId != null)
                ret["CreatorId"] = item.CreatorId.ToString();
            else
                ret["CreatorId"] = string.Empty;
            if (item.CreatorData != null)
                ret["CreatorData"] = item.CreatorData;
            else
                ret["CreatorData"] = string.Empty;
            ret["CurrentPermissions"] = item.CurrentPermissions.ToString();
            ret["Description"] = item.Description.ToString();
            ret["EveryOnePermissions"] = item.EveryOnePermissions.ToString();
            ret["Flags"] = item.Flags.ToString();
            ret["Folder"] = item.Folder.ToString();
            ret["GroupID"] = item.GroupID.ToString();
            ret["GroupOwned"] = item.GroupOwned.ToString();
            ret["GroupPermissions"] = item.GroupPermissions.ToString();
            ret["ID"] = item.ID.ToString();
            ret["InvType"] = item.InvType.ToString();
            ret["Name"] = item.Name.ToString();
            ret["NextPermissions"] = item.NextPermissions.ToString();
            ret["Owner"] = item.Owner.ToString();
            ret["SalePrice"] = item.SalePrice.ToString();
            ret["SaleType"] = item.SaleType.ToString();

            return ret;
        }

        private InventoryFolderBase BuildFolder(Dictionary<string,object> data)
        {
            InventoryFolderBase folder = new InventoryFolderBase
            {
                ParentID = new UUID(data["ParentID"].ToString()),
                Type = short.Parse(data["Type"].ToString()),
                Version = ushort.Parse(data["Version"].ToString()),
                Name = data["Name"].ToString(),
                Owner = new UUID(data["Owner"].ToString()),
                ID = new UUID(data["ID"].ToString())
            };

            return folder;
        }

        private InventoryItemBase BuildItem(Dictionary<string,object> data)
        {
            InventoryItemBase item = new InventoryItemBase
            {
                AssetID = new UUID(data["AssetID"].ToString()),
                AssetType = int.Parse(data["AssetType"].ToString()),
                Name = data["Name"].ToString(),
                Owner = new UUID(data["Owner"].ToString()),
                ID = new UUID(data["ID"].ToString()),
                InvType = int.Parse(data["InvType"].ToString()),
                Folder = new UUID(data["Folder"].ToString()),
                CreatorId = data["CreatorId"].ToString(),
                CreatorData = data["CreatorData"].ToString(),
                Description = data["Description"].ToString(),
                NextPermissions = uint.Parse(data["NextPermissions"].ToString()),
                CurrentPermissions = uint.Parse(data["CurrentPermissions"].ToString()),
                BasePermissions = uint.Parse(data["BasePermissions"].ToString()),
                EveryOnePermissions = uint.Parse(data["EveryOnePermissions"].ToString()),
                GroupPermissions = uint.Parse(data["GroupPermissions"].ToString()),
                GroupID = new UUID(data["GroupID"].ToString()),
                GroupOwned = bool.Parse(data["GroupOwned"].ToString()),
                SalePrice = int.Parse(data["SalePrice"].ToString()),
                SaleType = byte.Parse(data["SaleType"].ToString()),
                Flags = uint.Parse(data["Flags"].ToString()),
                CreationDate = int.Parse(data["CreationDate"].ToString())
            };

            return item;
        }

    }
}
