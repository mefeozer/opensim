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
using Nini.Config;
using log4net;
using Mono.Addins;
using System.Reflection;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors;
using OpenSim.Framework;

using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.UserAccounts
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RemoteUserAccountServicesConnector")]
    public class RemoteUserAccountServicesConnector : UserAccountServicesConnector,
            ISharedRegionModule, IUserAccountService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled = false;
        private UserAccountCache _Cache;

        public Type ReplaceableInterface => null;

        public string Name => "RemoteUserAccountServicesConnector";

        public override void Initialise(IConfigSource source)
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
                        _log.Error("[USER CONNECTOR]: UserAccountService missing from OpenSim.ini");
                        return;
                    }

                    _Enabled = true;

                    base.Initialise(source);
                    _Cache = new UserAccountCache();

                    _log.Info("[USER CONNECTOR]: Remote users enabled");
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

            scene.RegisterModuleInterface<IUserAccountService>(this);
            scene.RegisterModuleInterface<IUserAccountCacheModule>(_Cache);

            scene.EventManager.OnNewClient += OnNewClient;
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
        }

        // When a user actually enters the sim, clear them from
        // cache so the sim will have the current values for
        // flags, title, etc. And country, don't forget country!
        private void OnNewClient(IClientAPI client)
        {
            _Cache.Remove(client.Name);
        }

        #region Overwritten methods from IUserAccountService

        public override UserAccount GetUserAccount(UUID scopeID, UUID userID)
        {
            bool inCache = false;
            UserAccount account;
            account = _Cache.Get(userID, out inCache);
            if (inCache)
                return account;

            account = base.GetUserAccount(scopeID, userID);
            _Cache.Cache(userID, account);

            return account;
        }

        public override UserAccount GetUserAccount(UUID scopeID, string firstName, string lastName)
        {
            bool inCache = false;
            UserAccount account;
            account = _Cache.Get(firstName + " " + lastName, out inCache);
            if (inCache)
                return account;

            account = base.GetUserAccount(scopeID, firstName, lastName);
            if (account != null)
                _Cache.Cache(account.PrincipalID, account);

            return account;
        }

        public override List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs)
        {
            List<UserAccount> accs = new List<UserAccount>();
            List<string> missing = new List<string>();

            UUID uuid = UUID.Zero;
            UserAccount account;
            bool inCache = false;

            foreach(string id in IDs)
            {
                if(UUID.TryParse(id, out uuid))
                {
                    account = _Cache.Get(uuid, out inCache);
                    if (inCache)
                        accs.Add(account);
                    else
                        missing.Add(id);
                }
            }

            if(missing.Count > 0)
            {
                List<UserAccount> ext = base.GetUserAccounts(scopeID, missing);
                if(ext != null && ext.Count >0 )
                {
                    foreach(UserAccount acc in ext)
                    {
                        if(acc != null)
                        {
                            accs.Add(acc);
                            _Cache.Cache(acc.PrincipalID, acc);
                        }
                    }
                }
            }
            return accs;
        }

        public override bool StoreUserAccount(UserAccount data)
        {
            // This remote connector refuses to serve this method
            return false;
        }

        #endregion
    }
}
