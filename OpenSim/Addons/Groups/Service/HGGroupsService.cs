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
using Nini.Config;

using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;

namespace OpenSim.Groups
{
    public class HGGroupsService : GroupsService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IOfflineIMService _OfflineIM;
        private readonly IUserAccountService _UserAccounts;
        private readonly string _HomeURI;

        public HGGroupsService(IConfigSource config, IOfflineIMService im, IUserAccountService users, string homeURI)
            : base(config, string.Empty)
        {
            _OfflineIM = im;
            _UserAccounts = users;
            _HomeURI = homeURI;
            if (!_HomeURI.EndsWith("/"))
                _HomeURI += "/";
        }


        #region HG specific operations

        public bool CreateGroupProxy(string RequestingAgentID, string agentID,  string accessToken, UUID groupID, string serviceLocation, string name, out string reason)
        {
            reason = string.Empty;
            Uri uri;
            try
            {
                uri = new Uri(serviceLocation);
            }
            catch (UriFormatException)
            {
                reason = "Bad location for group proxy";
                return false;
            }

            // Check if it already exists
            GroupData grec = _Database.RetrieveGroup(groupID);
            if (grec == null ||
                !string.IsNullOrEmpty(grec.Data["Location"]) && !string.Equals(grec.Data["Location"], serviceLocation, StringComparison.CurrentCultureIgnoreCase))
            {
                // Create the group
                grec = new GroupData
                {
                    GroupID = groupID,
                    Data = new Dictionary<string, string>()
                };
                grec.Data["Name"] = name + " @ " + uri.Authority;
                grec.Data["Location"] = serviceLocation;
                grec.Data["Charter"] = string.Empty;
                grec.Data["InsigniaID"] = UUID.Zero.ToString();
                grec.Data["FounderID"] = UUID.Zero.ToString();
                grec.Data["MembershipFee"] = "0";
                grec.Data["OpenEnrollment"] = "0";
                grec.Data["ShowInList"] = "0";
                grec.Data["AllowPublish"] = "0";
                grec.Data["MaturePublish"] = "0";
                grec.Data["OwnerRoleID"] = UUID.Zero.ToString();


                if (!_Database.StoreGroup(grec))
                    return false;
            }

            if (string.IsNullOrEmpty(grec.Data["Location"]))
            {
                reason = "Cannot add proxy membership to non-proxy group";
                return false;
            }

            UUID uid = UUID.Zero;
            string url = string.Empty, first = string.Empty, last = string.Empty, tmp = string.Empty;
            Util.ParseUniversalUserIdentifier(RequestingAgentID, out uid, out url, out first, out last, out tmp);
            string fromName = first + "." + last + "@" + url;

            // Invite to group again
            InviteToGroup(fromName, groupID, new UUID(agentID), grec.Data["Name"]);

            // Stick the proxy membership in the DB already
            // we'll delete it if the agent declines the invitation
            MembershipData membership = new MembershipData
            {
                PrincipalID = agentID,
                GroupID = groupID,
                Data = new Dictionary<string, string>()
            };
            membership.Data["SelectedRoleID"] = UUID.Zero.ToString();
            membership.Data["Contribution"] = "0";
            membership.Data["ListInProfile"] = "1";
            membership.Data["AcceptNotices"] = "1";
            membership.Data["AccessToken"] = accessToken;

            _Database.StoreMember(membership);

            return true;
        }

        public bool RemoveAgentFromGroup(string RequestingAgentID, string AgentID, UUID GroupID, string token)
        {
            // check the token
            MembershipData membership = _Database.RetrieveMember(GroupID, AgentID);
            if (membership != null)
            {
                if (!string.IsNullOrEmpty(token) && token.Equals(membership.Data["AccessToken"]))
                {
                    return RemoveAgentFromGroup(RequestingAgentID, AgentID, GroupID);
                }
                else
                {
                    _log.DebugFormat("[Groups.HGGroupsService]: access token {0} did not match stored one {1}", token, membership.Data["AccessToken"]);
                    return false;
                }
            }
            else
            {
                _log.DebugFormat("[Groups.HGGroupsService]: membership not found for {0}", AgentID);
                return false;
            }
        }

