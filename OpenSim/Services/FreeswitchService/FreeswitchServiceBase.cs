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
using Nini.Config;
using OpenSim.Services.Base;
using log4net;

namespace OpenSim.Services.FreeswitchService
{
    public class FreeswitchServiceBase : ServiceBase
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected string _freeSwitchRealm;
        protected string _freeSwitchSIPProxy;
        protected bool _freeSwitchAttemptUseSTUN = false;
        protected string _freeSwitchEchoServer;
        protected int _freeSwitchEchoPort = 50505;
        protected string _freeSwitchDefaultWellKnownIP;
        protected int _freeSwitchDefaultTimeout = 5000;
        protected string _freeSwitchContext = "default";
        protected string _freeSwitchServerUser = "freeswitch";
        protected string _freeSwitchServerPass = "password";
        protected readonly string _freeSwitchAPIPrefix = "/fsapi";

        protected bool _Enabled = false;

        public FreeswitchServiceBase(IConfigSource config) : base(config)
        {
            //
            // Try reading the [FreeswitchService] section first, if it exists
            //
            IConfig freeswitchConfig = config.Configs["FreeswitchService"];
            if (freeswitchConfig != null)
            {
                _freeSwitchDefaultWellKnownIP = freeswitchConfig.GetString("ServerAddress", string.Empty);
                if (string.IsNullOrEmpty(_freeSwitchDefaultWellKnownIP))
                {
                    _log.Error("[FREESWITCH]: No ServerAddress given, cannot start service.");
                    return;
                }

                _freeSwitchRealm = freeswitchConfig.GetString("Realm", _freeSwitchDefaultWellKnownIP);
                _freeSwitchSIPProxy = freeswitchConfig.GetString("SIPProxy", _freeSwitchDefaultWellKnownIP + ":5060");
                _freeSwitchEchoServer = freeswitchConfig.GetString("EchoServer", _freeSwitchDefaultWellKnownIP);
                _freeSwitchEchoPort = freeswitchConfig.GetInt("EchoPort", _freeSwitchEchoPort);
                _freeSwitchAttemptUseSTUN = freeswitchConfig.GetBoolean("AttemptSTUN", false); // This may not work
                _freeSwitchDefaultTimeout = freeswitchConfig.GetInt("DefaultTimeout", _freeSwitchDefaultTimeout);
                _freeSwitchContext = freeswitchConfig.GetString("Context", _freeSwitchContext);
                _freeSwitchServerUser = freeswitchConfig.GetString("UserName", _freeSwitchServerUser);
                _freeSwitchServerPass = freeswitchConfig.GetString("Password", _freeSwitchServerPass);

                _Enabled = true;
            }
        }
    }
}
