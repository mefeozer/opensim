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
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Avatar.Lure
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LureModule")]
    public class LureModule : ISharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<Scene> _scenes = new List<Scene>();

        private IMessageTransferModule _TransferModule = null;
        private bool _Enabled = false;

        public void Initialise(IConfigSource config)
        {
            if (config.Configs["Messaging"] != null)
            {
                if (config.Configs["Messaging"].GetString(
                        "LureModule", "LureModule") ==
                        "LureModule")
                {
                    _Enabled = true;
                    _log.DebugFormat("[LURE MODULE]: {0} enabled", Name);
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            lock (_scenes)
            {
                _scenes.Add(scene);
                scene.EventManager.OnNewClient += OnNewClient;
                scene.EventManager.OnIncomingInstantMessage +=
                        OnGridInstantMessage;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            if (_TransferModule == null)
            {
                _TransferModule =
                    scene.RequestModuleInterface<IMessageTransferModule>();

                if (_TransferModule == null)
                {
                    _log.Error("[INSTANT MESSAGE]: No message transfer module, "+
                    "lures will not work!");

                    _Enabled = false;
                    _scenes.Clear();
                    scene.EventManager.OnNewClient -= OnNewClient;
                    scene.EventManager.OnIncomingInstantMessage -=
                            OnGridInstantMessage;
                }
            }

        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            lock (_scenes)
            {
                _scenes.Remove(scene);
                scene.EventManager.OnNewClient -= OnNewClient;
                scene.EventManager.OnIncomingInstantMessage -=
                        OnGridInstantMessage;
            }
        }

        void OnNewClient(IClientAPI client)
        {
            client.OnInstantMessage += OnInstantMessage;
            client.OnStartLure += OnStartLure;
            client.OnTeleportLureRequest += OnTeleportLureRequest;
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public string Name => "LureModule";

        public Type ReplaceableInterface => null;

        public void OnInstantMessage(IClientAPI client, GridInstantMessage im)
        {
            if (im.dialog == (byte)InstantMessageDialog.RequestLure)
            {
                if (_TransferModule != null)
                    _TransferModule.SendInstantMessage(im, delegate (bool success) { });
            }
        }

        public void OnStartLure(byte lureType, string message, UUID targetid, IClientAPI client)
        {
            if (!(client.Scene is Scene))
                return;

            Scene scene = (Scene)client.Scene;
            ScenePresence presence = scene.GetScenePresence(client.AgentId);

            // Round up Z co-ordinate rather than round-down by casting.  This stops tall avatars from being given
            // a teleport Z co-ordinate by short avatars that drops them through or embeds them in thin floors on
            // arrival.
            //
            // Ideally we would give the exact float position adjusting for the relative height of the two avatars
            // but it looks like a float component isn't possible with a parcel ID.
            UUID dest = Util.BuildFakeParcelID(
                    scene.RegionInfo.RegionHandle,
                    (uint)presence.AbsolutePosition.X,
                    (uint)presence.AbsolutePosition.Y,
                    (uint)presence.AbsolutePosition.Z + 2);

            _log.DebugFormat("[LURE MODULE]: TP invite with message {0}, type {1}", message, lureType);

            GridInstantMessage m;

            if (scene.Permissions.IsAdministrator(client.AgentId) && presence.IsViewerUIGod && !scene.Permissions.IsAdministrator(targetid))
            {
                m = new GridInstantMessage(scene, client.AgentId,
                        client.FirstName+" "+client.LastName, targetid,
                        (byte)InstantMessageDialog.GodLikeRequestTeleport, false,
                        message, dest, false, presence.AbsolutePosition,
                        new byte[0], true);
            }
            else
            {
                m = new GridInstantMessage(scene, client.AgentId,
                        client.FirstName+" "+client.LastName, targetid,
                        (byte)InstantMessageDialog.RequestTeleport, false,
                        message, dest, false, presence.AbsolutePosition,
                        new byte[0], true);
            }

            if (_TransferModule != null)
            {
                _TransferModule.SendInstantMessage(m,
                    delegate(bool success) { });
            }
        }

        public void OnTeleportLureRequest(UUID lureID, uint teleportFlags, IClientAPI client)
        {
            if (!(client.Scene is Scene))
                return;

            Scene scene = (Scene)client.Scene;

            ulong handle = 0;
            uint x = 128;
            uint y = 128;
            uint z = 70;

            Util.ParseFakeParcelID(lureID, out handle, out x, out y, out z);

            Vector3 position = new Vector3
            {
                X = (float)x,
                Y = (float)y,
                Z = (float)z
            };

            scene.RequestTeleportLocation(client, handle, position,
                    Vector3.Zero, teleportFlags);
        }

        private void OnGridInstantMessage(GridInstantMessage msg)
        {
            // Forward remote teleport requests
            //
            if (msg.dialog != (byte)InstantMessageDialog.RequestTeleport &&
                msg.dialog != (byte)InstantMessageDialog.GodLikeRequestTeleport &&
                msg.dialog != (byte)InstantMessageDialog.RequestLure)
                return;

            if (_TransferModule != null)
            {
                _TransferModule.SendInstantMessage(msg,
                    delegate(bool success) { });
            }
        }
    }
}
