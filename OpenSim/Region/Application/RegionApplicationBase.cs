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

using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim
{
    public abstract class RegionApplicationBase : BaseOpenSimServer
    {
        private static readonly ILog _log
            = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Dictionary<EndPoint, uint> _clientCircuits = new Dictionary<EndPoint, uint>();
        protected NetworkServersInfo _networkServersInfo;
        protected uint _httpServerPort;
        protected bool _httpServerSSL;
        protected ISimulationDataService _simulationDataService;
        protected IEstateDataService _estateDataService;

        public SceneManager SceneManager { get; protected set; }
        public NetworkServersInfo NetServersInfo => _networkServersInfo;
        public ISimulationDataService SimulationDataService => _simulationDataService;
        public IEstateDataService EstateDataService => _estateDataService;

        protected abstract void Initialize();

        protected abstract Scene CreateScene(RegionInfo regionInfo, ISimulationDataService simDataService, IEstateDataService estateDataService, AgentCircuitManager circuitManager);

        protected override void StartupSpecific()
        {
            SceneManager = SceneManager.Instance;

            Initialize();

            uint mainport = _networkServersInfo.HttpListenerPort;
            uint mainSSLport = _networkServersInfo.httpSSLPort;

            if (_networkServersInfo.HttpUsesSSL && mainport == mainSSLport)
            {
                _log.Error("[REGION SERVER]: HTTP Server config failed.   HTTP Server and HTTPS server must be on different ports");
            }

            if(_networkServersInfo.HttpUsesSSL)
            {
                _httpServer = new BaseHttpServer(
                        mainSSLport, _networkServersInfo.HttpUsesSSL,
                        _networkServersInfo.HttpSSLCN,
                        _networkServersInfo.HttpSSLCertPath, _networkServersInfo.HttpSSLCNCertPass);
                _httpServer.Start();
                MainServer.AddHttpServer(_httpServer);
            }

            // unsecure main server
            BaseHttpServer server = new BaseHttpServer(mainport);
            if(!_networkServersInfo.HttpUsesSSL)
            {
                _httpServer = server;
                server.Start();
            }
            else
                server.Start();

            MainServer.AddHttpServer(server);
            MainServer.UnSecureInstance = server;

            MainServer.Instance = _httpServer;

            // "OOB" Server
            if (_networkServersInfo.ssl_listener)
            {
                if (!_networkServersInfo.ssl_external)
                {
                    server = new BaseHttpServer(
                        _networkServersInfo.https_port, _networkServersInfo.ssl_listener,
                        _networkServersInfo.cert_path,
                        _networkServersInfo.cert_pass);

                    _log.InfoFormat("[REGION SERVER]: Starting OOB HTTPS server on port {0}", server.SSLPort);
                    server.Start();
                    MainServer.AddHttpServer(server);
                }
                else
                {
                    server = new BaseHttpServer(_networkServersInfo.https_port);

                    _log.InfoFormat("[REGION SERVER]: Starting HTTP server on port {0} for external HTTPS", server.Port);
                    server.Start();
                    MainServer.AddHttpServer(server);
                }
            }

            base.StartupSpecific();
        }

    }
}
