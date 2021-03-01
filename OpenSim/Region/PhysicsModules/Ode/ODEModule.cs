using System;
using System.Reflection;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.PhysicsModule.ODE
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ODEPhysicsScene")]
    public class OdeModule : INonSharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _Enabled = false;
        private IConfigSource _config;
        private OdeScene  _scene;

       #region INonSharedRegionModule

        public string Name => "OpenDynamicsEngine";

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

            if (Util.IsWindows())
                Util.LoadArchSpecificWindowsDll("ode.dll");

            // Initializing ODE only when a scene is created allows alternative ODE plugins to co-habit (according to
            // http://opensimulator.org/mantis/view.php?id=2750).
            SafeNativeMethods.InitODE();

            _scene = new OdeScene(scene, _config, Name, Version);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled || _scene == null)
                return;

            _scene.Dispose();
            _scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled || _scene == null)
                return;

            _scene.RegionLoaded();
        }
        #endregion
    }
}
