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
using System.Net;
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
// using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    /// <summary>
    /// SimulatorFeatures capability.
    /// </summary>
    /// <remarks>
    /// This is required for uploading Mesh.
    /// Since is accepts an open-ended response, we also send more information
    /// for viewers that care to interpret it.
    ///
    /// NOTE: Part of this code was adapted from the Aurora project, specifically
    /// the normal part of the response in the capability handler.
    /// </remarks>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SimulatorFeaturesModule")]
    public class SimulatorFeaturesModule : INonSharedRegionModule, ISimulatorFeaturesModule
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public event SimulatorFeaturesRequestDelegate OnSimulatorFeaturesRequest;

        private Scene _scene;

        /// <summary>
        /// Simulator features
        /// </summary>
        private readonly OSDMap _features = new OSDMap();

        private bool _ExportSupported = false;

        private bool _doScriptSyntax;

        static private readonly object _scriptSyntaxLock = new object();
        static private UUID _scriptSyntaxID = UUID.Zero;
        static private byte[] _scriptSyntaxXML = null;

        #region ISharedRegionModule Members

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["SimulatorFeatures"];
            _doScriptSyntax = true;
            if (config != null)
            {
                _ExportSupported = config.GetBoolean("ExportSupported", _ExportSupported);
                _doScriptSyntax = config.GetBoolean("ScriptSyntax", _doScriptSyntax);
            }

            ReadScriptSyntax();
            AddDefaultFeatures();
        }

        public void AddRegion(Scene s)
        {
            _scene = s;
            _scene.EventManager.OnRegisterCaps += RegisterCaps;

            _scene.RegisterModuleInterface<ISimulatorFeaturesModule>(this);
        }

        public void RemoveRegion(Scene s)
        {
            _scene.EventManager.OnRegisterCaps -= RegisterCaps;
        }

        public void RegionLoaded(Scene s)
        {
            GetGridExtraFeatures(s);
        }

        public void Close() { }

        public string Name => "SimulatorFeaturesModule";

        public Type ReplaceableInterface => null;

        #endregion

        /// <summary>
        /// Add default features
        /// </summary>
        /// <remarks>
        /// TODO: These should be added from other modules rather than hardcoded.
        /// </remarks>
        private void AddDefaultFeatures()
        {
            lock (_features)
            {
                _features["MeshRezEnabled"] = true;
                _features["MeshUploadEnabled"] = true;
                _features["MeshXferEnabled"] = true;

                _features["BakesOnMeshEnabled"] = true;

                _features["PhysicsMaterialsEnabled"] = true;
                OSDMap typesMap = new OSDMap();
                typesMap["convex"] = true;
                typesMap["none"] = true;
                typesMap["prim"] = true;
                _features["PhysicsShapeTypes"] = typesMap;

                if(_doScriptSyntax && _scriptSyntaxID != UUID.Zero)
                    _features["LSLSyntaxId"] = OSD.FromUUID(_scriptSyntaxID);

                OSDMap meshAnim = new OSDMap();
                meshAnim["AnimatedObjectMaxTris"] = OSD.FromInteger(150000);
                meshAnim["MaxAgentAnimatedObjectAttachments"] = OSD.FromInteger(2);
                _features["AnimatedObjects"] = meshAnim;

                _features["MaxAgentAttachments"] = OSD.FromInteger(Constants.MaxAgentAttachments);
                _features["MaxAgentGroupsBasic"] = OSD.FromInteger(Constants.MaxAgentGroups);
                _features["MaxAgentGroupsPremium"] = OSD.FromInteger(Constants.MaxAgentGroups);

                // Extra information for viewers that want to use it
                // TODO: Take these out of here into their respective modules, like map-server-url
                OSDMap extrasMap;
                if(_features.ContainsKey("OpenSimExtras"))
                {
                    extrasMap = (OSDMap)_features["OpenSimExtras"];
                }
                else
                    extrasMap = new OSDMap();

                extrasMap["AvatarSkeleton"] = true;
                extrasMap["AnimationSet"] = true;

                extrasMap["MinSimHeight"] = Constants.MinSimulationHeight;
                extrasMap["MaxSimHeight"] = Constants.MaxSimulationHeight;
                extrasMap["MinHeightmap"] = Constants.MinTerrainHeightmap;
                extrasMap["MaxHeightmap"] = Constants.MaxTerrainHeightmap;

                if (_ExportSupported)
                    extrasMap["ExportSupported"] = true;
                if (extrasMap.Count > 0)
                    _features["OpenSimExtras"] = extrasMap;
            }
        }

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            caps.RegisterSimpleHandler("SimulatorFeatures",
                new SimpleStreamHandler("/" + UUID.Random(),
                    delegate (IOSHttpRequest request, IOSHttpResponse response)
                    {
                        HandleSimulatorFeaturesRequest(request, response, agentID);
                    }));

            if (_doScriptSyntax && _scriptSyntaxID != UUID.Zero && _scriptSyntaxXML != null)
            {
                caps.RegisterSimpleHandler("LSLSyntax",
                    new SimpleStreamHandler("/" + UUID.Random(), HandleSyntaxRequest));
            }
        }

        public void AddFeature(string name, OSD value)
        {
            lock (_features)
                _features[name] = value;
        }

        public void AddOpenSimExtraFeature(string name, OSD value)
        {
            lock (_features)
            {
                OSDMap extrasMap;
                if (_features.TryGetValue("OpenSimExtras", out OSD extra))
                    extrasMap = extra as OSDMap;
                else
                {
                    extrasMap = new OSDMap();
                }
                extrasMap[name] = value;
                _features["OpenSimExtras"] = extrasMap;
            }
        }

        public bool RemoveFeature(string name)
        {
            lock (_features)
                return _features.Remove(name);
        }

        public bool TryGetFeature(string name, out OSD value)
        {
            lock (_features)
                return _features.TryGetValue(name, out value);
        }

        public bool TryGetOpenSimExtraFeature(string name, out OSD value)
        {
            value = null;
            lock (_features)
            {
                if (!_features.TryGetValue("OpenSimExtras", out OSD extra))
                    return false;
                if(!(extra is OSDMap))
                    return false;
                return (extra as OSDMap).TryGetValue(name, out value);
            }
        }

        public OSDMap GetFeatures()
        {
            lock (_features)
                return new OSDMap(_features);
        }

        private OSDMap DeepCopy()
        {
            // This isn't the cheapest way of doing this but the rate
            // of occurrence is low (on sim entry only) and it's a sure
            // way to get a true deep copy.
            OSD copy = OSDParser.DeserializeLLSDXml(OSDParser.SerializeLLSDXmlString(_features));

            return (OSDMap)copy;
        }

        private void HandleSimulatorFeaturesRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            //            _log.DebugFormat("[SIMULATOR FEATURES MODULE]: SimulatorFeatures request");

            if (request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            ScenePresence sp = _scene.GetScenePresence(agentID);
            if (sp == null)
            {
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.AddHeader("Retry-After", "5");
                return;
            }

            OSDMap copy = DeepCopy();

            // Let's add the agentID to the destination guide, if it is expecting that.
            if (copy.ContainsKey("OpenSimExtras") && ((OSDMap)copy["OpenSimExtras"]).ContainsKey("destination-guide-url"))
                ((OSDMap)copy["OpenSimExtras"])["destination-guide-url"] = Replace(((OSDMap)copy["OpenSimExtras"])["destination-guide-url"], "[USERID]", agentID.ToString());

            OnSimulatorFeaturesRequest?.Invoke(agentID, ref copy);

            //Send back data
            response.RawBuffer = Util.UTF8.GetBytes(OSDParser.SerializeLLSDXmlString(copy));
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private void HandleSyntaxRequest(IOSHttpRequest request, IOSHttpResponse response)
        {
            if (request.HttpMethod != "GET" || _scriptSyntaxXML == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            response.RawBuffer = _scriptSyntaxXML;
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        /// <summary>
        /// Gets the grid extra features.
        /// </summary>
        /// <param name='featuresURI'>
        /// The URI Robust uses to handle the get_extra_features request
        /// </param>

        private void GetGridExtraFeatures(Scene scene)
        {
            Dictionary<string, object> extraFeatures = scene.GridService.GetExtraFeatures();
            if (extraFeatures.ContainsKey("Result") && extraFeatures["Result"] != null && extraFeatures["Result"].ToString() == "Failure")
            {
                _log.WarnFormat("[SIMULATOR FEATURES MODULE]: Unable to retrieve grid-wide features");
                return;
            }

            GridInfo ginfo = scene.SceneGridInfo;
            lock (_features)
            {
                OSDMap extrasMap;
                if (_features.TryGetValue("OpenSimExtras", out OSD extra))
                    extrasMap = extra as OSDMap;
                else
                {
                    extrasMap = new OSDMap();
                }

                foreach (string key in extraFeatures.Keys)
                {
                    string val = (string)extraFeatures[key];
                    switch(key)
                    {
                        case "GridName":
                            ginfo.GridName = val;
                            break;
                        case "GridNick":
                            ginfo.GridNick = val;
                            break;
                        case "GridURL":
                            ginfo.GridUrl = val;
                            break;
                        case "GridURLAlias":
                            string[] vals = val.Split(',');
                            if(vals.Length > 0)
                                ginfo.GridUrlAlias = vals;
                            break;
                        case "search-server-url":
                            ginfo.SearchURL = val;
                            break;
                        case "destination-guide-url":
                            ginfo.DestinationGuideURL = val;
                            break;
                        /* keep this local to avoid issues with diferent modules
                        case "currency-base-uri":
                            ginfo.EconomyURL = val;
                            break;
                        */
                        default:
                            extrasMap[key] = val;
                            if (key == "ExportSupported")
                            {
                                bool.TryParse(val, out _ExportSupported);
                            }
                            break;
                    }

                }
                _features["OpenSimExtras"] = extrasMap;
            }
        }

        private string Replace(string url, string substring, string replacement)
        {
            if (!string.IsNullOrEmpty(url) && url.Contains(substring))
                return url.Replace(substring, replacement);

            return url;
        }

        private void ReadScriptSyntax()
        {
            lock(_scriptSyntaxLock)
            {
                if(!_doScriptSyntax || _scriptSyntaxID != UUID.Zero)
                    return;

                if(!File.Exists("ScriptSyntax.xml"))
                    return;

                try
                {
                    using (StreamReader sr = File.OpenText("ScriptSyntax.xml"))
                    {
                        StringBuilder sb = new StringBuilder(400*1024);

                        string s="";
                        char[] trimc = new char[] {' ','\t', '\n', '\r'};

                        s = sr.ReadLine();
                        if(s == null)
                            return;
                        s = s.Trim(trimc);
                        UUID id;
                        if(!UUID.TryParse(s,out id))
                            return;

                        while ((s = sr.ReadLine()) != null)
                        {
                            s = s.Trim(trimc);
                            if (string.IsNullOrEmpty(s) || s.StartsWith("<!--"))
                                continue;
                            sb.Append(s);
                        }
                        _scriptSyntaxXML = Util.UTF8.GetBytes(sb.ToString());
                        _scriptSyntaxID = id;
                    }
                }
                catch
                {
                    _log.Error("[SIMULATOR FEATURES MODULE] fail read ScriptSyntax.xml file");
                    _scriptSyntaxID = UUID.Zero;
                    _scriptSyntaxXML = null;
                }
            }
        }
    }
}
