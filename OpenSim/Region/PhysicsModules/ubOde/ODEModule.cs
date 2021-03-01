using System;
using System.Collections.Generic;
using System.Reflection;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.ubOde
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ubODEPhysicsScene")]
    class ubOdeModule : INonSharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly Dictionary<Scene, ODEScene> _scenes = new Dictionary<Scene, ODEScene>();
        private bool _Enabled = false;
        private IConfigSource _config;
        private bool OSOdeLib;


       #region INonSharedRegionModule

        public string Name => "ubODE";

        public string Version => "1.0";

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["Startup"];
            if (config != null)
            {
                string physics = config.GetString("physics", string.Empty);
                if (physics == Name)
                {
                    _config = source;
                    _Enabled = true;

                    if (Util.IsWindows())
                        Util.LoadArchSpecificWindowsDll("ode.dll");

                    SafeNativeMethods.InitODE();

                    string ode_config = SafeNativeMethods.GetConfiguration();
                    if (ode_config == null || ode_config == "" || !ode_config.Contains("ODE_OPENSIM"))
                    {
                        _log.Error("[ubODE] Native ode library version not supported");
                        _Enabled = false;
                        return;
                    }
                    _log.InfoFormat("[ubODE] ode library configuration: {0}", ode_config);
                    OSOdeLib = true;
                }
            }
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            if(_scenes.ContainsKey(scene)) // ???
                return;
            ODEScene newodescene = new ODEScene(scene, _config, Name, Version, OSOdeLib);
            _scenes[scene] = newodescene;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            // a odescene.dispose is called later directly by scene.cs
            // since it is seen as a module interface

            if(_scenes.ContainsKey(scene))
                _scenes.Remove(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            if(_scenes.ContainsKey(scene))
            {
                _scenes[scene].RegionLoaded();
            }

        }
        #endregion
    }
}
