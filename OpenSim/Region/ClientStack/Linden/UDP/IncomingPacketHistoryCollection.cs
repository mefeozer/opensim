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

using System.Collections.Generic;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    /// <summary>
    /// A circular buffer and hashset for tracking incoming packet sequence
    /// numbers
    /// </summary>
    public sealed class IncomingPacketHistoryCollection
    {
        private readonly uint[] _items;
        private readonly HashSet<uint> _hashSet;
        private int _first;
        private int _next;
        private readonly int _capacity;

        public IncomingPacketHistoryCollection(int capacity)
        {
            this._capacity = capacity;
            _items = new uint[capacity];
            _hashSet = new HashSet<uint>();
        }

        public bool TryEnqueue(uint ack)
        {
            lock (_hashSet)
            {
                if (_hashSet.Add(ack))
                {
                    _items[_next] = ack;
                    _next = (_next + 1) % _capacity;
                    if (_next == _first)
                    {
                        _hashSet.Remove(_items[_first]);
                        _first = (_first + 1) % _capacity;
                    }

                    return true;
                }
            }

            return false;
        }
    }
}
