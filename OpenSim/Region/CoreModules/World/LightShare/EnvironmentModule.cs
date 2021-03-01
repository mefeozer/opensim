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
using System.Net;
using System.Reflection;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using log4net;
using Nini.Config;
using Mono.Addins;

using Caps = OpenSim.Framework.Capabilities.Caps;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using MethodImplOptions = System.Runtime.CompilerServices.MethodImplOptions;

namespace OpenSim.Region.CoreModules.World.LightShare
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EnvironmentModule")]

    public class EnvironmentModule : INonSharedRegionModule, IEnvironmentModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene = null;
        private UUID regionID = UUID.Zero;
        private bool Enabled = false;
        private IEstateModule _estateModule;
        private IEventQueue _eventQueue;
        private IAssetService _assetService;
        private ILandChannel _landChannel;

        private static ViewerEnvironment _DefaultEnv = null;
        // 1/1 night day ratio
        //private static readonly string _defaultDayAssetID = "5646d39e-d3d7-6aff-ed71-30fc87d64a91";
        // 3/1 night day ratio
        private static readonly string _defaultDayAssetID = "5646d39e-d3d7-6aff-ed71-30fc87d64a92";
        private static readonly UUID _defaultDayAssetUUID = new UUID("5646d39e-d3d7-6aff-ed71-30fc87d64a92");
        //private static string _defaultSkyAssetID = "3ae23978-ac82-bcf3-a9cb-ba6e52dcb9ad";
        private static readonly UUID _defaultSkyAssetUUID = new UUID("3ae23978-ac82-bcf3-a9cb-ba6e52dcb9ad");
        //private static string _defaultWaterAssetID = "59d1a851-47e7-0e5f-1ed7-6b715154f41a";
        private static readonly UUID _defaultWaterAssetUUID = new UUID("59d1a851-47e7-0e5f-1ed7-6b715154f41a");

        private int _regionEnvVersion = -1;

        private double _framets;

        #region INonSharedRegionModule
        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];

            if (null == config)
                return;

            if (config.GetString("Cap_EnvironmentSettings", string.Empty) != "localhost")
            {
                _log.InfoFormat("[{0}]: Module is disabled.", Name);
                return;
            }

            Enabled = true;


            _log.InfoFormat("[{0}]: Module is enabled.", Name);
        }

        public void Close()
        {
        }

        public string Name => "EnvironmentModule";

        public Type ReplaceableInterface => null;

        public void AddRegion(Scene scene)
        {
            if (!Enabled)
                return;

            scene.RegisterModuleInterface<IEnvironmentModule>(this);
            _scene = scene;
            regionID = scene.RegionInfo.RegionID;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!Enabled)
                return;

            _estateModule = scene.RequestModuleInterface<IEstateModule>();
            if (_estateModule == null)
            {
                Enabled = false;
                return;
            }

            _eventQueue = _scene.RequestModuleInterface<IEventQueue>();
            if (_eventQueue == null)
            {
                Enabled = false;
                return;
            }

            _assetService = _scene.AssetService;
            if (_assetService == null)
            {
                Enabled = false;
                return;
            }

            _landChannel = _scene.LandChannel;
            if (_landChannel == null)
            {
                Enabled = false;
                return;
            }

            if (_DefaultEnv == null)
            {
                AssetBase defEnv = _assetService.Get(_defaultDayAssetID);
                if(defEnv != null)
                {
                    byte[] envData = defEnv.Data;
                    try
                    {
                        OSD oenv = OSDParser.Deserialize(envData);
                        _DefaultEnv = new ViewerEnvironment();
                        _DefaultEnv.CycleFromOSD(oenv);
                    }
                    catch ( Exception e)
                    {
                        _DefaultEnv = null;
                        _log.WarnFormat("[Environment {0}]: failed to decode default environment asset: {1}", _scene.Name, e.Message);
                    }
                }
            }
            if (_DefaultEnv == null)
                _DefaultEnv = new ViewerEnvironment();

            string senv = scene.SimulationDataService.LoadRegionEnvironmentSettings(scene.RegionInfo.RegionID);
            if(!string.IsNullOrEmpty(senv))
            {
                try
                {
                    OSD oenv = OSDParser.Deserialize(senv);
                    ViewerEnvironment VEnv = new ViewerEnvironment();
                    if(oenv is OSDArray)
                        VEnv.FromWLOSD(oenv);
                    else
                        VEnv.FromOSD(oenv);
                    scene.RegionEnvironment = VEnv;
                    _regionEnvVersion = VEnv.version;
                }
                catch (Exception e)
                {
                    _log.ErrorFormat("[Environment {0}] failed to load initial Environment {1}", _scene.Name, e.Message);
                    scene.RegionEnvironment = null;
                    _regionEnvVersion = -1;
                }
            }
            else
            {
                scene.RegionEnvironment = null;
                _regionEnvVersion = -1;
            }

            _framets = 0;
            UpdateEnvTime();
            scene.EventManager.OnRegisterCaps += OnRegisterCaps;
            scene.EventManager.OnFrame += UpdateEnvTime;
            scene.EventManager.OnAvatarEnteringNewParcel += OnAvatarEnteringNewParcel;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Enabled)
                return;

            scene.EventManager.OnRegisterCaps -= OnRegisterCaps;
        }
        #endregion

        #region IEnvironmentModule
        public void StoreOnRegion(ViewerEnvironment VEnv)
        {
            try
            {
                if (VEnv == null)
                {
                    _scene.SimulationDataService.RemoveRegionEnvironmentSettings(regionID);
                    _scene.RegionEnvironment = null;
                    _regionEnvVersion = -1;
                }
                else
                {
                    _regionEnvVersion++;
                    VEnv.version = _regionEnvVersion;
                    OSD env = VEnv.ToOSD();
                    //_scene.SimulationDataService.StoreRegionEnvironmentSettings(regionID, OSDParser.SerializeLLSDXmlString(env));
                    _scene.SimulationDataService.StoreRegionEnvironmentSettings(regionID, OSDParser.SerializeLLSDNotationFull(env));
                    _scene.RegionEnvironment = VEnv;
                }
                _framets = 0;
                UpdateEnvTime();
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[Environment {0}] failed to store Environment {1}", _scene.Name, e.Message);
            }
        }

        public void ResetEnvironmentSettings(UUID regionUUID)
        {
            if (!Enabled)
                return;

            StoreOnRegion(null);
            WindlightRefresh(0);
        }

        public void WindlightRefresh(int interpolate, bool forRegion = true)
        {
            List<byte[]> ls = null;
            _scene.ForEachRootScenePresence(delegate (ScenePresence sp)
            {
                if(sp.IsInTransit || sp.IsNPC)
                    return;
                
                IClientAPI client = sp.ControllingClient;

                if (!client.IsActive)
                    return;

                uint vflags = client.GetViewerCaps();

                if ((vflags & 0x8000) != 0)
                {
                    if (forRegion)
                        _estateModule.HandleRegionInfoRequest(client);
                }
                else if ((vflags & 0x4000) != 0)
                    _eventQueue.WindlightRefreshEvent(interpolate, client.AgentId);

                else
                {
                    if (ls == null)
                        ls = MakeLightShareData();
                    SendLightShare(client, ls);
                }
            });
        }

        public void WindlightRefreshForced(IScenePresence isp, int interpolate)
        {
            List<byte[]> ls = null;

            IClientAPI client = isp.ControllingClient;

            if (!client.IsActive)
                return;

            uint vflags = client.GetViewerCaps();

            if ((vflags & 0x8000) != 0)
            {
                ScenePresence sp = isp as ScenePresence;
                ILandObject lo = _scene.LandChannel.GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
                if (lo != null && lo.LandData != null && lo.LandData.Environment != null)
                    lo.SendLandUpdateToClient(client);
                _estateModule.HandleRegionInfoRequest(client);
            }
            else if ((vflags & 0x4000) != 0)
                _eventQueue.WindlightRefreshEvent(interpolate, client.AgentId);
            else
            {
                if (ls == null)
                    ls = MakeLightShareData();
                SendLightShare(client, ls);
            }
        }

        public void FromLightShare(RegionLightShareData ls)
        {
            if (!Enabled)
                return;

            ViewerEnvironment VEnv = new ViewerEnvironment();
            VEnv.FromLightShare(ls);

            StoreOnRegion(VEnv);
            WindlightRefresh(0);
        }

        public RegionLightShareData ToLightShare()
        {
            if (!Enabled)
                return new RegionLightShareData();

            RegionLightShareData ls = null;
            try
            {
                ViewerEnvironment VEnv = _scene.RegionEnvironment;
                if(VEnv == null)
                    return new RegionLightShareData();
                ls = VEnv.ToLightShare();
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[{0}]: Unable to convert environment to lightShare, Exception: {1} - {2}",
                    Name, e.Message, e.StackTrace);
            }
            if(ls == null)
                return new RegionLightShareData();
            return ls;
        }
        #endregion

        #region Events
        private void OnRegisterCaps(UUID agentID, Caps caps)
        {
            // _log.DebugFormat("[{0}]: Register capability for agentID {1} in region {2}",
            //       Name, agentID, caps.RegionName);

            caps.RegisterSimpleHandler("EnvironmentSettings",
                new SimpleStreamHandler("/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                {
                    processEnv(httpRequest, httpResponse, agentID);
                }));

            //Extended
            caps.RegisterSimpleHandler("ExtEnvironment",
                new SimpleStreamHandler("/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                {
                    processExtEnv(httpRequest, httpResponse, agentID, caps);
                }));
        }
        #endregion

        private void processEnv(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            switch (request.HttpMethod)
            {
                case "POST":
                    SetEnvironmentSettings(request, response, agentID);
                    return;
                case "GET":
                    GetEnvironmentSettings(response, agentID);
                    return;
                default:
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    return;
                }
            }
        }

        private void processExtEnv(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Caps caps)
        {
            switch(request.HttpMethod)
            {
                case "PUT":
                case "POST":
                    SetExtEnvironmentSettings(request, response, agentID, caps);
                    return;
                case "GET":
                    GetExtEnvironmentSettings(request, response, agentID);
                    return;
                case "DELETE":
                    DeleteExtEnvironmentSettings(request, response, agentID);
                    return;
                default:
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    return;
                }
            }
        }

        private void DeleteExtEnvironmentSettings(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, UUID agentID)
        {
            int parcel = -1;

            if (httpRequest.Query.Count > 0)
            {
                if (httpRequest.Query.ContainsKey("parcelid"))
                {
                    int.TryParse((string)httpRequest.Query["parcelid"], out parcel);
                }
            }

            if(parcel == -1)
            {
                StoreOnRegion(null);
                WindlightRefresh(0);
            }
            else
            {
                ILandObject land = _scene.LandChannel.GetLandObject(parcel);
                if (land != null && land.LandData != null)
                {
                    land.StoreEnvironment(null);
                    WindlightRefresh(0, false);
                }
            }

            osUTF8 sb = LLSDxmlEncode2.Start();
            LLSDxmlEncode2.AddMap(sb);
            LLSDxmlEncode2.AddElem("messageID", UUID.Zero, sb);
            LLSDxmlEncode2.AddElem("regionID", regionID, sb);
            LLSDxmlEncode2.AddElem("success", true, sb);
            LLSDxmlEncode2.AddEndMap(sb);
            httpResponse.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
        }

        private void GetExtEnvironmentSettings(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, UUID agentID)
        {
            int parcelid = -1;
            if (httpRequest.Query.Count > 0)
            {
                if (httpRequest.Query.ContainsKey("parcelid"))
                {
                    int.TryParse((string)httpRequest.Query["parcelid"], out parcelid);
                }
            }

            ScenePresence sp = _scene.GetScenePresence(agentID);
            if (sp == null)
            {
                httpResponse.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                httpResponse.AddHeader("Retry-After", "5");
                return;
            }

            ViewerEnvironment VEnv = null;
            if (sp.Environment != null)
                VEnv = sp.Environment;
            else if (parcelid == -1)
                VEnv = GetRegionEnvironment();
            else
            {
                if (_scene.RegionInfo.EstateSettings.AllowEnvironmentOverride)
                {
                    ILandObject land = _scene.LandChannel.GetLandObject(parcelid);
                    if(land != null && land.LandData != null && land.LandData.Environment != null)
                        VEnv = land.LandData.Environment;
                }
                if(VEnv == null)
                {
                    OSD def = ViewerEnvironment.DefaultToOSD(regionID, parcelid);
                    httpResponse.RawBuffer = OSDParser.SerializeLLSDXmlToBytes(def);
                    httpResponse.StatusCode = (int)HttpStatusCode.OK;
                    return;
                }
            }

            byte[] envBytes = VEnv.ToCapBytes(regionID, parcelid);
            if(envBytes == null)
            {
                osUTF8 sb = LLSDxmlEncode2.Start();
                LLSDxmlEncode2.AddArray(sb);
                LLSDxmlEncode2.AddMap(sb);
                LLSDxmlEncode2.AddElem("messageID", UUID.Zero, sb);
                LLSDxmlEncode2.AddElem("regionID", regionID, sb);
                LLSDxmlEncode2.AddEndMap(sb);
                LLSDxmlEncode2.AddEndArray(sb);
                httpResponse.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
            }
            else
                httpResponse.RawBuffer = envBytes;

            httpResponse.StatusCode = (int)HttpStatusCode.OK;
        }

        private void SetExtEnvironmentSettings(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, UUID agentID, Caps caps)
        {
            bool success = false;
            string message = "Could not process request";
            int parcel = -1;
            int track = -1;

            osUTF8 sb = LLSDxmlEncode2.Start();

            ScenePresence sp = _scene.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent || sp.IsNPC)
            {
                message = "Could not locate your avatar";
                goto Error;
            }

            if (httpRequest.Query.Count > 0)
            {
                if (httpRequest.Query.ContainsKey("parcelid"))
                {
                    if (!int.TryParse((string)httpRequest.Query["parcelid"], out parcel))
                    {
                        message = "Failed to decode request";
                        goto Error;
                    }
                }
                if (httpRequest.Query.ContainsKey("trackno"))
                {
                    if (!int.TryParse((string)httpRequest.Query["trackno"], out track))
                    {
                        message = "Failed to decode request";
                        goto Error;
                    }
                }
                if (track != -1)
                {
                    message = "Environment Track not supported";
                    goto Error;
                }
            }


            ViewerEnvironment VEnv = _scene.RegionEnvironment;
            ILandObject lchannel;
            if (parcel == -1)
            {
                if (!_scene.Permissions.CanIssueEstateCommand(agentID, false))
                {
                    message = "Insufficient estate permissions, settings has not been saved.";
                    goto Error;
                }
                VEnv = _scene.RegionEnvironment;
                lchannel = null;
            }
            else
            {
                lchannel = _landChannel.GetLandObject(parcel);
                if(lchannel == null || lchannel.LandData == null)
                {
                    message = "Could not locate requested parcel";
                    goto Error;
                }

                if (!_scene.Permissions.CanEditParcelProperties(agentID, lchannel, GroupPowers.AllowEnvironment, true)) // wrong
                {
                    message = "No permission to change parcel environment";
                    goto Error;
                }
                VEnv = lchannel.LandData.Environment;
            }

            try
            {
                OSD req = OSDParser.Deserialize(httpRequest.InputStream);
                if(req is OSDMap)
                {
                    OSDMap map = req as OSDMap;
                    if(map.TryGetValue("environment", out OSD env))
                    {
                        if (VEnv == null)
                            // need a proper clone
                            VEnv = _DefaultEnv.Clone();

                        OSDMap evmap = env as OSDMap;
                        if(evmap.TryGetValue("day_asset", out OSD tmp) && !evmap.ContainsKey("day_cycle"))
                        {
                            string id = tmp.AsString();
                            AssetBase asset = _assetService.Get(id);
                            if(asset == null || asset.Data == null || asset.Data.Length == 0)
                            {
                                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                                return;
                            }
                            try
                            {
                                OSD oenv = OSDParser.Deserialize(asset.Data);
                                evmap.TryGetValue("day_name", out tmp);
                                if(tmp is OSDString)
                                    VEnv.FromAssetOSD(tmp.AsString(), oenv);
                                else
                                    VEnv.FromAssetOSD(null, oenv);
                            }
                            catch
                            {
                                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                                return;
                            }
                        }
                        else
                            VEnv.FromOSD(env);

                        if(lchannel == null)
                        {
                            StoreOnRegion(VEnv);
                            _log.InfoFormat("[{0}]: ExtEnvironment region {1} settings from agentID {2} saved",
                                Name, caps.RegionName, agentID);
                        }
                        else
                        {
                            lchannel.StoreEnvironment(VEnv);
                            _log.InfoFormat("[{0}]: ExtEnvironment parcel {1} of region {2}  settings from agentID {3} saved",
                                Name, parcel, caps.RegionName, agentID);
                        }

                        WindlightRefresh(0, lchannel == null);
                        success = true;
                    }
                }
                else if (req is OSDArray)
                {
                    VEnv = new ViewerEnvironment();
                    VEnv.FromWLOSD(req);
                    StoreOnRegion(VEnv);
                    success = true;

                    WindlightRefresh(0);

                    _log.InfoFormat("[{0}]: ExtEnvironment region {1} settings from agentID {2} saved",
                                                    Name, caps.RegionName, agentID);

                    LLSDxmlEncode2.AddMap(sb);
                    LLSDxmlEncode2.AddElem("messageID", UUID.Zero, sb);
                    LLSDxmlEncode2.AddElem("regionID", regionID, sb);
                    LLSDxmlEncode2.AddElem("success", success, sb);
                    LLSDxmlEncode2.AddEndMap(sb);
                    httpResponse.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
                    httpResponse.StatusCode = (int)HttpStatusCode.OK;
                    return;
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[{0}]: ExtEnvironment settings not saved for region {1}, Exception: {2} - {3}",
                    Name, caps.RegionName, e.Message, e.StackTrace);

                success = false;
                message = string.Format("ExtEnvironment Set for region {0} has failed, settings not saved.", caps.RegionName);
            }

        Error:
            LLSDxmlEncode2.AddMap(sb);
                LLSDxmlEncode2.AddElem("success", success, sb);
                if(!success)
                    LLSDxmlEncode2.AddElem("message", message, sb);
            LLSDxmlEncode2.AddEndMap(sb);

            httpResponse.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
            httpResponse.StatusCode = (int)HttpStatusCode.OK;
        }

        private void GetEnvironmentSettings(IOSHttpResponse response, UUID agentID)
        {
            // _log.DebugFormat("[{0}]: Environment GET handle for agentID {1} in region {2}",
            //      Name, agentID, caps.RegionName);

            ViewerEnvironment VEnv = null;
            ScenePresence sp = _scene.GetScenePresence(agentID);
            if (sp == null)
            {
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.AddHeader("Retry-After", "5");
                return;
            }

            if (sp.Environment != null)
                VEnv = sp.Environment;
            else
            {
                if(_scene.RegionInfo.EstateSettings.AllowEnvironmentOverride)
                {
                    ILandObject land = _scene.LandChannel.GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
                    if (land != null && land.LandData != null && land.LandData.Environment != null)
                        VEnv = land.LandData.Environment;
                }
            }
            if (VEnv == null)
                VEnv = GetRegionEnvironment();

            byte[] envBytes = VEnv.ToCapWLBytes(UUID.Zero, regionID);
            if(envBytes == null)
            {
                osUTF8 sb = LLSDxmlEncode2.Start();
                    LLSDxmlEncode2.AddArray(sb);
                        LLSDxmlEncode2.AddMap(sb);
                            LLSDxmlEncode2.AddElem("messageID", UUID.Zero, sb);
                            LLSDxmlEncode2.AddElem("regionID", regionID, sb);
                        LLSDxmlEncode2.AddEndMap(sb);
                LLSDxmlEncode2.AddEndArray(sb);
                response.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
            }
            else
                response.RawBuffer = envBytes;

            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private void SetEnvironmentSettings(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            // _log.DebugFormat("[{0}]: Environment SET handle from agentID {1} in region {2}",
            //       Name, agentID, caps.RegionName);

            bool success = false;
            string fail_reason = "";

            if (!_scene.Permissions.CanIssueEstateCommand(agentID, false))
            {
                fail_reason = "Insufficient estate permissions, settings has not been saved.";
                goto Error;
            }

            ScenePresence sp = _scene.GetScenePresence(agentID);
            if (sp == null || sp.IsChildAgent || sp.IsNPC)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            if (sp.Environment != null)
            {
                fail_reason = "The environment you see is a forced one. Disable if on control object or tp out and back to region";
                goto Error;
            }

            ILandObject land = _scene.LandChannel.GetLandObject(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);
            if (land != null && land.LandData != null && land.LandData.Environment != null)
            {
                fail_reason = "The parcel where you are has own environment set. You need a updated viewer to change environment";
                goto Error;
            }
            try
            {
                ViewerEnvironment VEnv = new ViewerEnvironment();
                OSD env = OSDParser.Deserialize(request.InputStream);
                VEnv.FromWLOSD(env);

                StoreOnRegion(VEnv);

                WindlightRefresh(0);

                _log.InfoFormat("[{0}]: New Environment settings has been saved from agentID {1} in region {2}",
                    Name, agentID, _scene.Name);
                success = true;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[{0}]: Environment settings has not been saved for region {1}, Exception: {2} - {3}",
                    Name, _scene.Name, e.Message, e.StackTrace);

                success = false;
                fail_reason = string.Format("Environment Set for region {0} has failed, settings not saved.", _scene.Name);
            }

            Error:
            osUTF8 sb = LLSDxmlEncode2.Start();
                LLSDxmlEncode2.AddMap(sb);
                    LLSDxmlEncode2.AddElem("messageID", UUID.Zero, sb);
                    LLSDxmlEncode2.AddElem("regionID", regionID, sb);
                    LLSDxmlEncode2.AddElem("success", success, sb);
                    if(!success)
                        LLSDxmlEncode2.AddElem("fail_reason", fail_reason, sb);
                LLSDxmlEncode2.AddEndMap(sb);
            response.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        public byte[] GetDefaultAssetData(int type)
        {
            OSD osddata;
            switch(type)
            {
                case 0:
                    SkyData sky = new SkyData
                    {
                        Name = "DefaultSky"
                    };
                    osddata = sky.ToOSD();
                    break;
                case 1:
                    WaterData water = new WaterData
                    {
                        Name = "DefaultWater"
                    };
                    osddata = water.ToOSD();
                    break;
                case 2:
                    DayCycle day = new DayCycle
                    {
                        Name = "New Daycycle"
                    };
                    DayCycle.TrackEntry te = new DayCycle.TrackEntry();

                    WaterData dwater = new WaterData
                    {
                        Name = "DefaultWater"
                    };
                    day.waterframes["DefaultWater"] = dwater;
                    te.time = 0;
                    te.frameName = "DefaultWater";
                    day.waterTrack.Add(te);

                    SkyData dsky = new SkyData
                    {
                        Name = "DefaultSky"
                    };
                    day.skyframes["DefaultSky"] = dsky;
                    te.time = 0;
                    te.frameName = "DefaultSky";
                    day.skyTrack0.Add(te);

                    osddata = day.ToOSD();
                    break;
                default:
                    return null;
            }
            return OSDParser.SerializeLLSDNotationToBytes(osddata,true);
        }

        public UUID GetDefaultAsset(int type)
        {
            switch (type)
            {
                case 0:
                    return _defaultSkyAssetUUID;
                case 1:
                    return _defaultWaterAssetUUID;
                case 2:
                    return _defaultDayAssetUUID;
                default:
                    return UUID.Zero;
            }
        }

        public List<byte[]> MakeLightShareData()
        {
            if(_scene.RegionEnvironment == null)
                return null;

            RegionLightShareData wl = ToLightShare();
            byte[] mBlock = new byte[249];
            int pos = 0;

            wl.waterColor.ToBytes(mBlock, 0); pos += 12;
            Utils.FloatToBytes(wl.waterFogDensityExponent).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.underwaterFogModifier).CopyTo(mBlock, pos); pos += 4;
            wl.reflectionWaveletScale.ToBytes(mBlock, pos); pos += 12;
            Utils.FloatToBytes(wl.fresnelScale).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.fresnelOffset).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.refractScaleAbove).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.refractScaleBelow).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.blurMultiplier).CopyTo(mBlock, pos); pos += 4;
            wl.bigWaveDirection.ToBytes(mBlock, pos); pos += 8;
            wl.littleWaveDirection.ToBytes(mBlock, pos); pos += 8;
            wl.normalMapTexture.ToBytes(mBlock, pos); pos += 16;
            wl.horizon.ToBytes(mBlock, pos); pos += 16;
            Utils.FloatToBytes(wl.hazeHorizon).CopyTo(mBlock, pos); pos += 4;
            wl.blueDensity.ToBytes(mBlock, pos); pos += 16;
            Utils.FloatToBytes(wl.hazeDensity).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.densityMultiplier).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.distanceMultiplier).CopyTo(mBlock, pos); pos += 4;
            wl.sunMoonColor.ToBytes(mBlock, pos); pos += 16;
            Utils.FloatToBytes(wl.sunMoonPosition).CopyTo(mBlock, pos); pos += 4;
            wl.ambient.ToBytes(mBlock, pos); pos += 16;
            Utils.FloatToBytes(wl.eastAngle).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.sunGlowFocus).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.sunGlowSize).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.sceneGamma).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.starBrightness).CopyTo(mBlock, pos); pos += 4;
            wl.cloudColor.ToBytes(mBlock, pos); pos += 16;
            wl.cloudXYDensity.ToBytes(mBlock, pos); pos += 12;
            Utils.FloatToBytes(wl.cloudCoverage).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.cloudScale).CopyTo(mBlock, pos); pos += 4;
            wl.cloudDetailXYDensity.ToBytes(mBlock, pos); pos += 12;
            Utils.FloatToBytes(wl.cloudScrollX).CopyTo(mBlock, pos); pos += 4;
            Utils.FloatToBytes(wl.cloudScrollY).CopyTo(mBlock, pos); pos += 4;
            Utils.UInt16ToBytes(wl.maxAltitude).CopyTo(mBlock, pos); pos += 2;
            mBlock[pos] = Convert.ToByte(wl.cloudScrollXLock); pos++;
            mBlock[pos] = Convert.ToByte(wl.cloudScrollYLock); pos++;
            mBlock[pos] = Convert.ToByte(wl.drawClassicClouds); pos++;

            List<byte[]> param = new List<byte[]>();
            param.Add(mBlock);
            return param;
        }

        public void SendLightShare(IClientAPI client, List<byte[]> param)
        {
            if(param == null || param.Count == 0)
                client.SendGenericMessage("WindlightReset", UUID.Random(), new List<byte[]>());
            else
                client.SendGenericMessage("Windlight", UUID.Random(), param);
        }

        private void OnAvatarEnteringNewParcel(ScenePresence sp, int localLandID, UUID regionID)
        {
            if (sp.Environment != null || sp.IsNPC)
                return;

            if (!_scene.RegionInfo.EstateSettings.AllowEnvironmentOverride)
                return;

            IClientAPI client = sp.ControllingClient;
            uint vflags = client.GetViewerCaps();
            if((vflags & 0x8000) != 0)
                return;
             _eventQueue.WindlightRefreshEvent(1, client.AgentId);
        }

        private void UpdateEnvTime()
        {
            double now = Util.GetTimeStamp();
            if (now - _framets < 2.5) // this will be a conf option
                return;

            _framets = now;
            UpdateClientsSunTime();
        }

        private void UpdateClientsSunTime()
        {
            if(_scene.GetNumberOfClients() == 0)
                return;

            //_log.DebugFormat("{0} {1} {2} {3}", dayFrac, eepDayFrac, wldayFrac, Util.UnixTimeSinceEpoch_uS());

            _scene.ForEachRootScenePresence(delegate (ScenePresence sp)
            {
                if(sp.IsDeleted || sp.IsInTransit || sp.IsNPC)
                    return;

                ViewerEnvironment VEnv;
                if(sp.Environment != null)
                    VEnv = sp.Environment;
                else
                    VEnv = GetEnvironment(sp.AbsolutePosition.X, sp.AbsolutePosition.Y);

                float dayFrac = GetDayFractionTime(VEnv);

                IClientAPI client = sp.ControllingClient;
                uint vflags = client.GetViewerCaps();

                if ((vflags & 0x8000) != 0)
                {
                    client.SendViewerTime(Vector3.Zero, dayFrac * Utils.TWO_PI);
                    return;
                }

                if (dayFrac <= 0.25f)
                    dayFrac += 1.5f;
                else if (dayFrac > 0.75f)
                    dayFrac += 0.5f;
                else if (dayFrac >= 0.333333f)
                    dayFrac = 3f * dayFrac - 1f;
                else
                    dayFrac = 3f * dayFrac + 1f;

                dayFrac = Utils.Clamp(dayFrac, 0, 2f);
                dayFrac *= Utils.PI;

                client.SendViewerTime(Vector3.Zero, dayFrac);
            });
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public ViewerEnvironment GetEnvironment(Vector3 pos)
        {
            ILandObject lo = _landChannel.GetLandObject(pos.X, pos.Y);
            if (lo != null && lo.LandData != null && lo.LandData.Environment != null)
                return lo.LandData.Environment;

            return _scene.RegionEnvironment == null ? _DefaultEnv : _scene.RegionEnvironment;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public ViewerEnvironment GetEnvironment(float x, float y)
        {
            ILandObject lo = _landChannel.GetLandObject(x, y);
            if (lo != null && lo.LandData != null && lo.LandData.Environment != null)
                return lo.LandData.Environment;

            return _scene.RegionEnvironment == null ? _DefaultEnv : _scene.RegionEnvironment;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public ViewerEnvironment GetRegionEnvironment()
        {
            return _scene.RegionEnvironment == null ? _DefaultEnv : _scene.RegionEnvironment;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float GetDayFractionTime(ViewerEnvironment env)
        {
            double dayfrac = env.DayLength;
            dayfrac = (Util.UnixTimeSinceEpochSecs() + env.DayOffset) % dayfrac / dayfrac;
            return (float)Utils.Clamp(dayfrac, 0, 1);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public float GetRegionDayFractionTime()
        {
            return GetDayFractionTime(GetRegionEnvironment());
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public int GetDayLength(ViewerEnvironment env)
        {
            return env.DayLength;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public int GetDayOffset(ViewerEnvironment env)
        {
            return env.DayOffset;
        }

        public Vector3 GetSunDir(ViewerEnvironment env, float altitude)
        {
            env.getPositions(altitude, GetDayFractionTime(env), out Vector3 sundir, out Vector3 moondir,
                out Quaternion sunrot, out Quaternion moonrot);
            return sundir;
        }

        public Quaternion GetSunRot(ViewerEnvironment env, float altitude)
        {
            env.getPositions(altitude, GetDayFractionTime(env), out Vector3 sundir, out Vector3 moondir,
                out Quaternion sunrot, out Quaternion moonrot);
            return sunrot;
        }

        public Vector3 GetMoonDir(ViewerEnvironment env, float altitude)
        {
            env.getPositions(altitude, GetDayFractionTime(env), out Vector3 sundir, out Vector3 moondir,
                out Quaternion sunrot, out Quaternion moonrot);
            return moondir;
        }

        public Quaternion GetMoonRot(ViewerEnvironment env, float altitude)
        {
            env.getPositions(altitude, GetDayFractionTime(env), out Vector3 sundir, out Vector3 moondir,
                out Quaternion sunrot, out Quaternion moonrot);
            return moonrot;
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetRegionDayLength()
        {
            return GetRegionEnvironment().DayLength;
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetRegionDayOffset()
        {
            return GetRegionEnvironment().DayOffset;
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetRegionSunDir(float altitude)
        {
            return GetSunDir(GetRegionEnvironment(), altitude);
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion GetRegionSunRot(float altitude)
        {
            return GetSunRot(GetRegionEnvironment(), altitude);
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetRegionMoonDir(float altitude)
        {
            return GetMoonDir(GetRegionEnvironment(), altitude);
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion GetRegionMoonRot(float altitude)
        {
            return GetMoonRot(GetRegionEnvironment(), altitude);
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetDayLength(Vector3 pos)
        {
            return GetEnvironment(pos.X, pos.Y).DayLength;
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetDayOffset(Vector3 pos)
        {
            return GetEnvironment(pos.X, pos.Y).DayOffset;
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetSunDir(Vector3 pos)
        {
            return GetSunDir(GetEnvironment(pos.X, pos.Y), pos.Z);
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion GetSunRot(Vector3 pos)
        {
            return GetSunRot(GetEnvironment(pos.X, pos.Y), pos.Z);
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetMoonDir(Vector3 pos)
        {
            return GetMoonDir(GetEnvironment(pos.X, pos.Y), pos.Z);
        }

        [System.Runtime.CompilerServices.MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion GetMoonRot(Vector3 pos)
        {
            return GetMoonRot(GetEnvironment(pos.X, pos.Y), pos.Z);
        }

    }
}

