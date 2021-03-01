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

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.MuteList
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RemoteMuteListServicesConnector")]
    public class RemoteMuteListServicesConnector : ISharedRegionModule, IMuteListService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        #region ISharedRegionModule

        private bool _Enabled = false;

        private IMuteListService _remoteConnector;

        public Type ReplaceableInterface => null;

        public string Name => "RemoteMuteListServicesConnector";

        public void Initialise(IConfigSource source)
        {
           // only active for core mute lists module
            IConfig moduleConfig = source.Configs["Messaging"];
            if (moduleConfig == null)
                return;

            if (moduleConfig.GetString("MuteListModule", "None") != "MuteListModule")
                return;
            
            moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("MuteListService", "");
                if (name == Name)
                {
                    _remoteConnector = new MuteListServicesConnector(source);
                    _Enabled = true;
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

            scene.RegisterModuleInterface<IMuteListService>(this);
            _log.InfoFormat("[MUTELIST CONNECTOR]: Enabled for region {0}", scene.RegionInfo.RegionName);
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

        #endregion

        #region IMuteListService
        public byte[] MuteListRequest(UUID agentID, uint crc)
        {
            if (!_Enabled)
                return null;
            return _remoteConnector.MuteListRequest(agentID, crc);
        }

        public bool UpdateMute(MuteData mute)
        {
            if (!_Enabled)
                return false;
            return _remoteConnector.UpdateMute(mute);
        }

        public bool RemoveMute(UUID agentID, UUID muteID, string muteName)
        {
            if (!_Enabled)
                return false;
            return _remoteConnector.RemoveMute(agentID, muteID, muteName);
        }

        #endregion IMuteListService

    }
}
