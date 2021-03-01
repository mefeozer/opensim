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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

using Nwc.XmlRpc;

using log4net;
using Mono.Addins;
using Nini.Config;

using OpenMetaverse;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Avatar.XmlRpcGroups
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "XmlRpcGroupsServicesConnectorModule")]
    public class XmlRpcGroupsServicesConnectorModule : ISharedRegionModule, IGroupsServicesConnector
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _debugEnabled = false;

        public const GroupPowers DefaultEveryonePowers
            = GroupPowers.AllowSetHome
                | GroupPowers.Accountable
                | GroupPowers.JoinChat
                | GroupPowers.AllowVoiceChat
                | GroupPowers.ReceiveNotices
                | GroupPowers.StartProposal
                | GroupPowers.VoteOnProposal;

        // Would this be cleaner as (GroupPowers)ulong.MaxValue?
        public const GroupPowers DefaultOwnerPowers
            = GroupPowers.Accountable
                | GroupPowers.AllowEditLand
                | GroupPowers.AllowFly
                | GroupPowers.AllowLandmark
                | GroupPowers.AllowRez
                | GroupPowers.AllowSetHome
                | GroupPowers.AllowVoiceChat
                | GroupPowers.AssignMember
                | GroupPowers.AssignMemberLimited
                | GroupPowers.ChangeActions
                | GroupPowers.ChangeIdentity
                | GroupPowers.ChangeMedia
                | GroupPowers.ChangeOptions
                | GroupPowers.CreateRole
                | GroupPowers.DeedObject
                | GroupPowers.DeleteRole
                | GroupPowers.Eject
                | GroupPowers.FindPlaces
                | GroupPowers.Invite
                | GroupPowers.JoinChat
                | GroupPowers.LandChangeIdentity
                | GroupPowers.LandDeed
                | GroupPowers.LandDivideJoin
                | GroupPowers.LandEdit
                | GroupPowers.AllowEnvironment
                | GroupPowers.LandEjectAndFreeze
                | GroupPowers.LandGardening
                | GroupPowers.LandManageAllowed
                | GroupPowers.LandManageBanned
                | GroupPowers.LandManagePasses
                | GroupPowers.LandOptions
                | GroupPowers.LandRelease
                | GroupPowers.LandSetSale
                | GroupPowers.ModerateChat
                | GroupPowers.ObjectManipulate
                | GroupPowers.ObjectSetForSale
                | GroupPowers.ReceiveNotices
                | GroupPowers.RemoveMember
                | GroupPowers.ReturnGroupOwned
                | GroupPowers.ReturnGroupSet
                | GroupPowers.ReturnNonGroup
                | GroupPowers.RoleProperties
                | GroupPowers.SendNotices
                | GroupPowers.SetLandingPoint
                | GroupPowers.StartProposal
                | GroupPowers.VoteOnProposal;

        private bool _connectorEnabled = false;

        private string _groupsServerURI = string.Empty;

        private bool _disableKeepAlive = true;

        private string _groupReadKey  = string.Empty;
        private string _groupWriteKey = string.Empty;

        private IUserAccountService _accountService = null;

        private ExpiringCache<string, XmlRpcResponse> _memoryCache;
        private int _cacheTimeout = 30;

        // Used to track which agents are have dropped from a group chat session
        // Should be reset per agent, on logon
        // TODO: move this to Flotsam XmlRpc Service
        // SessionID, List<AgentID>
        private readonly Dictionary<UUID, List<UUID>> _groupsAgentsDroppedFromChatSession = new Dictionary<UUID, List<UUID>>();
        private readonly Dictionary<UUID, List<UUID>> _groupsAgentsInvitedToChatSession = new Dictionary<UUID, List<UUID>>();

        #region Region Module interfaceBase Members

        public string Name => "XmlRpcGroupsServicesConnector";

        // this module is not intended to be replaced, but there should only be 1 of them.
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig groupsConfig = config.Configs["Groups"];

            if (groupsConfig == null)
            {
                // Do not run this module by default.
                return;
            }
            else
            {
                // if groups aren't enabled, we're not needed.
                // if we're not specified as the connector to use, then we're not wanted
                if (groupsConfig.GetBoolean("Enabled", false) == false
                    || groupsConfig.GetString("ServicesConnectorModule", "XmlRpcGroupsServicesConnector") != Name)
                {
                    _connectorEnabled = false;
                    return;
                }

                _log.DebugFormat("[XMLRPC-GROUPS-CONNECTOR]: Initializing {0}", this.Name);

                _groupsServerURI = groupsConfig.GetString("GroupsServerURI", string.Empty);
                if (string.IsNullOrEmpty(_groupsServerURI))
                {
                    _log.ErrorFormat("Please specify a valid URL for GroupsServerURI in OpenSim.ini, [Groups]");
                    _connectorEnabled = false;
                    return;
                }

                _disableKeepAlive = groupsConfig.GetBoolean("XmlRpcDisableKeepAlive", true);

                _groupReadKey = groupsConfig.GetString("XmlRpcServiceReadKey", string.Empty);
                _groupWriteKey = groupsConfig.GetString("XmlRpcServiceWriteKey", string.Empty);

                _cacheTimeout = groupsConfig.GetInt("GroupsCacheTimeout", 30);

                if (_cacheTimeout == 0)
                {
                    _log.WarnFormat("[XMLRPC-GROUPS-CONNECTOR]: Groups Cache Disabled.");
                }
                else
                {
                    _log.InfoFormat("[XMLRPC-GROUPS-CONNECTOR]: Groups Cache Timeout set to {0}.", _cacheTimeout);
                }

                _debugEnabled = groupsConfig.GetBoolean("DebugEnabled", false);

                // If we got all the config options we need, lets start'er'up
                _memoryCache = new ExpiringCache<string, XmlRpcResponse>();
                _connectorEnabled = true;
            }
        }

        public void Close()
        {
        }

        public void AddRegion(OpenSim.Region.Framework.Scenes.Scene scene)
        {
            if (_connectorEnabled)
            {

                if (_accountService == null)
                {
                    _accountService = scene.UserAccountService;
                }


                scene.RegisterModuleInterface<IGroupsServicesConnector>(this);
            }
        }

        public void RemoveRegion(OpenSim.Region.Framework.Scenes.Scene scene)
        {
            if (scene.RequestModuleInterface<IGroupsServicesConnector>() == this)
            {
                scene.UnregisterModuleInterface<IGroupsServicesConnector>(this);
            }
        }

        public void RegionLoaded(OpenSim.Region.Framework.Scenes.Scene scene)
        {
            // TODO: May want to consider listenning for Agent Connections so we can pre-cache group info
            // scene.EventManager.OnNewClient += OnNewClient;
        }

        #endregion

        #region ISharedRegionModule Members

        public void PostInitialise()
        {
            // NoOp
        }

        #endregion

        #region IGroupsServicesConnector Members

        /// <summary>
        /// Create a Group, including Everyone and Owners Role, place FounderID in both groups, select Owner as selected role, and newly created group as agent's active role.
        /// </summary>
        public UUID CreateGroup(UUID requestingAgentID, string name, string charter, bool showInList, UUID insigniaID,
                                int membershipFee, bool openEnrollment, bool allowPublish,
                                bool maturePublish, UUID founderID)
        {
            UUID GroupID = UUID.Random();
            UUID OwnerRoleID = UUID.Random();

            Hashtable param = new Hashtable();
            param["GroupID"] = GroupID.ToString();
            param["Name"] = name;
            param["Charter"] = charter;
            param["ShowInList"] = showInList == true ? 1 : 0;
            param["InsigniaID"] = insigniaID.ToString();
            param["MembershipFee"] = membershipFee;
            param["OpenEnrollment"] = openEnrollment == true ? 1 : 0;
            param["AllowPublish"] = allowPublish == true ? 1 : 0;
            param["MaturePublish"] = maturePublish == true ? 1 : 0;
            param["FounderID"] = founderID.ToString();
            param["EveryonePowers"] = ((ulong)DefaultEveryonePowers).ToString();
            param["OwnerRoleID"] = OwnerRoleID.ToString();
            param["OwnersPowers"] = ((ulong)DefaultOwnerPowers).ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.createGroup", param);

            if (respData.Contains("error"))
            {
                // UUID is not nullable

                return UUID.Zero;
            }

            return UUID.Parse((string)respData["GroupID"]);
        }

        public void UpdateGroup(UUID requestingAgentID, UUID groupID, string charter, bool showInList,
                                UUID insigniaID, int membershipFee, bool openEnrollment,
                                bool allowPublish, bool maturePublish)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = groupID.ToString();
            param["Charter"] = charter;
            param["ShowInList"] = showInList == true ? 1 : 0;
            param["InsigniaID"] = insigniaID.ToString();
            param["MembershipFee"] = membershipFee;
            param["OpenEnrollment"] = openEnrollment == true ? 1 : 0;
            param["AllowPublish"] = allowPublish == true ? 1 : 0;
            param["MaturePublish"] = maturePublish == true ? 1 : 0;

            XmlRpcCall(requestingAgentID, "groups.updateGroup", param);
        }

        public void AddGroupRole(UUID requestingAgentID, UUID groupID, UUID roleID, string name, string description,
                                 string title, ulong powers)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = groupID.ToString();
            param["RoleID"] = roleID.ToString();
            param["Name"] = name;
            param["Description"] = description;
            param["Title"] = title;
            param["Powers"] = powers.ToString();

            XmlRpcCall(requestingAgentID, "groups.addRoleToGroup", param);
        }

        public void RemoveGroupRole(UUID requestingAgentID, UUID groupID, UUID roleID)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = groupID.ToString();
            param["RoleID"] = roleID.ToString();

            XmlRpcCall(requestingAgentID, "groups.removeRoleFromGroup", param);
        }

        public void UpdateGroupRole(UUID requestingAgentID, UUID groupID, UUID roleID, string name, string description,
                                    string title, ulong powers)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = groupID.ToString();
            param["RoleID"] = roleID.ToString();
            if (name != null)
            {
                param["Name"] = name;
            }
            if (description != null)
            {
                param["Description"] = description;
            }
            if (title != null)
            {
                param["Title"] = title;
            }
            param["Powers"] = powers.ToString();

            XmlRpcCall(requestingAgentID, "groups.updateGroupRole", param);
        }

        public GroupRecord GetGroupRecord(UUID requestingAgentID, UUID GroupID, string GroupName)
        {
            Hashtable param = new Hashtable();
            if (GroupID != UUID.Zero)
            {
                param["GroupID"] = GroupID.ToString();
            }
            if (!string.IsNullOrEmpty(GroupName))
            {
                param["Name"] = GroupName.ToString();
            }

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getGroup", param);

            if (respData.Contains("error"))
            {
                return null;
            }

            return GroupProfileHashtableToGroupRecord(respData);

        }

        public GroupProfileData GetMemberGroupProfile(UUID requestingAgentID, UUID GroupID, UUID AgentID)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = GroupID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getGroup", param);

            if (respData.Contains("error"))
            {
                // GroupProfileData is not nullable
                return new GroupProfileData();
            }

            GroupMembershipData MemberInfo = GetAgentGroupMembership(requestingAgentID, AgentID, GroupID);
            GroupProfileData MemberGroupProfile = GroupProfileHashtableToGroupProfileData(respData);
            if(MemberInfo != null)
            {
                MemberGroupProfile.MemberTitle = MemberInfo.GroupTitle;
                MemberGroupProfile.PowersMask = MemberInfo.GroupPowers;
            }
            return MemberGroupProfile;
        }

        public void SetAgentActiveGroup(UUID requestingAgentID, UUID AgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();

            XmlRpcCall(requestingAgentID, "groups.setAgentActiveGroup", param);
        }

        public void SetAgentActiveGroupRole(UUID requestingAgentID, UUID AgentID, UUID GroupID, UUID RoleID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();
            param["SelectedRoleID"] = RoleID.ToString();

            XmlRpcCall(requestingAgentID, "groups.setAgentGroupInfo", param);
        }

        public void SetAgentGroupInfo(UUID requestingAgentID, UUID AgentID, UUID GroupID, bool AcceptNotices, bool ListInProfile)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();
            param["AcceptNotices"] = AcceptNotices ? "1" : "0";
            param["ListInProfile"] = ListInProfile ? "1" : "0";

            XmlRpcCall(requestingAgentID, "groups.setAgentGroupInfo", param);

        }

        public void AddAgentToGroupInvite(UUID requestingAgentID, UUID inviteID, UUID groupID, UUID roleID, UUID agentID)
        {
            Hashtable param = new Hashtable();
            param["InviteID"] = inviteID.ToString();
            param["AgentID"] = agentID.ToString();
            param["RoleID"] = roleID.ToString();
            param["GroupID"] = groupID.ToString();

            XmlRpcCall(requestingAgentID, "groups.addAgentToGroupInvite", param);

        }

        public GroupInviteInfo GetAgentToGroupInvite(UUID requestingAgentID, UUID inviteID)
        {
            Hashtable param = new Hashtable();
            param["InviteID"] = inviteID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getAgentToGroupInvite", param);

            if (respData.Contains("error"))
            {
                return null;
            }

            GroupInviteInfo inviteInfo = new GroupInviteInfo
            {
                InviteID = inviteID,
                GroupID = UUID.Parse((string)respData["GroupID"]),
                RoleID = UUID.Parse((string)respData["RoleID"]),
                AgentID = UUID.Parse((string)respData["AgentID"])
            };

            return inviteInfo;
        }

        public void RemoveAgentToGroupInvite(UUID requestingAgentID, UUID inviteID)
        {
            Hashtable param = new Hashtable();
            param["InviteID"] = inviteID.ToString();

            XmlRpcCall(requestingAgentID, "groups.removeAgentToGroupInvite", param);
        }

        public void AddAgentToGroup(UUID requestingAgentID, UUID AgentID, UUID GroupID, UUID RoleID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();
            param["RoleID"] = RoleID.ToString();

            XmlRpcCall(requestingAgentID, "groups.addAgentToGroup", param);
        }

        public void RemoveAgentFromGroup(UUID requestingAgentID, UUID AgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();

            XmlRpcCall(requestingAgentID, "groups.removeAgentFromGroup", param);
        }

        public void AddAgentToGroupRole(UUID requestingAgentID, UUID AgentID, UUID GroupID, UUID RoleID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();
            param["RoleID"] = RoleID.ToString();

            XmlRpcCall(requestingAgentID, "groups.addAgentToGroupRole", param);
        }

        public void RemoveAgentFromGroupRole(UUID requestingAgentID, UUID AgentID, UUID GroupID, UUID RoleID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();
            param["RoleID"] = RoleID.ToString();

            XmlRpcCall(requestingAgentID, "groups.removeAgentFromGroupRole", param);
        }

        public List<DirGroupsReplyData> FindGroups(UUID requestingAgentID, string search)
        {
            Hashtable param = new Hashtable();
            param["Search"] = search;

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.findGroups", param);

            List<DirGroupsReplyData> findings = new List<DirGroupsReplyData>();

            if (!respData.Contains("error"))
            {
                Hashtable results = (Hashtable)respData["results"];
                foreach (Hashtable groupFind in results.Values)
                {
                    DirGroupsReplyData data = new DirGroupsReplyData
                    {
                        groupID = new UUID((string)groupFind["GroupID"])
                    };
                    ;
                    data.groupName = (string)groupFind["Name"];
                    data.members = int.Parse((string)groupFind["Members"]);
                    // data.searchOrder = order;

                    findings.Add(data);
                }
            }

            return findings;
        }

        public GroupMembershipData GetAgentGroupMembership(UUID requestingAgentID, UUID AgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getAgentGroupMembership", param);

            if (respData.Contains("error"))
            {
                return null;
            }

            GroupMembershipData data = HashTableToGroupMembershipData(respData);

            return data;
        }

        public GroupMembershipData GetAgentActiveMembership(UUID requestingAgentID, UUID AgentID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getAgentActiveMembership", param);

            if (respData.Contains("error"))
            {
                return null;
            }

            return HashTableToGroupMembershipData(respData);
        }

        public List<GroupMembershipData> GetAgentGroupMemberships(UUID requestingAgentID, UUID AgentID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getAgentGroupMemberships", param);

            List<GroupMembershipData> memberships = new List<GroupMembershipData>();

            if (!respData.Contains("error"))
            {
                foreach (object membership in respData.Values)
                {
                    memberships.Add(HashTableToGroupMembershipData((Hashtable)membership));
                }
            }

            return memberships;
        }

        public List<GroupRolesData> GetAgentGroupRoles(UUID requestingAgentID, UUID AgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["AgentID"] = AgentID.ToString();
            param["GroupID"] = GroupID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getAgentRoles", param);

            List<GroupRolesData> Roles = new List<GroupRolesData>();

            if (respData.Contains("error"))
            {
                return Roles;
            }

            foreach (Hashtable role in respData.Values)
            {
                GroupRolesData data = new GroupRolesData
                {
                    RoleID = new UUID((string)role["RoleID"]),
                    Name = (string)role["Name"],
                    Description = (string)role["Description"],
                    Powers = ulong.Parse((string)role["Powers"]),
                    Title = (string)role["Title"]
                };

                Roles.Add(data);
            }

            return Roles;
        }

        public List<GroupRolesData> GetGroupRoles(UUID requestingAgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = GroupID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getGroupRoles", param);

            List<GroupRolesData> Roles = new List<GroupRolesData>();

            if (respData.Contains("error"))
            {
                return Roles;
            }

            foreach (Hashtable role in respData.Values)
            {
                GroupRolesData data = new GroupRolesData
                {
                    Description = (string)role["Description"],
                    Members = int.Parse((string)role["Members"]),
                    Name = (string)role["Name"],
                    Powers = ulong.Parse((string)role["Powers"]),
                    RoleID = new UUID((string)role["RoleID"]),
                    Title = (string)role["Title"]
                };

                Roles.Add(data);
            }

            return Roles;
        }

        public List<GroupMembersData> GetGroupMembers(UUID requestingAgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = GroupID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getGroupMembers", param);

            List<GroupMembersData> members = new List<GroupMembersData>();

            if (respData.Contains("error"))
            {
                return members;
            }

            foreach (Hashtable membership in respData.Values)
            {
                GroupMembersData data = new GroupMembersData
                {
                    AcceptNotices = (string)membership["AcceptNotices"] == "1",
                    AgentID = new UUID((string)membership["AgentID"]),
                    Contribution = int.Parse((string)membership["Contribution"]),
                    IsOwner = (string)membership["IsOwner"] == "1",
                    ListInProfile = (string)membership["ListInProfile"] == "1",
                    AgentPowers = ulong.Parse((string)membership["AgentPowers"]),
                    Title = (string)membership["Title"]
                };
                if (membership.ContainsKey("OnlineStatus"))
                    data.OnlineStatus = (string)membership["OnlineStatus"];

                members.Add(data);
            }

            return members;
        }

        public List<GroupRoleMembersData> GetGroupRoleMembers(UUID requestingAgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = GroupID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getGroupRoleMembers", param);

            List<GroupRoleMembersData> members = new List<GroupRoleMembersData>();

            if (!respData.Contains("error"))
            {
                foreach (Hashtable membership in respData.Values)
                {
                    GroupRoleMembersData data = new GroupRoleMembersData
                    {
                        MemberID = new UUID((string)membership["AgentID"]),
                        RoleID = new UUID((string)membership["RoleID"])
                    };

                    members.Add(data);
                }
            }
            return members;
        }

        public List<GroupNoticeData> GetGroupNotices(UUID requestingAgentID, UUID GroupID)
        {
            Hashtable param = new Hashtable();
            param["GroupID"] = GroupID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getGroupNotices", param);

            List<GroupNoticeData> values = new List<GroupNoticeData>();

            if (!respData.Contains("error"))
            {
                foreach (Hashtable value in respData.Values)
                {
                    GroupNoticeData data = new GroupNoticeData
                    {
                        NoticeID = UUID.Parse((string)value["NoticeID"]),
                        Timestamp = uint.Parse((string)value["Timestamp"]),
                        FromName = (string)value["FromName"],
                        Subject = (string)value["Subject"],
                        HasAttachment = false,
                        AssetType = 0
                    };

                    values.Add(data);
                }
            }

            return values;
        }

        public GroupNoticeInfo GetGroupNotice(UUID requestingAgentID, UUID noticeID)
        {
            Hashtable param = new Hashtable();
            param["NoticeID"] = noticeID.ToString();

            Hashtable respData = XmlRpcCall(requestingAgentID, "groups.getGroupNotice", param);

            if (respData.Contains("error"))
            {
                return null;
            }

            GroupNoticeInfo data = new GroupNoticeInfo
            {
                GroupID = UUID.Parse((string)respData["GroupID"]),
                Message = (string)respData["Message"],
                BinaryBucket = Utils.HexStringToBytes((string)respData["BinaryBucket"], true)
            };
            data.noticeData.NoticeID = UUID.Parse((string)respData["NoticeID"]);
            data.noticeData.Timestamp = uint.Parse((string)respData["Timestamp"]);
            data.noticeData.FromName = (string)respData["FromName"];
            data.noticeData.Subject = (string)respData["Subject"];
            data.noticeData.HasAttachment = false;
            data.noticeData.AssetType = 0;

            if (data.Message == null)
            {
                data.Message = string.Empty;
            }

            return data;
        }

        public void AddGroupNotice(UUID requestingAgentID, UUID groupID, UUID noticeID, string fromName, string subject, string message, byte[] binaryBucket)
        {
            string binBucket = OpenMetaverse.Utils.BytesToHexString(binaryBucket, "");

            Hashtable param = new Hashtable();
            param["GroupID"] = groupID.ToString();
            param["NoticeID"] = noticeID.ToString();
            param["FromName"] = fromName;
            param["Subject"] = subject;
            param["Message"] = message;
            param["BinaryBucket"] = binBucket;
            param["TimeStamp"] = ((uint)Util.UnixTimeSinceEpoch()).ToString();

            XmlRpcCall(requestingAgentID, "groups.addGroupNotice", param);
        }

        #endregion

        #region GroupSessionTracking

        public void ResetAgentGroupChatSessions(UUID agentID)
        {
            foreach (List<UUID> agentList in _groupsAgentsDroppedFromChatSession.Values)
            {
                agentList.Remove(agentID);
            }
        }

        public bool hasAgentBeenInvitedToGroupChatSession(UUID agentID, UUID groupID)
        {
            // If we're  tracking this group, and we can find them in the tracking, then they've been invited
            return _groupsAgentsInvitedToChatSession.ContainsKey(groupID)
                && _groupsAgentsInvitedToChatSession[groupID].Contains(agentID);
        }

        public bool hasAgentDroppedGroupChatSession(UUID agentID, UUID groupID)
        {
            // If we're tracking drops for this group,
            // and we find them, well... then they've dropped
            return _groupsAgentsDroppedFromChatSession.ContainsKey(groupID)
                && _groupsAgentsDroppedFromChatSession[groupID].Contains(agentID);
        }

        public void AgentDroppedFromGroupChatSession(UUID agentID, UUID groupID)
        {
            if (_groupsAgentsDroppedFromChatSession.ContainsKey(groupID))
            {
            if (_groupsAgentsInvitedToChatSession[groupID].Contains(agentID))
                _groupsAgentsInvitedToChatSession[groupID].Remove(agentID);

                // If not in dropped list, add
                if (!_groupsAgentsDroppedFromChatSession[groupID].Contains(agentID))
                    _groupsAgentsDroppedFromChatSession[groupID].Add(agentID);
            }
        }

        public void AgentInvitedToGroupChatSession(UUID agentID, UUID groupID)
        {
            // Add Session Status if it doesn't exist for this session
            CreateGroupChatSessionTracking(groupID);

            // If nessesary, remove from dropped list
            if (_groupsAgentsDroppedFromChatSession[groupID].Contains(agentID))
                _groupsAgentsDroppedFromChatSession[groupID].Remove(agentID);

            if (!_groupsAgentsInvitedToChatSession[groupID].Contains(agentID))
                _groupsAgentsInvitedToChatSession[groupID].Add(agentID);
        }

        private void CreateGroupChatSessionTracking(UUID groupID)
        {
            if (!_groupsAgentsDroppedFromChatSession.ContainsKey(groupID))
            {
                _groupsAgentsDroppedFromChatSession.Add(groupID, new List<UUID>());
                _groupsAgentsInvitedToChatSession.Add(groupID, new List<UUID>());
            }

        }
        #endregion

        #region XmlRpcHashtableMarshalling
        private GroupProfileData GroupProfileHashtableToGroupProfileData(Hashtable groupProfile)
        {
            GroupProfileData group = new GroupProfileData
            {
                GroupID = UUID.Parse((string)groupProfile["GroupID"]),
                Name = (string)groupProfile["Name"]
            };

            if (groupProfile["Charter"] != null)
            {
                group.Charter = (string)groupProfile["Charter"];
            }

            group.ShowInList = (string)groupProfile["ShowInList"] == "1";
            group.InsigniaID = UUID.Parse((string)groupProfile["InsigniaID"]);
            group.MembershipFee = int.Parse((string)groupProfile["MembershipFee"]);
            group.OpenEnrollment = (string)groupProfile["OpenEnrollment"] == "1";
            group.AllowPublish = (string)groupProfile["AllowPublish"] == "1";
            group.MaturePublish = (string)groupProfile["MaturePublish"] == "1";
            group.FounderID = UUID.Parse((string)groupProfile["FounderID"]);
            group.OwnerRole = UUID.Parse((string)groupProfile["OwnerRoleID"]);

            group.GroupMembershipCount = int.Parse((string)groupProfile["GroupMembershipCount"]);
            group.GroupRolesCount = int.Parse((string)groupProfile["GroupRolesCount"]);

            return group;
        }

        private GroupRecord GroupProfileHashtableToGroupRecord(Hashtable groupProfile)
        {
            GroupRecord group = new GroupRecord
            {
                GroupID = UUID.Parse((string)groupProfile["GroupID"]),
                GroupName = groupProfile["Name"].ToString()
            };
            if (groupProfile["Charter"] != null)
            {
                group.Charter = (string)groupProfile["Charter"];
            }
            group.ShowInList = (string)groupProfile["ShowInList"] == "1";
            group.GroupPicture = UUID.Parse((string)groupProfile["InsigniaID"]);
            group.MembershipFee = int.Parse((string)groupProfile["MembershipFee"]);
            group.OpenEnrollment = (string)groupProfile["OpenEnrollment"] == "1";
            group.AllowPublish = (string)groupProfile["AllowPublish"] == "1";
            group.MaturePublish = (string)groupProfile["MaturePublish"] == "1";
            group.FounderID = UUID.Parse((string)groupProfile["FounderID"]);
            group.OwnerRoleID = UUID.Parse((string)groupProfile["OwnerRoleID"]);

            return group;
        }

        private static GroupMembershipData HashTableToGroupMembershipData(Hashtable respData)
        {
            GroupMembershipData data = new GroupMembershipData
            {
                AcceptNotices = (string)respData["AcceptNotices"] == "1",
                Contribution = int.Parse((string)respData["Contribution"]),
                ListInProfile = (string)respData["ListInProfile"] == "1",

                ActiveRole = new UUID((string)respData["SelectedRoleID"]),
                GroupTitle = (string)respData["Title"],

                GroupPowers = ulong.Parse((string)respData["GroupPowers"]),

                // Is this group the agent's active group

                GroupID = new UUID((string)respData["GroupID"])
            };

            UUID ActiveGroup = new UUID((string)respData["ActiveGroupID"]);
            data.Active = data.GroupID.Equals(ActiveGroup);

            data.AllowPublish = (string)respData["AllowPublish"] == "1";
            if (respData["Charter"] != null)
            {
                data.Charter = (string)respData["Charter"];
            }
            data.FounderID = new UUID((string)respData["FounderID"]);
            data.GroupID = new UUID((string)respData["GroupID"]);
            data.GroupName = (string)respData["GroupName"];
            data.GroupPicture = new UUID((string)respData["InsigniaID"]);
            data.MaturePublish = (string)respData["MaturePublish"] == "1";
            data.MembershipFee = int.Parse((string)respData["MembershipFee"]);
            data.OpenEnrollment = (string)respData["OpenEnrollment"] == "1";
            data.ShowInList = (string)respData["ShowInList"] == "1";

            return data;
        }

        #endregion

        /// <summary>
        /// Encapsulate the XmlRpc call to standardize security and error handling.
        /// </summary>
        private Hashtable XmlRpcCall(UUID requestingAgentID, string function, Hashtable param)
        {
            XmlRpcResponse resp = null;
            string CacheKey = null;

            // Only bother with the cache if it isn't disabled.
            if (_cacheTimeout > 0)
            {
                if (!function.StartsWith("groups.get"))
                {
                    // Any and all updates cause the cache to clear
                    _memoryCache.Clear();
                }
                else
                {
                    StringBuilder sb = new StringBuilder(requestingAgentID + function);
                    foreach (object key in param.Keys)
                    {
                        if (param[key] != null)
                        {
                            sb.AppendFormat(",{0}:{1}", key.ToString(), param[key].ToString());
                        }
                    }

                    CacheKey = sb.ToString();
                    _memoryCache.TryGetValue(CacheKey, out resp);
                }
            }

            if (resp == null)
            {
                if (_debugEnabled)
                    _log.DebugFormat("[XMLRPC-GROUPS-CONNECTOR]: Cache miss for key {0}", CacheKey);

                string UserService;
                UUID SessionID;
                GetClientGroupRequestID(requestingAgentID, out UserService, out SessionID);

                param.Add("RequestingAgentID", requestingAgentID.ToString());
                param.Add("RequestingAgentUserService", UserService);
                param.Add("RequestingSessionID", SessionID.ToString());
                param.Add("ReadKey", _groupReadKey);
                param.Add("WriteKey", _groupWriteKey);

                IList parameters = new ArrayList();
                parameters.Add(param);

                ConfigurableKeepAliveXmlRpcRequest req;
                req = new ConfigurableKeepAliveXmlRpcRequest(function, parameters, _disableKeepAlive);

                try
                {
                    resp = req.Send(_groupsServerURI);
                }
                catch (Exception e)
                {
                    _log.ErrorFormat(
                        "[XMLRPC-GROUPS-CONNECTOR]: An error has occured while attempting to access the XmlRpcGroups server method {0} at {1}: {2}",
                        function, _groupsServerURI, e.Message);

                    if(_debugEnabled)
                    {
                        _log.ErrorFormat("[XMLRPC-GROUPS-CONNECTOR]: {0}", e.StackTrace);

                        foreach (string ResponseLine in req.RequestResponse.Split(new string[] { Environment.NewLine }, StringSplitOptions.None))
                        {
                            _log.ErrorFormat("[XMLRPC-GROUPS-CONNECTOR]: {0} ", ResponseLine);
                        }

                        foreach (string key in param.Keys)
                        {
                            _log.WarnFormat("[XMLRPC-GROUPS-CONNECTOR]: {0} :: {1}", key, param[key].ToString());
                        }
                    }

                    if (_cacheTimeout > 0 && CacheKey != null)
                    {
                        _memoryCache.AddOrUpdate(CacheKey, resp, 10.0);
                    }
                    Hashtable respData = new Hashtable();
                    respData.Add("error", e.ToString());
                    return respData;
                }

                if (_cacheTimeout > 0 && CacheKey != null)
                {
                    _memoryCache.AddOrUpdate(CacheKey, resp, 10.0);
                }
            }

            if (resp.Value is Hashtable)
            {
                Hashtable respData = (Hashtable)resp.Value;
                if (respData.Contains("error") && !respData.Contains("succeed"))
                {
                    LogRespDataToConsoleError(requestingAgentID, function, param, respData);
                }

                return respData;
            }

            _log.ErrorFormat("[XMLRPC-GROUPS-CONNECTOR]: The XmlRpc server returned a {1} instead of a hashtable for {0}", function, resp.Value.GetType().ToString());

            if (resp.Value is ArrayList)
            {
                ArrayList al = (ArrayList)resp.Value;
                _log.ErrorFormat("[XMLRPC-GROUPS-CONNECTOR]: Contains {0} elements", al.Count);

                foreach (object o in al)
                {
                    _log.ErrorFormat("[XMLRPC-GROUPS-CONNECTOR]: {0} :: {1}", o.GetType().ToString(), o.ToString());
                }
            }
            else
            {
                _log.ErrorFormat("[XMLRPC-GROUPS-CONNECTOR]: Function returned: {0}", resp.Value.ToString());
            }

            Hashtable error = new Hashtable();
            error.Add("error", "invalid return value");
            return error;
        }

        private void LogRespDataToConsoleError(UUID requestingAgentID, string function, Hashtable param, Hashtable respData)
        {
            _log.ErrorFormat(
                "[XMLRPC-GROUPS-CONNECTOR]: Error when calling {0} for {1} with params {2}.  Response params are {3}",
                function, requestingAgentID, Util.PrettyFormatToSingleLine(param), Util.PrettyFormatToSingleLine(respData));
        }

        /// <summary>
        /// Group Request Tokens are an attempt to allow the groups service to authenticate
        /// requests.
        /// TODO: This broke after the big grid refactor, either find a better way, or discard this
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        private void GetClientGroupRequestID(UUID AgentID, out string UserServiceURL, out UUID SessionID)
        {
            UserServiceURL = "";
            SessionID = UUID.Zero;


            // Need to rework this based on changes to User Services
            /*
            UserAccount userAccount = _accountService.GetUserAccount(UUID.Zero,AgentID);
            if (userAccount == null)
            {
                // This should be impossible.  If I've been passed a reference to a client
                // that client should be registered with the UserService.  So something
                // is horribly wrong somewhere.

                _log.WarnFormat("[GROUPS]: Could not find a UserServiceURL for {0}", AgentID);

            }
            else if (userProfile is ForeignUserProfileData)
            {
                // They aren't from around here
                ForeignUserProfileData fupd = (ForeignUserProfileData)userProfile;
                UserServiceURL = fupd.UserServerURI;
                SessionID = fupd.CurrentAgent.SessionID;

            }
            else
            {
                // They're a local user, use this:
                UserServiceURL = _commManager.NetworkServersInfo.UserURL;
                SessionID = userProfile.CurrentAgent.SessionID;
            }
            */
        }

    }
}

