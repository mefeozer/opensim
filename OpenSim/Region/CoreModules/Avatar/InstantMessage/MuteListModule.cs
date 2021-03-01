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
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Mono.Addins;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Avatar.InstantMessage
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MuteListModule")]
    public class MuteListModule : ISharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        protected bool _Enabled = false;
        protected List<Scene> _SceneList = new List<Scene>();
        protected IMuteListService _service = null;
        private IUserManagement _userManagementModule;

        public void Initialise(IConfigSource config)
        {
            IConfig cnf = config.Configs["Messaging"];
            if (cnf == null)
                return;

            if (cnf.GetString("MuteListModule", "None") != "MuteListModule")
                return;

            _Enabled = true;
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            IXfer xfer = scene.RequestModuleInterface<IXfer>();
            if (xfer == null)
            {
                _log.ErrorFormat("[MuteListModule]: Xfer not available in region {0}. Module Disabled", scene.Name);
                _Enabled = false;
                return;
            }

            IMuteListService srv = scene.RequestModuleInterface<IMuteListService>();
            if(srv == null)
            {
                _log.ErrorFormat("[MuteListModule]: MuteListService not available in region {0}. Module Disabled", scene.Name);
                _Enabled = false;
                return;
            }

            lock (_SceneList)
            {
                if(_service == null)
                    _service = srv;
                if(_userManagementModule == null)
                     _userManagementModule = scene.RequestModuleInterface<IUserManagement>();
                _SceneList.Add(scene);
                scene.EventManager.OnNewClient += OnNewClient;
            }
        }

        public void RemoveRegion(Scene scene)
        {
            lock (_SceneList)
            {
                if(_SceneList.Contains(scene))
                {
                    _SceneList.Remove(scene);
                    scene.EventManager.OnNewClient -= OnNewClient;
                }
            }
        }

        public void PostInitialise()
        {
            if (!_Enabled)
                return;

            _log.Debug("[MuteListModule]: enabled");
        }

        public string Name => "MuteListModule";

        public Type ReplaceableInterface => null;

        public void Close()
        {
        }

        private bool IsForeign(IClientAPI client)
        {
            if(_userManagementModule == null)
                return false; // we can't check

            return !_userManagementModule.IsLocalGridUser(client.AgentId);
        }

        private void OnNewClient(IClientAPI client)
        {
            client.OnMuteListRequest += OnMuteListRequest;
            client.OnUpdateMuteListEntry += OnUpdateMuteListEntry;
            client.OnRemoveMuteListEntry += OnRemoveMuteListEntry;
        }

        private void OnMuteListRequest(IClientAPI client, uint crc)
        {
            if (!_Enabled || IsForeign(client))
            {
                if(crc == 0)
                    client.SendEmpytMuteList();
                else
                    client.SendUseCachedMuteList();
                return;
            }

            IXfer xfer = client.Scene.RequestModuleInterface<IXfer>();
            if (xfer == null)
            {
                if(crc == 0)
                    client.SendEmpytMuteList();
                else
                    client.SendUseCachedMuteList();
                return;
            }

            byte[] data = _service.MuteListRequest(client.AgentId, crc);
            if (data == null)
            {
                if(crc == 0)
                    client.SendEmpytMuteList();
                else
                    client.SendUseCachedMuteList();
                return;
            }

            if (data.Length == 0)
            {
                client.SendEmpytMuteList();
                return;
            }

            if (data.Length == 1)
            {
                if(crc == 0)
                    client.SendEmpytMuteList();
                else
                    client.SendUseCachedMuteList();
                return;
            }

            string filename = "mutes" + client.AgentId.ToString();
            xfer.AddNewFile(filename, data);
            client.SendMuteListUpdate(filename);
        }

        private void OnUpdateMuteListEntry(IClientAPI client, UUID muteID, string muteName, int muteType, uint muteFlags)
        {
            if (!_Enabled || IsForeign(client))
                return;

            UUID agentID = client.AgentId;
            if(muteType == 1) // agent
            {
                if(agentID == muteID)
                    return;
                if(_SceneList[0].Permissions.IsAdministrator(muteID))
                {
                    OnMuteListRequest(client, 0);
                    return;
                }
            }

            MuteData mute = new MuteData
            {
                AgentID = agentID,
                MuteID = muteID,
                MuteName = muteName,
                MuteType = muteType,
                MuteFlags = (int)muteFlags,
                Stamp = Util.UnixTimeSinceEpoch()
            };

            _service.UpdateMute(mute);
        }

        private void OnRemoveMuteListEntry(IClientAPI client, UUID muteID, string muteName)
        {
            if (!_Enabled || IsForeign(client))
                return;
            _service.RemoveMute(client.AgentId, muteID, muteName);
        }
    }
}

