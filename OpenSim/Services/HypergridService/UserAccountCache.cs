using System.Collections.Generic;
using OpenMetaverse;

using OpenSim.Services.Interfaces;

namespace OpenSim.Services.HypergridService
{
    public class UserAccountCache : IUserAccountService
    {
        private const double CACHE_EXPIRATION_SECONDS = 120000.0; // 33 hours!

//        private static readonly ILog _log =
//                LogManager.GetLogger(
//                MethodBase.GetCurrentMethod().DeclaringType);

        private readonly ExpiringCache<UUID, UserAccount> _UUIDCache;

        private readonly IUserAccountService _UserAccountService;

        private static UserAccountCache _Singleton;

        public static UserAccountCache CreateUserAccountCache(IUserAccountService u)
        {
            if (_Singleton == null)
                _Singleton = new UserAccountCache(u);

            return _Singleton;
        }

        private UserAccountCache(IUserAccountService u)
        {
            _UUIDCache = new ExpiringCache<UUID, UserAccount>();
            _UserAccountService = u;
        }

        public void Cache(UUID userID, UserAccount account)
        {
            // Cache even null accounts
            _UUIDCache.AddOrUpdate(userID, account, CACHE_EXPIRATION_SECONDS);

            //_log.DebugFormat("[USER CACHE]: cached user {0}", userID);
        }

        public UserAccount Get(UUID userID, out bool inCache)
        {
            UserAccount account = null;
            inCache = false;
            if (_UUIDCache.TryGetValue(userID, out account))
            {
                //_log.DebugFormat("[USER CACHE]: Account {0} {1} found in cache", account.FirstName, account.LastName);
                inCache = true;
                return account;
            }

            return null;
        }

        public UserAccount GetUser(string id)
        {
            UUID uuid = UUID.Zero;
            UUID.TryParse(id, out uuid);
            bool inCache = false;
            UserAccount account = Get(uuid, out inCache);
            if (!inCache)
            {
                account = _UserAccountService.GetUserAccount(UUID.Zero, uuid);
                Cache(uuid, account);
            }

            return account;
        }

        #region IUserAccountService
        public UserAccount GetUserAccount(UUID scopeID, UUID userID)
        {
            return GetUser(userID.ToString());
        }

        public UserAccount GetUserAccount(UUID scopeID, string FirstName, string LastName)
        {
            return null;
        }

        public UserAccount GetUserAccount(UUID scopeID, string Email)
        {
            return null;
        }

        public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string query)
        {
            return null;
        }

        public List<UserAccount> GetUserAccounts(UUID scopeID, string query)
        {
            return null;
        }

        public List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs)
        {
            return null;
        }

        public void InvalidateCache(UUID userID)
        {
            _UUIDCache.Remove(userID);
        }

        public bool StoreUserAccount(UserAccount data)
        {
            return false;
        }
        #endregion

    }

}
