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
using System.Globalization;
using System.Net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;


namespace OpenSim.Region.ClientStack.Linden
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EstateAcessCapModule")]
    public class EstateAccessCapModule : INonSharedRegionModule
    {
//        private static readonly ILog _log =
//            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _scene;
        private bool _Enabled = false;
        private string _capUrl;
        //IEstateModule _EstateModule;

        #region INonSharedRegionModule Members

        public void Initialise(IConfigSource pSource)
        {
            IConfig config = pSource.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            _capUrl = config.GetString("Cap_EstateAccess", string.Empty);
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

            IEstateModule _EstateModule = scene.RequestModuleInterface<IEstateModule>();
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

        public string Name => "EstateAccessCapModule";

        public Type ReplaceableInterface => null;

        #endregion

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            caps.RegisterSimpleHandler("EstateAccess",
                new SimpleStreamHandler("/" + UUID.Random(),
                delegate(IOSHttpRequest request, IOSHttpResponse response)
                {
                    ProcessRequest(request, response, agentID);
                }));
        }

        public void ProcessRequest(IOSHttpRequest request, IOSHttpResponse response, UUID AgentId)
        {
            if(request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            ScenePresence avatar;
            if (!_scene.TryGetScenePresence(AgentId, out avatar) || _scene.RegionInfo == null || _scene.RegionInfo.EstateSettings == null)
            {
                response.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }

            if (!_scene.Permissions.CanIssueEstateCommand(AgentId, false))
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            EstateSettings regionSettings = _scene.RegionInfo.EstateSettings;
            UUID[] managers = regionSettings.EstateManagers;
            UUID[] allowed = regionSettings.EstateAccess;
            UUID[] groups = regionSettings.EstateGroups;
            EstateBan[] EstateBans = regionSettings.EstateBans;

            osUTF8 sb = LLSDxmlEncode2.Start();
            LLSDxmlEncode2.AddMap(sb);

            if (allowed != null && allowed.Length > 0)
            {
                LLSDxmlEncode2.AddArray("AllowedAgents", sb);
                for (int i = 0; i < allowed.Length; ++i)
                {
                    UUID id = allowed[i];
                    if (id == UUID.Zero)
                        continue;
                    LLSDxmlEncode2.AddMap(sb);
                        LLSDxmlEncode2.AddElem("id", id, sb);
                    LLSDxmlEncode2.AddEndMap(sb);
                }
                LLSDxmlEncode2.AddEndArray(sb);
            }
            else
                LLSDxmlEncode2.AddEmptyArray("AllowedAgents", sb);

            if (groups != null && groups.Length > 0)
            {
                LLSDxmlEncode2.AddArray("AllowedGroups", sb);
                for (int i = 0; i < groups.Length; ++i)
                {
                    UUID id = groups[i];
                    if (id == UUID.Zero)
                        continue;
                    LLSDxmlEncode2.AddMap(sb);
                        LLSDxmlEncode2.AddElem("id", id, sb);
                    LLSDxmlEncode2.AddEndMap(sb);
                }
                LLSDxmlEncode2.AddEndArray(sb);
            }
            else
                LLSDxmlEncode2.AddEmptyArray("AllowedGroups", sb);

            if (EstateBans != null && EstateBans.Length > 0)
            {
                LLSDxmlEncode2.AddArray("BannedAgents", sb);
                for (int i = 0; i < EstateBans.Length; ++i)
                {
                    EstateBan ban = EstateBans[i];
                    UUID id = ban.BannedUserID;
                    if (id == UUID.Zero)
                        continue;
                    LLSDxmlEncode2.AddMap(sb);
                        LLSDxmlEncode2.AddElem("id", id, sb);
                        LLSDxmlEncode2.AddElem("banning_id", ban.BanningUserID, sb);
                        LLSDxmlEncode2.AddElem("last_login_date", "na", sb); // We will not have this. This information is far at grid
                        if (ban.BanTime == 0)
                            LLSDxmlEncode2.AddElem("ban_date", "0000-00-00 00:00", sb);
                        else
                            LLSDxmlEncode2.AddElem("ban_date", Util.ToDateTime(ban.BanTime).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), sb);
                    LLSDxmlEncode2.AddEndMap(sb);
                }
                LLSDxmlEncode2.AddEndArray(sb);
            }
            else
                LLSDxmlEncode2.AddEmptyArray("BannedAgents", sb);

            if (managers != null && managers.Length > 0)
            {
                LLSDxmlEncode2.AddArray("Managers", sb);
                for (int i = 0; i < managers.Length; ++i)
                {
                    LLSDxmlEncode2.AddMap(sb);
                        LLSDxmlEncode2.AddElem("agent_id", managers[i], sb);
                    LLSDxmlEncode2.AddEndMap(sb);
                }
                LLSDxmlEncode2.AddEndArray(sb);
            }
            else
                LLSDxmlEncode2.AddEmptyArray("Managers", sb);

            LLSDxmlEncode2.AddEndMap(sb);

            response.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
            response.StatusCode = (int)HttpStatusCode.OK;
        }
    }
}
