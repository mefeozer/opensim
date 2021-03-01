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
using System.Threading;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using Mono.Addins;

namespace OpenSim.ApplicationPlugins.LoadRegions
{
    [Extension(Path="/OpenSim/Startup", Id="LoadRegions", NodeName="Plugin")]
    public class LoadRegionsPlugin : IApplicationPlugin, IRegionCreator
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public event NewRegionCreated OnNewRegionCreated;
        private NewRegionCreated _newRegionCreatedHandler;

        #region IApplicationPlugin Members

        // TODO: required by IPlugin, but likely not at all right
        private readonly string _name = "LoadRegionsPlugin";
        private readonly string _version = "0.0";

        public string Version => _version;

        public string Name => _name;

        protected OpenSimBase _openSim;

        public void Initialise()
        {
            _log.Error("[LOAD REGIONS PLUGIN]: " + Name + " cannot be default-initialized!");
            throw new PluginNotInitialisedException(Name);
        }

        public void Initialise(OpenSimBase openSim)
        {
            _openSim = openSim;
            _openSim.ApplicationRegistry.RegisterInterface<IRegionCreator>(this);
        }

        public void PostInitialise()
        {
            //_log.Info("[LOADREGIONS]: Load Regions addin being initialised");

            IEstateLoader estateLoader = null;
            IRegionLoader regionLoader;
            if (_openSim.ConfigSource.Source.Configs["Startup"].GetString("region_info_source", "filesystem") == "filesystem")
            {
                _log.Info("[LOAD REGIONS PLUGIN]: Loading region configurations from filesystem");
                regionLoader = new RegionLoaderFileSystem();

                estateLoader = new EstateLoaderFileSystem(_openSim);
            }
            else
            {
                _log.Info("[LOAD REGIONS PLUGIN]: Loading region configurations from web");
                regionLoader = new RegionLoaderWebServer();
            }

            // Load Estates Before Regions!
            if(estateLoader != null)
            {
                estateLoader.SetIniConfigSource(_openSim.ConfigSource.Source);

                estateLoader.LoadEstates();
            }

            regionLoader.SetIniConfigSource(_openSim.ConfigSource.Source);
            RegionInfo[] regionsToLoad = regionLoader.LoadRegions();

            _log.Info("[LOAD REGIONS PLUGIN]: Loading specific shared modules...");
            //_log.Info("[LOAD REGIONS PLUGIN]: DynamicTextureModule...");
            //_openSim.ModuleLoader.LoadDefaultSharedModule(new DynamicTextureModule());
            //_log.Info("[LOAD REGIONS PLUGIN]: LoadImageURLModule...");
            //_openSim.ModuleLoader.LoadDefaultSharedModule(new LoadImageURLModule());
            //_log.Info("[LOAD REGIONS PLUGIN]: XMLRPCModule...");
            //_openSim.ModuleLoader.LoadDefaultSharedModule(new XMLRPCModule());
//            _log.Info("[LOADREGIONSPLUGIN]: AssetTransactionModule...");
//            _openSim.ModuleLoader.LoadDefaultSharedModule(new AssetTransactionModule());
            _log.Info("[LOAD REGIONS PLUGIN]: Done.");

            if (!CheckRegionsForSanity(regionsToLoad))
            {
                _log.Error("[LOAD REGIONS PLUGIN]: Halting startup due to conflicts in region configurations");
                Environment.Exit(1);
            }

            List<IScene> createdScenes = new List<IScene>();

            for (int i = 0; i < regionsToLoad.Length; i++)
            {
                IScene scene;
                _log.Debug("[LOAD REGIONS PLUGIN]: Creating Region: " + regionsToLoad[i].RegionName + " (ThreadID: " +
                            Thread.CurrentThread.ManagedThreadId.ToString() +
                            ")");

                bool changed = _openSim.PopulateRegionEstateInfo(regionsToLoad[i]);

                _openSim.CreateRegion(regionsToLoad[i], true, out scene);
                createdScenes.Add(scene);

                if (changed)
                    _openSim.EstateDataService.StoreEstateSettings(regionsToLoad[i].EstateSettings);
            }

            foreach (IScene scene in createdScenes)
            {
                scene.Start();

                _newRegionCreatedHandler = OnNewRegionCreated;
                if (_newRegionCreatedHandler != null)
                {
                    _newRegionCreatedHandler(scene);
                }
            }
        }

        public void Dispose()
        {
        }

        #endregion

        /// <summary>
        /// Check that region configuration information makes sense.
        /// </summary>
        /// <param name="regions"></param>
        /// <returns>True if we're sane, false if we're insane</returns>
        private bool CheckRegionsForSanity(RegionInfo[] regions)
        {
            if (regions.Length == 0)
                return true;

            foreach (RegionInfo region in regions)
            {
                if (region.RegionID == UUID.Zero)
                {
                    _log.ErrorFormat(
                        "[LOAD REGIONS PLUGIN]: Region {0} has invalid UUID {1}",
                        region.RegionName, region.RegionID);
                    return false;
                }
            }

            for (int i = 0; i < regions.Length - 1; i++)
            {
                for (int j = i + 1; j < regions.Length; j++)
                {
                    if (regions[i].RegionID == regions[j].RegionID)
                    {
                        _log.ErrorFormat(
                            "[LOAD REGIONS PLUGIN]: Regions {0} and {1} have the same UUID {2}",
                            regions[i].RegionName, regions[j].RegionName, regions[i].RegionID);
                        return false;
                    }
                    else if (
                        regions[i].RegionLocX == regions[j].RegionLocX && regions[i].RegionLocY == regions[j].RegionLocY)
                    {
                        _log.ErrorFormat(
                            "[LOAD REGIONS PLUGIN]: Regions {0} and {1} have the same grid location ({2}, {3})",
                            regions[i].RegionName, regions[j].RegionName, regions[i].RegionLocX, regions[i].RegionLocY);
                        return false;
                    }
                    else if (regions[i].InternalEndPoint.Port == regions[j].InternalEndPoint.Port)
                    {
                        _log.ErrorFormat(
                            "[LOAD REGIONS PLUGIN]: Regions {0} and {1} have the same internal IP port {2}",
                            regions[i].RegionName, regions[j].RegionName, regions[i].InternalEndPoint.Port);
                        return false;
                    }
                }
            }

            return true;
        }
    }
}
