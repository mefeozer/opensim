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
using System.Reflection;

using OpenSim.Framework;

using OpenMetaverse;
using MySql.Data.MySqlClient;

namespace OpenSim.Data.MySQL
{
    public class MySQLGroupsData : IGroupsData
    {
        private readonly MySqlGroupsGroupsHandler _Groups;
        private readonly MySqlGroupsMembershipHandler _Membership;
        private readonly MySqlGroupsRolesHandler _Roles;
        private readonly MySqlGroupsRoleMembershipHandler _RoleMembership;
        private readonly MySqlGroupsInvitesHandler _Invites;
        private readonly MySqlGroupsNoticesHandler _Notices;
        private readonly MySqlGroupsPrincipalsHandler _Principals;

        public MySQLGroupsData(string connectionString, string realm)
        {
            _Groups = new MySqlGroupsGroupsHandler(connectionString, realm + "_groups", realm + "_Store");
            _Membership = new MySqlGroupsMembershipHandler(connectionString, realm + "_membership");
            _Roles = new MySqlGroupsRolesHandler(connectionString, realm + "_roles");
            _RoleMembership = new MySqlGroupsRoleMembershipHandler(connectionString, realm + "_rolemembership");
            _Invites = new MySqlGroupsInvitesHandler(connectionString, realm + "_invites");
            _Notices = new MySqlGroupsNoticesHandler(connectionString, realm + "_notices");
            _Principals = new MySqlGroupsPrincipalsHandler(connectionString, realm + "_principals");
        }

        #region groups table
        public bool StoreGroup(GroupData data)
        {
            return _Groups.Store(data);
        }

        public GroupData RetrieveGroup(UUID groupID)
        {
            GroupData[] groups = _Groups.Get("GroupID", groupID.ToString());
            if (groups.Length > 0)
                return groups[0];

            return null;
        }

        public GroupData RetrieveGroup(string name)
        {
            GroupData[] groups = _Groups.Get("Name", name);
            if (groups.Length > 0)
                return groups[0];

            return null;
        }

        public GroupData[] RetrieveGroups(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                pattern = "1";
            else
                pattern = string.Format("Name LIKE '%{0}%'", MySqlHelper.EscapeString(pattern));

            return _Groups.Get(string.Format("ShowInList=1 AND ({0})", pattern));
        }

        public bool DeleteGroup(UUID groupID)
        {
            return _Groups.Delete("GroupID", groupID.ToString());
        }

        public int GroupsCount()
        {
            return (int)_Groups.GetCount("Location=\"\"");
        }

        #endregion

        #region membership table
        public MembershipData[] RetrieveMembers(UUID groupID)
        {
            return _Membership.Get("GroupID", groupID.ToString());
        }

        public MembershipData RetrieveMember(UUID groupID, string pricipalID)
        {
            MembershipData[] m = _Membership.Get(new string[] { "GroupID", "PrincipalID" },
                                                  new string[] { groupID.ToString(), pricipalID });
            if (m != null && m.Length > 0)
                return m[0];

            return null;
        }

        public MembershipData[] RetrieveMemberships(string pricipalID)
        {
            return _Membership.Get("PrincipalID", pricipalID.ToString());
        }

        public bool StoreMember(MembershipData data)
        {
            return _Membership.Store(data);
        }

        public bool DeleteMember(UUID groupID, string pricipalID)
        {
            return _Membership.Delete(new string[] { "GroupID", "PrincipalID" },
                                       new string[] { groupID.ToString(), pricipalID });
        }

        public int MemberCount(UUID groupID)
        {
            return (int)_Membership.GetCount("GroupID", groupID.ToString());
        }
        #endregion

        #region roles table
        public bool StoreRole(RoleData data)
        {
            return _Roles.Store(data);
        }

        public RoleData RetrieveRole(UUID groupID, UUID roleID)
        {
            RoleData[] data = _Roles.Get(new string[] { "GroupID", "RoleID" },
                                          new string[] { groupID.ToString(), roleID.ToString() });

            if (data != null && data.Length > 0)
                return data[0];

            return null;
        }

