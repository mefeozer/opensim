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
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Hypergrid;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.ServiceConnectorsIn.Hypergrid
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HypergridServiceInConnectorModule")]
    public class HypergridServiceInConnectorModule : ISharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static bool _Enabled = false;

        private IConfigSource _Config;
        private bool _Registered = false;
        private string _LocalServiceDll = string.Empty;
        private GatekeeperServiceInConnector _HypergridHandler;
        private UserAgentServerConnector _UASHandler;

        #region Region Module interface

        public void Initialise(IConfigSource config)
        {
            _Config = config;
            IConfig moduleConfig = config.Configs["Modules"];
            if (moduleConfig != null)
            {
                _Enabled = moduleConfig.GetBoolean("HypergridServiceInConnector", false);
                if (_Enabled)
                {
                    _log.Info("[HGGRID IN CONNECTOR]: Hypergrid Service In Connector enabled");
                    IConfig fconfig = config.Configs["FriendsService"];
                    if (fconfig != null)
                    {
                        _LocalServiceDll = fconfig.GetString("LocalServiceModule", _LocalServiceDll);
                        if (string.IsNullOrEmpty(_LocalServiceDll))
                            _log.WarnFormat("[HGGRID IN CONNECTOR]: Friends LocalServiceModule config missing");
                    }
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public Type ReplaceableInterface => null;

        public string Name => "HypergridService";

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;
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

            if (!_Registered)
            {
                _Registered = true;

                _log.Info("[HypergridService]: Starting...");

                ISimulationService simService = scene.RequestModuleInterface<ISimulationService>();
                IFriendsSimConnector friendsConn = scene.RequestModuleInterface<IFriendsSimConnector>();
                object[] args = new object[] { _Config };
//                IFriendsService friendsService = ServerUtils.LoadPlugin<IFriendsService>(_LocalServiceDll, args)
                ServerUtils.LoadPlugin<IFriendsService>(_LocalServiceDll, args);

                _HypergridHandler = new GatekeeperServiceInConnector(_Config, MainServer.Instance, simService);

                _UASHandler = new UserAgentServerConnector(_Config, MainServer.Instance, friendsConn);

                new HeloServiceInConnector(_Config, MainServer.Instance, "HeloService");

                new HGFriendsServerConnector(_Config, MainServer.Instance, "HGFriendsService", friendsConn);
            }
            scene.RegisterModuleInterface<IGatekeeperService>(_HypergridHandler.GateKeeper);
            scene.RegisterModuleInterface<IUserAgentService>(_UASHandler.HomeUsersService);
        }

        #endregion

    }
}
