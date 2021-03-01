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
using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes
{
    /// <summary>
    /// Specifies the fields that have been changed when sending a prim or
    /// avatar update
    /// </summary>
    [Flags]
    public enum ObjectPropertyUpdateFlags : byte
    {
        None = 0,
        Family = 1,
        Object = 2,

        NoFamily = unchecked((byte)~Family),
        NoObject = unchecked((byte)~Object)
    }

    public class EntityUpdate
    {
        // for priority queue
        public int PriorityQueue;
        public int PriorityQueueIndex;
        public ulong EntryOrder;

        private ISceneEntity _entity;
        private PrimUpdateFlags _flags;
        public ObjectPropertyUpdateFlags _propsFlags;

        public ObjectPropertyUpdateFlags PropsFlags
        {
            get => _propsFlags;
            set => _propsFlags = value;
        }

        public ISceneEntity Entity
        {
            get => _entity;
            internal set => _entity = value;
        }

        public PrimUpdateFlags Flags
        {
            get => _flags;
            set => _flags = value;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Update(int pqueue, ulong entry)
        {
            if ((_flags & PrimUpdateFlags.CancelKill) != 0)
            {
                if ((_flags & PrimUpdateFlags.UpdateProbe) != 0)
                    _flags = PrimUpdateFlags.UpdateProbe;
                else
                    _flags = PrimUpdateFlags.FullUpdatewithAnim;
            }

            PriorityQueue = pqueue;
            EntryOrder = entry;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void UpdateFromNew(EntityUpdate newupdate, int pqueue)
        {
            _propsFlags |= newupdate.PropsFlags;
            PrimUpdateFlags newFlags = newupdate.Flags;

            if ((newFlags & PrimUpdateFlags.UpdateProbe) != 0)
                _flags &= ~PrimUpdateFlags.UpdateProbe;

            if ((newFlags & PrimUpdateFlags.CancelKill) != 0)
            {
                if ((newFlags & PrimUpdateFlags.UpdateProbe) != 0)
                    _flags = PrimUpdateFlags.UpdateProbe;
                else
                    newFlags = PrimUpdateFlags.FullUpdatewithAnim;
            }
            else
                _flags |= newFlags;

            PriorityQueue = pqueue;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Free()
        {
            _entity = null;
            PriorityQueueIndex = -1;
            EntityUpdatesPool.Free(this);
        }

        public EntityUpdate(ISceneEntity entity, PrimUpdateFlags flags)
        {
            _entity = entity;
            _flags = flags;
        }

        public EntityUpdate(ISceneEntity entity, PrimUpdateFlags flags, bool sendfam, bool sendobj)
        {
            _entity = entity;
            _flags = flags;

            if (sendfam)
                _propsFlags |= ObjectPropertyUpdateFlags.Family;

            if (sendobj)
                _propsFlags |= ObjectPropertyUpdateFlags.Object;
        }

        public override string ToString()
        {
            return string.Format("[{0},{1},{2}]", PriorityQueue, EntryOrder, _entity.LocalId);
        }
    }

    public static class EntityUpdatesPool
    {
        const int MAXSIZE = 32768;
        const int PREALLOC = 16384;
        private static readonly EntityUpdate[] _pool = new EntityUpdate[MAXSIZE];
        private static readonly object _poollock = new object();
        private static int _poolPtr;
        //private static int _min = int.MaxValue;
        //private static int _max = int.MinValue;

        static EntityUpdatesPool()
        {
            for(int i = 0; i < PREALLOC; ++i)
                _pool[i] = new EntityUpdate(null, 0);
            _poolPtr = PREALLOC - 1;
        }

        public static EntityUpdate Get(ISceneEntity entity, PrimUpdateFlags flags)
        {
            lock (_poollock)
            {
                if (_poolPtr >= 0)
                {
                    EntityUpdate eu = _pool[_poolPtr];
                    _pool[_poolPtr] = null;
                    _poolPtr--;
                    //if (_min > _poolPtr)
                    //    _min = _poolPtr;
                    eu.Entity = entity;
                    eu.Flags = flags;
                    return eu;
                }
            }
            return new EntityUpdate(entity, flags);
        }

        public static EntityUpdate Get(ISceneEntity entity, PrimUpdateFlags flags, bool sendfam, bool sendobj)
        {
            lock (_poollock)
            {
                if (_poolPtr >= 0)
                {
                    EntityUpdate eu = _pool[_poolPtr];
                    _pool[_poolPtr] = null;
                    _poolPtr--;
                    //if (_min > _poolPtr)
                    //    _min = _poolPtr;
                    eu.Entity = entity;
                    eu.Flags = flags;
                    ObjectPropertyUpdateFlags tmp = 0;
                    if (sendfam)
                        tmp |= ObjectPropertyUpdateFlags.Family;

                    if (sendobj)
                        tmp |= ObjectPropertyUpdateFlags.Object;

                    eu.PropsFlags = tmp;
                    return eu;
                }
            }
            return new EntityUpdate(entity, flags, sendfam, sendobj);
        }

        public static void Free(EntityUpdate eu)
        {
            lock (_poollock)
            {
                if (_poolPtr < MAXSIZE - 1)
                {
                    _poolPtr++;
                    //if (_max < _poolPtr)
                    //    _max = _poolPtr;
                    _pool[_poolPtr] = eu;
                }
            }
        }
    }
}