        public RoleData[] RetrieveRoles(UUID groupID)
        {
            //return _Roles.RetrieveRoles(groupID);
            return _Roles.Get("GroupID", groupID.ToString());
        }

        public bool DeleteRole(UUID groupID, UUID roleID)
        {
            return _Roles.Delete(new string[] { "GroupID", "RoleID" },
                                  new string[] { groupID.ToString(), roleID.ToString() });
        }

        public int RoleCount(UUID groupID)
        {
            return (int)_Roles.GetCount("GroupID", groupID.ToString());
        }


        #endregion

        #region rolememberhip table
        public RoleMembershipData[] RetrieveRolesMembers(UUID groupID)
        {
            RoleMembershipData[] data = _RoleMembership.Get("GroupID", groupID.ToString());

            return data;
        }

        public RoleMembershipData[] RetrieveRoleMembers(UUID groupID, UUID roleID)
        {
            RoleMembershipData[] data = _RoleMembership.Get(new string[] { "GroupID", "RoleID" },
                                                             new string[] { groupID.ToString(), roleID.ToString() });

            return data;
        }

        public RoleMembershipData[] RetrieveMemberRoles(UUID groupID, string principalID)
        {
            RoleMembershipData[] data = _RoleMembership.Get(new string[] { "GroupID", "PrincipalID" },
                                                             new string[] { groupID.ToString(), principalID.ToString() });

            return data;
        }

        public RoleMembershipData RetrieveRoleMember(UUID groupID, UUID roleID, string principalID)
        {
            RoleMembershipData[] data = _RoleMembership.Get(new string[] { "GroupID", "RoleID", "PrincipalID" },
                                                             new string[] { groupID.ToString(), roleID.ToString(), principalID.ToString() });

            if (data != null && data.Length > 0)
                return data[0];

            return null;
        }

        public int RoleMemberCount(UUID groupID, UUID roleID)
        {
            return (int)_RoleMembership.GetCount(new string[] { "GroupID", "RoleID" },
                                                  new string[] { groupID.ToString(), roleID.ToString() });
        }

        public bool StoreRoleMember(RoleMembershipData data)
        {
            return _RoleMembership.Store(data);
        }

        public bool DeleteRoleMember(RoleMembershipData data)
        {
            return _RoleMembership.Delete(new string[] { "GroupID", "RoleID", "PrincipalID"},
                                           new string[] { data.GroupID.ToString(), data.RoleID.ToString(), data.PrincipalID });
        }

        public bool DeleteMemberAllRoles(UUID groupID, string principalID)
        {
            return _RoleMembership.Delete(new string[] { "GroupID", "PrincipalID" },
                                           new string[] { groupID.ToString(), principalID });
        }

        #endregion

        #region principals table
        public bool StorePrincipal(PrincipalData data)
        {
            return _Principals.Store(data);
        }

        public PrincipalData RetrievePrincipal(string principalID)
        {
            PrincipalData[] p = _Principals.Get("PrincipalID", principalID);
            if (p != null && p.Length > 0)
                return p[0];

            return null;
        }

        public bool DeletePrincipal(string principalID)
        {
            return _Principals.Delete("PrincipalID", principalID);
        }
        #endregion

        #region invites table

        public bool StoreInvitation(InvitationData data)
        {
            return _Invites.Store(data);
        }

        public InvitationData RetrieveInvitation(UUID inviteID)
        {
            InvitationData[] invites = _Invites.Get("InviteID", inviteID.ToString());

            if (invites != null && invites.Length > 0)
                return invites[0];

            return null;
        }

        public InvitationData RetrieveInvitation(UUID groupID, string principalID)
        {
            InvitationData[] invites = _Invites.Get(new string[] { "GroupID", "PrincipalID" },
                                                     new string[] { groupID.ToString(), principalID });

            if (invites != null && invites.Length > 0)
                return invites[0];

            return null;
        }

        public bool DeleteInvite(UUID inviteID)
        {
            return _Invites.Delete("InviteID", inviteID.ToString());
        }

