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
using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Presence
{
    public class BasePresenceServiceConnector : IPresenceService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected bool _Enabled;

        protected PresenceDetector _PresenceDetector;

        /// <summary>
        /// Underlying presence service.  Do not use directly.
        /// </summary>
        public IPresenceService _PresenceService;

        public Type ReplaceableInterface => null;

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            //            _log.DebugFormat(
            //                "[LOCAL PRESENCE CONNECTOR]: Registering IPresenceService to scene {0}", scene.RegionInfo.RegionName);

            scene.RegisterModuleInterface<IPresenceService>(this);
            _PresenceDetector.AddRegion(scene);

            _log.InfoFormat("[BASE PRESENCE SERVICE CONNECTOR]: Enabled for region {0}", scene.Name);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _PresenceDetector.RemoveRegion(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        #region IPresenceService

        public bool LoginAgent(string userID, UUID sessionID, UUID secureSessionID)
        {
            _log.Warn("[BASE PRESENCE SERVICE CONNECTOR]: LoginAgent connector not implemented at the simulators");
            return false;
        }

        public bool LogoutAgent(UUID sessionID)
        {
            return _PresenceService.LogoutAgent(sessionID);
        }

        public bool LogoutRegionAgents(UUID regionID)
        {
            return _PresenceService.LogoutRegionAgents(regionID);
        }

        public bool ReportAgent(UUID sessionID, UUID regionID)
        {
            return _PresenceService.ReportAgent(sessionID, regionID);
        }

        public PresenceInfo GetAgent(UUID sessionID)
        {
            return _PresenceService.GetAgent(sessionID);
        }

        public PresenceInfo[] GetAgents(string[] userIDs)
        {
            // Don't bother potentially making a useless network call if we not going to ask for any users anyway.
            if (userIDs.Length == 0)
                return new PresenceInfo[0];

            return _PresenceService.GetAgents(userIDs);
        }

        #endregion
    }
}