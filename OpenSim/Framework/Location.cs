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
using OpenMetaverse;

namespace OpenSim.Framework
{
    [Serializable]
    public class Location : ICloneable
    {
        private readonly uint _x;
        private readonly uint _y;

        public Location(uint x, uint y)
        {
            _x = x;
            _y = y;
        }

        public Location(ulong regionHandle)
        {
            _x =  (uint)(regionHandle >> 32);
            _y = (uint)(regionHandle & (ulong)uint.MaxValue);
        }

        public ulong RegionHandle => Utils.UIntsToLong(_x, _y);

        public uint X => _x;

        public uint Y => _y;

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(obj, this))
                return true;

            if (obj is Location)
            {
                return Equals((Location) obj);
            }

            return base.Equals(obj);
        }

        public bool Equals(Location loc)
        {
            return loc.X == X && loc.Y == Y;
        }

        public bool Equals(int x, int y)
        {
            return X == x && y == Y;
        }

        public static bool operator ==(Location o, object o2)
        {
            return o.Equals(o2);
        }

        public static bool operator !=(Location o, object o2)
        {
            return !o.Equals(o2);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() ^ Y.GetHashCode();
        }

        public object Clone()
        {
            return new Location(X, Y);
        }
    }
}
