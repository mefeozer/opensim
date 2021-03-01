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
using System.Net;
using System.Reflection;

using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Mono.Addins;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EstateChangeInfoCapModule")]
    public class EstateChangeInfoCapModule : INonSharedRegionModule
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _Enabled = false;
        private string _capUrl;
        IEstateModule _EstateModule;

        #region INonSharedRegionModule Members

        public void Initialise(IConfigSource pSource)
        {
            IConfig config = pSource.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            _capUrl = config.GetString("Cap_EstateChangeInfo", string.Empty);
            if (!string.IsNullOrEmpty(_capUrl) && _capUrl.Equals("localhost"))
                _Enabled = true;
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            _scene = scene;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            if (_scene == scene)
            {
                _scene.EventManager.OnRegisterCaps -= RegisterCaps;
                _scene = null;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            if (scene.RegionInfo == null || scene.RegionInfo.EstateSettings == null)
            {
                _Enabled = false;
                return;
            }

            _EstateModule = scene.RequestModuleInterface<IEstateModule>();
            if(_EstateModule == null)
            {
                _Enabled = false;
                return;
            }

            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void Close()
        {
        }

        public string Name => "EstateChangeInfoCapModule";

        public Type ReplaceableInterface => null;

        #endregion

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            caps.RegisterSimpleHandler("EstateChangeInfo",
                new SimpleStreamHandler("/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                    {
                        ProcessRequest(httpRequest, httpResponse, agentID, caps);
                    }));
        }

        public void ProcessRequest(IOSHttpRequest request, IOSHttpResponse response, UUID AgentId, Caps cap)
        {
            if(request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            ScenePresence avatar;
            if (!_scene.TryGetScenePresence(AgentId, out avatar) || !_scene.Permissions.CanIssueEstateCommand(AgentId, false))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (_scene.RegionInfo == null || _scene.RegionInfo.EstateSettings == null)
            {
                response.StatusCode = (int)HttpStatusCode.NotImplemented;
                return;
            }

            OSDMap r;
            try
            {
                r = (OSDMap)OSDParser.Deserialize(request.InputStream);
            }
            catch (Exception ex)
            {
                _log.Error("[UPLOAD OBJECT ASSET MODULE]: Error deserializing message " + ex.ToString());
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            bool ok = true;
            try
            {
                string estateName = r["estate_name"].AsString();
                UUID invoice = r["invoice"].AsUUID();
                //int sunHour = r["sun_hour"].AsInteger();
                //bool sunFixed = r["is_sun_fixed"].AsBoolean();
                bool externallyVisible = r["is_externally_visible"].AsBoolean();
                bool allowDirectTeleport = r["allow_direct_teleport"].AsBoolean();
                bool denyAnonymous = r["deny_anonymous"].AsBoolean();
                bool denyAgeUnverified = r["deny_age_unverified"].AsBoolean();
                bool alloVoiceChat = r["allow_voice_chat"].AsBoolean();
                // taxfree is now !AllowAccessOverride
                OSD tmp;
                bool overridePublicAccess = !_scene.RegionInfo.EstateSettings.TaxFree;
                if (r.TryGetValue("override_public_access", out tmp))
                    overridePublicAccess = !tmp.AsBoolean();
    
                bool allowEnvironmentOverride = _scene.RegionInfo.EstateSettings.AllowEnvironmentOverride;
                if (r.TryGetValue("override_environment", out tmp))
                    allowEnvironmentOverride = tmp.AsBoolean();

                ok = _EstateModule.handleEstateChangeInfoCap(estateName, invoice,
                        externallyVisible, allowDirectTeleport, denyAnonymous, denyAgeUnverified,
                        alloVoiceChat, overridePublicAccess, allowEnvironmentOverride);
            }
            catch
            {
                ok = false;
            }

            response.StatusCode = ok ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;
         }
    }
}
