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
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Estate
{
    public class TelehubManager
    {
        // private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        readonly Scene _Scene;

        public TelehubManager(Scene scene)
        {
            _Scene = scene;
        }

        // Connect the Telehub
        public void Connect(SceneObjectGroup grp)
        {
            _Scene.RegionInfo.RegionSettings.ClearSpawnPoints();

            _Scene.RegionInfo.RegionSettings.TelehubObject = grp.UUID;
            _Scene.RegionInfo.RegionSettings.Save();
        }

        // Disconnect the Telehub:
        public void Disconnect()
        {
            if (_Scene.RegionInfo.RegionSettings.TelehubObject == UUID.Zero)
                return;

            _Scene.RegionInfo.RegionSettings.TelehubObject = UUID.Zero;
            _Scene.RegionInfo.RegionSettings.ClearSpawnPoints();
            _Scene.RegionInfo.RegionSettings.Save();
        }

        // Add a SpawnPoint to the Telehub
        public void AddSpawnPoint(Vector3 point)
        {
            if (_Scene.RegionInfo.RegionSettings.TelehubObject == UUID.Zero)
                return;

            SceneObjectGroup grp = _Scene.GetSceneObjectGroup(_Scene.RegionInfo.RegionSettings.TelehubObject);
            if (grp == null)
                return;

            SpawnPoint sp = new SpawnPoint();
            sp.SetLocation(grp.AbsolutePosition, grp.GroupRotation, point);
            _Scene.RegionInfo.RegionSettings.AddSpawnPoint(sp);
            _Scene.RegionInfo.RegionSettings.Save();
        }

        // Remove a SpawnPoint from the Telehub
        public void RemoveSpawnPoint(int spawnpoint)
        {
            if (_Scene.RegionInfo.RegionSettings.TelehubObject == UUID.Zero)
                return;

            _Scene.RegionInfo.RegionSettings.RemoveSpawnPoint(spawnpoint);
            _Scene.RegionInfo.RegionSettings.Save();
        }
    }
}
