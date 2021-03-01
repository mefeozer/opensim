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

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors;

using OpenMetaverse;
using log4net;
using Mono.Addins;
using Nini.Config;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.GridUser
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RemoteGridUserServicesConnector")]
    public class RemoteGridUserServicesConnector : ISharedRegionModule, IGridUserService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private const int KEEPTIME = 30; // 30 secs
        private readonly ExpiringCacheOS<string, GridUserInfo> _Infos = new ExpiringCacheOS<string, GridUserInfo>(10000);

        ~RemoteGridUserServicesConnector()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private bool disposed = false;
        private void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                _Infos.Dispose();
            }
        }


        #region ISharedRegionModule

        private bool _Enabled = false;

        private ActivityDetector _ActivityDetector;
        private IGridUserService _RemoteConnector;

        public Type ReplaceableInterface => null;

        public string Name => "RemoteGridUserServicesConnector";

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("GridUserServices", "");
                if (name == Name)
                {
                    _RemoteConnector = new GridUserServicesConnector(source);

                    _Enabled = true;

                    _ActivityDetector = new ActivityDetector(this);

                    _log.Info("[REMOTE GRID USER CONNECTOR]: Remote grid user enabled");
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

            scene.RegisterModuleInterface<IGridUserService>(this);
            _ActivityDetector.AddRegion(scene);

            _log.InfoFormat("[REMOTE GRID USER CONNECTOR]: Enabled remote grid user for region {0}", scene.RegionInfo.RegionName);

        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _ActivityDetector.RemoveRegion(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

        }

        #endregion

        #region IGridUserService

        public GridUserInfo LoggedIn(string userID)
        {
            _log.Warn("[REMOTE GRID USER CONNECTOR]: LoggedIn not implemented at the simulators");
            return null;
        }

        public bool LoggedOut(string userID, UUID sessionID, UUID region, Vector3 position, Vector3 lookat)
        {
            _Infos.Remove(userID);
            return _RemoteConnector.LoggedOut(userID, sessionID, region, position, lookat);
        }

        public bool SetHome(string userID, UUID regionID, Vector3 position, Vector3 lookAt)
        {
            if (_RemoteConnector.SetHome(userID, regionID, position, lookAt))
            {
                if (_Infos.TryGetValue(userID, KEEPTIME * 1000, out GridUserInfo info))
                {
                    info.HomeRegionID = regionID;
                    info.HomePosition = position;
                    info.HomeLookAt = lookAt;
                }
                return true;
            }
            return false;
        }

        public bool SetLastPosition(string userID, UUID sessionID, UUID regionID, Vector3 position, Vector3 lookAt)
        {
            if (_RemoteConnector.SetLastPosition(userID, sessionID, regionID, position, lookAt))
            {
                if (_Infos.TryGetValue(userID, KEEPTIME * 1000, out GridUserInfo info))
                {
                    info.LastRegionID = regionID;
                    info.LastPosition = position;
                    info.LastLookAt = lookAt;
                }
                return true;
            }

            return false;
        }

        public GridUserInfo GetGridUserInfo(string userID)
        {
            if (_Infos.TryGetValue(userID, KEEPTIME * 1000, out GridUserInfo info))
                return info;

            info = _RemoteConnector.GetGridUserInfo(userID);
            _Infos.AddOrUpdate(userID, info, KEEPTIME);

            return info;
        }

        public GridUserInfo[] GetGridUserInfo(string[] userID)
        {
            return _RemoteConnector.GetGridUserInfo(userID);
        }

        #endregion

    }
}
