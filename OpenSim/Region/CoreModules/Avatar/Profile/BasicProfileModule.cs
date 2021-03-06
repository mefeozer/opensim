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
using System.Globalization;
using System.Reflection;

using OpenMetaverse;
using log4net;
using Nini.Config;
using Mono.Addins;

using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.Avatar.Profile
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "BasicProfileModule")]
    public class BasicProfileModule : IProfileModule, ISharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        //
        // Module vars
        //
        private readonly List<Scene> _Scenes = new List<Scene>();
        private bool _Enabled = false;

        #region ISharedRegionModule

        public void Initialise(IConfigSource config)
        {
            if(config.Configs["UserProfiles"] != null)
                return;

            _log.DebugFormat("[PROFILE MODULE]: Basic Profile Module enabled");
            _Enabled = true;
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            lock (_Scenes)
            {
                if (!_Scenes.Contains(scene))
                {
                    _Scenes.Add(scene);
                    // Hook up events
                    scene.EventManager.OnNewClient += OnNewClient;
                    scene.RegisterModuleInterface<IProfileModule>(this);
                }
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            lock (_Scenes)
            {
                _Scenes.Remove(scene);
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public string Name => "BasicProfileModule";

        public Type ReplaceableInterface => typeof(IProfileModule);

        #endregion

        /// New Client Event Handler
        private void OnNewClient(IClientAPI client)
        {
            //Profile
            client.OnRequestAvatarProperties += RequestAvatarProperties;
        }

        public void RequestAvatarProperties(IClientAPI remoteClient, UUID avatarID)
        {
            IScene s = remoteClient.Scene;
            if (!(s is Scene))
                return;

//            Scene scene = (Scene)s;

            string profileUrl = string.Empty;
            string aboutText = string.Empty;
            string firstLifeAboutText = string.Empty;
            UUID image = UUID.Zero;
            UUID firstLifeImage = UUID.Zero;
            UUID partner = UUID.Zero;
            uint wantMask = 0;
            string wantText = string.Empty;
            uint skillsMask = 0;
            string skillsText = string.Empty;
            string languages = string.Empty;

            UserAccount account = _Scenes[0].UserAccountService.GetUserAccount(_Scenes[0].RegionInfo.ScopeID, avatarID);

            string name = "Avatar";
            int created = 0;
            if (account != null)
            {
                name = account.FirstName + " " + account.LastName;
                created = account.Created;
            }
            byte[] membershipType = Utils.StringToBytes(name);

            profileUrl = "No profile data";
            aboutText = string.Empty;
            firstLifeAboutText = string.Empty;
            image = UUID.Zero;
            firstLifeImage = UUID.Zero;
            partner = UUID.Zero;

            remoteClient.SendAvatarProperties(avatarID, aboutText,
                        Util.ToDateTime(created).ToString(
                                "M/d/yyyy", CultureInfo.InvariantCulture),
                        membershipType, firstLifeAboutText,
                        0 & 0xff,
                        firstLifeImage, image, profileUrl, partner);

            //Viewer expects interest data when it asks for properties.
            remoteClient.SendAvatarInterestsReply(avatarID, wantMask, wantText,
                                                    skillsMask, skillsText, languages);
        }

    }
}
