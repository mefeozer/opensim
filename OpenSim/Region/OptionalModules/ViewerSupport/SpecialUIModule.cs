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
using System.IO;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
//using OpenSim.Framework.Capabilities;
using Nini.Config;
using log4net;
using Mono.Addins;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

namespace OpenSim.Region.OptionalModules.ViewerSupport
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SpecialUI")]
    public class SpecialUIModule : INonSharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const string VIEWER_SUPPORT_DIR = "ViewerSupport";

        private Scene _scene;
        private SimulatorFeaturesHelper _Helper;
        private bool _Enabled;
        private int _UserLevel;

        public string Name => "SpecialUIModule";

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            IConfig moduleConfig = config.Configs["SpecialUIModule"];
            if (moduleConfig != null)
            {
                _Enabled = moduleConfig.GetBoolean("enabled", false);
                if (_Enabled)
                {
                    _UserLevel = moduleConfig.GetInt("UserLevel", 0);
                    _log.Info("[SPECIAL UI]: SpecialUIModule enabled");
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
            }
        }

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
            _log.DebugFormat("[SPECIAL UI]: OnSimulatorFeaturesRequest in {0}", _scene.RegionInfo.RegionName);
            if (_Helper.UserLevel(agentID) <= _UserLevel)
            {
                OSD extrasMap;
                OSDMap specialUI = new OSDMap();
                using (StreamReader s = new StreamReader(Path.Combine(VIEWER_SUPPORT_DIR, "panel_toolbar.xml")))
                {
                    if (!features.TryGetValue("OpenSimExtras", out extrasMap))
                    {
                        extrasMap = new OSDMap();
                        features["OpenSimExtras"] = extrasMap;
                    }

                    specialUI["toolbar"] = OSDMap.FromString(s.ReadToEnd());
                    ((OSDMap)extrasMap)["special-ui"] = specialUI;
                }
                _log.DebugFormat("[SPECIAL UI]: Sending panel_toolbar.xml in {0}", _scene.RegionInfo.RegionName);

                if (Directory.Exists(Path.Combine(VIEWER_SUPPORT_DIR, "Floaters")))
                {
                    OSDMap floaters = new OSDMap();
                    uint n = 0;
                    foreach (string name in Directory.GetFiles(Path.Combine(VIEWER_SUPPORT_DIR, "Floaters"), "*.xml"))
                    {
                        using (StreamReader s = new StreamReader(name))
                        {
                            string simple_name = Path.GetFileNameWithoutExtension(name);
                            OSDMap floater = new OSDMap();
                            floaters[simple_name] = OSDMap.FromString(s.ReadToEnd());
                            n++;
                        }
                    }
                    specialUI["floaters"] = floaters;
                    _log.DebugFormat("[SPECIAL UI]: Sending {0} floaters", n);
                }
            }
            else
                _log.DebugFormat("[SPECIAL UI]: NOT Sending panel_toolbar.xml in {0}", _scene.RegionInfo.RegionName);

        }

    }

}
