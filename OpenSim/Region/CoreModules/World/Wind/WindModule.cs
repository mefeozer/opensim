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
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WindModule")]
    public class WindModule : IWindModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private uint _frame = 0;
        private int _dataVersion = 0;
        private int _frameUpdateRate = 150;
        //private Random _rndnums = new Random(Environment.TickCount);
        private Scene _scene = null;
        private bool _ready = false;
        private bool _inUpdate = false;

        private bool _enabled = false;
        private IConfig _windConfig;
        private IWindModelPlugin _activeWindPlugin = null;
        private string _dWindPluginName = "SimpleRandomWind";
        private readonly Dictionary<string, IWindModelPlugin> _availableWindPlugins = new Dictionary<string, IWindModelPlugin>();

        // Simplified windSpeeds based on the fact that the client protocal tracks at a resolution of 16m
        private Vector2[] windSpeeds = new Vector2[16 * 16];

        #region INonSharedRegionModule Methods

        public void Initialise(IConfigSource config)
        {
            _windConfig = config.Configs["Wind"];
//            string desiredWindPlugin = _dWindPluginName;

            if (_windConfig != null)
            {
                _enabled = _windConfig.GetBoolean("enabled", true);

                _frameUpdateRate = _windConfig.GetInt("wind_update_rate", 150);

                // Determine which wind model plugin is desired
                if (_windConfig.Contains("wind_plugin"))
                {
                    _dWindPluginName = _windConfig.GetString("wind_plugin", _dWindPluginName);
                }
            }

            if (_enabled)
            {
                _log.InfoFormat("[WIND] Enabled with an update rate of {0} frames.", _frameUpdateRate);

            }

        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _scene = scene;
            _frame = 0;
            // Register all the Wind Model Plug-ins
            foreach (IWindModelPlugin windPlugin in AddinManager.GetExtensionObjects("/OpenSim/WindModule", false))
            {
                _log.InfoFormat("[WIND] Found Plugin: {0}", windPlugin.Name);
                _availableWindPlugins.Add(windPlugin.Name, windPlugin);
            }

            // Check for desired plugin
            if (_availableWindPlugins.ContainsKey(_dWindPluginName))
            {
                _activeWindPlugin = _availableWindPlugins[_dWindPluginName];

                _log.InfoFormat("[WIND] {0} plugin found, initializing.", _dWindPluginName);

                if (_windConfig != null)
                {
                    _activeWindPlugin.Initialise();
                    _activeWindPlugin.WindConfig(_scene, _windConfig);
                }
            }

            // if the plug-in wasn't found, default to no wind.
            if (_activeWindPlugin == null)
            {
                _log.ErrorFormat("[WIND] Could not find specified wind plug-in: {0}", _dWindPluginName);
                _log.ErrorFormat("[WIND] Defaulting to no wind.");
            }

            // This one puts an entry in the main help screen
            //                _scene.AddCommand("Regions", this, "wind", "wind", "Usage: wind <plugin> <param> [value] - Get or Update Wind paramaters", null);

            // This one enables the ability to type just the base command without any parameters
            //                _scene.AddCommand("Regions", this, "wind", "", "", HandleConsoleCommand);

            // Get a list of the parameters for each plugin
            foreach (IWindModelPlugin windPlugin in _availableWindPlugins.Values)
            {
                //                    _scene.AddCommand("Regions", this, String.Format("wind base wind_plugin {0}", windPlugin.Name), String.Format("{0} - {1}", windPlugin.Name, windPlugin.Description), "", HandleConsoleBaseCommand);
                _scene.AddCommand(
                    "Regions",
                    this,
                    "wind base wind_update_rate",
                    "wind base wind_update_rate [<value>]",
                    "Get or set the wind update rate.",
                    "",
                    HandleConsoleBaseCommand);

                foreach (KeyValuePair<string, string> kvp in windPlugin.WindParams())
                {
                    string windCommand = string.Format("wind {0} {1}", windPlugin.Name, kvp.Key);
                    _scene.AddCommand("Regions", this, windCommand, string.Format("{0} [<value>]", windCommand), kvp.Value, "", HandleConsoleParamCommand);
                }
            }

            // Register event handlers for when Avatars enter the region, and frame ticks
            _scene.EventManager.OnFrame += WindUpdate;

            // Register the wind module
            _scene.RegisterModuleInterface<IWindModule>(this);

            // Generate initial wind values
            GenWind();
            // hopefully this will not be the same for all regions on same instance
            _dataVersion = _scene.AllocateIntId();
            // Mark Module Ready for duty
            _ready = true;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _ready = false;

            // REVIEW: If a region module is closed, is there a possibility that it'll re-open/initialize ??
            _activeWindPlugin = null;
            foreach (IWindModelPlugin windPlugin in _availableWindPlugins.Values)
            {
                windPlugin.Dispose();
            }

            _availableWindPlugins.Clear();

            //  Remove our hooks
            _scene.EventManager.OnFrame -= WindUpdate;
//            _scene.EventManager.OnMakeRootAgent -= OnAgentEnteredRegion;

        }

        public void Close()
        {
        }

        public string Name => "WindModule";

        public Type ReplaceableInterface => null;

        public void RegionLoaded(Scene scene)
        {
        }

        #endregion

        #region Console Commands
        private void ValidateConsole()
        {
            if (_scene.ConsoleScene() == null)
            {
                // FIXME: If console region is root then this will be printed by every module.  Currently, there is no
                // way to prevent this, short of making the entire module shared (which is complete overkill).
                // One possibility is to return a bool to signal whether the module has completely handled the command
                MainConsole.Instance.Output("Please change to a specific region in order to set Sun parameters.");
                return;
            }

            if (_scene.ConsoleScene() != _scene)
            {
                MainConsole.Instance.Output("Console Scene is not my scene.");
                return;
            }
        }

        /// <summary>
        /// Base console command handler, only used if a person specifies the base command with now options
        /// </summary>
        private void HandleConsoleCommand(string module, string[] cmdparams)
        {
            ValidateConsole();

            MainConsole.Instance.Output(
                "The wind command can be used to change the currently active wind model plugin and update the parameters for wind plugins.");
        }

        /// <summary>
        /// Called to change the active wind model plugin
        /// </summary>
        private void HandleConsoleBaseCommand(string module, string[] cmdparams)
        {
            ValidateConsole();

            if (cmdparams.Length != 4
                || !cmdparams[1].Equals("base"))
            {
                MainConsole.Instance.Output(
                    "Invalid parameters to change parameters for Wind module base, usage: wind base <parameter> <value>");

                return;
            }

            switch (cmdparams[2])
            {
                case "wind_update_rate":
                    int newRate = 1;

                    if (int.TryParse(cmdparams[3], out newRate))
                    {
                        _frameUpdateRate = newRate;
                    }
                    else
                    {
                        MainConsole.Instance.Output(
                            "Invalid value {0} specified for {1}", cmdparams[3], cmdparams[2]);

                        return;
                    }

                    break;
                case "wind_plugin":
                    string desiredPlugin = cmdparams[3];

                    if (desiredPlugin.Equals(_activeWindPlugin.Name))
                    {
                        MainConsole.Instance.Output("Wind model plugin {0} is already active", cmdparams[3]);

                        return;
                    }

                    if (_availableWindPlugins.ContainsKey(desiredPlugin))
                    {
                        _activeWindPlugin = _availableWindPlugins[cmdparams[3]];

                        MainConsole.Instance.Output("{0} wind model plugin now active", _activeWindPlugin.Name);
                    }
                    else
                    {
                        MainConsole.Instance.Output("Could not find wind model plugin {0}", desiredPlugin);
                    }
                    break;
            }
        }

        /// <summary>
        /// Called to change plugin parameters.
        /// </summary>
        private void HandleConsoleParamCommand(string module, string[] cmdparams)
        {
            ValidateConsole();

            // wind <plugin> <param> [value]
            if (cmdparams.Length != 4
                && cmdparams.Length != 3)
            {
                MainConsole.Instance.Output("Usage: wind <plugin> <param> [value]");
                return;
            }

            string plugin = cmdparams[1];
            string param = cmdparams[2];
            float value = 0f;
            if (cmdparams.Length == 4)
            {
                if (!float.TryParse(cmdparams[3], out value))
                {
                    MainConsole.Instance.Output("Invalid value {0}", cmdparams[3]);
                }

                try
                {
                    WindParamSet(plugin, param, value);
                    MainConsole.Instance.Output("{0} set to {1}", param, value);
                }
                catch (Exception e)
                {
                    MainConsole.Instance.Output("{0}", e.Message);
                }
            }
            else
            {
                try
                {
                    value = WindParamGet(plugin, param);
                    MainConsole.Instance.Output("{0} : {1}", param, value);
                }
                catch (Exception e)
                {
                    MainConsole.Instance.Output("{0}", e.Message);
                }
            }

        }
        #endregion


        #region IWindModule Methods

        /// <summary>
        /// Retrieve the wind speed at the given region coordinate.  This
        /// implimentation ignores Z.
        /// </summary>
        /// <param name="x">0...255</param>
        /// <param name="y">0...255</param>
        public Vector3 WindSpeed(int x, int y, int z)
        {
            if (_activeWindPlugin != null)
            {
                return _activeWindPlugin.WindSpeed(x, y, z);
            }
            else
            {
                return new Vector3(0.0f, 0.0f, 0.0f);
            }
        }

        public void WindParamSet(string plugin, string param, float value)
        {
            if (_availableWindPlugins.ContainsKey(plugin))
            {
                IWindModelPlugin windPlugin = _availableWindPlugins[plugin];
                windPlugin.WindParamSet(param, value);
            }
            else
            {
                throw new Exception(string.Format("Could not find plugin {0}", plugin));
            }
        }

        public float WindParamGet(string plugin, string param)
        {
            if (_availableWindPlugins.ContainsKey(plugin))
            {
                IWindModelPlugin windPlugin = _availableWindPlugins[plugin];
                return windPlugin.WindParamGet(param);
            }
            else
            {
                throw new Exception(string.Format("Could not find plugin {0}", plugin));
            }
        }

        public string WindActiveModelPluginName
        {
            get
            {
                if (_activeWindPlugin != null)
                {
                    return _activeWindPlugin.Name;
                }
                else
                {
                    return string.Empty;
                }
            }
        }

        #endregion

        /// <summary>
        /// Called on each frame update.  Updates the wind model and clients as necessary.
        /// </summary>
        public void WindUpdate()
        {
            if (!_ready || _inUpdate || _frame++ % _frameUpdateRate != 0)
                return;

            _inUpdate = true;
            Util.FireAndForget(delegate
            {
                try
                {
                    GenWind();
                    _scene.ForEachClient(delegate(IClientAPI client)
                    {
                        client.SendWindData(_dataVersion, windSpeeds);
                    });

                }
                finally
                {
                    _inUpdate = false;
                }
            },
            null, "WindModuleUpdate");
        }

        /// <summary>
        /// Calculate new wind
        /// returns false if no change
        /// </summary>

        private bool GenWind()
        {
            if (_activeWindPlugin != null && _activeWindPlugin.WindUpdate(_frame))
            {
                windSpeeds = _activeWindPlugin.WindLLClientArray();
                _dataVersion++;
                return true;
            }
            return false;
        }
    }
}
