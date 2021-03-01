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
using System.Linq;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Simulation
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LocalSimulationConnectorModule")]
    public class LocalSimulationConnectorModule : ISharedRegionModule, ISimulationService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Map region ID to scene.
        /// </summary>
        private readonly Dictionary<UUID, Scene> _scenes = new Dictionary<UUID, Scene>();

        /// <summary>
        /// Is this module enabled?
        /// </summary>
        private bool _ModuleEnabled = false;

        #region Region Module interface

        public void Initialise(IConfigSource configSource)
        {
            IConfig moduleConfig = configSource.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("SimulationServices", "");
                if (name == Name)
                {
                    InitialiseService(configSource);

                    _ModuleEnabled = true;

                    _log.Info("[LOCAL SIMULATION CONNECTOR]: Local simulation enabled.");
                }
            }
        }

        public void InitialiseService(IConfigSource configSource)
        {
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!_ModuleEnabled)
                return;

            Init(scene);
            scene.RegisterModuleInterface<ISimulationService>(this);
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_ModuleEnabled)
                return;

            RemoveScene(scene);
            scene.UnregisterModuleInterface<ISimulationService>(this);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void Close()
        {
        }

        public Type ReplaceableInterface => null;

        public string Name => "LocalSimulationConnectorModule";

        /// <summary>
        /// Can be called from other modules.
        /// </summary>
        /// <param name="scene"></param>
        public void RemoveScene(Scene scene)
        {
            lock (_scenes)
            {
                if (_scenes.ContainsKey(scene.RegionInfo.RegionID))
                    _scenes.Remove(scene.RegionInfo.RegionID);
                else
                    _log.WarnFormat(
                        "[LOCAL SIMULATION CONNECTOR]: Tried to remove region {0} but it was not present",
                        scene.RegionInfo.RegionName);
            }
        }

        /// <summary>
        /// Can be called from other modules.
        /// </summary>
        /// <param name="scene"></param>
        public void Init(Scene scene)
        {
            lock (_scenes)
            {
                if (!_scenes.ContainsKey(scene.RegionInfo.RegionID))
                    _scenes[scene.RegionInfo.RegionID] = scene;
                else
                    _log.WarnFormat(
                        "[LOCAL SIMULATION CONNECTOR]: Tried to add region {0} but it is already present",
                        scene.RegionInfo.RegionName);
            }
        }

        #endregion

        #region ISimulationService

        public IScene GetScene(UUID regionId)
        {
            if (_scenes.ContainsKey(regionId))
            {
                return _scenes[regionId];
            }
            else
            {
                // FIXME: This was pre-existing behaviour but possibly not a good idea, since it hides an error rather
                // than making it obvious and fixable.  Need to see if the error message comes up in practice.
                Scene s = _scenes.Values.ToArray()[0];

                _log.ErrorFormat(
                    "[LOCAL SIMULATION CONNECTOR]: Region with id {0} not found.  Returning {1} {2} instead",
                    regionId, s.RegionInfo.RegionName, s.RegionInfo.RegionID);

                return s;
            }
        }

        public ISimulationService GetInnerService()
        {
            return this;
        }

        /**
         * Agent-related communications
         */

        public bool CreateAgent(GridRegion source, GridRegion destination, AgentCircuitData aCircuit, uint teleportFlags, EntityTransferContext ctx, out string reason)
        {
            if (destination == null)
            {
                reason = "Given destination was null";
                _log.DebugFormat("[LOCAL SIMULATION CONNECTOR]: CreateAgent was given a null destination");
                return false;
            }

            if (_scenes.TryGetValue(destination.RegionID, out Scene destScene))
            {
//                    _log.DebugFormat("[LOCAL SIMULATION CONNECTOR]: Found region {0} to send SendCreateChildAgent", destination.RegionName);
                return destScene.NewUserConnection(aCircuit, teleportFlags, source, out reason);
            }

            reason = "Did not find region " + destination.RegionName;
            return false;
        }

        public bool UpdateAgent(GridRegion destination, AgentData cAgentData, EntityTransferContext ctx)
        {
            if (destination == null)
                return false;

            if (_scenes.ContainsKey(destination.RegionID))
            {
//                    _log.DebugFormat(
//                        "[LOCAL SIMULATION CONNECTOR]: Found region {0} {1} to send AgentUpdate",
//                        destination.RegionName, destination.RegionID);

                return _scenes[destination.RegionID].IncomingUpdateChildAgent(cAgentData);
            }

//            _log.DebugFormat(
//                "[LOCAL COMMS]: Did not find region {0} {1} for ChildAgentUpdate",
//                destination.RegionName, destination.RegionID);

            return false;
        }

        public bool UpdateAgent(GridRegion destination, AgentPosition agentPosition)
        {
            if (destination == null)
                return false;

            // We limit the number of messages sent for a position change to just one per
            // simulator so when we receive the update we need to hand it to each of the
            // scenes; scenes each check to see if the is a scene presence for the avatar
            // note that we really don't need the GridRegion for this call
            foreach (Scene s in _scenes.Values)
            {
//                _log.Debug("[LOCAL COMMS]: Found region to send ChildAgentUpdate");
                s.IncomingUpdateChildAgent(agentPosition);
            }

            //_log.Debug("[LOCAL COMMS]: region not found for ChildAgentUpdate");
            return true;
        }

        public bool QueryAccess(GridRegion destination, UUID agentID, string agentHomeURI, bool viaTeleport, Vector3 position, List<UUID> features, EntityTransferContext ctx, out string reason)
        {
            reason = "Communications failure";
            if (destination == null)
                return false;

            if (_scenes.ContainsKey(destination.RegionID))
            {
//                    _log.DebugFormat(
//                        "[LOCAL SIMULATION CONNECTOR]: Found region {0} {1} to send AgentUpdate",
//                        s.RegionInfo.RegionName, destination.RegionHandle);
                uint sizeX = _scenes[destination.RegionID].RegionInfo.RegionSizeX;
                uint sizeY = _scenes[destination.RegionID].RegionInfo.RegionSizeY;

                // Var regions here, and the requesting simulator is in an older version.
                // We will forbide this, because it crashes the viewers
                if (ctx.OutboundVersion < 0.3f && (sizeX != 256 || sizeY != 256))
                {
                    reason = "Destination is a variable-sized region, and source is an old simulator. Consider upgrading.";
                    _log.DebugFormat("[LOCAL SIMULATION CONNECTOR]: Request to access this variable-sized region from older simulator was denied");
                    return false;

                }


                return _scenes[destination.RegionID].QueryAccess(agentID, agentHomeURI, viaTeleport, position, features, out reason);
            }

            //_log.Debug("[LOCAL COMMS]: region not found for QueryAccess");
            return false;
        }

        public bool ReleaseAgent(UUID originId, UUID agentId, string uri)
        {
            if (_scenes.ContainsKey(originId))
            {
//                    _log.DebugFormat(
//                        "[LOCAL SIMULATION CONNECTOR]: Found region {0} {1} to send AgentUpdate",
//                        s.RegionInfo.RegionName, destination.RegionHandle);

                _scenes[originId].EntityTransferModule.AgentArrivedAtDestination(agentId);
                return true;
            }

            //_log.Debug("[LOCAL COMMS]: region not found in SendReleaseAgent " + origin);
            return false;
        }

        public bool CloseAgent(GridRegion destination, UUID id, string auth_token)
        {
            if (destination == null)
                return false;

            if (_scenes.ContainsKey(destination.RegionID))
            {
//                    _log.DebugFormat(
//                        "[LOCAL SIMULATION CONNECTOR]: Found region {0} {1} to send AgentUpdate",
//                        s.RegionInfo.RegionName, destination.RegionHandle);

                _scenes[destination.RegionID].CloseAgent(id, false, auth_token);
                return true;
            }
            //_log.Debug("[LOCAL COMMS]: region not found in SendCloseAgent");
            return false;
        }

        /**
         * Object-related communications
         */

        public bool CreateObject(GridRegion destination, Vector3 newPosition, ISceneObject sog, bool isLocalCall)
        {
            if (destination == null)
                return false;

            if (_scenes.ContainsKey(destination.RegionID))
            {
//                    _log.DebugFormat(
//                        "[LOCAL SIMULATION CONNECTOR]: Found region {0} {1} to send AgentUpdate",
//                        s.RegionInfo.RegionName, destination.RegionHandle);

                Scene s = _scenes[destination.RegionID];

                if (isLocalCall)
                {
                    // We need to make a local copy of the object
                    ISceneObject sogClone = sog.CloneForNewScene();
                    sogClone.SetState(sog.GetStateSnapshot(), s);
                    return s.IncomingCreateObject(newPosition, sogClone);
                }
                else
                {
                    // Use the object as it came through the wire
                    return s.IncomingCreateObject(newPosition, sog);
                }
            }

            return false;
        }

        #endregion

        #region Misc

        public bool IsLocalRegion(ulong regionhandle)
        {
            foreach (Scene s in _scenes.Values)
                if (s.RegionInfo.RegionHandle == regionhandle)
                    return true;

            return false;
        }

        public bool IsLocalRegion(UUID id)
        {
            return _scenes.ContainsKey(id);
        }

        #endregion
    }
}