        public ExtendedGroupRecord GetGroupRecord(string RequestingAgentID, UUID GroupID, string groupName, string token)
        {
            // check the token
            if (!VerifyToken(GroupID, RequestingAgentID, token))
                return null;

            ExtendedGroupRecord grec;
            if (GroupID == UUID.Zero)
                grec = GetGroupRecord(RequestingAgentID, groupName);
            else
                grec = GetGroupRecord(RequestingAgentID, GroupID);

            if (grec != null)
                FillFounderUUI(grec);

            return grec;
        }

        public List<ExtendedGroupMembersData> GetGroupMembers(string RequestingAgentID, UUID GroupID, string token)
        {
            if (!VerifyToken(GroupID, RequestingAgentID, token))
                return new List<ExtendedGroupMembersData>();

            List<ExtendedGroupMembersData> members = GetGroupMembers(RequestingAgentID, GroupID);

            // convert UUIDs to UUIs
            members.ForEach(delegate (ExtendedGroupMembersData m)
            {
                if (m.AgentID.ToString().Length == 36) // UUID
                {
                    UserAccount account = _UserAccounts.GetUserAccount(UUID.Zero, new UUID(m.AgentID));
                    if (account != null)
                        m.AgentID = Util.UniversalIdentifier(account.PrincipalID, account.FirstName, account.LastName, _HomeURI);
                }
            });

            return members;
        }

        public List<GroupRolesData> GetGroupRoles(string RequestingAgentID, UUID GroupID, string token)
        {
            if (!VerifyToken(GroupID, RequestingAgentID, token))
                return new List<GroupRolesData>();

            return GetGroupRoles(RequestingAgentID, GroupID);
        }

        public List<ExtendedGroupRoleMembersData> GetGroupRoleMembers(string RequestingAgentID, UUID GroupID, string token)
        {
            if (!VerifyToken(GroupID, RequestingAgentID, token))
                return new List<ExtendedGroupRoleMembersData>();

            List<ExtendedGroupRoleMembersData> rolemembers = GetGroupRoleMembers(RequestingAgentID, GroupID);

            // convert UUIDs to UUIs
            rolemembers.ForEach(delegate(ExtendedGroupRoleMembersData m)
            {
                if (m.MemberID.ToString().Length == 36) // UUID
                {
                    UserAccount account = _UserAccounts.GetUserAccount(UUID.Zero, new UUID(m.MemberID));
                    if (account != null)
                        m.MemberID = Util.UniversalIdentifier(account.PrincipalID, account.FirstName, account.LastName, _HomeURI);
                }
            });

            return rolemembers;
        }

        public bool AddNotice(string RequestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message,
            bool hasAttachment, byte attType, string attName, UUID attItemID, string attOwnerID)
        {
            // check that the group proxy exists
            ExtendedGroupRecord grec = GetGroupRecord(RequestingAgentID, groupID);
            if (grec == null)
            {
                _log.DebugFormat("[Groups.HGGroupsService]: attempt at adding notice to non-existent group proxy");
                return false;
            }

            // check that the group is remote
            if (string.IsNullOrEmpty(grec.ServiceLocation))
            {
                _log.DebugFormat("[Groups.HGGroupsService]: attempt at adding notice to local (non-proxy) group");
                return false;
            }

            // check that there isn't already a notice with the same ID
            if (GetGroupNotice(RequestingAgentID, noticeID) != null)
            {
                _log.DebugFormat("[Groups.HGGroupsService]: a notice with the same ID already exists", grec.ServiceLocation);
                return false;
            }

            // This has good intentions (security) but it will potentially DDS the origin...
            // We'll need to send a proof along with the message. Maybe encrypt the message
            // using key pairs
            //
            //// check that the notice actually exists in the origin
            //GroupsServiceHGConnector c = new GroupsServiceHGConnector(grec.ServiceLocation);
            //if (!c.VerifyNotice(noticeID, groupID))
            //{
            //    _log.DebugFormat("[Groups.HGGroupsService]: notice does not exist at origin {0}", grec.ServiceLocation);
            //    return false;
            //}

            // ok, we're good!
            return _AddNotice(groupID, noticeID, fromName, subject, message, hasAttachment, attType, attName, attItemID, attOwnerID);
        }

