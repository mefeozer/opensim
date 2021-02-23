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
using System.Linq;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Groups
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GroupsMessagingModule")]
    public class GroupsMessagingModule : ISharedRegionModule, IGroupsMessagingModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<Scene> _sceneList = new List<Scene>();
        private IPresenceService _presenceService;

        private IMessageTransferModule _msgTransferModule;
        private IUserManagement _userManagement;
        private IGroupsServicesConnector _groupData;

        // Config Options
        private bool _groupMessagingEnabled;
        private bool _debugEnabled;

        /// <summary>
        /// If enabled, module only tries to send group IMs to online users by querying cached presence information.
        /// </summary>
        private bool _messageOnlineAgentsOnly;

        /// <summary>
        /// Cache for online users.
        /// </summary>
        /// <remarks>
        /// Group ID is key, presence information for online members is value.
        /// Will only be non-null if _messageOnlineAgentsOnly = true
        /// We cache here so that group messages don't constantly have to re-request the online user list to avoid
        /// attempted expensive sending of messages to offline users.
        /// The tradeoff is that a user that comes online will not receive messages consistently from all other users
        /// until caches have updated.
        /// Therefore, we set the cache expiry to just 20 seconds.
        /// </remarks>
        private ExpiringCache<UUID, PresenceInfo[]> _usersOnlineCache;

        private const int UsersOnlineCacheExpirySeconds = 20;

        private readonly Dictionary<UUID, List<string>> _groupsAgentsDroppedFromChatSession = new Dictionary<UUID, List<string>>();
        private readonly Dictionary<UUID, List<string>> _groupsAgentsInvitedToChatSession = new Dictionary<UUID, List<string>>();

        #region Region Module interfaceBase Members

        public void Initialise(IConfigSource config)
        {
            IConfig groupsConfig = config.Configs["Groups"];

            if (groupsConfig == null)
                // Do not run this module by default.
                return;

            // if groups aren't enabled, we're not needed.
            // if we're not specified as the connector to use, then we're not wanted
            if (groupsConfig.GetBoolean("Enabled", false) == false
                    || groupsConfig.GetString("MessagingModule", "") != Name)
            {
                _groupMessagingEnabled = false;
                return;
            }

            _groupMessagingEnabled = groupsConfig.GetBoolean("MessagingEnabled", true);

            if (!_groupMessagingEnabled)
                return;

            _messageOnlineAgentsOnly = groupsConfig.GetBoolean("MessageOnlineUsersOnly", false);

            if (_messageOnlineAgentsOnly)
            {
                _usersOnlineCache = new ExpiringCache<UUID, PresenceInfo[]>();
            }
            else
            {
                Log.Error("[Groups.Messaging]: GroupsMessagingModule V2 requires MessageOnlineUsersOnly = true");
                _groupMessagingEnabled = false;
                return;
            }

            _debugEnabled = groupsConfig.GetBoolean("MessagingDebugEnabled", _debugEnabled);

            Log.InfoFormat(
                "[Groups.Messaging]: GroupsMessagingModule enabled with MessageOnlineOnly = {0}, DebugEnabled = {1}",
                _messageOnlineAgentsOnly, _debugEnabled);
        }

        public void AddRegion(Scene scene)
        {
            if (!_groupMessagingEnabled)
                return;

            scene.RegisterModuleInterface<IGroupsMessagingModule>(this);
            _sceneList.Add(scene);

            scene.EventManager.OnNewClient += OnNewClient;
            scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
            scene.EventManager.OnMakeChildAgent += OnMakeChildAgent;
            scene.EventManager.OnIncomingInstantMessage += OnGridInstantMessage;
            scene.EventManager.OnClientLogin += OnClientLogin;

            scene.AddCommand(
                "Debug",
                this,
                "debug groups messaging verbose",
                "debug groups messaging verbose <true|false>",
                "This setting turns on very verbose groups messaging debugging",
                HandleDebugGroupsMessagingVerbose);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_groupMessagingEnabled)
                return;

            if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: {0} called", MethodBase.GetCurrentMethod().Name);

            _groupData = scene.RequestModuleInterface<IGroupsServicesConnector>();

            // No groups module, no groups messaging
            if (_groupData == null)
            {
                Log.Error("[Groups.Messaging]: Could not get IGroupsServicesConnector, GroupsMessagingModule is now disabled.");
                RemoveRegion(scene);
                return;
            }

            _msgTransferModule = scene.RequestModuleInterface<IMessageTransferModule>();

            // No message transfer module, no groups messaging
            if (_msgTransferModule == null)
            {
                Log.Error("[Groups.Messaging]: Could not get MessageTransferModule");
                RemoveRegion(scene);
                return;
            }

            _userManagement = scene.RequestModuleInterface<IUserManagement>();

            // No groups module, no groups messaging
            if (_userManagement == null)
            {
                Log.Error("[Groups.Messaging]: Could not get IUserManagement, GroupsMessagingModule is now disabled.");
                RemoveRegion(scene);
                return;
            }

            if (_presenceService == null)
                _presenceService = scene.PresenceService;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_groupMessagingEnabled)
                return;

            if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: {0} called", MethodBase.GetCurrentMethod().Name);

            _sceneList.Remove(scene);
            scene.EventManager.OnNewClient -= OnNewClient;
            scene.EventManager.OnIncomingInstantMessage -= OnGridInstantMessage;
            scene.EventManager.OnClientLogin -= OnClientLogin;
            scene.UnregisterModuleInterface<IGroupsMessagingModule>(this);
        }

        public void Close()
        {
            if (!_groupMessagingEnabled)
                return;

            if (_debugEnabled) Log.Debug("[Groups.Messaging]: Shutting down GroupsMessagingModule module.");

            _sceneList.Clear();

            _groupData = null;
            _msgTransferModule = null;
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "Groups Messaging Module V2"; }
        }

        public void PostInitialise()
        {
            // NoOp
        }

        #endregion

        private void HandleDebugGroupsMessagingVerbose(object modules, string[] args)
        {
            if (args.Length < 5)
            {
                MainConsole.Instance.Output("Usage: debug groups messaging verbose <true|false>");
                return;
            }

            if (!bool.TryParse(args[4], out var verbose))
            {
                MainConsole.Instance.Output("Usage: debug groups messaging verbose <true|false>");
                return;
            }

            _debugEnabled = verbose;

            MainConsole.Instance.Output("{0} verbose logging set to {1}", Name, _debugEnabled);
        }

        /// <summary>
        /// Not really needed, but does confirm that the group exists.
        /// </summary>
        public bool StartGroupChatSession(UUID agentId, UUID groupId)
        {
            if (_debugEnabled)
                Log.DebugFormat("[Groups.Messaging]: {0} called", MethodBase.GetCurrentMethod().Name);

            GroupRecord groupInfo = _groupData.GetGroupRecord(agentId.ToString(), groupId, null);

            if (groupInfo != null)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public void SendMessageToGroup(GridInstantMessage im, UUID groupId)
        {
            SendMessageToGroup(im, groupId, UUID.Zero, null);
        }

        public void SendMessageToGroup(
            GridInstantMessage im, UUID groupId, UUID sendingAgentForGroupCalls, Func<GroupMembersData, bool> sendCondition)
        {
            int requestStartTick = Environment.TickCount;

            UUID fromAgentId = new UUID(im.fromAgentID);

            // Unlike current XmlRpcGroups, Groups V2 can accept UUID.Zero when a perms check for the requesting agent
            // is not necessary.
            List<GroupMembersData> groupMembers = _groupData.GetGroupMembers(UUID.Zero.ToString(), groupId);

            int groupMembersCount = groupMembers.Count;

            // In V2 we always only send to online members.
            // Sending to offline members is not an option.

            // We cache in order not to overwhelm the presence service on large grids with many groups.  This does
            // mean that members coming online will not see all group members until after _usersOnlineCacheExpirySeconds has elapsed.
            // (assuming this is the same across all grid simulators).
            if (!_usersOnlineCache.TryGetValue(groupId, out var onlineAgents))
            {
                string[] t1 = groupMembers.ConvertAll(gmd => gmd.AgentID.ToString()).ToArray();
                onlineAgents = _presenceService.GetAgents(t1);
                _usersOnlineCache.Add(groupId, onlineAgents, UsersOnlineCacheExpirySeconds);
            }

            HashSet<string> onlineAgentsUuidSet = new HashSet<string>();
            Array.ForEach(onlineAgents, pi => onlineAgentsUuidSet.Add(pi.UserID));

            groupMembers = groupMembers.Where(gmd => onlineAgentsUuidSet.Contains(gmd.AgentID.ToString())).ToList();

            //            if (_debugEnabled)
            //                    _log.DebugFormat(
            //                        "[Groups.Messaging]: SendMessageToGroup called for group {0} with {1} visible members, {2} online",
            //                        groupID, groupMembersCount, groupMembers.Count());

            im.imSessionID = groupId.Guid;
            im.fromGroup = true;
            IClientAPI thisClient = GetActiveClient(fromAgentId);
            if (thisClient != null)
            {
                im.RegionID = thisClient.Scene.RegionInfo.RegionID.Guid;
            }

            if (im.binaryBucket == null || im.binaryBucket.Length == 0 || im.binaryBucket.Length == 1 && im.binaryBucket[0] == 0)
            {
                ExtendedGroupRecord groupInfo = _groupData.GetGroupRecord(UUID.Zero.ToString(), groupId, null);
                if (groupInfo != null)
                    im.binaryBucket = Util.StringToBytes256(groupInfo.GroupName);
            }

            // Send to self first of all
            im.toAgentID = im.fromAgentID;
            im.fromGroup = true;
            ProcessMessageFromGroupSession(im);

            List<UUID> regions = new List<UUID>();
            List<UUID> clientsAlreadySent = new List<UUID>();

            // Then send to everybody else
            foreach (GroupMembersData member in groupMembers)
            {
                if (member.AgentID.Guid == im.fromAgentID)
                    continue;

                if (clientsAlreadySent.Contains(member.AgentID))
                    continue;

                clientsAlreadySent.Add(member.AgentID);

                if (sendCondition != null)
                {
                    if (!sendCondition(member))
                    {
                        if (_debugEnabled)
                            Log.DebugFormat(
                                "[Groups.Messaging]: Not sending to {0} as they do not fulfill send condition",
                                 member.AgentID);

                        continue;
                    }
                }
                else if (HasAgentDroppedGroupChatSession(member.AgentID.ToString(), groupId))
                {
                    // Don't deliver messages to people who have dropped this session
                    if (_debugEnabled)
                        Log.DebugFormat("[Groups.Messaging]: {0} has dropped session, not delivering to them", member.AgentID);

                    continue;
                }

                im.toAgentID = member.AgentID.Guid;

                IClientAPI client = GetActiveClient(member.AgentID);
                if (client == null)
                {
                    // If they're not local, forward across the grid
                    // BUT do it only once per region, please! Sim would be even better!
                    if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Delivering to {0} via Grid", member.AgentID);

                    bool reallySend = true;

                    PresenceInfo presence = onlineAgents.First(p => p.UserID == member.AgentID.ToString());
                    if (regions.Contains(presence.RegionID))
                        reallySend = false;
                    else
                        regions.Add(presence.RegionID);


                    if (reallySend)
                    {
                        // We have to create a new IM structure because the transfer module
                        // uses async send
                        GridInstantMessage msg = new GridInstantMessage(im, true);
                        _msgTransferModule.SendInstantMessage(msg, delegate { });
                    }
                }
                else
                {
                    // Deliver locally, directly
                    if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Passing to ProcessMessageFromGroupSession to deliver to {0} locally", client.Name);

                    ProcessMessageFromGroupSession(im);
                }

            }

            if (_debugEnabled)
                Log.DebugFormat(
                    "[Groups.Messaging]: SendMessageToGroup for group {0} with {1} visible members, {2} online took {3}ms",
                    groupId, groupMembersCount, groupMembers.Count, Environment.TickCount - requestStartTick);
        }

        #region SimGridEventHandlers

        void OnClientLogin(IClientAPI client)
        {
            if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: OnInstantMessage registered for {0}", client.Name);
        }

        private void OnNewClient(IClientAPI client)
        {
            if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: OnInstantMessage registered for {0}", client.Name);

            ResetAgentGroupChatSessions(client.AgentId.ToString());
        }

        void OnMakeRootAgent(ScenePresence sp)
        {
            sp.ControllingClient.OnInstantMessage += OnInstantMessage;
        }

        void OnMakeChildAgent(ScenePresence sp)
        {
            sp.ControllingClient.OnInstantMessage -= OnInstantMessage;
        }


        private void OnGridInstantMessage(GridInstantMessage msg)
        {
            // The instant message module will only deliver messages of dialog types:
            // MessageFromAgent, StartTyping, StopTyping, MessageFromObject
            //
            // Any other message type will not be delivered to a client by the
            // Instant Message Module

            UUID regionId = new UUID(msg.RegionID);
            if (_debugEnabled)
            {
                Log.DebugFormat("[Groups.Messaging]: {0} called, IM from region {1}",
                    MethodBase.GetCurrentMethod().Name, regionId);

                DebugGridInstantMessage(msg);
            }

            // Incoming message from a group
            if (!msg.fromGroup || msg.dialog != (byte) InstantMessageDialog.SessionSend) return;
            // We have to redistribute the message across all members of the group who are here
            // on this sim

            UUID groupId = new UUID(msg.imSessionID);

            Scene aScene = _sceneList[0];
            GridRegion regionOfOrigin = aScene.GridService.GetRegionByUUID(aScene.RegionInfo.ScopeID, regionId);

            List<GroupMembersData> groupMembers = _groupData.GetGroupMembers(UUID.Zero.ToString(), groupId);

            //if (_debugEnabled)
            //    foreach (GroupMembersData m in groupMembers)
            //        _log.DebugFormat("[Groups.Messaging]: member {0}", m.AgentID);

            foreach (Scene s in _sceneList)
            {
                s.ForEachScenePresence(sp =>
                {
                    // If we got this via grid messaging, it's because the caller thinks
                    // that the root agent is here. We should only send the IM to root agents.
                    if (sp.IsChildAgent)
                        return;

                    GroupMembersData m = groupMembers.Find(gmd => gmd.AgentID == sp.UUID);
                    if (m.AgentID == UUID.Zero)
                    {
                        if (_debugEnabled)
                            Log.DebugFormat("[Groups.Messaging]: skipping agent {0} because he is not a member of the group", sp.UUID);
                        return;
                    }

                    // Check if the user has an agent in the region where
                    // the IM came from, and if so, skip it, because the IM
                    // was already sent via that agent
                    if (regionOfOrigin != null)
                    {
                        AgentCircuitData aCircuit = s.AuthenticateHandler.GetAgentCircuitData(sp.UUID);
                        if (aCircuit != null)
                        {
                            if (aCircuit.ChildrenCapSeeds.Keys.Contains(regionOfOrigin.RegionHandle))
                            {
                                if (_debugEnabled)
                                    Log.DebugFormat("[Groups.Messaging]: skipping agent {0} because he has an agent in region of origin", sp.UUID);
                                return;
                            }
                            else
                            {
                                if (_debugEnabled)
                                    Log.DebugFormat("[Groups.Messaging]: not skipping agent {0}", sp.UUID);
                            }
                        }
                    }

                    UUID agentId = sp.UUID;
                    msg.toAgentID = agentId.Guid;

                    if (!HasAgentDroppedGroupChatSession(agentId.ToString(), groupId))
                    {
                        if (!HasAgentBeenInvitedToGroupChatSession(agentId.ToString(), groupId))
                            AddAgentToSession(agentId, groupId, msg);
                        else
                        {
                            if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Passing to ProcessMessageFromGroupSession to deliver to {0} locally", sp.Name);

                            ProcessMessageFromGroupSession(msg);
                        }
                    }
                });

            }
        }

        private void ProcessMessageFromGroupSession(GridInstantMessage msg)
        {
            if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Session message from {0} going to agent {1}", msg.fromAgentName, msg.toAgentID);

            UUID agentId = new UUID(msg.fromAgentID);
            UUID groupId = new UUID(msg.imSessionID);
            UUID toAgentId = new UUID(msg.toAgentID);

            switch (msg.dialog)
            {
                case (byte)InstantMessageDialog.SessionAdd:
                    AgentInvitedToGroupChatSession(agentId.ToString(), groupId);
                    break;

                case (byte)InstantMessageDialog.SessionDrop:
                    AgentDroppedFromGroupChatSession(agentId.ToString(), groupId);
                    break;

                case (byte)InstantMessageDialog.SessionSend:
                    // User hasn't dropped, so they're in the session,
                    // maybe we should deliver it.
                    IClientAPI client = GetActiveClient(new UUID(msg.toAgentID));
                    if (client != null)
                    {
                        // Deliver locally, directly
                        if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Delivering to {0} locally", client.Name);

                        if (!HasAgentDroppedGroupChatSession(toAgentId.ToString(), groupId))
                        {
                            if (!HasAgentBeenInvitedToGroupChatSession(toAgentId.ToString(), groupId))
                                // This actually sends the message too, so no need to resend it
                                // with client.SendInstantMessage
                                AddAgentToSession(toAgentId, groupId, msg);
                            else
                                client.SendInstantMessage(msg);
                        }
                    }
                    else
                    {
                        Log.WarnFormat("[Groups.Messaging]: Received a message over the grid for a client that isn't here: {0}", msg.toAgentID);
                    }
                    break;

                default:
                    Log.WarnFormat("[Groups.Messaging]: I don't know how to proccess a {0} message.", ((InstantMessageDialog)msg.dialog).ToString());
                    break;
            }
        }

        private void AddAgentToSession(UUID agentId, UUID groupId, GridInstantMessage msg)
        {
            // Agent not in session and hasn't dropped from session
            // Add them to the session for now, and Invite them
            AgentInvitedToGroupChatSession(agentId.ToString(), groupId);

            IClientAPI activeClient = GetActiveClient(agentId);
            if (activeClient != null)
            {
                GroupRecord groupInfo = _groupData.GetGroupRecord(UUID.Zero.ToString(), groupId, null);
                if (groupInfo != null)
                {
                    if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Sending chatterbox invite instant message");

                    UUID fromAgent = new UUID(msg.fromAgentID);
                    // Force? open the group session dialog???
                    // and simultanously deliver the message, so we don't need to do a seperate client.SendInstantMessage(msg);
                    IEventQueue eq = activeClient.Scene.RequestModuleInterface<IEventQueue>();
                    if (eq != null)
                    {
                        eq.ChatterboxInvitation(
                            groupId
                            , groupInfo.GroupName
                            , fromAgent
                            , msg.message
                            , agentId
                            , msg.fromAgentName
                            , msg.dialog
                            , msg.timestamp
                            , msg.offline == 1
                            , (int)msg.ParentEstateID
                            , msg.Position
                            , 1
                            , new UUID(msg.imSessionID)
                            , msg.fromGroup
                            , Utils.StringToBytes(groupInfo.GroupName)
                            );

                        var update = new GroupChatListAgentUpdateData(agentId);
                        var updates = new List<GroupChatListAgentUpdateData> { update };
                        eq.ChatterBoxSessionAgentListUpdates(groupId, new UUID(msg.toAgentID), updates);
                    }
                }
            }
        }

        #endregion


        #region ClientEvents
        private void OnInstantMessage(IClientAPI remoteClient, GridInstantMessage im)
        {
            if (_debugEnabled)
            {
                Log.DebugFormat("[Groups.Messaging]: {0} called", MethodBase.GetCurrentMethod().Name);

                DebugGridInstantMessage(im);
            }

            // Start group IM session
            if (im.dialog == (byte)InstantMessageDialog.SessionGroupStart)
            {
                if (_debugEnabled) Log.InfoFormat("[Groups.Messaging]: imSessionID({0}) toAgentID({1})", im.imSessionID, im.toAgentID);

                UUID groupId = new UUID(im.imSessionID);
                UUID agentId = new UUID(im.fromAgentID);

                GroupRecord groupInfo = _groupData.GetGroupRecord(UUID.Zero.ToString(), groupId, null);

                if (groupInfo != null)
                {
                    AgentInvitedToGroupChatSession(agentId.ToString(), groupId);

                    ChatterBoxSessionStartReplyViaCaps(remoteClient, groupInfo.GroupName, groupId);

                    IEventQueue queue = remoteClient.Scene.RequestModuleInterface<IEventQueue>();
                    if (queue != null)
                    {
                        var update = new GroupChatListAgentUpdateData(agentId);
                        var updates = new List<GroupChatListAgentUpdateData> { update };
                        queue.ChatterBoxSessionAgentListUpdates(groupId, remoteClient.AgentId, updates);
                    }
                }
            }

            // Send a message from locally connected client to a group
            if (im.dialog == (byte)InstantMessageDialog.SessionSend)
            {
                UUID groupId = new UUID(im.imSessionID);
                UUID agentId = new UUID(im.fromAgentID);

                if (_debugEnabled)
                    Log.DebugFormat("[Groups.Messaging]: Send message to session for group {0} with session ID {1}", groupId, im.imSessionID.ToString());

                //If this agent is sending a message, then they want to be in the session
                AgentInvitedToGroupChatSession(agentId.ToString(), groupId);

                SendMessageToGroup(im, groupId);
            }
        }

        #endregion

        void ChatterBoxSessionStartReplyViaCaps(IClientAPI remoteClient, string groupName, UUID groupId)
        {
            if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: {0} called", MethodBase.GetCurrentMethod().Name);

            OSDMap moderatedMap = new OSDMap(4)
            {
                { "voice", OSD.FromBoolean(false) }
            };

            OSDMap sessionMap = new OSDMap(4)
            {
                { "moderated_mode", moderatedMap },
                { "session_name", OSD.FromString(groupName) },
                { "type", OSD.FromInteger(0) },
                { "voice_enabled", OSD.FromBoolean(false) }
            };

            OSDMap bodyMap = new OSDMap(4)
            {
                { "session_id", OSD.FromUUID(groupId) },
                { "temp_session_id", OSD.FromUUID(groupId) },
                { "success", OSD.FromBoolean(true) },
                { "session_info", sessionMap }
            };

            IEventQueue queue = remoteClient.Scene.RequestModuleInterface<IEventQueue>();
            queue?.Enqueue(queue.BuildEvent("ChatterBoxSessionStartReply", bodyMap), remoteClient.AgentId);
        }

        private void DebugGridInstantMessage(GridInstantMessage im)
        {
            // Don't log any normal IMs (privacy!)
            if (_debugEnabled && im.dialog != (byte)InstantMessageDialog.MessageFromAgent)
            {
                Log.WarnFormat("[Groups.Messaging]: IM: fromGroup({0})", im.fromGroup ? "True" : "False");
                Log.WarnFormat("[Groups.Messaging]: IM: Dialog({0})", ((InstantMessageDialog)im.dialog).ToString());
                Log.WarnFormat("[Groups.Messaging]: IM: fromAgentID({0})", im.fromAgentID.ToString());
                Log.WarnFormat("[Groups.Messaging]: IM: fromAgentName({0})", im.fromAgentName);
                Log.WarnFormat("[Groups.Messaging]: IM: imSessionID({0})", im.imSessionID.ToString());
                Log.WarnFormat("[Groups.Messaging]: IM: message({0})", im.message);
                Log.WarnFormat("[Groups.Messaging]: IM: offline({0})", im.offline.ToString());
                Log.WarnFormat("[Groups.Messaging]: IM: toAgentID({0})", im.toAgentID.ToString());
                Log.WarnFormat("[Groups.Messaging]: IM: binaryBucket({0})", Utils.BytesToHexString(im.binaryBucket, "BinaryBucket"));
            }
        }

        #region Client Tools

        /// <summary>
        /// Try to find an active IClientAPI reference for agentID giving preference to root connections
        /// </summary>
        private IClientAPI GetActiveClient(UUID agentId)
        {
            if (_debugEnabled) Log.WarnFormat("[Groups.Messaging]: Looking for local client {0}", agentId);

            IClientAPI child = null;

            // Try root avatar first
            foreach (Scene scene in _sceneList)
            {
                ScenePresence sp = scene.GetScenePresence(agentId);
                if (sp != null)
                {
                    if (!sp.IsChildAgent)
                    {
                        if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Found root agent for client : {0}", sp.ControllingClient.Name);
                        return sp.ControllingClient;
                    }
                    else
                    {
                        if (_debugEnabled) Log.DebugFormat("[Groups.Messaging]: Found child agent for client : {0}", sp.ControllingClient.Name);
                        child = sp.ControllingClient;
                    }
                }
            }

            // If we didn't find a root, then just return whichever child we found, or null if none
            if (child == null)
            {
                if (_debugEnabled) Log.WarnFormat("[Groups.Messaging]: Could not find local client for agent : {0}", agentId);
            }
            else
            {
                if (_debugEnabled) Log.WarnFormat("[Groups.Messaging]: Returning child agent for client : {0}", child.Name);
            }
            return child;
        }

        #endregion

        #region GroupSessionTracking

        public void ResetAgentGroupChatSessions(string agentId)
        {
            foreach (List<string> agentList in _groupsAgentsDroppedFromChatSession.Values)
                agentList.Remove(agentId);

            foreach (List<string> agentList in _groupsAgentsInvitedToChatSession.Values)
                agentList.Remove(agentId);
        }

        public bool HasAgentBeenInvitedToGroupChatSession(string agentId, UUID groupId)
        {
            // If we're  tracking this group, and we can find them in the tracking, then they've been invited
            return _groupsAgentsInvitedToChatSession.ContainsKey(groupId)
                && _groupsAgentsInvitedToChatSession[groupId].Contains(agentId);
        }

        public bool HasAgentDroppedGroupChatSession(string agentId, UUID groupId)
        {
            // If we're tracking drops for this group,
            // and we find them, well... then they've dropped
            return _groupsAgentsDroppedFromChatSession.ContainsKey(groupId)
                && _groupsAgentsDroppedFromChatSession[groupId].Contains(agentId);
        }

        public void AgentDroppedFromGroupChatSession(string agentId, UUID groupId)
        {
            if (_groupsAgentsDroppedFromChatSession.ContainsKey(groupId))
            {
                // If not in dropped list, add
                if (!_groupsAgentsDroppedFromChatSession[groupId].Contains(agentId))
                {
                    _groupsAgentsDroppedFromChatSession[groupId].Add(agentId);
                }
            }
        }

        public void AgentInvitedToGroupChatSession(string agentId, UUID groupId)
        {
            // Add Session Status if it doesn't exist for this session
            CreateGroupChatSessionTracking(groupId);

            // If nessesary, remove from dropped list
            if (_groupsAgentsDroppedFromChatSession[groupId].Contains(agentId))
            {
                _groupsAgentsDroppedFromChatSession[groupId].Remove(agentId);
            }

            // Add to invited
            if (!_groupsAgentsInvitedToChatSession[groupId].Contains(agentId))
                _groupsAgentsInvitedToChatSession[groupId].Add(agentId);
        }

        private void CreateGroupChatSessionTracking(UUID groupId)
        {
            if (!_groupsAgentsDroppedFromChatSession.ContainsKey(groupId))
            {
                _groupsAgentsDroppedFromChatSession.Add(groupId, new List<string>());
                _groupsAgentsInvitedToChatSession.Add(groupId, new List<string>());
            }

        }
        #endregion

    }
}
