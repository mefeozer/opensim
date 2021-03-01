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
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using log4net;
using OpenSim.Region.DataSnapshot.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.DataSnapshot
{
    public class SnapshotStore
    {
        #region Class Members
        private readonly string _directory = "unyuu"; //not an attempt at adding RM references to core SVN, honest
        private readonly Dictionary<Scene, bool> _scenes = null;
        private readonly List<IDataSnapshotProvider> _providers = null;
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly Dictionary<string, string> _gridinfo = null;
        private readonly bool _cacheEnabled = true;
        #endregion

        public SnapshotStore(string directory, Dictionary<string, string> gridinfo) {
            _directory = directory;
            _scenes = new Dictionary<Scene, bool>();
            _providers = new List<IDataSnapshotProvider>();
            _gridinfo = gridinfo;

            if (Directory.Exists(_directory))
            {
                _log.Info("[DATASNAPSHOT]: Response and fragment cache directory already exists.");
            }
            else
            {
                // Try to create the directory.
                _log.Info("[DATASNAPSHOT]: Creating directory " + _directory);
                try
                {
                    Directory.CreateDirectory(_directory);
                }
                catch (Exception e)
                {
                    _log.Error("[DATASNAPSHOT]: Failed to create directory " + _directory, e);

                    //This isn't a horrible problem, just disable cacheing.
                    _cacheEnabled = false;
                    _log.Error("[DATASNAPSHOT]: Could not create directory, response cache has been disabled.");
                }
            }
        }

        public void ForceSceneStale(Scene scene)
        {
            _scenes[scene] = true;
            foreach(IDataSnapshotProvider pv in _providers)
            {
                if(pv.GetParentScene == scene && pv.Name == "LandSnapshot")
                    pv.Stale = true;
            }
        }

        #region Fragment storage
        public XmlNode GetFragment(IDataSnapshotProvider provider, XmlDocument factory)
        {
            XmlNode data = null;

            if (provider.Stale || !_cacheEnabled)
            {
                data = provider.RequestSnapshotData(factory);

                if (_cacheEnabled)
                {
                    string path = DataFileNameFragment(provider.GetParentScene, provider.Name);

                    try
                    {
                        using (XmlTextWriter snapXWriter = new XmlTextWriter(path, Encoding.Default))
                        {
                            snapXWriter.Formatting = Formatting.Indented;
                            snapXWriter.WriteStartDocument();
                            data.WriteTo(snapXWriter);
                            snapXWriter.WriteEndDocument();
                            snapXWriter.Flush();
                        }
                    }
                    catch (Exception e)
                    {
                        _log.WarnFormat("[DATASNAPSHOT]: Exception on writing to file {0}: {1}", path, e.Message);
                    }

                }

                //mark provider as not stale, parent scene as stale
                provider.Stale = false;
                _scenes[provider.GetParentScene] = true;

                _log.Debug("[DATASNAPSHOT]: Generated fragment response for provider type " + provider.Name);
            }
            else
            {
                string path = DataFileNameFragment(provider.GetParentScene, provider.Name);

                XmlDocument fragDocument = new XmlDocument
                {
                    PreserveWhitespace = true
                };
                fragDocument.Load(path);
                foreach (XmlNode node in fragDocument)
                {
                    data = factory.ImportNode(node, true);
                }

                _log.Debug("[DATASNAPSHOT]: Retrieved fragment response for provider type " + provider.Name);
            }

            return data;
        }
        #endregion

        #region Response storage
        public XmlNode GetScene(Scene scene, XmlDocument factory)
        {
            _log.Debug("[DATASNAPSHOT]: Data requested for scene " + scene.RegionInfo.RegionName);

            if (!_scenes.ContainsKey(scene)) {
                _scenes.Add(scene, true); //stale by default
            }

            XmlNode regionElement = null;

            if (!_scenes[scene])
            {
                _log.Debug("[DATASNAPSHOT]: Attempting to retrieve snapshot from cache.");
                //get snapshot from cache
                string path = DataFileNameScene(scene);

                XmlDocument fragDocument = new XmlDocument
                {
                    PreserveWhitespace = true
                };

                fragDocument.Load(path);

                foreach (XmlNode node in fragDocument)
                {
                    regionElement = factory.ImportNode(node, true);
                }

                _log.Debug("[DATASNAPSHOT]: Obtained snapshot from cache for " + scene.RegionInfo.RegionName);
            }
            else
            {
                _log.Debug("[DATASNAPSHOT]: Attempting to generate snapshot.");
                //make snapshot
                regionElement = MakeRegionNode(scene, factory);

                regionElement.AppendChild(GetGridSnapshotData(factory));
                XmlNode regionData = factory.CreateNode(XmlNodeType.Element, "data", "");

                foreach (IDataSnapshotProvider dataprovider in _providers)
                {
                    if (dataprovider.GetParentScene == scene)
                    {
                        regionData.AppendChild(GetFragment(dataprovider, factory));
                    }
                }

                regionElement.AppendChild(regionData);

                factory.AppendChild(regionElement);

                //save snapshot
                string path = DataFileNameScene(scene);

                try
                {
                    using (XmlTextWriter snapXWriter = new XmlTextWriter(path, Encoding.Default))
                    {
                        snapXWriter.Formatting = Formatting.Indented;
                        snapXWriter.WriteStartDocument();
                        regionElement.WriteTo(snapXWriter);
                        snapXWriter.WriteEndDocument();
                    }
                }
                catch (Exception e)
                {
                    _log.WarnFormat("[DATASNAPSHOT]: Exception on writing to file {0}: {1}", path, e.Message);
                }

                _scenes[scene] = false;

                _log.Debug("[DATASNAPSHOT]: Generated new snapshot for " + scene.RegionInfo.RegionName);
            }

            return regionElement;
        }

        #endregion

        #region Helpers
        private string DataFileNameFragment(Scene scene, string fragmentName)
        {
            return Path.Combine(_directory, Path.ChangeExtension(Sanitize(scene.RegionInfo.RegionName + "_" + fragmentName), "xml"));
        }

        private string DataFileNameScene(Scene scene)
        {
            return Path.Combine(_directory, Path.ChangeExtension(Sanitize(scene.RegionInfo.RegionName), "xml"));
            //return (_snapsDir + Path.DirectorySeparatorChar + scene.RegionInfo.RegionName + ".xml");
        }

        private static string Sanitize(string name)
        {
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidReStr = string.Format(@"[{0}]", invalidChars);
            string newname = Regex.Replace(name, invalidReStr, "_");
            return newname.Replace('.', '_');
        }

        private XmlNode MakeRegionNode(Scene scene, XmlDocument basedoc)
        {
            XmlNode docElement = basedoc.CreateNode(XmlNodeType.Element, "region", "");

            XmlAttribute attr = basedoc.CreateAttribute("category");
            attr.Value = GetRegionCategory(scene);
            docElement.Attributes.Append(attr);

            attr = basedoc.CreateAttribute("entities");
            attr.Value = scene.Entities.Count.ToString();
            docElement.Attributes.Append(attr);

            //attr = basedoc.CreateAttribute("parcels");
            //attr.Value = scene.LandManager.landList.Count.ToString();
            //docElement.Attributes.Append(attr);


            XmlNode infoblock = basedoc.CreateNode(XmlNodeType.Element, "info", "");

            XmlNode infopiece = basedoc.CreateNode(XmlNodeType.Element, "uuid", "");
            infopiece.InnerText = scene.RegionInfo.RegionID.ToString();
            infoblock.AppendChild(infopiece);

            infopiece = basedoc.CreateNode(XmlNodeType.Element, "url", "");
            infopiece.InnerText = scene.RegionInfo.ServerURI;
            infoblock.AppendChild(infopiece);

            infopiece = basedoc.CreateNode(XmlNodeType.Element, "name", "");
            infopiece.InnerText = scene.RegionInfo.RegionName;
            infoblock.AppendChild(infopiece);

            infopiece = basedoc.CreateNode(XmlNodeType.Element, "handle", "");
            infopiece.InnerText = scene.RegionInfo.RegionHandle.ToString();
            infoblock.AppendChild(infopiece);

            docElement.AppendChild(infoblock);

            _log.Debug("[DATASNAPSHOT]: Generated region node");
            return docElement;
        }

        private string GetRegionCategory(Scene scene)
        {
            if (scene.RegionInfo.RegionSettings.Maturity == 0)
                return "PG";

            if (scene.RegionInfo.RegionSettings.Maturity == 1)
                return "Mature";

            if (scene.RegionInfo.RegionSettings.Maturity == 2)
                return "Adult";

            return "Unknown";
        }

        private XmlNode GetGridSnapshotData(XmlDocument factory)
        {
            XmlNode griddata = factory.CreateNode(XmlNodeType.Element, "grid", "");

            foreach (KeyValuePair<string, string> GridData in _gridinfo)
            {
                //TODO: make it lowercase tag names for diva
                XmlNode childnode = factory.CreateNode(XmlNodeType.Element, GridData.Key, "");
                childnode.InnerText = GridData.Value;
                griddata.AppendChild(childnode);
            }

            _log.Debug("[DATASNAPSHOT]: Got grid snapshot data");

            return griddata;
        }
        #endregion

        #region Manage internal collections
        public void AddScene(Scene newScene)
        {
            _scenes.Add(newScene, true);
        }

        public void RemoveScene(Scene deadScene)
        {
            _scenes.Remove(deadScene);
        }

        public void AddProvider(IDataSnapshotProvider newProvider)
        {
            _providers.Add(newProvider);
        }

        public void RemoveProvider(IDataSnapshotProvider deadProvider)
        {
            _providers.Remove(deadProvider);
        }
        #endregion
    }
}
