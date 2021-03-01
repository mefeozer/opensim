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
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "GroupsServiceLocalConnectorModule")]
    public class GroupsServiceLocalConnectorModule : ISharedRegionModule, IGroupsServicesConnector
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled = false;
        private GroupsService _GroupsService;
        private IUserManagement _UserManagement;
        private List<Scene> _Scenes;
        private ForeignImporter _ForeignImporter;

        #region constructors
        public GroupsServiceLocalConnectorModule()
        {
        }

        public GroupsServiceLocalConnectorModule(IConfigSource config, IUserManagement uman)
        {
            Init(config);
            _UserManagement = uman;
            _ForeignImporter = new ForeignImporter(uman);
        }
        #endregion

        private void Init(IConfigSource config)
        {
            _GroupsService = new GroupsService(config);
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

            _log.DebugFormat("[Groups]: Initializing {0}", this.Name);
        }

        public string Name => "Groups Local Service Connector";

        public Type ReplaceableInterface => null;

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _log.DebugFormat("[Groups]: Registering {0} with {1}", this.Name, scene.RegionInfo.RegionName);
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
                _ForeignImporter = new ForeignImporter(_UserManagement);
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
            _log.DebugFormat("[Groups]: Creating group {0}", name);
            reason = string.Empty;
            return _GroupsService.CreateGroup(RequestingAgentID.ToString(), name, charter, showInList, insigniaID,
                    membershipFee, openEnrollment, allowPublish, maturePublish, founderID, out reason);
        }

        public bool UpdateGroup(string RequestingAgentID, UUID groupID, string charter, bool showInList, UUID insigniaID, int membershipFee,
            bool openEnrollment, bool allowPublish, bool maturePublish, out string reason)
        {
            reason = string.Empty;
            _GroupsService.UpdateGroup(RequestingAgentID, groupID, charter, showInList, insigniaID, membershipFee, openEnrollment, allowPublish, maturePublish);
            return true;
        }

        public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID, string GroupName)
        {
            if (GroupID != UUID.Zero)
                return _GroupsService.GetGroupRecord(RequestingAgentID, GroupID);
            else if (GroupName != null)
                return _GroupsService.GetGroupRecord(RequestingAgentID, GroupName);

            return null;
        }

        public List<DirGroupsReplyData> FindGroups(string RequestingAgentIDstr, string search)
        {
            return _GroupsService.FindGroups(RequestingAgentIDstr, search);
        }

        public List<GroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID)
        {
            List<ExtendedGroupMembersData> _members = _GroupsService.GetGroupMembers(RequestingAgentID, GroupID);
            if (_members != null && _members.Count > 0)
            {
                List<GroupMembersData> members = _members.ConvertAll<GroupMembersData>(new Converter<ExtendedGroupMembersData, GroupMembersData>(_ForeignImporter.ConvertGroupMembersData));
                return members;
            }

            return new List<GroupMembersData>();
        }

        public bool AddGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, out string reason)
        {
            return _GroupsService.AddGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers, out reason);
        }

        public bool UpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers)
        {
            return _GroupsService.UpdateGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers);
        }

        public void RemoveGroupRole(string RequestingAgentID, UUID groupID, UUID roleID)
        {
            _GroupsService.RemoveGroupRole(RequestingAgentID, groupID, roleID);
        }

        public List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID GroupID)
        {
            return _GroupsService.GetGroupRoles(RequestingAgentID, GroupID);
        }

        public List<GroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID GroupID)
        {
            List<ExtendedGroupRoleMembersData> _rm = _GroupsService.GetGroupRoleMembers(RequestingAgentID, GroupID);
            if (_rm != null && _rm.Count > 0)
            {
                List<GroupRoleMembersData> rm = _rm.ConvertAll<GroupRoleMembersData>(new Converter<ExtendedGroupRoleMembersData, GroupRoleMembersData>(_ForeignImporter.ConvertGroupRoleMembersData));
                return rm;
            }

            return new List<GroupRoleMembersData>();

        }

        public bool AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string token, out string reason)
        {
            return _GroupsService.AddAgentToGroup(RequestingAgentID, AgentID, GroupID, RoleID, token, out reason);
        }

        public void RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            _GroupsService.RemoveAgentFromGroup(RequestingAgentID, AgentID, GroupID);
        }

        public bool AddAgentToGroupInvite(string RequestingAgentID, UUID inviteID, UUID groupID, UUID roleID, string agentID)
        {
            return _GroupsService.AddAgentToGroupInvite(RequestingAgentID, inviteID, groupID, roleID, agentID);
        }

        public GroupInviteInfo GetAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            return _GroupsService.GetAgentToGroupInvite(RequestingAgentID, inviteID); ;
        }

        public void RemoveAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            _GroupsService.RemoveAgentToGroupInvite(RequestingAgentID, inviteID);
        }

        public void AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            _GroupsService.AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);
        }

        public void RemoveAgentFromGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            _GroupsService.RemoveAgentFromGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);
        }

        public List<GroupRolesData> GetAgentGroupRoles(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            return _GroupsService.GetAgentGroupRoles(RequestingAgentID, AgentID, GroupID);
        }

        public void SetAgentActiveGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            _GroupsService.SetAgentActiveGroup(RequestingAgentID, AgentID, GroupID);
        }

        public ExtendedGroupMembershipData GetAgentActiveMembership(string RequestingAgentID, string AgentID)
        {
            return _GroupsService.GetAgentActiveMembership(RequestingAgentID, AgentID);
        }

        public void SetAgentActiveGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            _GroupsService.SetAgentActiveGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);
        }

        public void UpdateMembership(string RequestingAgentID, string AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile)
        {
            _GroupsService.UpdateMembership(RequestingAgentID, AgentID, GroupID, AcceptNotices, ListInProfile);
        }

        public ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            return _GroupsService.GetAgentGroupMembership(RequestingAgentID, AgentID, GroupID); ;
        }

        public List<GroupMembershipData> GetAgentGroupMemberships(string RequestingAgentID, string AgentID)
        {
            return _GroupsService.GetAgentGroupMemberships(RequestingAgentID, AgentID);
        }

        public bool AddGroupNotice(string RequestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message,
            bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
        {
            return _GroupsService.AddGroupNotice(RequestingAgentID, groupID, noticeID, fromName, subject, message,
                hasAttachment, attType, attName, attItemID, attOwnerID);
        }

        public GroupNoticeInfo GetGroupNotice(string RequestingAgentID, UUID noticeID)
        {
            GroupNoticeInfo notice = _GroupsService.GetGroupNotice(RequestingAgentID, noticeID);

            //if (notice != null && notice.noticeData.HasAttachment && notice.noticeData.AttachmentOwnerID != null)
            //{
            //    UUID userID = UUID.Zero;
            //    string url = string.Empty, first = string.Empty, last = string.Empty, tmp = string.Empty;
            //    Util.ParseUniversalUserIdentifier(notice.noticeData.AttachmentOwnerID, out userID, out url, out first, out last, out tmp);
            //    if (url != string.Empty)
            //        _UserManagement.AddUser(userID, first, last, url);
            //}

            return notice;
        }

        public List<ExtendedGroupNoticeData> GetGroupNotices(string RequestingAgentID, UUID GroupID)
        {
            return _GroupsService.GetGroupNotices(RequestingAgentID, GroupID);
        }

        #endregion
    }
}
