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
using OpenSim.Services.Interfaces;

namespace OpenSim.OfflineIM
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OfflineIMConnectorModule")]
    public class OfflineIMRegionModule : ISharedRegionModule, IOfflineIMService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled = false;
        private readonly List<Scene> _SceneList = new List<Scene>();
        IMessageTransferModule _TransferModule = null;
        private bool _ForwardOfflineGroupMessages = true;

        private IOfflineIMService _OfflineIMService;

        public void Initialise(IConfigSource config)
        {
            IConfig cnf = config.Configs["Messaging"];
            if (cnf == null)
                return;
            if (cnf != null && cnf.GetString("OfflineMessageModule", string.Empty) != Name)
                return;

            _Enabled = true;

            string serviceLocation = cnf.GetString("OfflineMessageURL", string.Empty);
            if (string.IsNullOrEmpty(serviceLocation))
                _OfflineIMService = new OfflineIMService(config);
            else
                _OfflineIMService = new OfflineIMServiceRemoteConnector(config);

            _ForwardOfflineGroupMessages = cnf.GetBoolean("ForwardOfflineGroupMessages", _ForwardOfflineGroupMessages);
            _log.DebugFormat("[OfflineIM.V2]: Offline messages enabled by {0}", Name);
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            scene.RegisterModuleInterface<IOfflineIMService>(this);
            _SceneList.Add(scene);
            scene.EventManager.OnNewClient += OnNewClient;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            if (_TransferModule == null)
            {
                _TransferModule = scene.RequestModuleInterface<IMessageTransferModule>();
                if (_TransferModule == null)
                {
                    scene.EventManager.OnNewClient -= OnNewClient;

                    _SceneList.Clear();

                    _log.Error("[OfflineIM.V2]: No message transfer module is enabled. Disabling offline messages");
                }
                _TransferModule.OnUndeliveredMessage += UndeliveredMessage;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _SceneList.Remove(scene);
            scene.EventManager.OnNewClient -= OnNewClient;
            _TransferModule.OnUndeliveredMessage -= UndeliveredMessage;

            scene.ForEachClient(delegate(IClientAPI client)
            {
                client.OnRetrieveInstantMessages -= RetrieveInstantMessages;
            });
        }

        public void PostInitialise()
        {
        }

        public string Name => "Offline Message Module V2";

        public Type ReplaceableInterface => null;

        public void Close()
        {
            _SceneList.Clear();
        }

        private Scene FindScene(UUID agentID)
        {
            foreach (Scene s in _SceneList)
            {
                ScenePresence presence = s.GetScenePresence(agentID);
                if (presence != null && !presence.IsChildAgent)
                    return s;
            }
            return null;
        }

        private IClientAPI FindClient(UUID agentID)
        {
            foreach (Scene s in _SceneList)
            {
                ScenePresence presence = s.GetScenePresence(agentID);
                if (presence != null && !presence.IsChildAgent)
                    return presence.ControllingClient;
            }
            return null;
        }

        private void OnNewClient(IClientAPI client)
        {
            client.OnRetrieveInstantMessages += RetrieveInstantMessages;
        }

        private void RetrieveInstantMessages(IClientAPI client)
        {
            _log.DebugFormat("[OfflineIM.V2]: Retrieving stored messages for {0}", client.AgentId);

            List<GridInstantMessage> msglist = _OfflineIMService.GetMessages(client.AgentId);

            if (msglist == null)
                _log.DebugFormat("[OfflineIM.V2]: WARNING null message list.");

            foreach (GridInstantMessage im in msglist)
            {
                if (im.dialog == (byte)InstantMessageDialog.InventoryOffered)
                    // send it directly or else the item will be given twice
                    client.SendInstantMessage(im);
                else
                {
                    // Send through scene event manager so all modules get a chance
                    // to look at this message before it gets delivered.
                    //
                    // Needed for proper state management for stored group
                    // invitations
                    //
                    Scene s = FindScene(client.AgentId);
                    if (s != null)
                        s.EventManager.TriggerIncomingInstantMessage(im);
                }
            }
        }

        private void UndeliveredMessage(GridInstantMessage im)
        {
            if (im.dialog != (byte)InstantMessageDialog.MessageFromObject &&
                im.dialog != (byte)InstantMessageDialog.MessageFromAgent &&
                im.dialog != (byte)InstantMessageDialog.GroupNotice &&
                im.dialog != (byte)InstantMessageDialog.GroupInvitation &&
                im.dialog != (byte)InstantMessageDialog.InventoryOffered)
            {
                return;
            }

            if (!_ForwardOfflineGroupMessages)
            {
                if (im.dialog == (byte)InstantMessageDialog.GroupNotice ||
                    im.dialog == (byte)InstantMessageDialog.GroupInvitation)
                    return;
            }

            string reason = string.Empty;
            bool success = _OfflineIMService.StoreMessage(im, out reason);

            if (im.dialog == (byte)InstantMessageDialog.MessageFromAgent)
            {
                IClientAPI client = FindClient(new UUID(im.fromAgentID));
                if (client == null)
                    return;

                client.SendInstantMessage(new GridInstantMessage(
                        null, new UUID(im.toAgentID),
                        "System", new UUID(im.fromAgentID),
                        (byte)InstantMessageDialog.MessageFromAgent,
                        "User is not logged in. " +
                        (success ? "Message saved." : "Message not saved: " + reason),
                        false, new Vector3()));
            }
        }

        #region IOfflineIM

        public List<GridInstantMessage> GetMessages(UUID principalID)
        {
            return _OfflineIMService.GetMessages(principalID);
        }

        public bool StoreMessage(GridInstantMessage im, out string reason)
        {
            return _OfflineIMService.StoreMessage(im, out reason);
        }

        public void DeleteMessages(UUID userID)
        {
            _OfflineIMService.DeleteMessages(userID);
        }

        #endregion
    }
}

