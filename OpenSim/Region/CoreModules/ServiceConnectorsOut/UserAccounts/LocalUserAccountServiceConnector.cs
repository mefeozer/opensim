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
using Mono.Addins;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAccounts
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalUserAccountServicesConnector")]
    public class LocalUserAccountServicesConnector : ISharedRegionModule, IUserAccountService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// This is not on the IUserAccountService.  It's only being used so that standalone scenes can punch through
        /// to a local UserAccountService when setting up an estate manager.
        /// </summary>
        public IUserAccountService UserAccountService { get; private set; }

        private UserAccountCache _Cache;

        private bool _Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface => null;

        public string Name => "LocalUserAccountServicesConnector";

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("UserAccountServices", "");
                if (name == Name)
                {
                    IConfig userConfig = source.Configs["UserAccountService"];
                    if (userConfig == null)
                    {
                        _log.Error("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: UserAccountService missing from OpenSim.ini");
                        return;
                    }

                    string serviceDll = userConfig.GetString("LocalServiceModule", string.Empty);

                    if (string.IsNullOrEmpty(serviceDll))
                    {
                        _log.Error("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: No LocalServiceModule named in section UserService");
                        return;
                    }

                    object[] args = new object[] { source };
                    UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(serviceDll, args);

                    if (UserAccountService == null)
                    {
                        _log.ErrorFormat(
                            "[LOCAL USER ACCOUNT SERVICE CONNECTOR]: Cannot load user account service specified as {0}", serviceDll);
                        return;
                    }
                    _Enabled = true;
                    _Cache = new UserAccountCache();

                    _log.Info("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: Local user connector enabled");
                }
            }
        }

        public void PostInitialise()
        {
            if (!_Enabled)
                return;
        }

        public void Close()
        {
            if (!_Enabled)
                return;
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            // FIXME: Why do we bother setting this module and caching up if we just end up registering the inner
            // user account service?!
            scene.RegisterModuleInterface<IUserAccountService>(UserAccountService);
            scene.RegisterModuleInterface<IUserAccountCacheModule>(_Cache);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            _log.InfoFormat("[LOCAL USER ACCOUNT SERVICE CONNECTOR]: Enabled local user accounts for region {0}", scene.RegionInfo.RegionName);
        }

        #endregion

        #region IUserAccountService

        public UserAccount GetUserAccount(UUID scopeID, UUID userID)
        {
            bool inCache = false;
            UserAccount account;
            account = _Cache.Get(userID, out inCache);
            if (inCache)
                return account;

            account = UserAccountService.GetUserAccount(scopeID, userID);
            _Cache.Cache(userID, account);

            return account;
        }

        public UserAccount GetUserAccount(UUID scopeID, string firstName, string lastName)
        {
            bool inCache = false;
            UserAccount account;
            account = _Cache.Get(firstName + " " + lastName, out inCache);
            if (inCache)
                return account;

            account = UserAccountService.GetUserAccount(scopeID, firstName, lastName);
            if (account != null)
                _Cache.Cache(account.PrincipalID, account);

            return account;
        }

        public UserAccount GetUserAccount(UUID scopeID, string Email)
        {
            return UserAccountService.GetUserAccount(scopeID, Email);
        }

        public List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs)
        {
            List<UserAccount> ret = new List<UserAccount>();
            List<string> missing = new List<string>();

            // still another cache..
            bool inCache = false;
            UUID uuid = UUID.Zero;
            UserAccount account;
            foreach(string id in IDs)
            {
                if(UUID.TryParse(id, out uuid))
                {
                    account = _Cache.Get(uuid, out inCache);
                    if (inCache)
                        ret.Add(account);
                    else
                        missing.Add(id);
                }
            }

            if(missing.Count == 0)
                return ret;

            List<UserAccount> ext = UserAccountService.GetUserAccounts(scopeID, missing);
            if(ext != null && ext.Count > 0)
            {
                foreach(UserAccount acc in ext)
                {
                    if(acc != null)
                    {
                        ret.Add(acc);
                        _Cache.Cache(acc.PrincipalID, acc);
                    }
                }
            }
            return ret;
        }

        public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string query)
        {
            return null;
        }

        public List<UserAccount> GetUserAccounts(UUID scopeID, string query)
        {
            return UserAccountService.GetUserAccounts(scopeID, query);
        }

        // Update all updatable fields
        //
        public bool StoreUserAccount(UserAccount data)
        {
            bool ret = UserAccountService.StoreUserAccount(data);
            if (ret)
                _Cache.Cache(data.PrincipalID, data);
            return ret;
        }

        public void InvalidateCache(UUID userID)
        {
            _Cache.Invalidate(userID);
        }

        #endregion
    }
}
