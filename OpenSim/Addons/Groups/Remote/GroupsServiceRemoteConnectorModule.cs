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
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;

using OpenMetaverse;
using Mono.Addins;
using log4net;
using Nini.Config;

namespace OpenSim.Groups
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GroupsServiceRemoteConnectorModule")]
    public class GroupsServiceRemoteConnectorModule : ISharedRegionModule, IGroupsServicesConnector
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled = false;
        private GroupsServiceRemoteConnector _GroupsService;
        private IUserManagement _UserManagement;
        private List<Scene> _Scenes;

        private RemoteConnectorCacheWrapper _CacheWrapper;

        #region constructors
        public GroupsServiceRemoteConnectorModule()
        {
        }

        public GroupsServiceRemoteConnectorModule(IConfigSource config, IUserManagement uman)
        {
            Init(config);
            _UserManagement = uman;
            _CacheWrapper = new RemoteConnectorCacheWrapper(_UserManagement);

        }
        #endregion

        private void Init(IConfigSource config)
        {
            _GroupsService = new GroupsServiceRemoteConnector(config);
            _Scenes = new List<Scene>();

        }

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

            Init(config);

            _Enabled = true;
            _log.DebugFormat("[Groups.RemoteConnector]: Initializing {0}", this.Name);
        }

        public string Name => "Groups Remote Service Connector";

        public Type ReplaceableInterface => null;

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _log.DebugFormat("[Groups.RemoteConnector]: Registering {0} with {1}", this.Name, scene.RegionInfo.RegionName);
            scene.RegisterModuleInterface<IGroupsServicesConnector>(this);
            _Scenes.Add(scene);
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

        #region IGroupsServicesConnector

        public UUID CreateGroup(UUID RequestingAgentID, string name, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment,
            bool allowPublish, bool maturePublish, UUID founderID, out string reason)
        {
            _log.DebugFormat("[Groups.RemoteConnector]: Creating group {0}", name);
            string r = string.Empty;

            UUID groupID = _CacheWrapper.CreateGroup(RequestingAgentID, delegate
            {
                return _GroupsService.CreateGroup(RequestingAgentID.ToString(), name, charter, showInList, insigniaID,
                    membershipFee, openEnrollment, allowPublish, maturePublish, founderID, out r);
            });

            reason = r;
            return groupID;
        }

        public bool UpdateGroup(string RequestingAgentID, UUID groupID, string charter, bool showInList, UUID insigniaID, int membershipFee,
            bool openEnrollment, bool allowPublish, bool maturePublish, out string reason)
        {
            string r = string.Empty;

            bool success = _CacheWrapper.UpdateGroup(groupID, delegate
            {
                return _GroupsService.UpdateGroup(RequestingAgentID, groupID, charter, showInList, insigniaID, membershipFee, openEnrollment, allowPublish, maturePublish);
            });

            reason = r;
            return success;
        }

        public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID, string GroupName)
        {
            if (GroupID == UUID.Zero && (GroupName == null || GroupName != null && string.IsNullOrEmpty(GroupName)))
                return null;

            return _CacheWrapper.GetGroupRecord(RequestingAgentID,GroupID,GroupName, delegate
            {
                return _GroupsService.GetGroupRecord(RequestingAgentID, GroupID, GroupName);
            });
        }

        public List<DirGroupsReplyData> FindGroups(string RequestingAgentIDstr, string search)
        {
            // TODO!
            return _GroupsService.FindGroups(RequestingAgentIDstr, search);
        }

        public bool AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string token, out string reason)
        {
            string agentFullID = AgentID;
            _log.DebugFormat("[Groups.RemoteConnector]: Add agent {0} to group {1}", agentFullID, GroupID);
            string r = string.Empty;

            bool success = _CacheWrapper.AddAgentToGroup(RequestingAgentID, AgentID, GroupID, delegate
            {
                return _GroupsService.AddAgentToGroup(RequestingAgentID, agentFullID, GroupID, RoleID, token, out r);
            });

            reason = r;
            return success;
        }

        public void RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            _CacheWrapper.RemoveAgentFromGroup(RequestingAgentID, AgentID, GroupID, delegate
            {
                _GroupsService.RemoveAgentFromGroup(RequestingAgentID, AgentID, GroupID);
            });

        }

        public void SetAgentActiveGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            _CacheWrapper.SetAgentActiveGroup(AgentID, delegate
            {
                return _GroupsService.SetAgentActiveGroup(RequestingAgentID, AgentID, GroupID);
            });
        }

        public ExtendedGroupMembershipData GetAgentActiveMembership(string RequestingAgentID, string AgentID)
        {
            return _CacheWrapper.GetAgentActiveMembership(AgentID, delegate
            {
                return _GroupsService.GetMembership(RequestingAgentID, AgentID, UUID.Zero);
            });
        }

        public ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            return _CacheWrapper.GetAgentGroupMembership(AgentID, GroupID, delegate
            {
                return _GroupsService.GetMembership(RequestingAgentID, AgentID, GroupID);
            });
        }

        public List<GroupMembershipData> GetAgentGroupMemberships(string RequestingAgentID, string AgentID)
        {
            return _CacheWrapper.GetAgentGroupMemberships(AgentID, delegate
            {
                return _GroupsService.GetMemberships(RequestingAgentID, AgentID);
            });
        }


        public List<GroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID)
        {
            return _CacheWrapper.GetGroupMembers(RequestingAgentID, GroupID, delegate
            {
                return _GroupsService.GetGroupMembers(RequestingAgentID, GroupID);
            });
        }

        public bool AddGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, out string reason)
        {
            string r = string.Empty;
            bool success = _CacheWrapper.AddGroupRole(groupID, roleID, description, name, powers, title, delegate
            {
                return _GroupsService.AddGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers, out r);
            });

            reason = r;
            return success;
        }

        public bool UpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers)
        {
            return _CacheWrapper.UpdateGroupRole(groupID, roleID, name, description, title, powers, delegate
            {
                return _GroupsService.UpdateGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers);
            });
        }

        public void RemoveGroupRole(string RequestingAgentID, UUID groupID, UUID roleID)
        {
            _CacheWrapper.RemoveGroupRole(RequestingAgentID, groupID, roleID, delegate
            {
                _GroupsService.RemoveGroupRole(RequestingAgentID, groupID, roleID);
            });
        }

        public List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID GroupID)
        {
            return _CacheWrapper.GetGroupRoles(RequestingAgentID, GroupID, delegate
            {
                return _GroupsService.GetGroupRoles(RequestingAgentID, GroupID);
            });
        }

        public List<GroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID GroupID)
        {
            return _CacheWrapper.GetGroupRoleMembers(RequestingAgentID, GroupID, delegate
            {
                return _GroupsService.GetGroupRoleMembers(RequestingAgentID, GroupID);
            });
        }

        public void AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            _CacheWrapper.AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID, delegate
            {
                return _GroupsService.AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);
            });
        }

        public void RemoveAgentFromGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            _CacheWrapper.RemoveAgentFromGroupRole(RequestingAgentID, AgentID, GroupID, RoleID, delegate
            {
                return _GroupsService.RemoveAgentFromGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);
            });
        }

        public List<GroupRolesData> GetAgentGroupRoles(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            return _CacheWrapper.GetAgentGroupRoles(RequestingAgentID, AgentID, GroupID, delegate
            {
                return _GroupsService.GetAgentGroupRoles(RequestingAgentID, AgentID, GroupID); ;
            });
        }

        public void SetAgentActiveGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            _CacheWrapper.SetAgentActiveGroupRole(AgentID, GroupID, delegate
            {
                _GroupsService.SetAgentActiveGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);
            });
        }

        public void UpdateMembership(string RequestingAgentID, string AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile)
        {
            _CacheWrapper.UpdateMembership(AgentID, GroupID, AcceptNotices, ListInProfile, delegate
            {
                _GroupsService.UpdateMembership(RequestingAgentID, AgentID, GroupID, AcceptNotices, ListInProfile);
            });
        }

        public bool AddAgentToGroupInvite(string RequestingAgentID, UUID inviteID, UUID groupID, UUID roleID, string agentID)
        {
            return _GroupsService.AddAgentToGroupInvite(RequestingAgentID, inviteID, groupID, roleID, agentID);
        }

        public GroupInviteInfo GetAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            return _GroupsService.GetAgentToGroupInvite(RequestingAgentID, inviteID);
        }

        public void RemoveAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            _GroupsService.RemoveAgentToGroupInvite(RequestingAgentID, inviteID);
        }

        public bool AddGroupNotice(string RequestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message,
            bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
        {
            GroupNoticeInfo notice = new GroupNoticeInfo
            {
                GroupID = groupID,
                Message = message,
                noticeData = new ExtendedGroupNoticeData
                {
                    AttachmentItemID = attItemID,
                    AttachmentName = attName,
                    AttachmentOwnerID = attOwnerID.ToString(),
                    AttachmentType = attType,
                    FromName = fromName,
                    HasAttachment = hasAttachment,
                    NoticeID = noticeID,
                    Subject = subject,
                    Timestamp = (uint)Util.UnixTimeSinceEpoch()
                }
            };

            return _CacheWrapper.AddGroupNotice(groupID, noticeID, notice, delegate
            {
                return _GroupsService.AddGroupNotice(RequestingAgentID, groupID, noticeID, fromName, subject, message,
                            hasAttachment, attType, attName, attItemID, attOwnerID);
            });
        }

        public GroupNoticeInfo GetGroupNotice(string RequestingAgentID, UUID noticeID)
        {
            return _CacheWrapper.GetGroupNotice(noticeID, delegate
            {
                return _GroupsService.GetGroupNotice(RequestingAgentID, noticeID);
            });
        }

        public List<ExtendedGroupNoticeData> GetGroupNotices(string RequestingAgentID, UUID GroupID)
        {
            return _CacheWrapper.GetGroupNotices(GroupID, delegate
            {
                return _GroupsService.GetGroupNotices(RequestingAgentID, GroupID);
            });
        }

        #endregion
    }

}