namespace Nwc.XmlRpc
{
    using System;
    using System.Collections;
    using System.IO;
    using System.Xml;
    using System.Net;
    using System.Text;

    /// <summary>Class supporting the request side of an XML-RPC transaction.</summary>
    public class ConfigurableKeepAliveXmlRpcRequest : XmlRpcRequest
    {
        private readonly XmlRpcRequestSerializer _serializer = new XmlRpcRequestSerializer();
        private readonly XmlRpcResponseDeserializer _deserializer = new XmlRpcResponseDeserializer();
        private readonly bool _disableKeepAlive = true;

        public string RequestResponse = string.Empty;

        /// <summary>Instantiate an <c>XmlRpcRequest</c> for a specified method and parameters.</summary>
        /// <param name="methodName"><c>String</c> designating the <i>object.method</i> on the server the request
        /// should be directed to.</param>
        /// <param name="parameters"><c>ArrayList</c> of XML-RPC type parameters to invoke the request with.</param>
        public ConfigurableKeepAliveXmlRpcRequest(string methodName, IList parameters, bool disableKeepAlive)
        {
            MethodName = methodName;
            _params = parameters;
            _disableKeepAlive = disableKeepAlive;
        }

        /// <summary>Send the request to the server.</summary>
        /// <param name="url"><c>String</c> The url of the XML-RPC server.</param>
        /// <returns><c>XmlRpcResponse</c> The response generated.</returns>
        public XmlRpcResponse Send(string url)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            if (request == null)
                throw new XmlRpcException(XmlRpcErrorCodes.TRANSPORT_ERROR,
                              XmlRpcErrorCodes.TRANSPORT_ERROR_MSG + ": Could not create request with " + url);
            request.Method = "POST";
            request.ContentType = "text/xml";
            request.AllowWriteStreamBuffering = true;
            request.KeepAlive = !_disableKeepAlive;
            request.Timeout = 30000;

            using (Stream stream = request.GetRequestStream())
            {
                using (XmlTextWriter xml = new XmlTextWriter(stream, Encoding.ASCII))
                {
                    _serializer.Serialize(xml, this);
                    xml.Flush();
                }
            }

            XmlRpcResponse resp;

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                using (Stream s = response.GetResponseStream())
                {
                    using (StreamReader input = new StreamReader(s))
                    {
                        string inputXml = input.ReadToEnd();

                        try
                        {
                            resp = (XmlRpcResponse)_deserializer.Deserialize(inputXml);
                        }
                        catch (Exception e)
                        {
                            RequestResponse = inputXml;
                            throw e;
                        }
                    }
                }
            }

            return resp;
        }
    }
}
