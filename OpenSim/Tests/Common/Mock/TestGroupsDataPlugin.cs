using System.Collections.Generic;
using System.Linq;

using OpenMetaverse;
using OpenSim.Data;

namespace OpenSim.Tests.Common.Mock
{
    public class TestGroupsDataPlugin : IGroupsData
    {
        class CompositeKey
        {
            private readonly string _key;
            public string Key => _key;

            public CompositeKey(UUID _k1, string _k2)
            {
                _key = _k1.ToString() + _k2;
            }

            public CompositeKey(UUID _k1, string _k2, string _k3)
            {
                _key = _k1.ToString() + _k2 + _k3;
            }

            public override bool Equals(object obj)
            {
                if (obj is CompositeKey)
                {
                    return Key == ((CompositeKey)obj).Key;
                }
                return false;
            }

            public override int GetHashCode()
            {
                return base.GetHashCode();
            }

            public override string ToString()
            {
                return Key;
            }
        }

        private readonly Dictionary<UUID, GroupData> _Groups;
        private readonly Dictionary<CompositeKey, MembershipData> _Membership;
        private readonly Dictionary<CompositeKey, RoleData> _Roles;
        private readonly Dictionary<CompositeKey, RoleMembershipData> _RoleMembership;
        private Dictionary<UUID, InvitationData> _Invites;
        private Dictionary<UUID, NoticeData> _Notices;
        private readonly Dictionary<string, PrincipalData> _Principals;

        public TestGroupsDataPlugin(string connectionString, string realm)
        {
            _Groups = new Dictionary<UUID, GroupData>();
            _Membership = new Dictionary<CompositeKey, MembershipData>();
            _Roles = new Dictionary<CompositeKey, RoleData>();
            _RoleMembership = new Dictionary<CompositeKey, RoleMembershipData>();
            _Invites = new Dictionary<UUID, InvitationData>();
            _Notices = new Dictionary<UUID, NoticeData>();
            _Principals = new Dictionary<string, PrincipalData>();
        }

        #region groups table
        public bool StoreGroup(GroupData data)
        {
            return false;
        }

        public GroupData RetrieveGroup(UUID groupID)
        {
            if (_Groups.ContainsKey(groupID))
                return _Groups[groupID];

            return null;
        }

        public GroupData RetrieveGroup(string name)
        {
            return _Groups.Values.First(g => g.Data.ContainsKey("Name") && g.Data["Name"] == name);
        }

        public GroupData[] RetrieveGroups(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                pattern = "1";

            IEnumerable<GroupData> groups = _Groups.Values.Where(g => g.Data.ContainsKey("Name") && (g.Data["Name"].StartsWith(pattern) || g.Data["Name"].EndsWith(pattern)));

            return groups != null ? groups.ToArray() : new GroupData[0];
        }

        public bool DeleteGroup(UUID groupID)
        {
            return _Groups.Remove(groupID);
        }

        public int GroupsCount()
        {
            return _Groups.Count;
        }
        #endregion

        #region membership table
        public MembershipData RetrieveMember(UUID groupID, string pricipalID)
        {
            CompositeKey dkey = new CompositeKey(groupID, pricipalID);
            if (_Membership.ContainsKey(dkey))
                return _Membership[dkey];

            return null;
        }

        public MembershipData[] RetrieveMembers(UUID groupID)
        {
            IEnumerable<CompositeKey> keys = _Membership.Keys.Where(k => k.Key.StartsWith(groupID.ToString()));
            return keys.Where(_Membership.ContainsKey).Select(x => _Membership[x]).ToArray();
        }

        public MembershipData[] RetrieveMemberships(string principalID)
        {
            IEnumerable<CompositeKey> keys = _Membership.Keys.Where(k => k.Key.EndsWith(principalID.ToString()));
            return keys.Where(_Membership.ContainsKey).Select(x => _Membership[x]).ToArray();
        }

        public MembershipData[] RetrievePrincipalGroupMemberships(string principalID)
        {
            return RetrieveMemberships(principalID);
        }

        public MembershipData RetrievePrincipalGroupMembership(string principalID, UUID groupID)
        {
            CompositeKey dkey = new CompositeKey(groupID, principalID);
            if (_Membership.ContainsKey(dkey))
                return _Membership[dkey];
            return null;
        }

        public bool StoreMember(MembershipData data)
        {
            CompositeKey dkey = new CompositeKey(data.GroupID, data.PrincipalID);
            _Membership[dkey] = data;
            return true;
        }

        public bool DeleteMember(UUID groupID, string principalID)
        {
            CompositeKey dkey = new CompositeKey(groupID, principalID);
            if (_Membership.ContainsKey(dkey))
                return _Membership.Remove(dkey);

            return false;
        }

        public int MemberCount(UUID groupID)
        {
            return _Membership.Count;
        }
        #endregion

        #region roles table
        public bool StoreRole(RoleData data)
        {
            CompositeKey dkey = new CompositeKey(data.GroupID, data.RoleID.ToString());
            _Roles[dkey] = data;
            return true;
        }

