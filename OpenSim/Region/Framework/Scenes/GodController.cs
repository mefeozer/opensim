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

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes
{
    public class GodController
    {
        public enum ImplicitGodLevels : int
        {
            EstateManager = 210,    // estate manager implicit god level
            RegionOwner = 220       // region owner implicit god level should be >= than estate
        }

        readonly ScenePresence _scenePresence;
        readonly Scene _scene;
        protected bool _allowGridGods;
        protected bool _forceGridGodsOnly;
        protected bool _regionOwnerIsGod;
        protected bool _regionManagerIsGod;
        protected bool _forceGodModeAlwaysOn;
        protected bool _allowGodActionsWithoutGodMode;

        protected int _userLevel = 0;
        // the god level from local or grid user rights
        protected int _rightsGodLevel = 0;
        // the level seen by viewers
        protected int _viewergodlevel = 0;
        // new level that can be fixed or equal to godlevel, acording to options
        protected int _godlevel = 0;
        protected int _lastLevelToViewer = 0;

        public GodController(Scene scene, ScenePresence sp, int userlevel)
        {
            _scene = scene;
            _scenePresence = sp;
            _userLevel = userlevel;

            IConfigSource config = scene.Config;

            string[] sections = new string[] { "Startup", "Permissions" };

            // God level is based on UserLevel. Gods will have that
            // level grid-wide. Others may become god locally but grid
            // gods are god everywhere.
            _allowGridGods =
                    Util.GetConfigVarFromSections<bool>(config,
                    "allow_grid_gods", sections, false);

            // If grid gods are active, dont allow any other gods
            _forceGridGodsOnly =
                    Util.GetConfigVarFromSections<bool>(config,
                    "force_grid_gods_only", sections, false);

            if(!_forceGridGodsOnly)
            {
                // The owner of a region is a god in his region only.
                _regionOwnerIsGod =
                    Util.GetConfigVarFromSections<bool>(config,
                    "region_owner_is_god", sections, true);

                // Region managers are gods in the regions they manage.
                _regionManagerIsGod =
                    Util.GetConfigVarFromSections<bool>(config,
                    "region_manager_is_god", sections, false);

            }
            else
                _allowGridGods = true; // reduce potencial user mistakes
                 
            // God mode should be turned on in the viewer whenever
            // the user has god rights somewhere. They may choose
            // to turn it off again, though.
            _forceGodModeAlwaysOn =
                    Util.GetConfigVarFromSections<bool>(config,
                    "automatic_gods", sections, false);

            // The user can execute any and all god functions, as
            // permitted by the viewer UI, without actually "godding
            // up". This is the default state in 0.8.2.
            _allowGodActionsWithoutGodMode =
                    Util.GetConfigVarFromSections<bool>(config,
                    "implicit_gods", sections, false);

            _rightsGodLevel = CalcRightsGodLevel();

            if(_allowGodActionsWithoutGodMode)
            {
                _godlevel = _rightsGodLevel;
                _forceGodModeAlwaysOn = false;
            }

            else if(_forceGodModeAlwaysOn)
            {
                _viewergodlevel = _rightsGodLevel;
                _godlevel = _rightsGodLevel;
            }

            _scenePresence.IsGod = _godlevel >= 200;
            _scenePresence.IsViewerUIGod = _viewergodlevel >= 200;
        }

        // calculates god level at sp creation from local and grid user god rights
        // for now this is assumed static until user leaves region.
        // later estate and gride level updates may update this
        protected int CalcRightsGodLevel()
        {
            int level = 0;
            if (_allowGridGods && _userLevel >= 200)
                level = _userLevel;

            if(_forceGridGodsOnly || level >= (int)ImplicitGodLevels.RegionOwner)
                return level;

            if (_regionOwnerIsGod && _scene.RegionInfo.EstateSettings.IsEstateOwner(_scenePresence.UUID))
                level = (int)ImplicitGodLevels.RegionOwner;

            if(level >= (int)ImplicitGodLevels.EstateManager)
                return level;

            if (_regionManagerIsGod && _scene.Permissions.IsEstateManager(_scenePresence.UUID))
                level = (int)ImplicitGodLevels.EstateManager;

            return level;
        }

        protected bool CanBeGod()
        {
            return _rightsGodLevel >= 200;
        }

        protected void UpdateGodLevels(bool viewerState)
        {
            if(!CanBeGod())
            {
                _viewergodlevel = 0;
                _godlevel = 0;
                _scenePresence.IsGod = false;
                _scenePresence.IsViewerUIGod = false;
                return;
            }

            // legacy some are controled by viewer, others are static
            if(_allowGodActionsWithoutGodMode)
            {
                if(viewerState)
                    _viewergodlevel = _rightsGodLevel;
                else
                    _viewergodlevel = 0;

                _godlevel = _rightsGodLevel;
            }
            else
            {
                // new all change with viewer
                if(viewerState)
                {
                    _viewergodlevel = _rightsGodLevel;
                    _godlevel = _rightsGodLevel;
                }
                else
                {
                    _viewergodlevel = 0;
                    _godlevel = 0;
                }
            }
            _scenePresence.IsGod = _godlevel >= 200;
            _scenePresence.IsViewerUIGod = _viewergodlevel >= 200;
        }

        public void SyncViewerState()
        {
            if(_lastLevelToViewer == _viewergodlevel)
                return;

            _lastLevelToViewer = _viewergodlevel;

            if(_scenePresence.IsChildAgent)
                return;            

            _scenePresence.ControllingClient.SendAdminResponse(UUID.Zero, (uint)_viewergodlevel);
        }

        public void RequestGodMode(bool god)
        {
            UpdateGodLevels(god);

            if(_lastLevelToViewer != _viewergodlevel)
            {
                _scenePresence.ControllingClient.SendAdminResponse(UUID.Zero, (uint)_viewergodlevel);
                _lastLevelToViewer = _viewergodlevel;
            }
        }

       public OSD State()
        {
            OSDMap godMap = new OSDMap(2);
            bool _viewerUiIsGod = _viewergodlevel >= 200;
            godMap.Add("ViewerUiIsGod", OSD.FromBoolean(_viewerUiIsGod));

            return godMap;
        }

        public void SetState(OSD state)
        {
            bool newstate = false;
            if(_forceGodModeAlwaysOn)
                newstate = _viewergodlevel >= 200;
            if(state != null)
            {
                OSDMap s = (OSDMap)state;

                if (s.ContainsKey("ViewerUiIsGod"))
                    newstate = s["ViewerUiIsGod"].AsBoolean();
                _lastLevelToViewer = _viewergodlevel; // we are not changing viewer level by default
            }       
            UpdateGodLevels(newstate);
        }

        public void HasMovedAway()
        {
            _lastLevelToViewer = 0;
            if(_forceGodModeAlwaysOn)
            {
                _viewergodlevel = _rightsGodLevel;
                _godlevel = _rightsGodLevel;
            }
        }

        public int UserLevel
        {
            get => _userLevel;
            set => _userLevel = value;
        }

        public int ViwerUIGodLevel => _viewergodlevel;

        public int GodLevel => _godlevel;
    }
}
