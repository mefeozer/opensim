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

namespace OpenSim.Region.CoreModules.Avatar.InstantMessage
{
    public struct SendReply
    {
        public bool Success;
        public string Message;
        public int Disposition;
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "OfflineMessageModule")]
    public class OfflineMessageModule : ISharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool enabled = true;
        private bool _UseNewAvnCode = false;
        private readonly List<Scene> _SceneList = new List<Scene>();
        private string _RestURL = string.Empty;
        IMessageTransferModule _TransferModule = null;
        private bool _ForwardOfflineGroupMessages = true;
        private readonly Dictionary<IClientAPI, List<UUID>> _repliesSent= new Dictionary<IClientAPI, List<UUID>>();

        public void Initialise(IConfigSource config)
        {
            IConfig cnf = config.Configs["Messaging"];
            if (cnf == null)
            {
                enabled = false;
                return;
            }
            if (cnf != null && cnf.GetString("OfflineMessageModule", "None") !=
                    "OfflineMessageModule")
            {
                enabled = false;
                return;
            }

            _RestURL = cnf.GetString("OfflineMessageURL", "");
            if (_RestURL == "")
            {
                _log.Error("[OFFLINE MESSAGING] Module was enabled, but no URL is given, disabling");
                enabled = false;
                return;
            }

            _ForwardOfflineGroupMessages = cnf.GetBoolean("ForwardOfflineGroupMessages", _ForwardOfflineGroupMessages);
            _UseNewAvnCode = cnf.GetBoolean("UseNewAvnCode", _UseNewAvnCode);
        }

        public void AddRegion(Scene scene)
        {
            if (!enabled)
                return;

            lock (_SceneList)
            {
                _SceneList.Add(scene);

                scene.EventManager.OnNewClient += OnNewClient;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!enabled)
                return;

            if (_TransferModule == null)
            {
                _TransferModule = scene.RequestModuleInterface<IMessageTransferModule>();
                if (_TransferModule == null)
                {
                    scene.EventManager.OnNewClient -= OnNewClient;

                    enabled = false;
                    _SceneList.Clear();

                    _log.Error("[OFFLINE MESSAGING] No message transfer module is enabled. Diabling offline messages");
                }
                _TransferModule.OnUndeliveredMessage += UndeliveredMessage;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!enabled)
                return;

            lock (_SceneList)
            {
                _SceneList.Remove(scene);
            }
        }

        public void PostInitialise()
        {
            if (!enabled)
                return;

            _log.Debug("[OFFLINE MESSAGING] Offline messages enabled");
        }

        public string Name => "OfflineMessageModule";

        public Type ReplaceableInterface => null;

        public void Close()
        {
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
            client.OnLogout += OnClientLoggedOut;
        }

        public void OnClientLoggedOut(IClientAPI client)
        {
            _repliesSent.Remove(client);
        }

        private void RetrieveInstantMessages(IClientAPI client)
        {
            if (string.IsNullOrEmpty(_RestURL))
            {
                return;
            }
            else
            {
                _log.DebugFormat("[OFFLINE MESSAGING]: Retrieving stored messages for {0}", client.AgentId);

                List<GridInstantMessage> msglist
                    = SynchronousRestObjectRequester.MakeRequest<UUID, List<GridInstantMessage>>(
                        "POST", _RestURL + "/RetrieveMessages/", client.AgentId);

                if (msglist != null)
                {
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

                            im.offline = 1;

                            Scene s = FindScene(client.AgentId);
                            if (s != null)
                                s.EventManager.TriggerIncomingInstantMessage(im);
                        }
                    }
                }
            }
        }

        private void UndeliveredMessage(GridInstantMessage im)
        {
            if (im.dialog != (byte)InstantMessageDialog.MessageFromObject &&
                im.dialog != (byte)InstantMessageDialog.MessageFromAgent &&
                im.dialog != (byte)InstantMessageDialog.GroupNotice &&
                im.dialog != (byte)InstantMessageDialog.GroupInvitation &&
                im.dialog != (byte)InstantMessageDialog.InventoryOffered &&
                im.dialog != (byte)InstantMessageDialog.TaskInventoryOffered)
            {
                return;
            }

            if (!_ForwardOfflineGroupMessages)
            {
                if (im.dialog == (byte)InstantMessageDialog.GroupNotice ||
                    im.dialog == (byte)InstantMessageDialog.GroupInvitation)
                    return;
            }

            if(_UseNewAvnCode)
            {
                Scene scene = FindScene(new UUID(im.fromAgentID));
                if (scene == null)
                    scene = _SceneList[0];

                UUID scopeID = scene.RegionInfo.ScopeID;
                SendReply reply = SynchronousRestObjectRequester.MakeRequest<GridInstantMessage, SendReply>(
                    "POST", _RestURL+"/SaveMessage/?scope=" + scopeID.ToString(), im, 20000);

                if (im.dialog == (byte)InstantMessageDialog.MessageFromAgent)
                {
                    IClientAPI client = FindClient(new UUID(im.fromAgentID));
                    if (client == null)
                        return;

                    if (string.IsNullOrEmpty(reply.Message))
                        reply.Message = "User is not logged in. " + (reply.Success ? "Message saved." : "Message not saved");

                    bool sendReply = true;

                    switch (reply.Disposition)
                    {
                        case 0: // Normal
                            break;
                        case 1: // Only once per user
                           if (_repliesSent.ContainsKey(client) && _repliesSent[client].Contains(new UUID(im.toAgentID)))
                                sendReply = false;
                            else
                            {
                                if (!_repliesSent.ContainsKey(client))
                                    _repliesSent[client] = new List<UUID>();
                                _repliesSent[client].Add(new UUID(im.toAgentID));
                            }
                            break;
                    }

                    if (sendReply)
                    {
                        client.SendInstantMessage(new GridInstantMessage(
                                null, new UUID(im.toAgentID),
                                "System", new UUID(im.fromAgentID),
                                (byte)InstantMessageDialog.MessageFromAgent,
                                reply.Message,
                                false, new Vector3()));
                    }
                }
            }
            else
            {
                bool success = SynchronousRestObjectRequester.MakeRequest<GridInstantMessage, bool>(
                    "POST", _RestURL+"/SaveMessage/", im, 20000);

                if (im.dialog == (byte)InstantMessageDialog.MessageFromAgent)
                {
                    IClientAPI client = FindClient(new UUID(im.fromAgentID));
                    if (client == null)
                        return;

                    client.SendInstantMessage(new GridInstantMessage(
                        null, new UUID(im.toAgentID),
                        "System", new UUID(im.fromAgentID),
                        (byte)InstantMessageDialog.MessageFromAgent,
                        "User is not logged in. "+
                        (success ? "Message saved." : "Message not saved"),
                        false, new Vector3()));
                }
            }
        }
    }
}

