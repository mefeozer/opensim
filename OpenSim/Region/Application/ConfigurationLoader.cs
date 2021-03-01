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
using System.IO;
using System.Reflection;
using System.Xml;
using log4net;
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim
{
    /// <summary>
    /// Loads the Configuration files into nIni
    /// </summary>
    public class ConfigurationLoader
    {

        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Various Config settings the region needs to start
        /// Physics Engine, Mesh Engine, GridMode, PhysicsPrim allowed, Neighbor,
        /// StorageDLL, Storage Connection String, Estate connection String, Client Stack
        /// Standalone settings.
        /// </summary>
        protected ConfigSettings _configSettings;

        /// <summary>
        /// A source of Configuration data
        /// </summary>
        protected OpenSimConfigSource _config;

        /// <summary>
        /// Grid Service Information.  This refers to classes and addresses of the grid service
        /// </summary>
        protected NetworkServersInfo _networkServersInfo;

        /// <summary>
        /// Loads the region configuration
        /// </summary>
        /// <param name="argvSource">Parameters passed into the process when started</param>
        /// <param name="configSettings"></param>
        /// <param name="networkInfo"></param>
        /// <returns>A configuration that gets passed to modules</returns>
        public OpenSimConfigSource LoadConfigSettings(
                IConfigSource argvSource, out ConfigSettings configSettings,
                out NetworkServersInfo networkInfo)
        {
            _configSettings = configSettings = new ConfigSettings();
            _networkServersInfo = networkInfo = new NetworkServersInfo();
            IConfig startupConfig = argvSource.Configs["Startup"];

            List<string> sources = new List<string>();

            AddOpensimDefaultsIniToSource(startupConfig, sources);
            AddOpenSimIniToSource(startupConfig, sources);

            _config = new OpenSimConfigSource
            {
                Source = new IniConfigSource()
            };

            _log.Info("[CONFIG]: Reading configuration settings");


            bool iniFileExists = false;
            for (int i = 0; i < sources.Count; i++)
            {
                if (ReadConfig(_config, sources[i]))
                {
                    iniFileExists = true;
                    AddIncludes(_config, sources);
                }
            }

            // Override distro settings with contents of inidirectory
            string iniDirName = startupConfig.GetString("inidirectory", "config");
            string iniDirPath = Path.Combine(Util.configDir(), iniDirName);

            if (Directory.Exists(iniDirPath))
            {
                _log.InfoFormat("[CONFIG]: Searching folder {0} for config ini files", iniDirPath);
                List<string> overrideSources = new List<string>();

                string[] fileEntries = Directory.GetFiles(iniDirName);
                foreach (string filePath in fileEntries)
                {
                    if (Path.GetExtension(filePath).ToLower() == ".ini")
                    {
                        if (!sources.Contains(Path.GetFullPath(filePath)))
                        {
                            overrideSources.Add(Path.GetFullPath(filePath));
                            // put it in sources too, to avoid circularity
                            sources.Add(Path.GetFullPath(filePath));
                        }
                    }
                }


                if (overrideSources.Count > 0)
                {
                    OpenSimConfigSource overrideConfig = new OpenSimConfigSource
                    {
                        Source = new IniConfigSource()
                    };

                    for (int i = 0; i < overrideSources.Count; i++)
                    {
                        if (ReadConfig(overrideConfig, overrideSources[i]))
                        {
                            iniFileExists = true;
                            AddIncludes(overrideConfig, overrideSources);
                        }
                    }
                    _config.Source.Merge(overrideConfig.Source);
                }
            }

            ExitForNoConfig(iniFileExists, sources);

            // Merge OpSys env vars
            _log.Info("[CONFIG]: Loading environment variables for Config");
            Util.MergeEnvironmentToConfig(_config.Source);

            // Make sure command line options take precedence
            _config.Source.Merge(argvSource);

            _config.Source.ReplaceKeyValues();

            ReadConfigSettings();

            return _config;
        }

        private void AddOpensimDefaultsIniToSource(IConfig startupConfig, List<string> sources)
        {
            string masterFileName = startupConfig.GetString("inimaster", "OpenSimDefaults.ini");

            if (masterFileName == "none")
                masterFileName = string.Empty;

            if (IsUri(masterFileName))
            {
                if (!sources.Contains(masterFileName))
                    sources.Add(masterFileName);
            }
            else
            {
                string masterFilePath = Path.GetFullPath(
                        Path.Combine(Util.configDir(), masterFileName));

                if (!string.IsNullOrEmpty(masterFileName))
                {
                    if (File.Exists(masterFilePath))
                    {
                        if (!sources.Contains(masterFilePath))
                            sources.Add(masterFilePath);
                    }
                    else
                    {
                        _log.ErrorFormat("Master ini file {0} not found", Path.GetFullPath(masterFilePath));
                        Environment.Exit(1);
                    }
                }
            }
        }

        private static void ExitForNoConfig(bool iniFileExists, List<string> sources)
        {
            if (sources.Count == 0)
            {
                _log.FatalFormat("[CONFIG]: Could not load any configuration");
                Environment.Exit(1);
            }
            else if (!iniFileExists)
            {
                _log.FatalFormat("[CONFIG]: Could not load any configuration");
                _log.FatalFormat("[CONFIG]: Configuration exists, but there was an error loading it!");
                Environment.Exit(1);
            }
        }

        private void AddOpenSimIniToSource(IConfig startupConfig, List<string> sources)
        {
            string iniFileName = startupConfig.GetString("inifile", "OpenSim.ini");
            if (IsUri(iniFileName))
            {
                if (!sources.Contains(iniFileName))
                    sources.Add(iniFileName);
            }
            else
            {
                string iniFilePath = Path.GetFullPath(
                    Path.Combine(Util.configDir(), iniFileName));

                if (!File.Exists(iniFilePath))
                {
                    iniFileName = "OpenSim.xml";
                    iniFilePath = Path.GetFullPath(Path.Combine(Util.configDir(), iniFileName));
                }

                if (File.Exists(iniFilePath))
                {
                    if (!sources.Contains(iniFilePath))
                        sources.Add(iniFilePath);
                }
            }
        }

        /// <summary>
        /// Adds the included files as ini configuration files
        /// </summary>
        /// <param name="sources">List of URL strings or filename strings</param>
        private void AddIncludes(OpenSimConfigSource configSource, List<string> sources)
        {
            //loop over config sources
            foreach (IConfig config in configSource.Source.Configs)
            {
                // Look for Include-* in the key name
                string[] keys = config.GetKeys();
                foreach (string k in keys)
                {
                    if (k.StartsWith("Include-"))
                    {
                        // read the config file to be included.
                        string file = config.GetString(k);
                        if (IsUri(file))
                        {
                            if (!sources.Contains(file))
                                sources.Add(file);
                        }
                        else
                        {
                            string basepath = Path.GetFullPath(Util.configDir());
                            // Resolve relative paths with wildcards
                            string chunkWithoutWildcards = file;
                            string chunkWithWildcards = string.Empty;
                            int wildcardIndex = file.IndexOfAny(new char[] { '*', '?' });
                            if (wildcardIndex != -1)
                            {
                                chunkWithoutWildcards = file.Substring(0, wildcardIndex);
                                chunkWithWildcards = file.Substring(wildcardIndex);
                            }
                            string path = Path.Combine(basepath, chunkWithoutWildcards);
                            path = Path.GetFullPath(path) + chunkWithWildcards;
                            string[] paths = Util.Glob(path);

                            // If the include path contains no wildcards, then warn the user that it wasn't found.
                            if (wildcardIndex == -1 && paths.Length == 0)
                            {
                                _log.WarnFormat("[CONFIG]: Could not find include file {0}", path);
                            }
                            else
                            {
                                foreach (string p in paths)
                                {
                                    if (!sources.Contains(p))
                                        sources.Add(p);
                                }
                            }
                        }
                    }
                }
            }
        }
        /// <summary>
        /// Check if we can convert the string to a URI
        /// </summary>
        /// <param name="file">String uri to the remote resource</param>
        /// <returns>true if we can convert the string to a Uri object</returns>
        bool IsUri(string file)
        {
            Uri configUri;

            return Uri.TryCreate(file, UriKind.Absolute,
                    out configUri) && configUri.Scheme == Uri.UriSchemeHttp;
        }

        /// <summary>
        /// Provide same ini loader functionality for standard ini and master ini - file system or XML over http
        /// </summary>
        /// <param name="iniPath">Full path to the ini</param>
        /// <returns></returns>
        private bool ReadConfig(OpenSimConfigSource configSource, string iniPath)
        {
            bool success = false;

            if (!IsUri(iniPath))
            {
                _log.InfoFormat("[CONFIG]: Reading configuration file {0}", Path.GetFullPath(iniPath));

                configSource.Source.Merge(new IniConfigSource(iniPath));
                success = true;
            }
            else
            {
                _log.InfoFormat("[CONFIG]: {0} is a http:// URI, fetching ...", iniPath);

                // The ini file path is a http URI
                // Try to read it
                try
                {
                    XmlReader r = XmlReader.Create(iniPath);
                    XmlConfigSource cs = new XmlConfigSource(r);
                    configSource.Source.Merge(cs);

                    success = true;
                }
                catch (Exception e)
                {
                    _log.FatalFormat("[CONFIG]: Exception reading config from URI {0}\n" + e.ToString(), iniPath);
                    Environment.Exit(1);
                }
            }
            return success;
        }

        /// <summary>
        /// Read initial region settings from the ConfigSource
        /// </summary>
        protected virtual void ReadConfigSettings()
        {
            IConfig startupConfig = _config.Source.Configs["Startup"];
            if (startupConfig != null)
            {
                _configSettings.PhysicsEngine = startupConfig.GetString("physics");
                _configSettings.MeshEngineName = startupConfig.GetString("meshing");

                _configSettings.ClientstackDll
                    = startupConfig.GetString("clientstack_plugin", "OpenSim.Region.ClientStack.LindenUDP.dll");
            }

            _networkServersInfo.loadFromConfiguration(_config.Source);
        }
    }
}
