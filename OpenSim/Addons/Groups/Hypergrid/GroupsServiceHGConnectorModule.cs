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

using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;

using OpenMetaverse;
using Mono.Addins;
using log4net;
using Nini.Config;

namespace OpenSim.Groups
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GroupsServiceHGConnectorModule")]
    public class GroupsServiceHGConnectorModule : ISharedRegionModule, IGroupsServicesConnector
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled = false;
        private IGroupsServicesConnector _LocalGroupsConnector;
        private string _LocalGroupsServiceLocation;
        private IUserManagement _UserManagement;
        private IOfflineIMService _OfflineIM;
        private IMessageTransferModule _Messaging;
        private List<Scene> _Scenes;
        private ForeignImporter _ForeignImporter;
        private string _ServiceLocation;
        private IConfigSource _Config;

        private readonly Dictionary<string, GroupsServiceHGConnector> _NetworkConnectors = new Dictionary<string, GroupsServiceHGConnector>();
        private RemoteConnectorCacheWrapper _CacheWrapper; // for caching info of external group services

        #region ISharedRegionModule

        public void Initialise(IConfigSource config)
        {
            IConfig groupsConfig = config.Configs["Groups"];
            if (groupsConfig == null)
                return;

            if (groupsConfig.GetBoolean("Enabled", false) == false
                    || groupsConfig.GetString("ServicesConnectorModule", string.Empty) != Name)
            {
                return;
            }

            _Config = config;
            _ServiceLocation = groupsConfig.GetString("LocalService", "local"); // local or remote
            _LocalGroupsServiceLocation = groupsConfig.GetString("GroupsExternalURI", "http://127.0.0.1");
            _Scenes = new List<Scene>();

            _Enabled = true;

            _log.DebugFormat("[Groups]: Initializing {0} with LocalService {1}", this.Name, _ServiceLocation);
        }

        public string Name => "Groups HG Service Connector";

        public Type ReplaceableInterface => null;

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _log.DebugFormat("[Groups]: Registering {0} with {1}", this.Name, scene.RegionInfo.RegionName);
            scene.RegisterModuleInterface<IGroupsServicesConnector>(this);
            _Scenes.Add(scene);

            scene.EventManager.OnNewClient += OnNewClient;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            scene.UnregisterModuleInterface<IGroupsServicesConnector>(this);
            _Scenes.Remove(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            if (_UserManagement == null)
            {
                _UserManagement = scene.RequestModuleInterface<IUserManagement>();
                _OfflineIM = scene.RequestModuleInterface<IOfflineIMService>();
                _Messaging = scene.RequestModuleInterface<IMessageTransferModule>();
                _ForeignImporter = new ForeignImporter(_UserManagement);

                if (_ServiceLocation.Equals("local"))
                {
                    _LocalGroupsConnector = new GroupsServiceLocalConnectorModule(_Config, _UserManagement);
                    // Also, if local, create the endpoint for the HGGroupsService
                    new HgGroupsServiceRobustConnector(_Config, MainServer.Instance, string.Empty,
                        scene.RequestModuleInterface<IOfflineIMService>(), scene.RequestModuleInterface<IUserAccountService>());

                }
                else
                    _LocalGroupsConnector = new GroupsServiceRemoteConnectorModule(_Config, _UserManagement);

                _CacheWrapper = new RemoteConnectorCacheWrapper(_UserManagement);
            }

        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        #endregion

        private void OnNewClient(IClientAPI client)
        {
            client.OnCompleteMovementToRegion += OnCompleteMovementToRegion;
        }

        void OnCompleteMovementToRegion(IClientAPI client, bool arg2)
        {
            object sp = null;
            if (client.Scene.TryGetScenePresence(client.AgentId, out sp))
            {
                if (sp is ScenePresence && ((ScenePresence)sp).PresenceType != PresenceType.Npc)
                {
                    AgentCircuitData aCircuit = ((ScenePresence)sp).Scene.AuthenticateHandler.GetAgentCircuitData(client.AgentId);
                    if (aCircuit != null && (aCircuit.teleportFlags & (uint)Constants.TeleportFlags.ViaHGLogin) != 0 &&
                        _OfflineIM != null && _Messaging != null)
                    {
                        List<GridInstantMessage> ims = _OfflineIM.GetMessages(aCircuit.AgentID);
                        if (ims != null && ims.Count > 0)
                            foreach (GridInstantMessage im in ims)
                                _Messaging.SendInstantMessage(im, delegate(bool success) { });
                    }
                }
            }
        }

        #region IGroupsServicesConnector

        public UUID CreateGroup(UUID RequestingAgentID, string name, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment,
            bool allowPublish, bool maturePublish, UUID founderID, out string reason)
        {
            reason = string.Empty;
            if (_UserManagement.IsLocalGridUser(RequestingAgentID))
                return _LocalGroupsConnector.CreateGroup(RequestingAgentID, name, charter, showInList, insigniaID,
                    membershipFee, openEnrollment, allowPublish, maturePublish, founderID, out reason);
            else
            {
                reason = "Only local grid users are allowed to create a new group";
                return UUID.Zero;
            }
        }

        public bool UpdateGroup(string RequestingAgentID, UUID groupID, string charter, bool showInList, UUID insigniaID, int membershipFee,
            bool openEnrollment, bool allowPublish, bool maturePublish, out string reason)
        {
            reason = string.Empty;
            string url = string.Empty;
            string name = string.Empty;
            if (IsLocal(groupID, out url, out name))
                return _LocalGroupsConnector.UpdateGroup(AgentUUI(RequestingAgentID), groupID, charter, showInList, insigniaID, membershipFee,
                    openEnrollment, allowPublish, maturePublish, out reason);
            else
            {
                reason = "Changes to remote group not allowed. Please go to the group's original world.";
                return false;
            }
        }

        public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID, string GroupName)
        {
            string url = string.Empty;
            string name = string.Empty;
            if (IsLocal(GroupID, out url, out name))
                return _LocalGroupsConnector.GetGroupRecord(AgentUUI(RequestingAgentID), GroupID, GroupName);
            else if (!string.IsNullOrEmpty(url))
            {
                ExtendedGroupMembershipData membership = _LocalGroupsConnector.GetAgentGroupMembership(RequestingAgentID, RequestingAgentID, GroupID);
                string accessToken = string.Empty;
                if (membership != null)
                    accessToken = membership.AccessToken;
                else
                    return null;

                GroupsServiceHGConnector c = GetConnector(url);
                if (c != null)
                {
                    ExtendedGroupRecord grec = _CacheWrapper.GetGroupRecord(RequestingAgentID, GroupID, GroupName, delegate
                    {
                        return c.GetGroupRecord(AgentUUIForOutside(RequestingAgentID), GroupID, GroupName, accessToken);
                    });

                    if (grec != null)
                        ImportForeigner(grec.FounderUUI);
                    return grec;
                }
            }

            return null;
        }

        public List<DirGroupsReplyData> FindGroups(string RequestingAgentIDstr, string search)
        {
            return _LocalGroupsConnector.FindGroups(RequestingAgentIDstr, search);
        }

        public List<GroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID)
        {
            string url = string.Empty, gname = string.Empty;
            if (IsLocal(GroupID, out url, out gname))
            {
                string agentID = AgentUUI(RequestingAgentID);
                return _LocalGroupsConnector.GetGroupMembers(agentID, GroupID);
            }
            else if (!string.IsNullOrEmpty(url))
            {
                ExtendedGroupMembershipData membership = _LocalGroupsConnector.GetAgentGroupMembership(RequestingAgentID, RequestingAgentID, GroupID);
                string accessToken = string.Empty;
                if (membership != null)
                    accessToken = membership.AccessToken;
                else
                    return null;

                GroupsServiceHGConnector c = GetConnector(url);
                if (c != null)
                {
                    return _CacheWrapper.GetGroupMembers(RequestingAgentID, GroupID, delegate
                    {
                        return c.GetGroupMembers(AgentUUIForOutside(RequestingAgentID), GroupID, accessToken);
                    });

                }
            }
            return new List<GroupMembersData>();
        }

        public bool AddGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, out string reason)
        {
            reason = string.Empty;
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(groupID, out url, out gname))
                return _LocalGroupsConnector.AddGroupRole(AgentUUI(RequestingAgentID), groupID, roleID, name, description, title, powers, out reason);
            else
            {
                reason = "Operation not allowed outside this group's origin world.";
                return false;
            }
        }

        public bool UpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(groupID, out url, out gname))
                return _LocalGroupsConnector.UpdateGroupRole(AgentUUI(RequestingAgentID), groupID, roleID, name, description, title, powers);
            else
            {
                return false;
            }

        }

        public void RemoveGroupRole(string RequestingAgentID, UUID groupID, UUID roleID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(groupID, out url, out gname))
                _LocalGroupsConnector.RemoveGroupRole(AgentUUI(RequestingAgentID), groupID, roleID);
            else
            {
                return;
            }
        }

        public List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID groupID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(groupID, out url, out gname))
                return _LocalGroupsConnector.GetGroupRoles(AgentUUI(RequestingAgentID), groupID);
            else if (!string.IsNullOrEmpty(url))
            {
                ExtendedGroupMembershipData membership = _LocalGroupsConnector.GetAgentGroupMembership(RequestingAgentID, RequestingAgentID, groupID);
                string accessToken = string.Empty;
                if (membership != null)
                    accessToken = membership.AccessToken;
                else
                    return null;

                GroupsServiceHGConnector c = GetConnector(url);
                if (c != null)
                {
                    return _CacheWrapper.GetGroupRoles(RequestingAgentID, groupID, delegate
                    {
                        return c.GetGroupRoles(AgentUUIForOutside(RequestingAgentID), groupID, accessToken);
                    });

                }
            }

            return new List<GroupRolesData>();
        }

        public List<GroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID groupID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(groupID, out url, out gname))
                return _LocalGroupsConnector.GetGroupRoleMembers(AgentUUI(RequestingAgentID), groupID);
            else if (!string.IsNullOrEmpty(url))
            {
                ExtendedGroupMembershipData membership = _LocalGroupsConnector.GetAgentGroupMembership(RequestingAgentID, RequestingAgentID, groupID);
                string accessToken = string.Empty;
                if (membership != null)
                    accessToken = membership.AccessToken;
                else
                    return null;

                GroupsServiceHGConnector c = GetConnector(url);
                if (c != null)
                {
                    return _CacheWrapper.GetGroupRoleMembers(RequestingAgentID, groupID, delegate
                    {
                        return c.GetGroupRoleMembers(AgentUUIForOutside(RequestingAgentID), groupID, accessToken);
                    });

                }
            }

            return new List<GroupRoleMembersData>();
        }

        public bool AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string token, out string reason)
        {
            string url = string.Empty;
            string name = string.Empty;
            reason = string.Empty;

            UUID uid = new UUID(AgentID);
            if (IsLocal(GroupID, out url, out name))
            {
                if (_UserManagement.IsLocalGridUser(uid)) // local user
                {
                    // normal case: local group, local user
                    return _LocalGroupsConnector.AddAgentToGroup(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID, RoleID, token, out reason);
                }
                else // local group, foreign user
                {
                    // the user is accepting the  invitation, or joining, where the group resides
                    token = UUID.Random().ToString();
                    bool success = _LocalGroupsConnector.AddAgentToGroup(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID, RoleID, token, out reason);

                    if (success)
                    {
                        // Here we always return true. The user has been added to the local group,
                        // independent of whether the remote operation succeeds or not
                        url = _UserManagement.GetUserServerURL(uid, "GroupsServerURI");
                        if (string.IsNullOrEmpty(url))
                        {
                            reason = "You don't have an accessible groups server in your home world. You membership to this group in only within this grid.";
                            return true;
                        }

                        GroupsServiceHGConnector c = GetConnector(url);
                        if (c != null)
                            c.CreateProxy(AgentUUI(RequestingAgentID), AgentID, token, GroupID, _LocalGroupsServiceLocation, name, out reason);
                        return true;
                    }
                    return false;
                }
            }
            else if (_UserManagement.IsLocalGridUser(uid)) // local user
            {
                // foreign group, local user. She's been added already by the HG service.
                // Let's just check
                if (_LocalGroupsConnector.GetAgentGroupMembership(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID) != null)
                    return true;
            }

            reason = "Operation not allowed outside this group's origin world";
            return false;
        }


        public void RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            string url = string.Empty, name = string.Empty;
            if (!IsLocal(GroupID, out url, out name) && !string.IsNullOrEmpty(url))
            {
                ExtendedGroupMembershipData membership = _LocalGroupsConnector.GetAgentGroupMembership(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID);
                if (membership != null)
                {
                    GroupsServiceHGConnector c = GetConnector(url);
                    if (c != null)
                        c.RemoveAgentFromGroup(AgentUUIForOutside(AgentID), GroupID, membership.AccessToken);
                }
            }

            // remove from local service
            _LocalGroupsConnector.RemoveAgentFromGroup(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID);
        }

        public bool AddAgentToGroupInvite(string RequestingAgentID, UUID inviteID, UUID groupID, UUID roleID, string agentID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(groupID, out url, out gname))
                return _LocalGroupsConnector.AddAgentToGroupInvite(AgentUUI(RequestingAgentID), inviteID, groupID, roleID, AgentUUI(agentID));
            else
                return false;
        }

        public GroupInviteInfo GetAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            return _LocalGroupsConnector.GetAgentToGroupInvite(AgentUUI(RequestingAgentID), inviteID);
        }

        public void RemoveAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            _LocalGroupsConnector.RemoveAgentToGroupInvite(AgentUUI(RequestingAgentID), inviteID);
        }

        public void AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(GroupID, out url, out gname))
                _LocalGroupsConnector.AddAgentToGroupRole(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID, RoleID);

        }

        public void RemoveAgentFromGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(GroupID, out url, out gname))
                _LocalGroupsConnector.RemoveAgentFromGroupRole(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID, RoleID);
        }

        public List<GroupRolesData> GetAgentGroupRoles(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(GroupID, out url, out gname))
                return _LocalGroupsConnector.GetAgentGroupRoles(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID);
            else
                return new List<GroupRolesData>();
        }

        public void SetAgentActiveGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(GroupID, out url, out gname))
                _LocalGroupsConnector.SetAgentActiveGroup(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID);
        }

        public ExtendedGroupMembershipData GetAgentActiveMembership(string RequestingAgentID, string AgentID)
        {
            return _LocalGroupsConnector.GetAgentActiveMembership(AgentUUI(RequestingAgentID), AgentUUI(AgentID));
        }

        public void SetAgentActiveGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(GroupID, out url, out gname))
                _LocalGroupsConnector.SetAgentActiveGroupRole(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID, RoleID);
        }

        public void UpdateMembership(string RequestingAgentID, string AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile)
        {
            _LocalGroupsConnector.UpdateMembership(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID, AcceptNotices, ListInProfile);
        }

        public ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(GroupID, out url, out gname))
                return _LocalGroupsConnector.GetAgentGroupMembership(AgentUUI(RequestingAgentID), AgentUUI(AgentID), GroupID);
            else
                return null;
        }

        public List<GroupMembershipData> GetAgentGroupMemberships(string RequestingAgentID, string AgentID)
        {
            return _LocalGroupsConnector.GetAgentGroupMemberships(AgentUUI(RequestingAgentID), AgentUUI(AgentID));
        }

        public bool AddGroupNotice(string RequestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message,
            bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
        {
            string url = string.Empty, gname = string.Empty;

            if (IsLocal(groupID, out url, out gname))
            {
                if (_LocalGroupsConnector.AddGroupNotice(AgentUUI(RequestingAgentID), groupID, noticeID, fromName, subject, message,
                        hasAttachment, attType, attName, attItemID, AgentUUI(attOwnerID)))
                {
                    // then send the notice to every grid for which there are members in this group
                    List<GroupMembersData> members = _LocalGroupsConnector.GetGroupMembers(AgentUUI(RequestingAgentID), groupID);
                    List<string> urls = new List<string>();
                    foreach (GroupMembersData m in members)
                    {
                        if (!_UserManagement.IsLocalGridUser(m.AgentID))
                        {
                            string gURL = _UserManagement.GetUserServerURL(m.AgentID, "GroupsServerURI");
                            if (!urls.Contains(gURL))
                                urls.Add(gURL);
                        }
                    }

                    // so we have the list of urls to send the notice to
                    // this may take a long time...
                    WorkManager.RunInThread(delegate
                    {
                        foreach (string u in urls)
                        {
                            GroupsServiceHGConnector c = GetConnector(u);
                            if (c != null)
                            {
                                c.AddNotice(AgentUUIForOutside(RequestingAgentID), groupID, noticeID, fromName, subject, message,
                                    hasAttachment, attType, attName, attItemID, AgentUUIForOutside(attOwnerID));
                            }
                        }
                    }, null, string.Format("AddGroupNotice (agent {0}, group {1})", RequestingAgentID, groupID));

                    return true;
                }

                return false;
            }
            else
                return false;
        }

        public GroupNoticeInfo GetGroupNotice(string RequestingAgentID, UUID noticeID)
        {
            GroupNoticeInfo notice = _LocalGroupsConnector.GetGroupNotice(AgentUUI(RequestingAgentID), noticeID);

            if (notice != null && notice.noticeData.HasAttachment && notice.noticeData.AttachmentOwnerID != null)
               ImportForeigner(notice.noticeData.AttachmentOwnerID);

            return notice;
        }

        public List<ExtendedGroupNoticeData> GetGroupNotices(string RequestingAgentID, UUID GroupID)
        {
            return _LocalGroupsConnector.GetGroupNotices(AgentUUI(RequestingAgentID), GroupID);
        }

        #endregion

        #region hypergrid groups

        private string AgentUUI(string AgentIDStr)
        {
            UUID AgentID = UUID.Zero;
            if (!UUID.TryParse(AgentIDStr, out AgentID) || AgentID == UUID.Zero)
                return UUID.Zero.ToString();

            if (_UserManagement.IsLocalGridUser(AgentID))
                return AgentID.ToString();

            AgentCircuitData agent = null;
            foreach (Scene scene in _Scenes)
            {
                agent = scene.AuthenticateHandler.GetAgentCircuitData(AgentID);
                if (agent != null)
                    break;
            }
            if (agent != null)
                return Util.ProduceUserUniversalIdentifier(agent);

            // we don't know anything about this foreign user
            // try asking the user management module, which may know more
            return _UserManagement.GetUserUUI(AgentID);

        }

        private string AgentUUIForOutside(string AgentIDStr)
        {
            UUID AgentID = UUID.Zero;
            if (!UUID.TryParse(AgentIDStr, out AgentID) || AgentID == UUID.Zero)
                return UUID.Zero.ToString();

            AgentCircuitData agent = null;
            foreach (Scene scene in _Scenes)
            {
                agent = scene.AuthenticateHandler.GetAgentCircuitData(AgentID);
                if (agent != null)
                    break;
            }
            if (agent == null) // oops
                return AgentID.ToString();

            return Util.ProduceUserUniversalIdentifier(agent);
        }

        private UUID ImportForeigner(string uID)
        {
            UUID userID = UUID.Zero;
            string url = string.Empty, first = string.Empty, last = string.Empty, tmp = string.Empty;
            if (Util.ParseUniversalUserIdentifier(uID, out userID, out url, out first, out last, out tmp))
                _UserManagement.AddUser(userID, first, last, url);

            return userID;
        }

        private bool IsLocal(UUID groupID, out string serviceLocation, out string name)
        {
            serviceLocation = string.Empty;
            name = string.Empty;
            if (groupID.Equals(UUID.Zero))
                return true;

            ExtendedGroupRecord group = _LocalGroupsConnector.GetGroupRecord(UUID.Zero.ToString(), groupID, string.Empty);
            if (group == null)
            {
                //_log.DebugFormat("[XXX]: IsLocal? group {0} not found -- no.", groupID);
                return false;
            }

            serviceLocation = group.ServiceLocation;
            name = group.GroupName;
            bool isLocal = string.IsNullOrEmpty(@group.ServiceLocation);
            //_log.DebugFormat("[XXX]: IsLocal? {0}", isLocal);
            return isLocal;
        }

        private GroupsServiceHGConnector GetConnector(string url)
        {
            lock (_NetworkConnectors)
            {
                if (_NetworkConnectors.ContainsKey(url))
                    return _NetworkConnectors[url];

                GroupsServiceHGConnector c = new GroupsServiceHGConnector(url);
                _NetworkConnectors[url] = c;
            }

            return _NetworkConnectors[url];
        }
        #endregion
    }
}
