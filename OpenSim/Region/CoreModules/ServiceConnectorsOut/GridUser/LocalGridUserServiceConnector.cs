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
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.GridUser
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalGridUserServicesConnector")]
    public class LocalGridUserServicesConnector : ISharedRegionModule, IGridUserService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IGridUserService _GridUserService;

        private ActivityDetector _ActivityDetector;

        private bool _Enabled = false;

        #region ISharedRegionModule

        public Type ReplaceableInterface => null;

        public string Name => "LocalGridUserServicesConnector";

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("GridUserServices", "");
                if (name == Name)
                {
                    IConfig userConfig = source.Configs["GridUserService"];
                    if (userConfig == null)
                    {
                        _log.Error("[LOCAL GRID USER SERVICE CONNECTOR]: GridUserService missing from OpenSim.ini");
                        return;
                    }

                    string serviceDll = userConfig.GetString("LocalServiceModule", string.Empty);

                    if (string.IsNullOrEmpty(serviceDll))
                    {
                        _log.Error("[LOCAL GRID USER SERVICE CONNECTOR]: No LocalServiceModule named in section GridUserService");
                        return;
                    }

                    object[] args = new object[] { source };
                    _GridUserService = ServerUtils.LoadPlugin<IGridUserService>(serviceDll, args);

                    if (_GridUserService == null)
                    {
                        _log.ErrorFormat(
                            "[LOCAL GRID USER SERVICE CONNECTOR]: Cannot load user account service specified as {0}", serviceDll);
                        return;
                    }

                    _ActivityDetector = new ActivityDetector(this);

                    _Enabled = true;

                    _log.Info("[LOCAL GRID USER SERVICE CONNECTOR]: Local grid user connector enabled");
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

            scene.RegisterModuleInterface<IGridUserService>(_GridUserService);
            _ActivityDetector.AddRegion(scene);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            scene.UnregisterModuleInterface<IGridUserService>(this);
            _ActivityDetector.RemoveRegion(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            _log.InfoFormat("[LOCAL GRID USER SERVICE CONNECTOR]: Enabled local grid user for region {0}", scene.RegionInfo.RegionName);
        }

        #endregion

        #region IGridUserService

        public GridUserInfo LoggedIn(string userID)
        {
            return _GridUserService.LoggedIn(userID);
        }

        public bool LoggedOut(string userID, UUID sessionID, UUID regionID, Vector3 lastPosition, Vector3 lastLookAt)
        {
            return _GridUserService.LoggedOut(userID, sessionID, regionID, lastPosition, lastLookAt);
        }

        public bool SetHome(string userID, UUID homeID, Vector3 homePosition, Vector3 homeLookAt)
        {
            return _GridUserService.SetHome(userID, homeID, homePosition, homeLookAt);
        }

        public bool SetLastPosition(string userID, UUID sessionID, UUID regionID, Vector3 lastPosition, Vector3 lastLookAt)
        {
            return _GridUserService.SetLastPosition(userID, sessionID, regionID, lastPosition, lastLookAt);
        }

        public GridUserInfo GetGridUserInfo(string userID)
        {
            return _GridUserService.GetGridUserInfo(userID);
        }
        public GridUserInfo[] GetGridUserInfo(string[] userID)
        {
            return _GridUserService.GetGridUserInfo(userID);
        }

        #endregion

    }
}
