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
using OpenMetaverse.Packets;

namespace OpenSim.Framework
{
    /// <summary>
    /// Enapsulate statistics for a simulator/scene.
    ///
    /// TODO: This looks very much like the OpenMetaverse SimStatsPacket.  It should be much more generic stats
    /// storage.
    /// </summary>
    public class SimStats
    {
        public uint RegionX => _regionX;
        private readonly uint _regionX;

        public uint RegionY => _regionY;
        private readonly uint _regionY;

        public SimStatsPacket.RegionBlock RegionBlock => _regionBlock;
        private readonly SimStatsPacket.RegionBlock _regionBlock;

        public SimStatsPacket.StatBlock[] StatsBlock => _statsBlock;
        private readonly SimStatsPacket.StatBlock[] _statsBlock;

        public SimStatsPacket.StatBlock[] ExtraStatsBlock => _extraStatsBlock;
        private readonly SimStatsPacket.StatBlock[] _extraStatsBlock;

        public uint RegionFlags => _regionFlags;
        private readonly uint _regionFlags;

        public uint ObjectCapacity => _objectCapacity;
        private readonly uint _objectCapacity;

        public UUID RegionUUID => regionUUID;
        private readonly UUID regionUUID;

        public SimStats(
            uint regionX, uint regionY, uint regionFlags, uint objectCapacity,
            SimStatsPacket.RegionBlock regionBlock, SimStatsPacket.StatBlock[] statsBlock,
            SimStatsPacket.StatBlock[] ExtraStatsBlock, UUID pRUUID)
        {
            regionUUID = pRUUID;
            _regionX = regionX;
            _regionY = regionY;
            _regionFlags = regionFlags;
            _objectCapacity = objectCapacity;
            _regionBlock = regionBlock;
            _statsBlock = statsBlock;
            _extraStatsBlock = ExtraStatsBlock;
        }
    }
}