        public bool VerifyNotice(UUID noticeID, UUID groupID)
        {
            GroupNoticeInfo notice = GetGroupNotice(string.Empty, noticeID);

            if (notice == null)
                return false;

            if (notice.GroupID != groupID)
                return false;

            return true;
        }

        #endregion

        private void InviteToGroup(string fromName, UUID groupID, UUID invitedAgentID, string groupName)
        {
            // Todo: Security check, probably also want to send some kind of notification
            UUID InviteID = UUID.Random();

            if (AddAgentToGroupInvite(InviteID, groupID, invitedAgentID.ToString()))
            {
                Guid inviteUUID = InviteID.Guid;

                GridInstantMessage msg = new GridInstantMessage
                {
                    imSessionID = inviteUUID,

                    // msg.fromAgentID = agentID.Guid;
                    fromAgentID = groupID.Guid,
                    toAgentID = invitedAgentID.Guid,
                    //msg.timestamp = (uint)Util.UnixTimeSinceEpoch();
                    timestamp = 0,
                    fromAgentName = fromName,
                    message = string.Format("Please confirm your acceptance to join group {0}.", groupName),
                    dialog = (byte)OpenMetaverse.InstantMessageDialog.GroupInvitation,
                    fromGroup = true,
                    offline = 0,
                    ParentEstateID = 0,
                    Position = Vector3.Zero,
                    RegionID = UUID.Zero.Guid,
                    binaryBucket = new byte[20]
                };

                string reason = string.Empty;
                _OfflineIM.StoreMessage(msg, out reason);

            }
        }

        private bool AddAgentToGroupInvite(UUID inviteID, UUID groupID, string agentID)
        {
            // Check whether the invitee is already a member of the group
            MembershipData m = _Database.RetrieveMember(groupID, agentID);
            if (m != null)
                return false;

            // Check whether there are pending invitations and delete them
            InvitationData invite = _Database.RetrieveInvitation(groupID, agentID);
            if (invite != null)
                _Database.DeleteInvite(invite.InviteID);

            invite = new InvitationData
            {
                InviteID = inviteID,
                PrincipalID = agentID,
                GroupID = groupID,
                RoleID = UUID.Zero,
                Data = new Dictionary<string, string>()
            };

            return _Database.StoreInvitation(invite);
        }

        private void FillFounderUUI(ExtendedGroupRecord grec)
        {
            UserAccount account = _UserAccounts.GetUserAccount(UUID.Zero, grec.FounderID);
            if (account != null)
                grec.FounderUUI = Util.UniversalIdentifier(account.PrincipalID, account.FirstName, account.LastName, _HomeURI);
        }

        private bool VerifyToken(UUID groupID, string agentID, string token)
        {
            // check the token
            MembershipData membership = _Database.RetrieveMember(groupID, agentID);
            if (membership != null)
            {
                if (!string.IsNullOrEmpty(token) && token.Equals(membership.Data["AccessToken"]))
                    return true;
                else
                    _log.DebugFormat("[Groups.HGGroupsService]: access token {0} did not match stored one {1}", token, membership.Data["AccessToken"]);
            }
            else
                _log.DebugFormat("[Groups.HGGroupsService]: membership not found for {0}", agentID);

            return false;
        }
    }
}