        public void DeleteOldInvites()
        {
            _Invites.DeleteOld();
        }

        #endregion

        #region notices table

        public bool StoreNotice(NoticeData data)
        {
            return _Notices.Store(data);
        }

        public NoticeData RetrieveNotice(UUID noticeID)
        {
            NoticeData[] notices = _Notices.Get("NoticeID", noticeID.ToString());

            if (notices != null && notices.Length > 0)
                return notices[0];

            return null;
        }

        public NoticeData[] RetrieveNotices(UUID groupID)
        {
            NoticeData[] notices = _Notices.Get("GroupID", groupID.ToString());

            return notices;
        }

        public bool DeleteNotice(UUID noticeID)
        {
            return _Notices.Delete("NoticeID", noticeID.ToString());
        }

        public void DeleteOldNotices()
        {
            _Notices.DeleteOld();
        }

        #endregion

        #region combinations
        public MembershipData RetrievePrincipalGroupMembership(string principalID, UUID groupID)
        {
            // TODO
            return null;
        }
        public MembershipData[] RetrievePrincipalGroupMemberships(string principalID)
        {
            // TODO
            return null;
        }

        #endregion
    }

    public class MySqlGroupsGroupsHandler : MySQLGenericTableHandler<GroupData>
    {
        protected override Assembly Assembly =>
            // WARNING! Moving migrations to this assembly!!!
            GetType().Assembly;

        public MySqlGroupsGroupsHandler(string connectionString, string realm, string store)
            : base(connectionString, realm, store)
        {
        }

    }

    public class MySqlGroupsMembershipHandler : MySQLGenericTableHandler<MembershipData>
    {
        protected override Assembly Assembly =>
            // WARNING! Moving migrations to this assembly!!!
            GetType().Assembly;

        public MySqlGroupsMembershipHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }

    }

    public class MySqlGroupsRolesHandler : MySQLGenericTableHandler<RoleData>
    {
        protected override Assembly Assembly =>
            // WARNING! Moving migrations to this assembly!!!
            GetType().Assembly;

        public MySqlGroupsRolesHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }

    }

    public class MySqlGroupsRoleMembershipHandler : MySQLGenericTableHandler<RoleMembershipData>
    {
        protected override Assembly Assembly =>
            // WARNING! Moving migrations to this assembly!!!
            GetType().Assembly;

        public MySqlGroupsRoleMembershipHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }

    }

    public class MySqlGroupsInvitesHandler : MySQLGenericTableHandler<InvitationData>
    {
        protected override Assembly Assembly =>
            // WARNING! Moving migrations to this assembly!!!
            GetType().Assembly;

        public MySqlGroupsInvitesHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }

        public void DeleteOld()
        {
            uint now = (uint)Util.UnixTimeSinceEpoch();

            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = string.Format("delete from {0} where TMStamp < NOW() - INTERVAL 2 WEEK", _Realm);

                ExecuteNonQuery(cmd);
            }

        }
    }

    public class MySqlGroupsNoticesHandler : MySQLGenericTableHandler<NoticeData>
    {
        protected override Assembly Assembly =>
            // WARNING! Moving migrations to this assembly!!!
            GetType().Assembly;

        public MySqlGroupsNoticesHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }

        public void DeleteOld()
        {
            uint now = (uint)Util.UnixTimeSinceEpoch();

            using (MySqlCommand cmd = new MySqlCommand())
            {
                cmd.CommandText = string.Format("delete from {0} where TMStamp < ?tstamp", _Realm);
                cmd.Parameters.AddWithValue("?tstamp", now - 14 * 24 * 60 * 60); // > 2 weeks old

                ExecuteNonQuery(cmd);
            }

        }
    }

    public class MySqlGroupsPrincipalsHandler : MySQLGenericTableHandler<PrincipalData>
    {
        protected override Assembly Assembly =>
            // WARNING! Moving migrations to this assembly!!!
            GetType().Assembly;

        public MySqlGroupsPrincipalsHandler(string connectionString, string realm)
            : base(connectionString, realm, string.Empty)
        {
        }
    }
}
