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
using System.Timers;
using log4net;
using Nini.Config;

using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Groups
{
    public class GroupsService : GroupsServiceBase
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const GroupPowers DefaultEveryonePowers =
            GroupPowers.AllowSetHome |
            GroupPowers.Accountable |
            GroupPowers.JoinChat |
            GroupPowers.AllowVoiceChat |
            GroupPowers.ReceiveNotices |
            GroupPowers.StartProposal |
            GroupPowers.VoteOnProposal;

        public const GroupPowers OfficersPowers = DefaultEveryonePowers |
            GroupPowers.AllowFly |
            GroupPowers.AllowLandmark |
            GroupPowers.AllowRez |
            GroupPowers.AssignMemberLimited |
            GroupPowers.ChangeIdentity |
            GroupPowers.ChangeMedia |
            GroupPowers.ChangeOptions |
            GroupPowers.DeedObject |
            GroupPowers.Eject |
            GroupPowers.FindPlaces |
            GroupPowers.Invite |
            GroupPowers.LandChangeIdentity |
            GroupPowers.LandDeed |
            GroupPowers.LandDivideJoin |
            GroupPowers.LandEdit |
            GroupPowers.AllowEnvironment |
            GroupPowers.LandEjectAndFreeze |
            GroupPowers.LandGardening |
            GroupPowers.LandManageAllowed |
            GroupPowers.LandManageBanned |
            GroupPowers.LandManagePasses |
            GroupPowers.LandOptions |
            GroupPowers.LandRelease |
            GroupPowers.LandSetSale |
            GroupPowers.MemberVisible |
            GroupPowers.ModerateChat |
            GroupPowers.ObjectManipulate |
            GroupPowers.ObjectSetForSale |
            GroupPowers.ReturnGroupOwned |
            GroupPowers.ReturnGroupSet |
            GroupPowers.ReturnNonGroup |
            GroupPowers.RoleProperties |
            GroupPowers.SendNotices |
            GroupPowers.SetLandingPoint;

        public const GroupPowers OwnerPowers = OfficersPowers | 
            GroupPowers.Accountable |
            GroupPowers.AllowEditLand |
            GroupPowers.AssignMember |
            GroupPowers.ChangeActions |
            GroupPowers.CreateRole |
            GroupPowers.DeleteRole |
            GroupPowers.ExperienceAdmin |
            GroupPowers.ExperienceCreator |
            GroupPowers.GroupBanAccess |
            GroupPowers.HostEvent |
            GroupPowers.RemoveMember;

        #region Daily Cleanup

        private readonly Timer _CleanupTimer;

        public GroupsService(IConfigSource config, string configName)
            : base(config, configName)
        {
        }

        public GroupsService(IConfigSource config)
            : this(config, string.Empty)
        {
            // Once a day
            _CleanupTimer = new Timer(24 * 60 * 60 * 1000)
            {
                AutoReset = true
            };
            _CleanupTimer.Elapsed += new ElapsedEventHandler(_CleanupTimer_Elapsed);
            _CleanupTimer.Enabled = true;
            _CleanupTimer.Start();
        }

        private void _CleanupTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            _Database.DeleteOldNotices();
            _Database.DeleteOldInvites();
        }

        #endregion

        public UUID CreateGroup(string RequestingAgentID, string name, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment,
            bool allowPublish, bool maturePublish, UUID founderID, out string reason)
        {
            reason = string.Empty;

            // Check if the group already exists
            if (_Database.RetrieveGroup(name) != null)
            {
                reason = "A group with that name already exists";
                return UUID.Zero;
            }

            // Create the group
            GroupData data = new GroupData
            {
                GroupID = UUID.Random(),
                Data = new Dictionary<string, string>()
            };
            data.Data["Name"] = name;
            data.Data["Charter"] = charter;
            data.Data["InsigniaID"] = insigniaID.ToString();
            data.Data["FounderID"] = founderID.ToString();
            data.Data["MembershipFee"] = membershipFee.ToString();
            data.Data["OpenEnrollment"] = openEnrollment ? "1" : "0";
            data.Data["ShowInList"] = showInList ? "1" : "0";
            data.Data["AllowPublish"] = allowPublish ? "1" : "0";
            data.Data["MaturePublish"] = maturePublish ? "1" : "0";
            UUID ownerRoleID = UUID.Random();
            data.Data["OwnerRoleID"] = ownerRoleID.ToString();

            if (!_Database.StoreGroup(data))
                return UUID.Zero;

            // Create Everyone role
            _AddOrUpdateGroupRole(RequestingAgentID, data.GroupID, UUID.Zero, "Everyone", "Everyone in the group is in the everyone role.", "Member of " + name, (ulong)DefaultEveryonePowers, true);

            // Create Officers role
            UUID officersRoleID = UUID.Random();
            _AddOrUpdateGroupRole(RequestingAgentID, data.GroupID, officersRoleID, "Officers", "The officers of the group, with more powers than regular members.", "Officer of " + name, (ulong)OfficersPowers, true);

            // Create Owner role
            _AddOrUpdateGroupRole(RequestingAgentID, data.GroupID, ownerRoleID, "Owners", "Owners of the group", "Owner of " + name, (ulong)OwnerPowers, true);

            // Add founder to group
            _AddAgentToGroup(RequestingAgentID, founderID.ToString(), data.GroupID, ownerRoleID);
            _AddAgentToGroup(RequestingAgentID, founderID.ToString(), data.GroupID, officersRoleID);

            return data.GroupID;
        }

        public void UpdateGroup(string RequestingAgentID, UUID groupID, string charter, bool showInList, UUID insigniaID, int membershipFee, bool openEnrollment, bool allowPublish, bool maturePublish)
        {
            GroupData data = _Database.RetrieveGroup(groupID);
            if (data == null)
                return;

            // Check perms
            if (!HasPower(RequestingAgentID, groupID, GroupPowers.ChangeActions))
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at updating group {1} denied because of lack of permission", RequestingAgentID, groupID);
                return;
            }

            data.GroupID = groupID;
            data.Data["Charter"] = charter;
            data.Data["ShowInList"] = showInList ? "1" : "0";
            data.Data["InsigniaID"] = insigniaID.ToString();
            data.Data["MembershipFee"] = membershipFee.ToString();
            data.Data["OpenEnrollment"] = openEnrollment ? "1" : "0";
            data.Data["AllowPublish"] = allowPublish ? "1" : "0";
            data.Data["MaturePublish"] = maturePublish ? "1" : "0";

            _Database.StoreGroup(data);

        }

        public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID)
        {
            GroupData data = _Database.RetrieveGroup(GroupID);

            return _GroupDataToRecord(data);
        }

        public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, string GroupName)
        {
            GroupData data = _Database.RetrieveGroup(GroupName);

            return _GroupDataToRecord(data);
        }

        public List<DirGroupsReplyData> FindGroups(string RequestingAgentID, string search)
        {
            List<DirGroupsReplyData> groups = new List<DirGroupsReplyData>();

            GroupData[] data = _Database.RetrieveGroups(search);

            if (data != null && data.Length > 0)
            {
                foreach (GroupData d in data)
                {
                    // Don't list group proxies
                    if (d.Data.ContainsKey("Location") && !string.IsNullOrEmpty(d.Data["Location"]))
                        continue;

                    int nmembers = _Database.MemberCount(d.GroupID);
                    if(nmembers == 0)
                        continue;

                    DirGroupsReplyData g = new DirGroupsReplyData();

                    if (d.Data.ContainsKey("Name"))
                        g.groupName = d.Data["Name"];
                    else
                    {
                        _log.DebugFormat("[Groups]: Key Name not found");
                        continue;
                    }

                    g.groupID = d.GroupID;
                    g.members = nmembers;

                    groups.Add(g);
                }
            }

            return groups;
        }

        public List<ExtendedGroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID)
        {
            List<ExtendedGroupMembersData> members = new List<ExtendedGroupMembersData>();

            GroupData group = _Database.RetrieveGroup(GroupID);
            if (group == null)
                return members;

            // Unfortunately this doesn't quite work on legacy group data because of a bug
            // that's also being fixed here on CreateGroup. The OwnerRoleID sent to the DB was wrong.
            // See how to find the ownerRoleID a few lines below.
            UUID ownerRoleID = new UUID(group.Data["OwnerRoleID"]);

            RoleData[] roles = _Database.RetrieveRoles(GroupID);
            if (roles == null)
                // something wrong with this group
                return members;
            List<RoleData> rolesList = new List<RoleData>(roles);

            // Let's find the "real" ownerRoleID
            RoleData ownerRole = rolesList.Find(r => r.Data["Powers"] == ((long)OwnerPowers).ToString());
            if (ownerRole != null)
                ownerRoleID = ownerRole.RoleID;

            // Check visibility?
            // When we don't want to check visibility, we pass it "all" as the requestingAgentID
            bool checkVisibility = !RequestingAgentID.Equals(UUID.Zero.ToString());

            if (checkVisibility)
            {
                // Is the requester a member of the group?
                bool isInGroup = false;
                if (_Database.RetrieveMember(GroupID, RequestingAgentID) != null)
                    isInGroup = true;

                if (!isInGroup) // reduce the roles to the visible ones
                    rolesList = rolesList.FindAll(r => (ulong.Parse(r.Data["Powers"]) & (ulong)GroupPowers.MemberVisible) != 0);
            }

            MembershipData[] datas = _Database.RetrieveMembers(GroupID);
            if (datas == null || datas != null && datas.Length == 0)
                return members;

            // OK, we have everything we need

            foreach (MembershipData d in datas)
            {
                RoleMembershipData[] rolememberships = _Database.RetrieveMemberRoles(GroupID, d.PrincipalID);
                List<RoleMembershipData> rolemembershipsList = new List<RoleMembershipData>(rolememberships);

                ExtendedGroupMembersData m = new ExtendedGroupMembersData();

                // What's this person's current role in the group?
                UUID selectedRole = new UUID(d.Data["SelectedRoleID"]);
                RoleData selected = rolesList.Find(r => r.RoleID == selectedRole);

                if (selected != null)
                {
                    m.Title = selected.Data["Title"];
                    m.AgentPowers = ulong.Parse(selected.Data["Powers"]);
                }

                m.AgentID = d.PrincipalID;
                m.AcceptNotices = d.Data["AcceptNotices"] == "1" ? true : false;
                m.Contribution = int.Parse(d.Data["Contribution"]);
                m.ListInProfile = d.Data["ListInProfile"] == "1" ? true : false;

                GridUserData gud = _GridUserService.Get(d.PrincipalID);
                if (gud != null)
                {
                    if (bool.Parse(gud.Data["Online"]))
                    {
                        m.OnlineStatus = @"Online";
                    }
                    else
                    {
                        int unixtime = int.Parse(gud.Data["Login"]);
                        // The viewer is very picky about how these strings are formed. Eg. it will crash on malformed dates!
                        m.OnlineStatus = unixtime == 0 ? @"unknown" : Util.ToDateTime(unixtime).ToString("MM/dd/yyyy");
                    }
                }

                // Is this person an owner of the group?
                m.IsOwner = rolemembershipsList.Find(r => r.RoleID == ownerRoleID) != null ? true : false;

                members.Add(m);
            }

            return members;
        }

        public bool AddGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, out string reason)
        {
            reason = string.Empty;
            // check that the requesting agent has permissions to add role
            if (!HasPower(RequestingAgentID, groupID, GroupPowers.CreateRole))
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at creating role in group {1} denied because of lack of permission", RequestingAgentID, groupID);
                reason = "Insufficient permission to create role";
                return false;
            }

            return _AddOrUpdateGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers, true);

        }

        public bool UpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers)
        {
            // check perms
            if (!HasPower(RequestingAgentID, groupID, GroupPowers.ChangeActions))
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at changing role in group {1} denied because of lack of permission", RequestingAgentID, groupID);
                return false;
            }

            return _AddOrUpdateGroupRole(RequestingAgentID, groupID, roleID, name, description, title, powers, false);
        }

        public void RemoveGroupRole(string RequestingAgentID, UUID groupID, UUID roleID)
        {
            // check perms
            if (!HasPower(RequestingAgentID, groupID, GroupPowers.DeleteRole))
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at deleting role from group {1} denied because of lack of permission", RequestingAgentID, groupID);
                return;
            }

            // Can't delete Everyone and Owners roles
            if (roleID == UUID.Zero)
            {
                _log.DebugFormat("[Groups]: Attempt at deleting Everyone role from group {0} denied", groupID);
                return;
            }

            GroupData group = _Database.RetrieveGroup(groupID);
            if (group == null)
            {
                _log.DebugFormat("[Groups]: Attempt at deleting role from non-existing group {0}", groupID);
                return;
            }

            if (roleID == new UUID(group.Data["OwnerRoleID"]))
            {
                _log.DebugFormat("[Groups]: Attempt at deleting Owners role from group {0} denied", groupID);
                return;
            }

            _RemoveGroupRole(groupID, roleID);
        }

        public List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID GroupID)
        {
            // TODO: check perms
            return _GetGroupRoles(GroupID);
        }

        public List<ExtendedGroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID GroupID)
        {
            // TODO: check perms

            // Is the requester a member of the group?
            bool isInGroup = false;
            if (_Database.RetrieveMember(GroupID, RequestingAgentID) != null)
                isInGroup = true;

            return _GetGroupRoleMembers(GroupID, isInGroup);
        }

        public bool AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string token, out string reason)
        {
            reason = string.Empty;

            _AddAgentToGroup(RequestingAgentID, AgentID, GroupID, RoleID, token);

            return true;
        }

        public bool RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            // check perms
            if (RequestingAgentID != AgentID && !HasPower(RequestingAgentID, GroupID, GroupPowers.Eject))
                return false;

            _RemoveAgentFromGroup(RequestingAgentID, AgentID, GroupID);

            return true;
        }

        public bool AddAgentToGroupInvite(string RequestingAgentID, UUID inviteID, UUID groupID, UUID roleID, string agentID)
        {
            // Check whether the invitee is already a member of the group
            MembershipData m = _Database.RetrieveMember(groupID, agentID);
            if (m != null)
                return false;

            // Check permission to invite
            if (!HasPower(RequestingAgentID, groupID, GroupPowers.Invite))
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at inviting to group {1} denied because of lack of permission", RequestingAgentID, groupID);
                return false;
            }

            // Check whether there are pending invitations and delete them
            InvitationData invite = _Database.RetrieveInvitation(groupID, agentID);
            if (invite != null)
                _Database.DeleteInvite(invite.InviteID);

            invite = new InvitationData
            {
                InviteID = inviteID,
                PrincipalID = agentID,
                GroupID = groupID,
                RoleID = roleID,
                Data = new Dictionary<string, string>()
            };

            return _Database.StoreInvitation(invite);
        }

        public GroupInviteInfo GetAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            InvitationData data = _Database.RetrieveInvitation(inviteID);

            if (data == null)
                return null;

            GroupInviteInfo inviteInfo = new GroupInviteInfo
            {
                AgentID = data.PrincipalID,
                GroupID = data.GroupID,
                InviteID = data.InviteID,
                RoleID = data.RoleID
            };

            return inviteInfo;
        }

        public void RemoveAgentToGroupInvite(string RequestingAgentID, UUID inviteID)
        {
            _Database.DeleteInvite(inviteID);
        }

        public bool AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            //if (!_Database.CheckOwnerRole(RequestingAgentID, GroupID, RoleID))
            //    return;

            // check permissions
            bool limited = HasPower(RequestingAgentID, GroupID, GroupPowers.AssignMemberLimited);
            bool unlimited = HasPower(RequestingAgentID, GroupID, GroupPowers.AssignMember) || IsOwner(RequestingAgentID, GroupID);
            if (!limited && !unlimited)
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at assigning {1} to role {2} denied because of lack of permission", RequestingAgentID, AgentID, RoleID);
                return false;
            }

            // AssignMemberLimited means that the person can assign another person to the same roles that she has in the group
            if (!unlimited && limited)
            {
                // check whether person's has this role
                RoleMembershipData rolemembership = _Database.RetrieveRoleMember(GroupID, RoleID, RequestingAgentID);
                if (rolemembership == null)
                {
                    _log.DebugFormat("[Groups]: ({0}) Attempt at assigning {1} to role {2} denied because of limited permission", RequestingAgentID, AgentID, RoleID);
                    return false;
                }
            }

            _AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);

            return true;
        }

        public bool RemoveAgentFromGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            // Don't remove from Everyone role!
            if (RoleID == UUID.Zero)
                return false;

            // check permissions
            bool limited = HasPower(RequestingAgentID, GroupID, GroupPowers.AssignMemberLimited);
            bool unlimited = HasPower(RequestingAgentID, GroupID, GroupPowers.AssignMember) || IsOwner(RequestingAgentID, GroupID);
            if (!limited && !unlimited)
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at removing {1} from role {2} denied because of lack of permission", RequestingAgentID, AgentID, RoleID);
                return false;
            }

            // AssignMemberLimited means that the person can assign another person to the same roles that she has in the group
            if (!unlimited && limited)
            {
                // check whether person's has this role
                RoleMembershipData rolemembership = _Database.RetrieveRoleMember(GroupID, RoleID, RequestingAgentID);
                if (rolemembership == null)
                {
                    _log.DebugFormat("[Groups]: ({0}) Attempt at removing {1} from role {2} denied because of limited permission", RequestingAgentID, AgentID, RoleID);
                    return false;
                }
            }

            RoleMembershipData rolemember = _Database.RetrieveRoleMember(GroupID, RoleID, AgentID);

            if (rolemember == null)
                return false;

            _Database.DeleteRoleMember(rolemember);

            // Find another role for this person
            UUID newRoleID = UUID.Zero; // Everyone
            RoleMembershipData[] rdata = _Database.RetrieveMemberRoles(GroupID, AgentID);
            if (rdata != null)
                foreach (RoleMembershipData r in rdata)
                {
                    if (r.RoleID != UUID.Zero)
                    {
                        newRoleID = r.RoleID;
                        break;
                    }
                }

            MembershipData member = _Database.RetrieveMember(GroupID, AgentID);
            if (member != null)
            {
                member.Data["SelectedRoleID"] = newRoleID.ToString();
                _Database.StoreMember(member);
            }

            return true;
        }

        public List<GroupRolesData> GetAgentGroupRoles(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            List<GroupRolesData> roles = new List<GroupRolesData>();
            // TODO: check permissions

            RoleMembershipData[] data = _Database.RetrieveMemberRoles(GroupID, AgentID);
            if (data == null || data != null && data.Length ==0)
                return roles;

            foreach (RoleMembershipData d in data)
            {
                RoleData rdata = _Database.RetrieveRole(GroupID, d.RoleID);
                if (rdata == null) // hippos
                    continue;

                GroupRolesData r = new GroupRolesData
                {
                    Name = rdata.Data["Name"],
                    Powers = ulong.Parse(rdata.Data["Powers"]),
                    RoleID = rdata.RoleID,
                    Title = rdata.Data["Title"]
                };

                roles.Add(r);
            }

            return roles;
        }

        public ExtendedGroupMembershipData SetAgentActiveGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            // TODO: check perms
            PrincipalData principal = new PrincipalData
            {
                PrincipalID = AgentID,
                ActiveGroupID = GroupID
            };
            _Database.StorePrincipal(principal);

            return GetAgentGroupMembership(RequestingAgentID, AgentID, GroupID);
        }

        public ExtendedGroupMembershipData GetAgentActiveMembership(string RequestingAgentID, string AgentID)
        {
            // 1. get the principal data for the active group
            PrincipalData principal = _Database.RetrievePrincipal(AgentID);
            if (principal == null)
                return null;

            return GetAgentGroupMembership(RequestingAgentID, AgentID, principal.ActiveGroupID);
        }

        public ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            return GetAgentGroupMembership(RequestingAgentID, AgentID, GroupID, null);
        }

        private ExtendedGroupMembershipData GetAgentGroupMembership(string RequestingAgentID, string AgentID, UUID GroupID, MembershipData membership)
        {
            // 2. get the active group
            GroupData group = _Database.RetrieveGroup(GroupID);
            if (group == null)
                return null;

            // 3. get the membership info if we don't have it already
            if (membership == null)
            {
                membership = _Database.RetrieveMember(group.GroupID, AgentID);
                if (membership == null)
                    return null;
            }

            // 4. get the active role
            UUID activeRoleID = new UUID(membership.Data["SelectedRoleID"]);
            RoleData role = _Database.RetrieveRole(group.GroupID, activeRoleID);

            ExtendedGroupMembershipData data = new ExtendedGroupMembershipData
            {
                AcceptNotices = membership.Data["AcceptNotices"] == "1" ? true : false,
                AccessToken = membership.Data["AccessToken"],
                Active = true,
                ActiveRole = activeRoleID,
                AllowPublish = group.Data["AllowPublish"] == "1" ? true : false,
                Charter = group.Data["Charter"],
                Contribution = int.Parse(membership.Data["Contribution"]),
                FounderID = new UUID(group.Data["FounderID"]),
                GroupID = new UUID(group.GroupID),
                GroupName = group.Data["Name"],
                GroupPicture = new UUID(group.Data["InsigniaID"])
            };
            if (role != null)
            {
                data.GroupPowers = ulong.Parse(role.Data["Powers"]);
                data.GroupTitle = role.Data["Title"];
            }
            data.ListInProfile = membership.Data["ListInProfile"] == "1" ? true : false;
            data.MaturePublish = group.Data["MaturePublish"] == "1" ? true : false;
            data.MembershipFee = int.Parse(group.Data["MembershipFee"]);
            data.OpenEnrollment = group.Data["OpenEnrollment"] == "1" ? true : false;
            data.ShowInList = group.Data["ShowInList"] == "1" ? true : false;

            return data;
        }

        public List<GroupMembershipData> GetAgentGroupMemberships(string RequestingAgentID, string AgentID)
        {
            List<GroupMembershipData> memberships = new List<GroupMembershipData>();

            // 1. Get all the groups that this person is a member of
            MembershipData[] mdata = _Database.RetrieveMemberships(AgentID);

            if (mdata == null || mdata != null && mdata.Length == 0)
                return memberships;

            foreach (MembershipData d in mdata)
            {
                GroupMembershipData gmember = GetAgentGroupMembership(RequestingAgentID, AgentID, d.GroupID, d);
                if (gmember != null)
                {
                    memberships.Add(gmember);
                    //_log.DebugFormat("[XXX]: Member of {0} as {1}", gmember.GroupName, gmember.GroupTitle);
                    //Util.PrintCallStack();
                }
            }

            return memberships;
        }

        public void SetAgentActiveGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            MembershipData data = _Database.RetrieveMember(GroupID, AgentID);
            if (data == null)
                return;

            data.Data["SelectedRoleID"] = RoleID.ToString();
            _Database.StoreMember(data);
        }

        public void UpdateMembership(string RequestingAgentID, string AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile)
        {
            // TODO: check perms

            MembershipData membership = _Database.RetrieveMember(GroupID, AgentID);
            if (membership == null)
                return;

            membership.Data["AcceptNotices"] = AcceptNotices ? "1" : "0";
            membership.Data["ListInProfile"] = ListInProfile ? "1" : "0";

            _Database.StoreMember(membership);
        }

        public bool AddGroupNotice(string RequestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message,
            bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
        {
            // Check perms
            if (!HasPower(RequestingAgentID, groupID, GroupPowers.SendNotices))
            {
                _log.DebugFormat("[Groups]: ({0}) Attempt at sending notice to group {1} denied because of lack of permission", RequestingAgentID, groupID);
                return false;
            }

            return _AddNotice(groupID, noticeID, fromName, subject, message, hasAttachment, attType, attName, attItemID, attOwnerID);
        }

        public GroupNoticeInfo GetGroupNotice(string RequestingAgentID, UUID noticeID)
        {
            NoticeData data = _Database.RetrieveNotice(noticeID);

            if (data == null)
                return null;

            return _NoticeDataToInfo(data);
        }

        public List<ExtendedGroupNoticeData> GetGroupNotices(string RequestingAgentID, UUID groupID)
        {
            NoticeData[] data = _Database.RetrieveNotices(groupID);
            List<ExtendedGroupNoticeData> infos = new List<ExtendedGroupNoticeData>();

            if (data == null || data != null && data.Length == 0)
                return infos;

            foreach (NoticeData d in data)
            {
                ExtendedGroupNoticeData info = _NoticeDataToData(d);
                infos.Add(info);
            }

            return infos;
        }

        public void ResetAgentGroupChatSessions(string agentID)
        {
        }

        public bool hasAgentBeenInvitedToGroupChatSession(string agentID, UUID groupID)
        {
            return false;
        }

        public bool hasAgentDroppedGroupChatSession(string agentID, UUID groupID)
        {
            return false;
        }

        public void AgentDroppedFromGroupChatSession(string agentID, UUID groupID)
        {
        }

        public void AgentInvitedToGroupChatSession(string agentID, UUID groupID)
        {
        }

        #region Actions without permission checks

        protected void _AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            _AddAgentToGroup(RequestingAgentID, AgentID, GroupID, RoleID, string.Empty);
        }

        protected void _RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID)
        {
            // 1. Delete membership
            _Database.DeleteMember(GroupID, AgentID);

            // 2. Remove from rolememberships
            _Database.DeleteMemberAllRoles(GroupID, AgentID);

            // 3. if it was active group, inactivate it
            PrincipalData principal = _Database.RetrievePrincipal(AgentID);
            if (principal != null && principal.ActiveGroupID == GroupID)
            {
                principal.ActiveGroupID = UUID.Zero;
                _Database.StorePrincipal(principal);
            }
        }

        protected void _AddAgentToGroup(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID, string accessToken)
        {
            // Check if it's already there
            MembershipData data = _Database.RetrieveMember(GroupID, AgentID);
            if (data != null)
                return;

            // Add the membership
            data = new MembershipData
            {
                PrincipalID = AgentID,
                GroupID = GroupID,
                Data = new Dictionary<string, string>()
            };
            data.Data["SelectedRoleID"] = RoleID.ToString();
            data.Data["Contribution"] = "0";
            data.Data["ListInProfile"] = "1";
            data.Data["AcceptNotices"] = "1";
            data.Data["AccessToken"] = accessToken;

            _Database.StoreMember(data);

            // Add principal to everyone role
            _AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, UUID.Zero);

            // Add principal to role, if different from everyone role
            if (RoleID != UUID.Zero)
                _AddAgentToGroupRole(RequestingAgentID, AgentID, GroupID, RoleID);

            // Make this the active group
            PrincipalData pdata = new PrincipalData
            {
                PrincipalID = AgentID,
                ActiveGroupID = GroupID
            };
            _Database.StorePrincipal(pdata);

        }

        protected bool _AddOrUpdateGroupRole(string RequestingAgentID, UUID groupID, UUID roleID, string name, string description, string title, ulong powers, bool add)
        {
            RoleData data = _Database.RetrieveRole(groupID, roleID);

            if (add && data != null) // it already exists, can't create
            {
                _log.DebugFormat("[Groups]: Group {0} already exists. Can't create it again", groupID);
                return false;
            }

            if (!add && data == null) // it doesn't exist, can't update
            {
                _log.DebugFormat("[Groups]: Group {0} doesn't exist. Can't update it", groupID);
                return false;
            }

            if (add)
                data = new RoleData();

            data.GroupID = groupID;
            data.RoleID = roleID;
            data.Data = new Dictionary<string, string>();
            data.Data["Name"] = name;
            data.Data["Description"] = description;
            data.Data["Title"] = title;
            data.Data["Powers"] = powers.ToString();

            return _Database.StoreRole(data);
        }

        protected void _RemoveGroupRole(UUID groupID, UUID roleID)
        {
            _Database.DeleteRole(groupID, roleID);
        }

        protected void _AddAgentToGroupRole(string RequestingAgentID, string AgentID, UUID GroupID, UUID RoleID)
        {
            RoleMembershipData data = _Database.RetrieveRoleMember(GroupID, RoleID, AgentID);
            if (data != null)
                return;

            data = new RoleMembershipData
            {
                GroupID = GroupID,
                PrincipalID = AgentID,
                RoleID = RoleID
            };
            _Database.StoreRoleMember(data);

            // Make it the SelectedRoleID
            MembershipData membership = _Database.RetrieveMember(GroupID, AgentID);
            if (membership == null)
            {
                _log.DebugFormat("[Groups]: ({0}) No such member {0} in group {1}", AgentID, GroupID);
                return;
            }

            membership.Data["SelectedRoleID"] = RoleID.ToString();
            _Database.StoreMember(membership);

        }

        protected List<GroupRolesData> _GetGroupRoles(UUID groupID)
        {
            List<GroupRolesData> roles = new List<GroupRolesData>();

            RoleData[] data = _Database.RetrieveRoles(groupID);

            if (data == null || data != null && data.Length == 0)
                return roles;

            foreach (RoleData d in data)
            {
                GroupRolesData r = new GroupRolesData
                {
                    Description = d.Data["Description"],
                    Members = _Database.RoleMemberCount(groupID, d.RoleID),
                    Name = d.Data["Name"],
                    Powers = ulong.Parse(d.Data["Powers"]),
                    RoleID = d.RoleID,
                    Title = d.Data["Title"]
                };

                roles.Add(r);
            }

            return roles;
        }

        protected List<ExtendedGroupRoleMembersData> _GetGroupRoleMembers(UUID GroupID, bool isInGroup)
        {
            List<ExtendedGroupRoleMembersData> rmembers = new List<ExtendedGroupRoleMembersData>();

            RoleData[] rdata = new RoleData[0];
            if (!isInGroup)
            {
                rdata = _Database.RetrieveRoles(GroupID);
                if (rdata == null || rdata != null && rdata.Length == 0)
                    return rmembers;
            }
            List<RoleData> rlist = new List<RoleData>(rdata);
            if (!isInGroup)
                rlist = rlist.FindAll(r => (ulong.Parse(r.Data["Powers"]) & (ulong)GroupPowers.MemberVisible) != 0);

            RoleMembershipData[] data = _Database.RetrieveRolesMembers(GroupID);

            if (data == null || data != null && data.Length == 0)
                return rmembers;

            foreach (RoleMembershipData d in data)
            {
                if (!isInGroup)
                {
                    RoleData rd = rlist.Find(_r => _r.RoleID == d.RoleID); // visible role
                    if (rd == null)
                        continue;
                }

                ExtendedGroupRoleMembersData r = new ExtendedGroupRoleMembersData
                {
                    MemberID = d.PrincipalID,
                    RoleID = d.RoleID
                };

                rmembers.Add(r);
            }

            return rmembers;
        }

        protected bool _AddNotice(UUID groupID, UUID noticeID, string fromName, string subject, string message,
            bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
        {
            NoticeData data = new NoticeData
            {
                GroupID = groupID,
                NoticeID = noticeID,
                Data = new Dictionary<string, string>()
            };
            data.Data["FromName"] = fromName;
            data.Data["Subject"] = subject;
            data.Data["Message"] = message;
            data.Data["HasAttachment"] = hasAttachment ? "1" : "0";
            if (hasAttachment)
            {
                data.Data["AttachmentType"] = attType.ToString();
                data.Data["AttachmentName"] = attName;
                data.Data["AttachmentItemID"] = attItemID.ToString();
                data.Data["AttachmentOwnerID"] = attOwnerID;
            }
            data.Data["TMStamp"] = ((uint)Util.UnixTimeSinceEpoch()).ToString();

            return _Database.StoreNotice(data);
        }

        #endregion

        #region structure translations
        ExtendedGroupRecord _GroupDataToRecord(GroupData data)
        {
            if (data == null)
                return null;

            ExtendedGroupRecord rec = new ExtendedGroupRecord
            {
                AllowPublish = data.Data["AllowPublish"] == "1" ? true : false,
                Charter = data.Data["Charter"],
                FounderID = new UUID(data.Data["FounderID"]),
                GroupID = data.GroupID,
                GroupName = data.Data["Name"],
                GroupPicture = new UUID(data.Data["InsigniaID"]),
                MaturePublish = data.Data["MaturePublish"] == "1" ? true : false,
                MembershipFee = int.Parse(data.Data["MembershipFee"]),
                OpenEnrollment = data.Data["OpenEnrollment"] == "1" ? true : false,
                OwnerRoleID = new UUID(data.Data["OwnerRoleID"]),
                ShowInList = data.Data["ShowInList"] == "1" ? true : false,
                ServiceLocation = data.Data["Location"],
                MemberCount = _Database.MemberCount(data.GroupID),
                RoleCount = _Database.RoleCount(data.GroupID)
            };

            return rec;
        }

        GroupNoticeInfo _NoticeDataToInfo(NoticeData data)
        {
            GroupNoticeInfo notice = new GroupNoticeInfo
            {
                GroupID = data.GroupID,
                Message = data.Data["Message"],
                noticeData = _NoticeDataToData(data)
            };

            return notice;
        }

        ExtendedGroupNoticeData _NoticeDataToData(NoticeData data)
        {
            ExtendedGroupNoticeData notice = new ExtendedGroupNoticeData
            {
                FromName = data.Data["FromName"],
                NoticeID = data.NoticeID,
                Subject = data.Data["Subject"],
                Timestamp = uint.Parse((string)data.Data["TMStamp"]),
                HasAttachment = data.Data["HasAttachment"] == "1" ? true : false
            };
            if (notice.HasAttachment)
            {
                notice.AttachmentName = data.Data["AttachmentName"];
                notice.AttachmentItemID = new UUID(data.Data["AttachmentItemID"].ToString());
                notice.AttachmentType = byte.Parse(data.Data["AttachmentType"].ToString());
                notice.AttachmentOwnerID = data.Data["AttachmentOwnerID"].ToString();
            }


            return notice;
        }

        #endregion

        #region permissions
        private bool HasPower(string agentID, UUID groupID, GroupPowers power)
        {
            RoleMembershipData[] rmembership = _Database.RetrieveMemberRoles(groupID, agentID);
            if (rmembership == null || rmembership != null && rmembership.Length == 0)
                return false;

            foreach (RoleMembershipData rdata in rmembership)
            {
                RoleData role = _Database.RetrieveRole(groupID, rdata.RoleID);
                if ( (ulong.Parse(role.Data["Powers"]) & (ulong)power) != 0 )
                    return true;
            }
            return false;
        }

        private bool IsOwner(string agentID, UUID groupID)
        {
            GroupData group = _Database.RetrieveGroup(groupID);
            if (group == null)
                return false;

            RoleMembershipData rmembership = _Database.RetrieveRoleMember(groupID, new UUID(group.Data["OwnerRoleID"]), agentID);
            if (rmembership == null)
                return false;

            return true;
        }
        #endregion

    }
}
