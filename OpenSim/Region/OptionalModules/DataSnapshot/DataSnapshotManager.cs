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
*
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Xml;
using log4net;
using Nini.Config;
using OpenMetaverse;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Region.DataSnapshot.Interfaces;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.DataSnapshot
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "DataSnapshotManager")]
    public class DataSnapshotManager : ISharedRegionModule, IDataSnapshot
    {
        #region Class members
        //Information from config
        private bool _enabled = false;
        private bool _configLoaded = false;
        private readonly List<string> _disabledModules = new List<string>();
        private readonly Dictionary<string, string> _gridinfo = new Dictionary<string, string>();
        private string _snapsDir = "DataSnapshot";
        private string _exposure_level = "minimum";

        //Lists of stuff we need
        private readonly List<Scene> _scenes = new List<Scene>();
        private readonly List<IDataSnapshotProvider> _dataproviders = new List<IDataSnapshotProvider>();

        //Various internal objects
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        internal object _syncInit = new object();
        private readonly object _serializeGen = new object();

        //DataServices and networking
        private string _dataServices = "noservices";
        public string _listener_port = ConfigSettings.DefaultRegionHttpPort.ToString();
        public string _hostname = "127.0.0.1";
        private UUID _Secret = UUID.Random();
        private bool _servicesNotified = false;

        //Update timers
        private int _period = 20; // in seconds
        private int _maxStales = 500;
        private int _stales = 0;
        private int _lastUpdate = 0;

        //Program objects
        private SnapshotStore _snapStore = null;

        #endregion

        #region Properties

        public string ExposureLevel => _exposure_level;

        public UUID Secret => _Secret;

        #endregion

        #region Region Module interface

        public void Initialise(IConfigSource config)
        {
            if (!_configLoaded)
            {
                _configLoaded = true;
                //_log.Debug("[DATASNAPSHOT]: Loading configuration");
                //Read from the config for options
                lock (_syncInit)
                {
                    try
                    {
                        _enabled = config.Configs["DataSnapshot"].GetBoolean("index_sims", _enabled);
                        string gatekeeper = Util.GetConfigVarFromSections<string>(config, "GatekeeperURI",
                            new string[] { "Startup", "Hypergrid", "GridService" }, string.Empty);
                        // Legacy. Remove soon!
                        if (string.IsNullOrEmpty(gatekeeper))
                        {
                            IConfig conf = config.Configs["GridService"];
                            if (conf != null)
                                gatekeeper = conf.GetString("Gatekeeper", gatekeeper);
                        }
                        if (!string.IsNullOrEmpty(gatekeeper))
                            _gridinfo.Add("gatekeeperURL", gatekeeper);

                        _gridinfo.Add(
                            "name", config.Configs["DataSnapshot"].GetString("gridname", "the lost continent of hippo"));

                        _exposure_level = config.Configs["DataSnapshot"].GetString("data_exposure", _exposure_level);
                        _exposure_level = _exposure_level.ToLower();
                        if(_exposure_level !="all" && _exposure_level != "minimum")
                        {
                            _log.ErrorFormat("[DATASNAPSHOT]: unknown data_exposure option: '{0}'. defaulting to minimum",_exposure_level);
                            _exposure_level = "minimum";
                        }

                        _period = config.Configs["DataSnapshot"].GetInt("default_snapshot_period", _period);
                        _maxStales = config.Configs["DataSnapshot"].GetInt("max_changes_before_update", _maxStales);
                        _snapsDir = config.Configs["DataSnapshot"].GetString("snapshot_cache_directory", _snapsDir);
                        _listener_port = config.Configs["Network"].GetString("http_listener_port", _listener_port);

                        _dataServices = config.Configs["DataSnapshot"].GetString("data_services", _dataServices);
                        // New way of spec'ing data services, one per line
                        AddDataServicesVars(config.Configs["DataSnapshot"]);

                        string[] annoying_string_array = config.Configs["DataSnapshot"].GetString("disable_modules", "").Split(".".ToCharArray());
                        foreach (string bloody_wanker in annoying_string_array)
                        {
                            _disabledModules.Add(bloody_wanker);
                        }
                        _lastUpdate = Environment.TickCount;
                    }
                    catch (Exception)
                    {
                        _log.Warn("[DATASNAPSHOT]: Could not load configuration. DataSnapshot will be disabled.");
                        _enabled = false;
                        return;
                    }
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _scenes.Add(scene);

            if (_snapStore == null)
            {
                _hostname = scene.RegionInfo.ExternalHostName;
                _snapStore = new SnapshotStore(_snapsDir, _gridinfo);
            }

            _snapStore.AddScene(scene);

            Assembly currentasm = Assembly.GetExecutingAssembly();

            foreach (Type pluginType in currentasm.GetTypes())
            {
                if (pluginType.IsPublic)
                {
                    if (!pluginType.IsAbstract)
                    {
                        if (pluginType.GetInterface("IDataSnapshotProvider") != null)
                        {
                            IDataSnapshotProvider module = (IDataSnapshotProvider)Activator.CreateInstance(pluginType);
                            module.Initialize(scene, this);
                            module.OnStale += MarkDataStale;

                            _dataproviders.Add(module);
                            _snapStore.AddProvider(module);

                            _log.Debug("[DATASNAPSHOT]: Added new data provider type: " + pluginType.Name);
                        }
                    }
                }
            }
            _log.DebugFormat("[DATASNAPSHOT]: Module added to Scene {0}.", scene.RegionInfo.RegionName);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _log.Info("[DATASNAPSHOT]: Region " + scene.RegionInfo.RegionName + " is being removed, removing from indexing");
            Scene restartedScene = SceneForUUID(scene.RegionInfo.RegionID);

            _scenes.Remove(restartedScene);
            _snapStore.RemoveScene(restartedScene);

            //Getting around the fact that we can't remove objects from a collection we are enumerating over
            List<IDataSnapshotProvider> providersToRemove = new List<IDataSnapshotProvider>();

            foreach (IDataSnapshotProvider provider in _dataproviders)
            {
                if (provider.GetParentScene == restartedScene)
                {
                    providersToRemove.Add(provider);
                }
            }

            foreach (IDataSnapshotProvider provider in providersToRemove)
            {
                _dataproviders.Remove(provider);
                _snapStore.RemoveProvider(provider);
            }

            _snapStore.RemoveScene(restartedScene);
        }

        public void PostInitialise()
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_enabled)
                return;

            if (!_servicesNotified)
            {
                //Hand it the first scene, assuming that all scenes have the same BaseHTTPServer
                new DataRequestHandler(scene, this);

                if (_dataServices != "" && _dataServices != "noservices")
                    NotifyDataServices(_dataServices, "online");

                _servicesNotified = true;
            }
        }

        public void Close()
        {
            if (!_enabled)
                return;

            if (_enabled && _dataServices != "" && _dataServices != "noservices")
                NotifyDataServices(_dataServices, "offline");
        }

        public string Name => "External Data Generator";

        public Type ReplaceableInterface => null;

        #endregion

        #region Associated helper functions

        public Scene SceneForName(string name)
        {
            foreach (Scene scene in _scenes)
                if (scene.RegionInfo.RegionName == name)
                    return scene;

            return null;
        }

        public Scene SceneForUUID(UUID id)
        {
            foreach (Scene scene in _scenes)
                if (scene.RegionInfo.RegionID == id)
                    return scene;

            return null;
        }

        private void AddDataServicesVars(IConfig config)
        {
            // Make sure the services given this way aren't in _dataServices already
            List<string> servs = new List<string>(_dataServices.Split(new char[] { ';' }));

            StringBuilder sb = new StringBuilder();
            string[] keys = config.GetKeys();

            if (keys.Length > 0)
            {
                IEnumerable<string> serviceKeys = keys.Where(value => value.StartsWith("DATA_SRV_"));
                foreach (string serviceKey in serviceKeys)
                {
                    string keyValue = config.GetString(serviceKey, string.Empty).Trim();
                    if (!servs.Contains(keyValue))
                        sb.Append(keyValue).Append(";");
                }
            }

            _dataServices = _dataServices == "noservices" ? sb.ToString() : sb.Append(_dataServices).ToString();
        }

        #endregion

        #region [Public] Snapshot storage functions

        /**
         * Reply to the http request
         */

        public XmlDocument GetSnapshot(string regionName)
        {
            if(!Monitor.TryEnter(_serializeGen,30000))
            {
                return null;
            }

            CheckStale();

            XmlDocument requestedSnap = new XmlDocument();
            requestedSnap.AppendChild(requestedSnap.CreateXmlDeclaration("1.0", null, null));
            requestedSnap.AppendChild(requestedSnap.CreateWhitespace(Environment.NewLine));

            XmlNode regiondata = requestedSnap.CreateNode(XmlNodeType.Element, "regiondata", "");
            try
            {
                if (string.IsNullOrEmpty(regionName))
                {
                    XmlNode timerblock = requestedSnap.CreateNode(XmlNodeType.Element, "expire", "");
                    timerblock.InnerText = _period.ToString();
                    regiondata.AppendChild(timerblock);

                    regiondata.AppendChild(requestedSnap.CreateWhitespace(Environment.NewLine));
                    foreach (Scene scene in _scenes)
                    {
                        regiondata.AppendChild(_snapStore.GetScene(scene, requestedSnap));
                    }
                }
                else
                {
                    Scene scene = SceneForName(regionName);
                    regiondata.AppendChild(_snapStore.GetScene(scene, requestedSnap));
                }
                requestedSnap.AppendChild(regiondata);
                regiondata.AppendChild(requestedSnap.CreateWhitespace(Environment.NewLine));
            }
            catch (XmlException e)
            {
                _log.Warn("[DATASNAPSHOT]: XmlException while trying to load snapshot: " + e.ToString());
                requestedSnap = GetErrorMessage(regionName, e);
            }
            catch (Exception e)
            {
                _log.Warn("[DATASNAPSHOT]: Caught unknown exception while trying to load snapshot: " + e.StackTrace);
                requestedSnap = GetErrorMessage(regionName, e);
            }
            finally
            {
                Monitor.Exit(_serializeGen);
            }

            return requestedSnap;

        }

        private XmlDocument GetErrorMessage(string regionName, Exception e)
        {
            XmlDocument errorMessage = new XmlDocument();
            XmlNode error = errorMessage.CreateNode(XmlNodeType.Element, "error", "");
            XmlNode region = errorMessage.CreateNode(XmlNodeType.Element, "region", "");
            region.InnerText = regionName;

            XmlNode exception = errorMessage.CreateNode(XmlNodeType.Element, "exception", "");
            exception.InnerText = e.ToString();

            error.AppendChild(region);
            error.AppendChild(exception);
            errorMessage.AppendChild(error);

            return errorMessage;
        }

        #endregion

        #region External data services
        private void NotifyDataServices(string servicesStr, string serviceName)
        {
            Stream reply = null;
            string delimStr = ";";
            char [] delimiter = delimStr.ToCharArray();

            string[] services = servicesStr.Split(delimiter, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < services.Length; i++)
            {
                string url = services[i].Trim();
                using (RestClient cli = new RestClient(url))
                {
                    cli.AddQueryParameter("service", serviceName);
                    cli.AddQueryParameter("host", _hostname);
                    cli.AddQueryParameter("port", _listener_port);
                    cli.AddQueryParameter("secret", _Secret.ToString());
                    cli.RequestMethod = "GET";
                    try
                    {
                        using(reply = cli.Request(null))
                        {
                            byte[] response = new byte[1024];
                            reply.Read(response, 0, 1024);
                        }
                    }
                    catch (WebException)
                    {
                        _log.Warn("[DATASNAPSHOT]: Unable to notify " + url);
                    }
                    catch (Exception e)
                    {
                        _log.Warn("[DATASNAPSHOT]: Ignoring unknown exception " + e.ToString());
                    }

                    // This is not quite working, so...
                    // string responseStr = Util.UTF8.GetString(response);
                    _log.Info("[DATASNAPSHOT]: data service " + url + " notified. Secret: " + _Secret);
                }
            }
        }
        #endregion

        #region Latency-based update functions

        public void MarkDataStale(IDataSnapshotProvider provider)
        {
            //Behavior here: Wait _period seconds, then update if there has not been a request in _period seconds
            //or _maxStales has been exceeded
            _stales++;
        }

        private void CheckStale()
        {
            // Wrap check
            if (Environment.TickCount < _lastUpdate)
            {
                _lastUpdate = Environment.TickCount;
            }

            if (_stales >= _maxStales)
            {
                if (Environment.TickCount - _lastUpdate >= 20000)
                {
                    _stales = 0;
                    _lastUpdate = Environment.TickCount;
                    MakeEverythingStale();
                }
            }
            else
            {
                if (_lastUpdate + 1000 * _period < Environment.TickCount)
                {
                    _stales = 0;
                    _lastUpdate = Environment.TickCount;
                    MakeEverythingStale();
                }
            }
        }

        public void MakeEverythingStale()
        {
            _log.Debug("[DATASNAPSHOT]: Marking all scenes as stale.");
            foreach (Scene scene in _scenes)
            {
                _snapStore.ForceSceneStale(scene);
            }
        }
        #endregion

    }
}