        public RoleData RetrieveRole(UUID groupID, UUID roleID)
        {
            CompositeKey dkey = new CompositeKey(groupID, roleID.ToString());
            if (_Roles.ContainsKey(dkey))
                return _Roles[dkey];

            return null;
        }

        public RoleData[] RetrieveRoles(UUID groupID)
        {
            IEnumerable<CompositeKey> keys = _Roles.Keys.Where(k => k.Key.StartsWith(groupID.ToString()));
            return keys.Where(_Roles.ContainsKey).Select(x => _Roles[x]).ToArray();
        }

        public bool DeleteRole(UUID groupID, UUID roleID)
        {
            CompositeKey dkey = new CompositeKey(groupID, roleID.ToString());
            if (_Roles.ContainsKey(dkey))
                return _Roles.Remove(dkey);

            return false;
        }

        public int RoleCount(UUID groupID)
        {
            return _Roles.Count;
        }
        #endregion

        #region rolememberhip table
        public RoleMembershipData[] RetrieveRolesMembers(UUID groupID)
        {
            IEnumerable<CompositeKey> keys = _Roles.Keys.Where(k => k.Key.StartsWith(groupID.ToString()));
            return keys.Where(_RoleMembership.ContainsKey).Select(x => _RoleMembership[x]).ToArray();
        }

        public RoleMembershipData[] RetrieveRoleMembers(UUID groupID, UUID roleID)
        {
            IEnumerable<CompositeKey> keys = _Roles.Keys.Where(k => k.Key.StartsWith(groupID.ToString() + roleID.ToString()));
            return keys.Where(_RoleMembership.ContainsKey).Select(x => _RoleMembership[x]).ToArray();
        }

        public RoleMembershipData[] RetrieveMemberRoles(UUID groupID, string principalID)
        {
            IEnumerable<CompositeKey> keys = _Roles.Keys.Where(k => k.Key.StartsWith(groupID.ToString()) && k.Key.EndsWith(principalID));
            return keys.Where(_RoleMembership.ContainsKey).Select(x => _RoleMembership[x]).ToArray();
        }

        public RoleMembershipData RetrieveRoleMember(UUID groupID, UUID roleID, string principalID)
        {
            CompositeKey dkey = new CompositeKey(groupID, roleID.ToString(), principalID);
            if (_RoleMembership.ContainsKey(dkey))
                return _RoleMembership[dkey];

            return null;
        }

        public int RoleMemberCount(UUID groupID, UUID roleID)
        {
            return _RoleMembership.Count;
        }

        public bool StoreRoleMember(RoleMembershipData data)
        {
            CompositeKey dkey = new CompositeKey(data.GroupID, data.RoleID.ToString(), data.PrincipalID);
            _RoleMembership[dkey] = data;
            return true;
        }

        public bool DeleteRoleMember(RoleMembershipData data)
        {
            CompositeKey dkey = new CompositeKey(data.GroupID, data.RoleID.ToString(), data.PrincipalID);
            if (_RoleMembership.ContainsKey(dkey))
                return _RoleMembership.Remove(dkey);

            return false;
        }

        public bool DeleteMemberAllRoles(UUID groupID, string principalID)
        {
            List<CompositeKey> keys = _RoleMembership.Keys.Where(k => k.Key.StartsWith(groupID.ToString()) && k.Key.EndsWith(principalID)).ToList();
            foreach (CompositeKey k in keys)
                _RoleMembership.Remove(k);
            return true;
        }
        #endregion

        #region principals table
        public bool StorePrincipal(PrincipalData data)
        {
            _Principals[data.PrincipalID] = data;
            return true;
        }

        public PrincipalData RetrievePrincipal(string principalID)
        {
            if (_Principals.ContainsKey(principalID))
                return _Principals[principalID];

            return null;
        }

        public bool DeletePrincipal(string principalID)
        {
            if (_Principals.ContainsKey(principalID))
                return _Principals.Remove(principalID);
            return false;
        }
        #endregion

        #region invites table
        public bool StoreInvitation(InvitationData data)
        {
            return false;
        }

        public InvitationData RetrieveInvitation(UUID inviteID)
        {
            return null;
        }

        public InvitationData RetrieveInvitation(UUID groupID, string principalID)
        {
            return null;
        }

        public bool DeleteInvite(UUID inviteID)
        {
            return false;
        }

        public void DeleteOldInvites()
        {
        }
        #endregion

        #region notices table
        public bool StoreNotice(NoticeData data)
        {
            return false;
        }

        public NoticeData RetrieveNotice(UUID noticeID)
        {
            return null;
        }

        public NoticeData[] RetrieveNotices(UUID groupID)
        {
            return new NoticeData[0];
        }

        public bool DeleteNotice(UUID noticeID)
        {
            return false;
        }

        public void DeleteOldNotices()
        {
        }
        #endregion

    }
}
