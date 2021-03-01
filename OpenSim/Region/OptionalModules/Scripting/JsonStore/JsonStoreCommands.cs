/*
 * Copyright (c) Contributors
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
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
using Mono.Addins;

using System;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.OptionalModules.Scripting.JsonStore
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "JsonStoreCommandsModule")]

    public class JsonStoreCommandsModule  : INonSharedRegionModule
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IConfig _config = null;
        private bool _enabled = false;

        private Scene _scene = null;
        //private IJsonStoreModule _store;
        private JsonStoreModule _store;

#region Region Module interface

        // -----------------------------------------------------------------
        /// <summary>
        /// Name of this shared module is it's class name
        /// </summary>
        // -----------------------------------------------------------------
        public string Name => this.GetType().Name;

        // -----------------------------------------------------------------
        /// <summary>
        /// Initialise this shared module
        /// </summary>
        /// <param name="scene">this region is getting initialised</param>
        /// <param name="source">nini config, we are not using this</param>
        // -----------------------------------------------------------------
        public void Initialise(IConfigSource config)
        {
            try
            {
                if ((_config = config.Configs["JsonStore"]) == null)
                {
                    // There is no configuration, the module is disabled
                    // _log.InfoFormat("[JsonStore] no configuration info");
                    return;
                }

                _enabled = _config.GetBoolean("Enabled", _enabled);
            }
            catch (Exception e)
            {
                _log.Error("[JsonStore]: initialization error: {0}", e);
                return;
            }

            if (_enabled)
                _log.DebugFormat("[JsonStore]: module is enabled");
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// everything is loaded, perform post load configuration
        /// </summary>
        // -----------------------------------------------------------------
        public void PostInitialise()
        {
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// Nothing to do on close
        /// </summary>
        // -----------------------------------------------------------------
        public void Close()
        {
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// </summary>
        // -----------------------------------------------------------------
        public void AddRegion(Scene scene)
        {
            if (_enabled)
            {
                _scene = scene;

            }
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// </summary>
        // -----------------------------------------------------------------
        public void RemoveRegion(Scene scene)
        {
            // need to remove all references to the scene in the subscription
            // list to enable full garbage collection of the scene object
        }

        // -----------------------------------------------------------------
        /// <summary>
        /// Called when all modules have been added for a region. This is
        /// where we hook up events
        /// </summary>
        // -----------------------------------------------------------------
        public void RegionLoaded(Scene scene)
        {
            if (_enabled)
            {
                _scene = scene;

                _store = (JsonStoreModule) _scene.RequestModuleInterface<IJsonStoreModule>();
                if (_store == null)
                {
                    _log.ErrorFormat("[JsonStoreCommands]: JsonModule interface not defined");
                    _enabled = false;
                    return;
                }

                scene.AddCommand("JsonStore", this, "jsonstore stats", "jsonstore stats",
                                 "Display statistics about the state of the JsonStore module", "",
                                 CmdStats);
            }
        }

        /// -----------------------------------------------------------------
        /// <summary>
        /// </summary>
        // -----------------------------------------------------------------
        public Type ReplaceableInterface => null;

        #endregion

#region Commands

        private void CmdStats(string module, string[] cmd)
        {
            if (MainConsole.Instance.ConsoleScene != _scene && MainConsole.Instance.ConsoleScene != null)
                return;

            JsonStoreStats stats = _store.GetStoreStats();
            MainConsole.Instance.Output("{0}\t{1}", _scene.RegionInfo.RegionName, stats.StoreCount);
        }

#endregion

    }
}
