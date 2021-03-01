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
using System.Collections.Generic;
using System.Threading;

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
//using OpenSim.Framework.Capabilities;
using Nini.Config;
using log4net;
using Mono.Addins;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using TeleportFlags = OpenSim.Framework.Constants.TeleportFlags;

namespace OpenSim.Region.OptionalModules.ViewerSupport
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "CameraOnlyMode")]
    public class CameraOnlyModeModule : INonSharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private SimulatorFeaturesHelper _Helper;
        private bool _Enabled;
        private int _UserLevel;

        public string Name => "CameraOnlyModeModule";

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig moduleConfig = config.Configs["CameraOnlyModeModule"];
            if (moduleConfig != null)
            {
                _Enabled = moduleConfig.GetBoolean("enabled", false);
                if (_Enabled)
                {
                    _UserLevel = moduleConfig.GetInt("UserLevel", 0);
                    _log.Info("[CAMERA-ONLY MODE]: CameraOnlyModeModule enabled");
                }

            }
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (_Enabled)
            {
                _scene = scene;
                //_scene.EventManager.OnMakeRootAgent += (OnMakeRootAgent);
            }
        }

        //private void OnMakeRootAgent(ScenePresence obj)
        //{
        //    throw new NotImplementedException();
        //}

        public void RegionLoaded(Scene scene)
        {
            if (_Enabled)
            {
                _Helper = new SimulatorFeaturesHelper(scene);

                ISimulatorFeaturesModule featuresModule = _scene.RequestModuleInterface<ISimulatorFeaturesModule>();
                if (featuresModule != null)
                    featuresModule.OnSimulatorFeaturesRequest += OnSimulatorFeaturesRequest;
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        private void OnSimulatorFeaturesRequest(UUID agentID, ref OSDMap features)
        {
            if (!_Enabled)
                return;

            _log.DebugFormat("[CAMERA-ONLY MODE]: OnSimulatorFeaturesRequest in {0}", _scene.RegionInfo.RegionName);
            if (_Helper.UserLevel(agentID) <= _UserLevel)
            {
                OSDMap extrasMap;
                if (features.ContainsKey("OpenSimExtras"))
                {
                    extrasMap = (OSDMap)features["OpenSimExtras"];
                }
                else
                {
                    extrasMap = new OSDMap();
                    features["OpenSimExtras"] = extrasMap;
                }
                extrasMap["camera-only-mode"] = OSDMap.FromString("true");
                _log.DebugFormat("[CAMERA-ONLY MODE]: Sent in {0}", _scene.RegionInfo.RegionName);
            }
            else
                _log.DebugFormat("[CAMERA-ONLY MODE]: NOT Sending camera-only-mode in {0}", _scene.RegionInfo.RegionName);
        }

        private void DetachAttachments(UUID agentID)
        {
            ScenePresence sp = _scene.GetScenePresence(agentID);
            if ((sp.TeleportFlags & TeleportFlags.ViaLogin) != 0)
                // Wait a little, cos there's weird stuff going on at  login related to
                // the Current Outfit Folder
                Thread.Sleep(8000);

            if (sp != null && _scene.AttachmentsModule != null)
            {
                List<SceneObjectGroup> attachs = sp.GetAttachments();
                if (attachs != null && attachs.Count > 0)
                {
                    foreach (SceneObjectGroup sog in attachs)
                    {
                        _log.DebugFormat("[CAMERA-ONLY MODE]: Forcibly detaching attach {0} from {1} in {2}",
                            sog.Name, sp.Name, _scene.RegionInfo.RegionName);

                        _scene.AttachmentsModule.DetachSingleAttachmentToInv(sp, sog);
                    }
                }
            }
        }

    }

}
