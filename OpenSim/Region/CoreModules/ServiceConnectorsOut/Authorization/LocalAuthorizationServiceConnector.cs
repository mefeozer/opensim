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

using log4net;
using Mono.Addins;
using Nini.Config;
using System;
using System.Reflection;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Authorization
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalAuthorizationServicesConnector")]
    public class LocalAuthorizationServicesConnector : INonSharedRegionModule, IAuthorizationService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private IAuthorizationService _AuthorizationService;
        private Scene _Scene;
        private IConfig _AuthorizationConfig;

        private bool _Enabled = false;

        public Type ReplaceableInterface => null;

        public string Name => "LocalAuthorizationServicesConnector";

        public void Initialise(IConfigSource source)
        {
            _log.Info("[AUTHORIZATION CONNECTOR]: Initialise");

            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("AuthorizationServices", string.Empty);
                if (name == Name)
                {
                    _Enabled = true;
                    _AuthorizationConfig = source.Configs["AuthorizationService"];
                    _log.Info("[AUTHORIZATION CONNECTOR]: Local authorization connector enabled");
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            scene.RegisterModuleInterface<IAuthorizationService>(this);
            _Scene = scene;
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            _AuthorizationService = new AuthorizationService(_AuthorizationConfig, _Scene);

            _log.InfoFormat(
                "[AUTHORIZATION CONNECTOR]: Enabled local authorization for region {0}",
                scene.RegionInfo.RegionName);
        }

        public bool IsAuthorizedForRegion(
            string userID, string firstName, string lastName, string regionID, out string message)
        {
            message = "";
            if (!_Enabled)
                return true;

            return _AuthorizationService.IsAuthorizedForRegion(userID, firstName, lastName, regionID, out message);
        }
    }
}