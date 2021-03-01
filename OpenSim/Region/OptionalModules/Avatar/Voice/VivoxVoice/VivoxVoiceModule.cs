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

using System;
using System.IO;
using System.Net;
using System.Xml;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using OpenMetaverse;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;

using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;
using OpenMetaverse.StructuredData;

namespace OpenSim.Region.OptionalModules.Avatar.Voice.VivoxVoice
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "VivoxVoiceModule")]
    public class VivoxVoiceModule : ISharedRegionModule
    {

        // channel distance model values
        public const int CHAN_DIST_NONE     = 0; // no attenuation
        public const int CHAN_DIST_INVERSE  = 1; // inverse distance attenuation
        public const int CHAN_DIST_LINEAR   = 2; // linear attenuation
        public const int CHAN_DIST_EXPONENT = 3; // exponential attenuation
        public const int CHAN_DIST_DEFAULT  = CHAN_DIST_LINEAR;

        // channel type values
        public static readonly string CHAN_TYPE_POSITIONAL   = "positional";
        public static readonly string CHAN_TYPE_CHANNEL      = "channel";
        public static readonly string CHAN_TYPE_DEFAULT      = CHAN_TYPE_POSITIONAL;

        // channel mode values
        public static readonly string CHAN_MODE_OPEN         = "open";
        public static readonly string CHAN_MODE_LECTURE      = "lecture";
        public static readonly string CHAN_MODE_PRESENTATION = "presentation";
        public static readonly string CHAN_MODE_AUDITORIUM   = "auditorium";
        public static readonly string CHAN_MODE_DEFAULT      = CHAN_MODE_OPEN;

        // unconstrained default values
        public const double CHAN_ROLL_OFF_DEFAULT            = 2.0;  // rate of attenuation
        public const double CHAN_ROLL_OFF_MIN                = 1.0;
        public const double CHAN_ROLL_OFF_MAX                = 4.0;
        public const int    CHAN_MAX_RANGE_DEFAULT           = 60;   // distance at which channel is silent
        public const int    CHAN_MAX_RANGE_MIN               = 0;
        public const int    CHAN_MAX_RANGE_MAX               = 160;
        public const int    CHAN_CLAMPING_DISTANCE_DEFAULT   = 10;   // distance before attenuation applies
        public const int    CHAN_CLAMPING_DISTANCE_MIN       = 0;
        public const int    CHAN_CLAMPING_DISTANCE_MAX       = 160;

        // Infrastructure
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly object vlock  = new object();

        // Control info, e.g. vivox server, admin user, admin password
        private static bool   _pluginEnabled  = false;
        private static bool   _adminConnected = false;

        private static string _vivoxServer;
        private static string _vivoxSipUri;
        private static string _vivoxVoiceAccountApi;
        private static string _vivoxAdminUser;
        private static string _vivoxAdminPassword;
        private static string _authToken = string.Empty;

        private static int    _vivoxChannelDistanceModel;
        private static double _vivoxChannelRollOff;
        private static int    _vivoxChannelMaximumRange;
        private static string _vivoxChannelMode;
        private static string _vivoxChannelType;
        private static int    _vivoxChannelClampingDistance;

        private static readonly Dictionary<string,string> _parents = new Dictionary<string,string>();
        private static bool _dumpXml;

        private IConfig _config;

        private object _Lock;

        public void Initialise(IConfigSource config)
        {
            MainConsole.Instance.Commands.AddCommand("vivox", false, "vivox debug", "vivox debug <on>|<off>", "Set vivox debugging", HandleDebug);

            _config = config.Configs["VivoxVoice"];

            if (null == _config)
                return;

            if (!_config.GetBoolean("enabled", false))
                return;

            _Lock = new object();

            try
            {
                // retrieve configuration variables
                _vivoxServer = _config.GetString("vivox_server", string.Empty);
                _vivoxSipUri = _config.GetString("vivox_sip_uri", string.Empty);
                _vivoxAdminUser = _config.GetString("vivox_admin_user", string.Empty);
                _vivoxAdminPassword = _config.GetString("vivox_admin_password", string.Empty);

                _vivoxChannelDistanceModel = _config.GetInt("vivox_channel_distance_model", CHAN_DIST_DEFAULT);
                _vivoxChannelRollOff = _config.GetDouble("vivox_channel_roll_off", CHAN_ROLL_OFF_DEFAULT);
                _vivoxChannelMaximumRange = _config.GetInt("vivox_channel_max_range", CHAN_MAX_RANGE_DEFAULT);
                _vivoxChannelMode = _config.GetString("vivox_channel_mode", CHAN_MODE_DEFAULT).ToLower();
                _vivoxChannelType = _config.GetString("vivox_channel_type", CHAN_TYPE_DEFAULT).ToLower();
                _vivoxChannelClampingDistance = _config.GetInt("vivox_channel_clamping_distance",
                                                                              CHAN_CLAMPING_DISTANCE_DEFAULT);
                _dumpXml = _config.GetBoolean("dump_xml", false);

                // Validate against constraints and default if necessary
                if (_vivoxChannelRollOff < CHAN_ROLL_OFF_MIN || _vivoxChannelRollOff > CHAN_ROLL_OFF_MAX)
                {
                    _log.WarnFormat("[VivoxVoice] Invalid value for roll off ({0}), reset to {1}.",
                                              _vivoxChannelRollOff, CHAN_ROLL_OFF_DEFAULT);
                    _vivoxChannelRollOff = CHAN_ROLL_OFF_DEFAULT;
                }

                if (_vivoxChannelMaximumRange < CHAN_MAX_RANGE_MIN || _vivoxChannelMaximumRange > CHAN_MAX_RANGE_MAX)
                {
                    _log.WarnFormat("[VivoxVoice] Invalid value for maximum range ({0}), reset to {1}.",
                                              _vivoxChannelMaximumRange, CHAN_MAX_RANGE_DEFAULT);
                    _vivoxChannelMaximumRange = CHAN_MAX_RANGE_DEFAULT;
                }

                if (_vivoxChannelClampingDistance < CHAN_CLAMPING_DISTANCE_MIN ||
                                            _vivoxChannelClampingDistance > CHAN_CLAMPING_DISTANCE_MAX)
                {
                    _log.WarnFormat("[VivoxVoice] Invalid value for clamping distance ({0}), reset to {1}.",
                                              _vivoxChannelClampingDistance, CHAN_CLAMPING_DISTANCE_DEFAULT);
                    _vivoxChannelClampingDistance = CHAN_CLAMPING_DISTANCE_DEFAULT;
                }

                switch (_vivoxChannelMode)
                {
                    case "open" : break;
                    case "lecture" : break;
                    case "presentation" : break;
                    case "auditorium" : break;
                    default :
                        _log.WarnFormat("[VivoxVoice] Invalid value for channel mode ({0}), reset to {1}.",
                                                  _vivoxChannelMode, CHAN_MODE_DEFAULT);
                        _vivoxChannelMode = CHAN_MODE_DEFAULT;
                        break;
                }

                switch (_vivoxChannelType)
                {
                    case "positional" : break;
                    case "channel" : break;
                    default :
                        _log.WarnFormat("[VivoxVoice] Invalid value for channel type ({0}), reset to {1}.",
                                                  _vivoxChannelType, CHAN_TYPE_DEFAULT);
                        _vivoxChannelType = CHAN_TYPE_DEFAULT;
                        break;
                }


                // Admin interface required values
                if (string.IsNullOrEmpty(_vivoxServer) ||
                    string.IsNullOrEmpty(_vivoxSipUri) ||
                    string.IsNullOrEmpty(_vivoxAdminUser) ||
                    string.IsNullOrEmpty(_vivoxAdminPassword))
                {
                    _log.Error("[VivoxVoice] plugin mis-configured");
                    _log.Info("[VivoxVoice] plugin disabled: incomplete configuration");
                    return;
                }

                //_vivoxVoiceAccountApi = String.Format("https://{0}:443/api2", _vivoxServer);
                _vivoxVoiceAccountApi = string.Format("http://{0}/api2", _vivoxServer); // fs <6.3 seems to not like https here
                if (!Uri.TryCreate(_vivoxVoiceAccountApi, UriKind.Absolute, out Uri accoutURI))
                {
                    _log.Error("[VivoxVoice] invalid vivox server");
                    return;
                }

                if (!Uri.TryCreate("http://" + _vivoxSipUri, UriKind.Absolute, out Uri spiURI))
                {
                    _log.Error("[VivoxVoice] invalid vivox sip server");
                    return;
                }

                _log.InfoFormat("[VivoxVoice] using vivox server {0}", _vivoxServer);

                // Get admin rights and cleanup any residual channel definition

                DoAdminLogin();

                _pluginEnabled = true;

                _log.Info("[VivoxVoice] plugin enabled");
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[VivoxVoice] plugin initialization failed: {0}", e.Message);
                _log.DebugFormat("[VivoxVoice] plugin initialization failed: {0}", e.ToString());
                return;
            }
        }

        public void AddRegion(Scene scene)
        {
            if (_pluginEnabled)
            {
                lock (vlock)
                {
                    string channelId = string.Empty;

                    string sceneUUID  = scene.RegionInfo.RegionID.ToString();
                    string sceneName  = scene.RegionInfo.RegionName;

                    // Make sure that all local channels are deleted.
                    // So we have to search for the children, and then do an
                    // iteration over the set of chidren identified.
                    // This assumes that there is just one directory per
                    // region.

                    /* this is not working, can not fix without api spec that vivox is refusing 

                    if (VivoxTryGetDirectory(sceneUUID + "D", out channelId))
                    {
                        _log.DebugFormat("[VivoxVoice]: region {0}: uuid {1}: located directory id {2}",
                                          sceneName, sceneUUID, channelId);

                        XmlElement children = VivoxListChildren(channelId);
                        string count;

                        if (XmlFind(children, "response.level0.channel-search.count", out count))
                        {
                            int cnum = Convert.ToInt32(count);
                            for (int i = 0; i < cnum; i++)
                            {
                                string id;
                                if (XmlFind(children, "response.level0.channel-search.channels.channels.level4.id", i, out id))
                                {
                                    if (!IsOK(VivoxDeleteChannel(channelId, id)))
                                        _log.WarnFormat("[VivoxVoice] Channel delete failed {0}:{1}:{2}", i, channelId, id);
                                }
                            }
                        }
                    }
                    else
                    {
                        if (!VivoxTryCreateDirectory(sceneUUID + "D", sceneName, out channelId))
                        {
                            _log.WarnFormat("[VivoxVoice] Create failed <{0}:{1}:{2}>",
                                             "*", sceneUUID, sceneName);
                            channelId = String.Empty;
                        }
                    }
                    */

                    // Create a dictionary entry unconditionally. This eliminates the
                    // need to check for a parent in the core code. The end result is
                    // the same, if the parent table entry is an empty string, then
                    // region channels will be created as first-level channels.
                    lock (_parents)
                    {
                        if (_parents.ContainsKey(sceneUUID))
                        {
                            RemoveRegion(scene);
                            _parents.Add(sceneUUID, channelId);
                        }
                        else
                        {
                            _parents.Add(sceneUUID, channelId);
                        }
                    }
                }

                // we need to capture scene in an anonymous method
                // here as we need it later in the callbacks
                scene.EventManager.OnRegisterCaps += delegate(UUID agentID, Caps caps)
                    {
                        OnRegisterCaps(scene, agentID, caps);
                    };
            }
        }

        public void RegionLoaded(Scene scene)
        {
            // Do nothing.
        }

        public void RemoveRegion(Scene scene)
        {
            if (_pluginEnabled)
            {
                lock (vlock)
                {
                    string channelId;

                    string sceneUUID  = scene.RegionInfo.RegionID.ToString();
                    string sceneName  = scene.RegionInfo.RegionName;

                    // Make sure that all local channels are deleted.
                    // So we have to search for the children, and then do an
                    // iteration over the set of chidren identified.
                    // This assumes that there is just one directory per
                    // region.
                    if (VivoxTryGetDirectory(sceneUUID + "D", out channelId))
                    {
                        _log.DebugFormat("[VivoxVoice]: region {0}: uuid {1}: located directory id {2}",
                                          sceneName, sceneUUID, channelId);

                        XmlElement children = VivoxListChildren(channelId);
                        string count;

                        if (XmlFind(children, "response.level0.channel-search.count", out count))
                        {
                            int cnum = Convert.ToInt32(count);
                            for (int i = 0; i < cnum; i++)
                            {
                                string id;
                                if (XmlFind(children, "response.level0.channel-search.channels.channels.level4.id", i, out id))
                                {
                                    if (!IsOK(VivoxDeleteChannel(channelId, id)))
                                        _log.WarnFormat("[VivoxVoice] Channel delete failed {0}:{1}:{2}", i, channelId, id);
                                }
                            }
                        }
                        if (!IsOK(VivoxDeleteChannel(null, channelId)))
                            _log.WarnFormat("[VivoxVoice] Parent channel delete failed {0}:{1}:{2}", sceneName, sceneUUID, channelId);
                    }

                    // Remove the channel umbrella entry

                    lock (_parents)
                    {
                        if (_parents.ContainsKey(sceneUUID))
                        {
                            _parents.Remove(sceneUUID);
                        }
                    }
                }
            }
        }

        public void PostInitialise()
        {
            // Do nothing.
        }

        public void Close()
        {
            if (_pluginEnabled)
                VivoxLogout();
        }

        public Type ReplaceableInterface => null;

        public string Name => "VivoxVoiceModule";

        public bool IsSharedModule => true;

        // <summary>
        // OnRegisterCaps is invoked via the scene.EventManager
        // everytime OpenSim hands out capabilities to a client
        // (login, region crossing). We contribute two capabilities to
        // the set of capabilities handed back to the client:
        // ProvisionVoiceAccountRequest and ParcelVoiceInfoRequest.
        //
        // ProvisionVoiceAccountRequest allows the client to obtain
        // the voice account credentials for the avatar it is
        // controlling (e.g., user name, password, etc).
        //
        // ParcelVoiceInfoRequest is invoked whenever the client
        // changes from one region or parcel to another.
        //
        // Note that OnRegisterCaps is called here via a closure
        // delegate containing the scene of the respective region (see
        // Initialise()).
        // </summary>
        public void OnRegisterCaps(Scene scene, UUID agentID, Caps caps)
        {
            _log.DebugFormat("[VivoxVoice] OnRegisterCaps: agentID {0} caps {1}", agentID, caps);

            caps.RegisterSimpleHandler("ProvisionVoiceAccountRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                    {
                        ProvisionVoiceAccountRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            caps.RegisterSimpleHandler("ParcelVoiceInfoRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                    {
                        ParcelVoiceInfoRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            //caps.RegisterSimpleHandler("ChatSessionRequest",
            //      new SimpleStreamHandler("/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
            //      {
            //          ChatSessionRequest(httpRequest, httpResponse, agentID, scene);
            //      }));
        }

        /// <summary>
        /// Callback for a client request for Voice Account Details
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ProvisionVoiceAccountRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if(request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;
            try
            {
                ScenePresence avatar = null;
                string        avatarName = null;

                if (scene == null)
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }

                avatar = scene.GetScenePresence(agentID);
                int nretries = 10;
                while (avatar == null && nretries-- > 0)
                {
                    Thread.Sleep(100);
                    avatar = scene.GetScenePresence(agentID);
                }

                if(avatar == null)
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }

                avatarName = avatar.Name;

                _log.DebugFormat("[VivoxVoice][PROVISIONVOICE]: scene = {0}, agentID = {1}", scene.Name, agentID);
//                    _log.DebugFormat("[VivoxVoice][PROVISIONVOICE]: request: {0}, path: {1}, param: {2}",
//                                      request, path, param);

                XmlElement    resp;
                bool          retry = false;
                string        agentname = "x" + Convert.ToBase64String(agentID.GetBytes());
                string        password  = new UUID(Guid.NewGuid()).ToString().Replace('-','Z').Substring(0,16);
                string        code = string.Empty;

                agentname = agentname.Replace('+', '-').Replace('/', '_');

                do
                {
                    resp = VivoxGetAccountInfo(agentname);

                    if (XmlFind(resp, "response.level0.status", out code))
                    {
                        if (code != "OK")
                        {
                            if (XmlFind(resp, "response.level0.body.code", out code))
                            {
                                // If the request was recognized, then this should be set to something
                                switch (code)
                                {
                                    case "201" : // Account expired
                                        _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Get account information failed : expired credentials",
                                                          avatarName);
                                        _adminConnected = false;
                                        retry = DoAdminLogin();
                                        break;

                                    case "202" : // Missing credentials
                                        _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Get account information failed : missing credentials",
                                                          avatarName);
                                        break;

                                    case "212" : // Not authorized
                                        _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Get account information failed : not authorized",
                                                          avatarName);
                                        break;

                                    case "300" : // Required parameter missing
                                        _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Get account information failed : parameter missing",
                                                          avatarName);
                                        break;

                                    case "403" : // Account does not exist
                                        resp = VivoxCreateAccount(agentname,password);
                                        // Note: This REALLY MUST BE status. Create Account does not return code.
                                        if (XmlFind(resp, "response.level0.status", out code))
                                        {
                                            switch (code)
                                            {
                                                case "201" : // Account expired
                                                    _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Create account information failed : expired credentials",
                                                                      avatarName);
                                                    _adminConnected = false;
                                                    retry = DoAdminLogin();
                                                    break;

                                                case "202" : // Missing credentials
                                                    _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Create account information failed : missing credentials",
                                                                      avatarName);
                                                    break;

                                                case "212" : // Not authorized
                                                    _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Create account information failed : not authorized",
                                                                      avatarName);
                                                    break;

                                                case "300" : // Required parameter missing
                                                    _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Create account information failed : parameter missing",
                                                                      avatarName);
                                                    break;

                                                case "400" : // Create failed
                                                    _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Create account information failed : create failed",
                                                                      avatarName);
                                                    break;
                                            }
                                        }
                                        break;

                                    case "404" : // Failed to retrieve account
                                        _log.ErrorFormat("[VivoxVoice]: avatar \"{0}\": Get account information failed : retrieve failed");
                                        // [AMW] Sleep and retry for a fixed period? Or just abandon?
                                        break;
                                }
                            }
                        }
                    }
                }
                while (retry);

                if (code != "OK")
                {
                    _log.DebugFormat("[VivoxVoice][PROVISIONVOICE]: Get Account Request failed for \"{0}\"", avatarName);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }

                // Unconditionally change the password on each request
                VivoxPassword(agentname, password);

                // fast foward encode
                osUTF8 lsl = LLSDxmlEncode2.Start();
                LLSDxmlEncode2.AddMap(lsl);
                LLSDxmlEncode2.AddElem("username", agentname, lsl);
                LLSDxmlEncode2.AddElem("password", password, lsl);
                LLSDxmlEncode2.AddElem("voice_sip_uri_hostname", _vivoxSipUri, lsl);
                LLSDxmlEncode2.AddElem("voice_account_server_name", _vivoxVoiceAccountApi, lsl);
                LLSDxmlEncode2.AddEndMap(lsl);

                response.RawBuffer = LLSDxmlEncode2.EndToBytes(lsl);
                return;
            }
            catch (Exception e)
            {
                _log.DebugFormat("[VivoxVoice][PROVISIONVOICE]: : {0} failed", e.ToString());
            }
            response.RawBuffer = osUTF8.GetASCIIBytes("<llsd><undef /></llsd>");
        }

        /// <summary>
        /// Callback for a client request for ParcelVoiceInfo
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ParcelVoiceInfoRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;

            ScenePresence avatar = scene.GetScenePresence(agentID);
            if(avatar == null)
            {
                response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                return;
            }

            string avatarName = avatar.Name;

            // - check whether we have a region channel in our cache
            // - if not:
            //       create it and cache it
            // - send it to the client
            // - send channel_uri: as "sip:regionID@_sipDomain"
            try
            {
                string channel_uri;

                if (scene.LandChannel == null)
                {
                    _log.ErrorFormat("region \"{0}\": avatar \"{1}\": land data not yet available",
                                                      scene.RegionInfo.RegionName, avatarName);
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }

                // get channel_uri: check first whether estate
                // settings allow voice, then whether parcel allows
                // voice, if all do retrieve or obtain the parcel
                // voice channel
                LandData land = scene.GetLandData(avatar.AbsolutePosition);
                if (land == null)
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }

                // _log.DebugFormat("[VivoxVoice][PARCELVOICE]: region \"{0}\": Parcel \"{1}\" ({2}): avatar \"{3}\": request: {4}, path: {5}, param: {6}",
                //     scene.RegionInfo.RegionName, land.Name, land.LocalID, avatarName, request, path, param);
                // _log.DebugFormat("[VivoxVoice][PARCELVOICE]: avatar \"{0}\": location: {1} {2} {3}",
                //                   avatarName, avatar.AbsolutePosition.X, avatar.AbsolutePosition.Y, avatar.AbsolutePosition.Z);

                if (!scene.RegionInfo.EstateSettings.AllowVoice)
                {
                    //_log.DebugFormat("[VivoxVoice][PARCELVOICE]: region \"{0}\": voice not enabled in estate settings",
                    //                  scene.RegionInfo.RegionName);
                    channel_uri = string.Empty;
                }
                else if (!scene.RegionInfo.EstateSettings.TaxFree && (land.Flags & (uint)ParcelFlags.AllowVoiceChat) == 0)
                {
                    //_log.DebugFormat("[VivoxVoice][PARCELVOICE]: region \"{0}\": Parcel \"{1}\" ({2}): avatar \"{3}\": voice not enabled for parcel",
                    //                  scene.RegionInfo.RegionName, land.Name, land.LocalID, avatarName);
                    channel_uri = string.Empty;
                }
                else
                {
                    channel_uri = RegionGetOrCreateChannel(scene, land);
                }

                // _log.DebugFormat("[VivoxVoice][PARCELVOICE]: region \"{0}\": Parcel \"{1}\" ({2}): avatar \"{3}\": {4}",
                //      scene.RegionInfo.RegionName, land.Name, land.LocalID, avatarName, r);
                // fast foward encode
                osUTF8 lsl = LLSDxmlEncode2.Start();
                LLSDxmlEncode2.AddMap(lsl);
                LLSDxmlEncode2.AddElem("parcel_local_id", land.LocalID, lsl);
                LLSDxmlEncode2.AddElem("region_name", scene.Name, lsl);
                LLSDxmlEncode2.AddMap("voice_credentials",lsl);
                LLSDxmlEncode2.AddElem("channel_uri", channel_uri, lsl);
                //LLSDxmlEncode2.AddElem("channel_credentials", channel_credentials, lsl);
                LLSDxmlEncode2.AddEndMap(lsl);
                LLSDxmlEncode2.AddEndMap(lsl);

                response.RawBuffer = LLSDxmlEncode2.EndToBytes(lsl);
                return;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[VivoxVoice][PARCELVOICE]: region \"{0}\": avatar \"{1}\": {2}, retry later",
                                  scene.RegionInfo.RegionName, avatarName, e.Message);
            }
            response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
        }

        /// <summary>
        /// Callback for a client request for a private chat channel
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ChatSessionRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            //            ScenePresence avatar = scene.GetScenePresence(agentID);
            //            string        avatarName = avatar.Name;

            //            _log.DebugFormat("[VivoxVoice][CHATSESSION]: avatar \"{0}\": request: {1}, path: {2}, param: {3}",
            //                              avatarName, request, path, param);
            response.RawBuffer = Util.UTF8.GetBytes("<llsd>true</llsd>");
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private string RegionGetOrCreateChannel(Scene scene, LandData land)
        {
            string channelUri = null;
            string channelId = null;

            string landUUID;
            string landName;
            string parentId;

            lock (_parents)
                parentId = _parents[scene.RegionInfo.RegionID.ToString()];

            // Create parcel voice channel. If no parcel exists, then the voice channel ID is the same
            // as the directory ID. Otherwise, it reflects the parcel's ID.
            if ((land.Flags & (uint)ParcelFlags.UseEstateVoiceChan) == 0)
            {
                landName = string.Format("{0}:{1}", scene.RegionInfo.RegionName, land.Name);
                landUUID = land.GlobalID.ToString();
                _log.DebugFormat("[VivoxVoice]: Region:Parcel \"{0}\": parcel id {1}: using channel name {2}",
                                  landName, land.LocalID, landUUID);
            }
            else
            {
                landName = string.Format("{0}:{1}", scene.RegionInfo.RegionName, scene.RegionInfo.RegionName);
                landUUID = scene.RegionInfo.RegionID.ToString();
                _log.DebugFormat("[VivoxVoice]: Region:Parcel \"{0}\": parcel id {1}: using channel name {2}",
                                  landName, land.LocalID, landUUID);
            }

            lock (vlock)
            {
                // Added by Adam to help debug channel not availible errors.
                if (VivoxTryGetChannel(parentId, landUUID, out channelId, out channelUri))
                    _log.DebugFormat("[VivoxVoice] Found existing channel at " + channelUri);
                else if (VivoxTryCreateChannel(parentId, landUUID, landName, out channelUri))
                    _log.DebugFormat("[VivoxVoice] Created new channel at " + channelUri);
                else
                    throw new Exception("vivox channel uri not available");

                _log.DebugFormat("[VivoxVoice]: Region:Parcel \"{0}\": parent channel id {1}: retrieved parcel channel_uri {2} ",
                                  landName, parentId, channelUri);
            }

            return channelUri;
        }

        private static readonly string _vivoxLoginPath = "https://{0}/api2/viv_signin.php?userid={1}&pwd={2}";

        /// <summary>
        /// Perform administrative login for Vivox.
        /// Returns a hash table containing values returned from the request.
        /// </summary>
        private XmlElement VivoxLogin(string name, string password)
        {
            string requrl = string.Format(_vivoxLoginPath, _vivoxServer, name, password);
            return VivoxCall(requrl, false);
        }

        private static readonly string _vivoxLogoutPath = "https://{0}/api2/viv_signout.php?auth_token={1}";

        /// <summary>
        /// Perform administrative logout for Vivox.
        /// </summary>
        private XmlElement VivoxLogout()
        {
            string requrl = string.Format(_vivoxLogoutPath, _vivoxServer, _authToken);
            return VivoxCall(requrl, false);
        }


        private static readonly string _vivoxGetAccountPath = "https://{0}/api2/viv_get_acct.php?auth_token={1}&user_name={2}";

        /// <summary>
        /// Retrieve account information for the specified user.
        /// Returns a hash table containing values returned from the request.
        /// </summary>
        private XmlElement VivoxGetAccountInfo(string user)
        {
            string requrl = string.Format(_vivoxGetAccountPath, _vivoxServer, _authToken, user);
            return VivoxCall(requrl, true);
        }


        private static readonly string _vivoxNewAccountPath = "https://{0}/api2/viv_ad_acct_new.php?username={1}&pwd={2}&auth_token={3}";

        /// <summary>
        /// Creates a new account.
        /// For now we supply the minimum set of values, which
        /// is user name and password. We *can* supply a lot more
        /// demographic data.
        /// </summary>
        private XmlElement VivoxCreateAccount(string user, string password)
        {
            string requrl = string.Format(_vivoxNewAccountPath, _vivoxServer, user, password, _authToken);
            return VivoxCall(requrl, true);
        }


        private static readonly string _vivoxPasswordPath = "https://{0}/api2/viv_ad_password.php?user_name={1}&new_pwd={2}&auth_token={3}";

        /// <summary>
        /// Change the user's password.
        /// </summary>
        private XmlElement VivoxPassword(string user, string password)
        {
            string requrl = string.Format(_vivoxPasswordPath, _vivoxServer, user, password, _authToken);
            return VivoxCall(requrl, true);
        }


        private static readonly string _vivoxChannelPath = "https://{0}/api2/viv_chan_mod.php?mode={1}&chan_name={2}&auth_token={3}";

        /// <summary>
        /// Create a channel.
        /// Once again, there a multitude of options possible. In the simplest case
        /// we specify only the name and get a non-persistent cannel in return. Non
        /// persistent means that the channel gets deleted if no-one uses it for
        /// 5 hours. To accomodate future requirements, it may be a good idea to
        /// initially create channels under the umbrella of a parent ID based upon
        /// the region name. That way we have a context for side channels, if those
        /// are required in a later phase.
        ///
        /// In this case the call handles parent and description as optional values.
        /// </summary>
        private bool VivoxTryCreateChannel(string parent, string channelId, string description, out string channelUri)
        {
            string requrl = string.Format(_vivoxChannelPath, _vivoxServer, "create", channelId, _authToken);

            if (!string.IsNullOrEmpty(parent))
            {
                requrl = string.Format("{0}&chan_parent={1}", requrl, parent);
            }
            if (!string.IsNullOrEmpty(description))
            {
                requrl = string.Format("{0}&chan_desc={1}", requrl, description);
            }

            requrl = string.Format("{0}&chan_type={1}",              requrl, _vivoxChannelType);
            requrl = string.Format("{0}&chan_mode={1}",              requrl, _vivoxChannelMode);
            requrl = string.Format("{0}&chan_roll_off={1}",          requrl, _vivoxChannelRollOff);
            requrl = string.Format("{0}&chan_dist_model={1}",        requrl, _vivoxChannelDistanceModel);
            requrl = string.Format("{0}&chan_max_range={1}",         requrl, _vivoxChannelMaximumRange);
            requrl = string.Format("{0}&chan_clamping_distance={1}", requrl, _vivoxChannelClampingDistance);

            XmlElement resp = VivoxCall(requrl, true);
            if (XmlFind(resp, "response.level0.body.chan_uri", out channelUri))
                return true;

            channelUri = string.Empty;
            return false;
        }

        /// <summary>
        /// Create a directory.
        /// Create a channel with an unconditional type of "dir" (indicating directory).
        /// This is used to create an arbitrary name tree for partitioning of the
        /// channel name space.
        /// The parent and description are optional values.
        /// </summary>
        private bool VivoxTryCreateDirectory(string dirId, string description, out string channelId)
        {
            /* this is not working, and can not fix without api spec, that vivox is refusing me

            string requrl = String.Format(_vivoxChannelPath, _vivoxServer, "create", dirId, _authToken);

            // if (parent != null && parent != String.Empty)
            // {
            //     requrl = String.Format("{0}&chan_parent={1}", requrl, parent);
            // }

            if (!string.IsNullOrEmpty(description))
            {
                requrl = String.Format("{0}&chan_desc={1}", requrl, description);
            }
            requrl = String.Format("{0}&chan_type={1}", requrl, "dir");

            XmlElement resp = VivoxCall(requrl, true);
            if (IsOK(resp) && XmlFind(resp, "response.level0.body.chan_id", out channelId))
                return true;
            */
            channelId = string.Empty;
            return false;
        }

        private static readonly string _vivoxChannelSearchPath = "https://{0}/api2/viv_chan_search.php?cond_channame={1}&auth_token={2}";

        /// <summary>
        /// Retrieve a channel.
        /// Once again, there a multitude of options possible. In the simplest case
        /// we specify only the name and get a non-persistent cannel in return. Non
        /// persistent means that the channel gets deleted if no-one uses it for
        /// 5 hours. To accomodate future requirements, it may be a good idea to
        /// initially create channels under the umbrella of a parent ID based upon
        /// the region name. That way we have a context for side channels, if those
        /// are required in a later phase.
        /// In this case the call handles parent and description as optional values.
        /// </summary>
        private bool VivoxTryGetChannel(string channelParent, string channelName,
                                        out string channelId, out string channelUri)
        {
            string count;

            string requrl = string.Format(_vivoxChannelSearchPath, _vivoxServer, channelName, _authToken);
            XmlElement resp = VivoxCall(requrl, true);

            if (XmlFind(resp, "response.level0.channel-search.count", out count))
            {
                int channels = Convert.ToInt32(count);

                // Bug in Vivox Server r2978 where count returns 0
                // Found by Adam
                if (channels == 0)
                {
                    for (int j=0;j<100;j++)
                    {
                        string tmpId;
                        if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.id", j, out tmpId))
                            break;

                        channels = j + 1;
                    }
                }

                for (int i = 0; i < channels; i++)
                {
                    string name;
                    string id;
                    string type;
                    string uri;
                    string parent;

                    // skip if not a channel
                    if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.type", i, out type) ||
                        type != "channel" && type != "positional_M")
                    {
                        _log.Debug("[VivoxVoice] Skipping Channel " + i + " as it's not a channel.");
                        continue;
                    }

                    // skip if not the name we are looking for
                    if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.name", i, out name) ||
                        name != channelName)
                    {
                        _log.Debug("[VivoxVoice] Skipping Channel " + i + " as it has no name.");
                        continue;
                    }

                    // skip if parent does not match
                    if (channelParent != null && !XmlFind(resp, "response.level0.channel-search.channels.channels.level4.parent", i, out parent))
                    {
                        _log.Debug("[VivoxVoice] Skipping Channel " + i + "/" + name + " as it's parent doesnt match");
                        continue;
                    }

                    // skip if no channel id available
                    if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.id", i, out id))
                    {
                        _log.Debug("[VivoxVoice] Skipping Channel " + i + "/" + name + " as it has no channel ID");
                        continue;
                    }

                    // skip if no channel uri available
                    if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.uri", i, out uri))
                    {
                        _log.Debug("[VivoxVoice] Skipping Channel " + i + "/" + name + " as it has no channel URI");
                        continue;
                    }

                    channelId = id;
                    channelUri = uri;

                    return true;
                }
            }
            else
            {
                _log.Debug("[VivoxVoice] No count element?");
            }

            channelId = string.Empty;
            channelUri = string.Empty;

            // Useful incase something goes wrong.
            //_log.Debug("[VivoxVoice] Could not find channel in XMLRESP: " + resp.InnerXml);

            return false;
        }

        private bool VivoxTryGetDirectory(string directoryName, out string directoryId)
        {
            string count;

            string requrl = string.Format(_vivoxChannelSearchPath, _vivoxServer, directoryName, _authToken);
            XmlElement resp = VivoxCall(requrl, true);

            if (XmlFind(resp, "response.level0.channel-search.count", out count))
            {
                int channels = Convert.ToInt32(count);
                for (int i = 0; i < channels; i++)
                {
                    string name;
                    string id;
                    string type;

                    // skip if not a directory
                    if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.type", i, out type) ||
                        type != "dir")
                        continue;

                    // skip if not the name we are looking for
                    if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.name", i, out name) ||
                        name != directoryName)
                        continue;

                    // skip if no channel id available
                    if (!XmlFind(resp, "response.level0.channel-search.channels.channels.level4.id", i, out id))
                        continue;

                    directoryId = id;
                    return true;
                }
            }

            directoryId = string.Empty;
            return false;
        }

        // private static readonly string _vivoxChannelById = "https://{0}/api2/viv_chan_mod.php?mode={1}&chan_id={2}&auth_token={3}";

        // private XmlElement VivoxGetChannelById(string parent, string channelid)
        // {
        //     string requrl = String.Format(_vivoxChannelById, _vivoxServer, "get", channelid, _authToken);

        //     if (parent != null && parent != String.Empty)
        //         return VivoxGetChild(parent, channelid);
        //     else
        //         return VivoxCall(requrl, true);
        // }

        private static readonly string _vivoxChannelDel = "https://{0}/api2/viv_chan_mod.php?mode={1}&chan_id={2}&auth_token={3}";

        /// <summary>
        /// Delete a channel.
        /// Once again, there a multitude of options possible. In the simplest case
        /// we specify only the name and get a non-persistent cannel in return. Non
        /// persistent means that the channel gets deleted if no-one uses it for
        /// 5 hours. To accomodate future requirements, it may be a good idea to
        /// initially create channels under the umbrella of a parent ID based upon
        /// the region name. That way we have a context for side channels, if those
        /// are required in a later phase.
        /// In this case the call handles parent and description as optional values.
        /// </summary>

        private XmlElement VivoxDeleteChannel(string parent, string channelid)
        {
            string requrl = string.Format(_vivoxChannelDel, _vivoxServer, "delete", channelid, _authToken);
            if (!string.IsNullOrEmpty(parent))
            {
                requrl = string.Format("{0}&chan_parent={1}", requrl, parent);
            }
            return VivoxCall(requrl, true);
        }

        private static readonly string _vivoxChannelSearch = "https://{0}/api2/viv_chan_search.php?&cond_chanparent={1}&auth_token={2}";

        /// <summary>
        /// Return information on channels in the given directory
        /// </summary>

        private XmlElement VivoxListChildren(string channelid)
        {
            string requrl = string.Format(_vivoxChannelSearch, _vivoxServer, channelid, _authToken);
            return VivoxCall(requrl, true);
        }

        // private XmlElement VivoxGetChild(string parent, string child)
        // {

        //     XmlElement children = VivoxListChildren(parent);
        //     string count;

        //    if (XmlFind(children, "response.level0.channel-search.count", out count))
        //     {
        //         int cnum = Convert.ToInt32(count);
        //         for (int i = 0; i < cnum; i++)
        //         {
        //             string name;
        //             string id;
        //             if (XmlFind(children, "response.level0.channel-search.channels.channels.level4.name", i, out name))
        //             {
        //                 if (name == child)
        //                 {
        //                    if (XmlFind(children, "response.level0.channel-search.channels.channels.level4.id", i, out id))
        //                     {
        //                         return VivoxGetChannelById(null, id);
        //                     }
        //                 }
        //             }
        //         }
        //     }

        //     // One we *know* does not exist.
        //     return VivoxGetChannel(null, Guid.NewGuid().ToString());

        // }

        /// <summary>
        /// This method handles the WEB side of making a request over the
        /// Vivox interface. The returned values are tansferred to a has
        /// table which is returned as the result.
        /// The outcome of the call can be determined by examining the
        /// status value in the hash table.
        /// </summary>
        private XmlElement VivoxCall(string requrl, bool admin)
        {

            XmlDocument doc = null;

            // If this is an admin call, and admin is not connected,
            // and the admin id cannot be connected, then fail.
            if (admin && !_adminConnected && !DoAdminLogin())
                return null;

            doc = new XmlDocument();

            // Let's serialize all calls to Vivox. Most of these are driven by
            // the clients (CAPs), when the user arrives at the region. We don't
            // want to issue many simultaneous http requests to Vivox, because mono
            // doesn't like that
            lock (_Lock)
            {
                try
                {
                    // Otherwise prepare the request
                    //_log.DebugFormat("[VivoxVoice] Sending request <{0}>", requrl);

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(requrl);
                    req.ServerCertificateValidationCallback = WebUtil.ValidateServerCertificateNoChecks; // vivox servers have invalid certs

                    // We are sending just parameters, no content
                    req.ContentLength = 0;

                    // Send request and retrieve the response
                    using (HttpWebResponse rsp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = rsp.GetResponseStream())
                    using (XmlReader rdr = new XmlReader(s))
                            doc.Load(rdr);
                }
                catch (Exception e)
                {
                    _log.ErrorFormat("[VivoxVoice] Error in admin call : {0}", e.Message);
                }
            }

            // If we're debugging server responses, dump the whole
            // load now
            if (_dumpXml) XmlScanl(doc.DocumentElement,0);

            return doc.DocumentElement;
        }

        /// <summary>
        /// Just say if it worked.
        /// </summary>
        private bool IsOK(XmlElement resp)
        {
            string status;
            XmlFind(resp, "response.level0.status", out status);
            return status == "OK";
        }

        /// <summary>
        /// Login has been factored in this way because it gets called
        /// from several places in the module, and we want it to work
        /// the same way each time.
        /// </summary>
        private bool DoAdminLogin()
        {
            _log.Debug("[VivoxVoice] Establishing admin connection");

            lock (vlock)
            {
                if (!_adminConnected)
                {
                    string status = "Unknown";
                    XmlElement resp = null;

                    resp = VivoxLogin(_vivoxAdminUser, _vivoxAdminPassword);

                    if (XmlFind(resp, "response.level0.body.status", out status))
                    {
                        if (status == "Ok")
                        {
                            _log.Info("[VivoxVoice] Admin connection established");
                            if (XmlFind(resp, "response.level0.body.auth_token", out _authToken))
                            {
                                if (_dumpXml) _log.DebugFormat("[VivoxVoice] Auth Token <{0}>",
                                                            _authToken);
                                _adminConnected = true;
                            }
                        }
                        else
                        {
                            _log.WarnFormat("[VivoxVoice] Admin connection failed, status = {0}",
                                  status);
                        }
                    }
                }
            }

            return _adminConnected;
        }

        /// <summary>
        /// The XmlScan routine is provided to aid in the
        /// reverse engineering of incompletely
        /// documented packets returned by the Vivox
        /// voice server. It is only called if the
        /// _dumpXml switch is set.
        /// </summary>
        private void XmlScanl(XmlElement e, int index)
        {
            if (e.HasChildNodes)
            {
                _log.DebugFormat("<{0}>".PadLeft(index+5), e.Name);
                XmlNodeList children = e.ChildNodes;
                foreach (XmlNode node in children)
                   switch (node.NodeType)
                   {
                        case XmlNodeType.Element :
                            XmlScanl((XmlElement)node, index+1);
                            break;
                        case XmlNodeType.Text :
                            _log.DebugFormat("\"{0}\"".PadLeft(index+5), node.Value);
                            break;
                        default :
                            break;
                   }
                _log.DebugFormat("</{0}>".PadLeft(index+6), e.Name);
            }
            else
            {
                _log.DebugFormat("<{0}/>".PadLeft(index+6), e.Name);
            }
        }

        private static readonly char[] C_POINT = {'.'};

        /// <summary>
        /// The Find method is passed an element whose
        /// inner text is scanned in an attempt to match
        /// the name hierarchy passed in the 'tag' parameter.
        /// If the whole hierarchy is resolved, the InnerText
        /// value at that point is returned. Note that this
        /// may itself be a subhierarchy of the entire
        /// document. The function returns a boolean indicator
        /// of the search's success. The search is performed
        /// by the recursive Search method.
        /// </summary>
        private bool XmlFind(XmlElement root, string tag, int nth, out string result)
        {
            if (root == null || tag == null || string.IsNullOrEmpty(tag))
            {
                result = string.Empty;
                return false;
            }
            return XmlSearch(root,tag.Split(C_POINT),0, ref nth, out result);
        }

        private bool XmlFind(XmlElement root, string tag, out string result)
        {
            int nth = 0;
            if (root == null || tag == null || string.IsNullOrEmpty(tag))
            {
                result = string.Empty;
                return false;
            }
            return XmlSearch(root,tag.Split(C_POINT),0, ref nth, out result);
        }

        /// <summary>
        /// XmlSearch is initially called by XmlFind, and then
        /// recursively called by itself until the document
        /// supplied to XmlFind is either exhausted or the name hierarchy
        /// is matched.
        ///
        /// If the hierarchy is matched, the value is returned in
        /// result, and true returned as the function's
        /// value. Otherwise the result is set to the empty string and
        /// false is returned.
        /// </summary>
        private bool XmlSearch(XmlElement e, string[] tags, int index, ref int nth, out string result)
        {
            if (index == tags.Length || e.Name != tags[index])
            {
                result = string.Empty;
                return false;
            }

            if (tags.Length-index == 1)
            {
                if (nth == 0)
                {
                    result = e.InnerText;
                    return true;
                }
                else
                {
                    nth--;
                    result = string.Empty;
                    return false;
                }
            }

            if (e.HasChildNodes)
            {
                XmlNodeList children = e.ChildNodes;
                foreach (XmlNode node in children)
                {
                   switch (node.NodeType)
                   {
                        case XmlNodeType.Element :
                            if (XmlSearch((XmlElement)node, tags, index+1, ref nth, out result))
                                return true;
                            break;

                        default :
                            break;
                    }
                }
            }

            result = string.Empty;
            return false;
        }

        private void HandleDebug(string module, string[] cmd)
        {
            if (cmd.Length < 3)
            {
                MainConsole.Instance.Output("Error: missing on/off flag");
                return;
            }

            if (cmd[2] == "on")
                _dumpXml = true;
            else if (cmd[2] == "off")
                _dumpXml = false;
            else
                MainConsole.Instance.Output("Error: only on and off are supported");
        }
    }
}
