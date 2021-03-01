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
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Scenes.Serialization;
using PermissionMask = OpenSim.Framework.PermissionMask;

namespace OpenSim.Region.CoreModules.World.Objects.BuySell
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "BuySellModule")]
    public class BuySellModule : IBuySellModule, INonSharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Scene _scene = null;
        protected IDialogModule _dialogModule;

        public string Name => "Object BuySell Module";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source) {}

        public void AddRegion(Scene scene)
        {
            _scene = scene;
            _scene.RegisterModuleInterface<IBuySellModule>(this);
            _scene.EventManager.OnNewClient += SubscribeToClientEvents;
        }

        public void RemoveRegion(Scene scene)
        {
            _scene.EventManager.OnNewClient -= SubscribeToClientEvents;
        }

        public void RegionLoaded(Scene scene)
        {
            _dialogModule = scene.RequestModuleInterface<IDialogModule>();
        }

        public void Close()
        {
            RemoveRegion(_scene);
        }

        public void SubscribeToClientEvents(IClientAPI client)
        {
            client.OnObjectSaleInfo += ObjectSaleInfo;
        }

        protected void ObjectSaleInfo(
            IClientAPI client, UUID agentID, UUID sessionID, uint localID, byte saleType, int salePrice)
        {
            SceneObjectPart part = _scene.GetSceneObjectPart(localID);
            if (part == null)
                return;

            SceneObjectGroup sog = part.ParentGroup;
            if (sog == null || sog.IsDeleted)
                return;

            // Does the user have the power to put the object on sale?
            if (!_scene.Permissions.CanSellObject(client, sog, saleType))
            {
                client.SendAgentAlertMessage("You don't have permission to set object on sale", false);
                return;
            }

            part = sog.RootPart;

            part.ObjectSaleType = saleType;
            part.SalePrice = salePrice;

            sog.HasGroupChanged = true;

            part.SendPropertiesToClient(client);
        }

        public bool BuyObject(IClientAPI remoteClient, UUID categoryID, uint localID, byte saleType, int salePrice)
        {
            SceneObjectPart rootpart = _scene.GetSceneObjectPart(localID);

            if (rootpart == null)
                return false;

            SceneObjectGroup group = rootpart.ParentGroup;
            if(group == null || group.IsDeleted || group.inTransit)
                return false;

            // make sure we are not buying a child part
            rootpart = group.RootPart;            

            switch ((SaleType)saleType)
            {
                case SaleType.Original: // Sell as original (in-place sale)
                    uint effectivePerms = group.EffectiveOwnerPerms;

                    if ((effectivePerms & (uint)PermissionMask.Transfer) == 0)
                    {
                        if (_dialogModule != null)
                            _dialogModule.SendAlertToUser(remoteClient, "This item doesn't appear to be for sale");
                        return false;
                    }

                    group.SetOwner(remoteClient.AgentId, remoteClient.ActiveGroupId);

                    if (_scene.Permissions.PropagatePermissions())
                    {
                        foreach (SceneObjectPart child in group.Parts)
                        {
                            child.Inventory.ChangeInventoryOwner(remoteClient.AgentId);
                            child.TriggerScriptChangedEvent(Changed.OWNER);
                            child.ApplyNextOwnerPermissions();
                        }
                        group.InvalidateDeepEffectivePerms();
                    }

                    rootpart.ObjectSaleType = 0;
                    rootpart.SalePrice = 10;
                    rootpart.ClickAction = 0;

                    group.HasGroupChanged = true;
                    rootpart.SendPropertiesToClient(remoteClient);
                    rootpart.TriggerScriptChangedEvent(Changed.OWNER);
                    group.ResumeScripts();
                    rootpart.ScheduleFullUpdate();

                    break;

                case SaleType.Copy: // Sell a copy
                    uint perms = group.EffectiveOwnerPerms;

                    if ((perms & (uint)PermissionMask.Transfer) == 0)
                    {
                        if (_dialogModule != null)
                            _dialogModule.SendAlertToUser(remoteClient, "This item doesn't appear to be for sale");
                        return false;
                    }

                    if ((perms & (uint)PermissionMask.Copy) == 0)
                    {
                        if (_dialogModule != null)
                            _dialogModule.SendAlertToUser(remoteClient, "This sale has been blocked by the permissions system");
                        return false;
                    }

                    // save sell data
                    int price = rootpart.SalePrice;
                    byte clickAction = rootpart.ClickAction;

                    // reset sale data for the copy
                    rootpart.ObjectSaleType = 0;
                    rootpart.SalePrice = 10;
                    rootpart.ClickAction = 0;

                    string sceneObjectXml = SceneObjectSerializer.ToOriginalXmlFormat(group);

                    rootpart.ObjectSaleType = saleType;
                    rootpart.SalePrice = price;
                    rootpart.ClickAction = clickAction;

                    string name = rootpart.Name;
                    string desc = rootpart.Description;

                    AssetBase asset = _scene.CreateAsset(
                        name, desc,
                        (sbyte)AssetType.Object,
                        Utils.StringToBytes(sceneObjectXml),
                        rootpart.CreatorID);
                    _scene.AssetService.Store(asset);

                    InventoryItemBase item = new InventoryItemBase
                    {
                        CreatorId = rootpart.CreatorID.ToString(),
                        CreatorData = rootpart.CreatorData,

                        ID = UUID.Random(),
                        Owner = remoteClient.AgentId,
                        AssetID = asset.FullID,
                        Description = desc,
                        Name = name,
                        AssetType = asset.Type,
                        InvType = (int)InventoryType.Object,
                        Folder = categoryID
                    };

                    perms = group.CurrentAndFoldedNextPermissions();
                    // apply parts inventory next perms            
                    PermissionsUtil.ApplyNoModFoldedPermissions(perms, ref perms);
                    // change to next owner perms
                    perms &=  rootpart.NextOwnerMask; 
                    // update folded
                    perms = PermissionsUtil.FixAndFoldPermissions(perms);

                    item.BasePermissions = perms;
                    item.CurrentPermissions = perms;
                    item.NextPermissions = rootpart.NextOwnerMask & perms;
                    item.EveryOnePermissions = rootpart.EveryoneMask & perms;
                    item.GroupPermissions = rootpart.GroupMask & perms;

                    item.Flags |= (uint)InventoryItemFlags.ObjectSlamPerm;
                    item.CreationDate = Util.UnixTimeSinceEpoch();

                    if (_scene.AddInventoryItem(item))
                    {
                        remoteClient.SendInventoryItemCreateUpdate(item, 0);
                    }
                    else
                    {
                        if (_dialogModule != null)
                            _dialogModule.SendAlertToUser(remoteClient, "Cannot buy now. Your inventory is unavailable");
                        return false;
                    }
                    break;

                case SaleType.Contents: // Sell contents
                    List<UUID> invList = rootpart.Inventory.GetInventoryList();

                    bool okToSell = true;

                    foreach (UUID invID in invList)
                    {
                        TaskInventoryItem item1 = rootpart.Inventory.GetInventoryItem(invID);
                        if ((item1.CurrentPermissions &
                                (uint)PermissionMask.Transfer) == 0)
                        {
                            okToSell = false;
                            break;
                        }
                    }

                    if (!okToSell)
                    {
                        if (_dialogModule != null)
                            _dialogModule.SendAlertToUser(
                                remoteClient, "This item's inventory doesn't appear to be for sale");
                        return false;
                    }

                    if (invList.Count > 0)
                        _scene.MoveTaskInventoryItems(remoteClient.AgentId, rootpart.Name, rootpart, invList);
                    break;

                default:
                    return false;
            }

            return true;
        }
    }
}
