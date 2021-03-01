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
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using log4net;
using Nini.Config;
using System.Reflection;
using OpenSim.Data;
using OpenSim.Framework;

namespace OpenSim.Services.AuthenticationService
{
    // Generic Authentication service used for identifying
    // and authenticating principals.
    // Principals may be clients acting on users' behalf,
    // or any other components that need
    // verifiable identification.
    //
    public class PasswordAuthenticationService :
            AuthenticationServiceBase, IAuthenticationService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        public PasswordAuthenticationService(IConfigSource config, IUserAccountService userService) :
                base(config, userService)
        {
            _log.Debug("[AUTH SERVICE]: Started with User Account access");
        }

        public PasswordAuthenticationService(IConfigSource config) :
                base(config)
        {
        }

        public string Authenticate(UUID principalID, string password, int lifetime)
        {
            UUID realID;
            return Authenticate(principalID, password, lifetime, out realID);
        }

        public string Authenticate(UUID principalID, string password, int lifetime, out UUID realID)
        {
            realID = UUID.Zero;

            _log.DebugFormat("[AUTH SERVICE]: Authenticating for {0}, user account service present: {1}", principalID, _UserAccountService != null);
            AuthenticationData data = _Database.Get(principalID);
            UserAccount user = null;
            if (_UserAccountService != null)
                user = _UserAccountService.GetUserAccount(UUID.Zero, principalID);

            if (data == null || data.Data == null)
            {
                _log.DebugFormat("[AUTH SERVICE]: PrincipalID {0} or its data not found", principalID);
                return string.Empty;
            }

            if (!data.Data.ContainsKey("passwordHash") ||
                !data.Data.ContainsKey("passwordSalt"))
            {
                return string.Empty;
            }

            string hashed = Util.Md5Hash(password + ":" +
                    data.Data["passwordSalt"].ToString());

//            _log.DebugFormat("[PASS AUTH]: got {0}; hashed = {1}; stored = {2}", password, hashed, data.Data["passwordHash"].ToString());

            if (data.Data["passwordHash"].ToString() == hashed)
            {
                return GetToken(principalID, lifetime);
            }

            if (user == null)
            {
                _log.DebugFormat("[PASS AUTH]: No user record for {0}", principalID);
                return string.Empty;
            }

            int impersonateFlag = 1 << 6;

            if ((user.UserFlags & impersonateFlag) == 0)
                return string.Empty;

            _log.DebugFormat("[PASS AUTH]: Attempting impersonation");

            List<UserAccount> accounts = _UserAccountService.GetUserAccountsWhere(UUID.Zero, "UserLevel >= 200");
            if (accounts == null || accounts.Count == 0)
            {
                _log.DebugFormat("[PASS AUTH]: No suitable gods found");
                return string.Empty;
            }

            foreach (UserAccount a in accounts)
            {
                data = _Database.Get(a.PrincipalID);
                if (data == null || data.Data == null ||
                    !data.Data.ContainsKey("passwordHash") ||
                    !data.Data.ContainsKey("passwordSalt"))
                {
                    _log.DebugFormat("[PASS AUTH]: {0} {1} has no suitable password set", a.FirstName, a.LastName);
                    continue;
                }

//                _log.DebugFormat("[PASS AUTH]: Trying {0}", data.PrincipalID);

                hashed = Util.Md5Hash(password + ":" +
                        data.Data["passwordSalt"].ToString());

                if (data.Data["passwordHash"].ToString() == hashed)
                {
                    _log.DebugFormat("[PASS AUTH]: {0} {1} impersonating {2}, proceeding with login", a.FirstName, a.LastName, principalID);
                    realID = a.PrincipalID;
                    return GetToken(principalID, lifetime);
                }
                else
                {
                    _log.DebugFormat(
                        "[AUTH SERVICE]: Salted hash {0} of given password did not match salted hash of {1} for PrincipalID {2}.  Authentication failure.",
                        hashed, data.Data["passwordHash"], data.PrincipalID);
                }
            }

            _log.DebugFormat("[PASS AUTH]: Impersonation of {0} failed", principalID);
            return string.Empty;
        }
    }
}
