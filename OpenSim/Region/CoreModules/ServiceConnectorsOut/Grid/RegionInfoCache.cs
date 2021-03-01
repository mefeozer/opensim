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
using System.Threading;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using OpenSim.Framework;
using OpenMetaverse;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using Timer = System.Threading.Timer;
using ReaderWriterLockSlim = System.Threading.ReaderWriterLockSlim;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Grid
{
    public class RegionInfoCache
    {
        // private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private const int CACHE_EXPIRATION_SECONDS = 120; // 2 minutes  opensim regions change a lot
        private const int CACHE_PURGE_TIME = 60000; // milliseconds
        public const ulong HANDLEMASK = 0xffffff00ffffff00ul;
        public const ulong HANDLECOORDMASK = 0xffffff00ul;

        private static readonly object _creationLock = new object();

        private static readonly Dictionary<ulong, int> _expireControl = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, GridRegion> _byHandler = new Dictionary<ulong, GridRegion>();
        private static readonly Dictionary<string, GridRegion> _byName = new Dictionary<string, GridRegion>();
        private static readonly Dictionary<UUID, GridRegion> _byUUID = new Dictionary<UUID, GridRegion>();
        // includes handles to the inside of large regions
        private static readonly Dictionary<ulong, GridRegion> _innerHandles = new Dictionary<ulong, GridRegion>();

        //private static bool disposed;
        private static ReaderWriterLockSlim _rwLock;
        private static Timer _timer;
        private static double starttimeS;

        public RegionInfoCache()
        {
            lock(_creationLock)
            {
                if(_rwLock == null)
                {
                    starttimeS = Util.GetTimeStamp();

                    _rwLock = new ReaderWriterLockSlim();
                    if (_timer == null)
                        _timer = new Timer(PurgeCache, null, CACHE_PURGE_TIME, Timeout.Infinite);
                }
            }
        }
        /* not if static
        ~RegionInfoCache()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true;
                if (_rwLock != null)
                {
                    _rwLock.Dispose();
                    _rwLock = null;
                }

                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }
        */

        public bool AddOrUpdate(GridRegion rinfo)
        {
            return AddOrUpdate(rinfo, CACHE_EXPIRATION_SECONDS);
        }

        public bool AddOrUpdate(GridRegion rinfo, int expire)
        {
            //if (rinfo == null || disposed)
            if (rinfo == null)
                return false;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }

                int newexpire = (int)(Util.GetTimeStamp() - starttimeS) + expire;

                ulong handle = rinfo.RegionHandle & HANDLEMASK;
                if (_expireControl.ContainsKey(handle))
                {
                    if (_expireControl[handle] < newexpire)
                        _expireControl[handle] = newexpire;
                }
                else
                {
                    _expireControl[handle] = newexpire;
                }

                if (_innerHandles.TryGetValue(handle, out GridRegion oldr))
                    removeFromInner(oldr);
                addToInner(rinfo);

                _byHandler[handle] = rinfo;
                _byName[rinfo.RegionName.ToLowerInvariant()] = rinfo;
                _byUUID[rinfo.RegionID] = rinfo;
                return true;
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public void Cache(GridRegion rinfo)
        {
            AddOrUpdate(rinfo, CACHE_EXPIRATION_SECONDS);
        }

        public void Cache(GridRegion rinfo, int expireSeconds)
        {
            AddOrUpdate(rinfo, expireSeconds);
        }

        public void Cache(GridRegion rinfo, float expireSeconds)
        {
            AddOrUpdate(rinfo, (int)expireSeconds);
        }

        public void Cache(UUID scopeID, GridRegion rinfo)
        {
            AddOrUpdate(rinfo, CACHE_EXPIRATION_SECONDS);
        }

        public void CacheLocal(GridRegion rinfo)
        {
            AddOrUpdate(rinfo, 100000);
        }

        public void CacheNearNeighbour(UUID scopeID, GridRegion rinfo)
        {
            AddOrUpdate(rinfo, CACHE_EXPIRATION_SECONDS);
        }

        public void Cache(UUID scopeID, GridRegion rinfo, int expireSeconds)
        {
            AddOrUpdate(rinfo, expireSeconds);
        }

        public void Cache(UUID scopeID, GridRegion rinfo, float expireSeconds)
        {
            AddOrUpdate(rinfo, (int)expireSeconds);
        }

        public void Clear()
        {
            //if (disposed)
            //    return;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }

                _expireControl.Clear();
                _byHandler.Clear();
                _byName.Clear();
                _byUUID.Clear();
                _innerHandles.Clear();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public bool Contains(ulong handle)
        {
            //if (disposed)
            //    return false;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                return _byHandler.ContainsKey(handle & HANDLEMASK);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        public bool Contains(GridRegion rinfo)
        {
            //if (disposed)
            //    return false;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                if(!_byHandler.TryGetValue(rinfo.RegionHandle & HANDLEMASK, out GridRegion rcur))
                    return false;

                return rcur.RegionID == rinfo.RegionID &&
                    rcur.RegionSizeX == rinfo.RegionSizeX &&
                    rcur.RegionSizeY == rinfo.RegionSizeY;
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        public bool Contains(UUID scope, ulong handle)
        {
            return Contains(handle);
        }

        public bool Contains(UUID scope, GridRegion rinfo)
        {
            return Contains(rinfo);
        }

        public int Count()
        {
            //if (disposed)
            //    return 0;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                return _byName.Count;
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        public void Remove(GridRegion rinfo)
        {
            if (rinfo == null)
                return;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }

                _byName.Remove(rinfo.RegionName.ToLowerInvariant());
                _byUUID.Remove(rinfo.RegionID);

                ulong handle = rinfo.RegionHandle & HANDLEMASK;
                _byHandler.Remove(handle);
                removeFromInner(rinfo);
                if (_expireControl.ContainsKey(handle))
                {
                    _expireControl.Remove(handle);
                    if (_expireControl.Count == 0)
                    {
                        _byHandler.Clear();
                        _byName.Clear();
                        _byUUID.Clear();
                        _innerHandles.Clear();
                    }
                }
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public void Remove(ulong regionHandle)
        {
            //if(disposed)
            //    return;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }

                regionHandle &= HANDLEMASK;

                if (_byHandler.TryGetValue(regionHandle, out GridRegion r))
                {
                    _byName.Remove(r.RegionName.ToLowerInvariant());
                    _byUUID.Remove(r.RegionID);
                    removeFromInner(r);
                    _byHandler.Remove(regionHandle);
                }

                if (_expireControl.ContainsKey(regionHandle))
                {
                    _expireControl.Remove(regionHandle);
                    if (_expireControl.Count == 0)
                    {
                        _byHandler.Clear();
                        _byName.Clear();
                        _byUUID.Clear();
                        _innerHandles.Clear();
                    }
                }
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public void Remove(UUID scopeID, GridRegion rinfo)
        {
            Remove(rinfo);
        }

        public void Remove(UUID scopeID, ulong regionHandle)
        {
            Remove(regionHandle);
        }

        public GridRegion Get(UUID scopeID, UUID regionID, out bool inCache)
        {
            inCache = TryGet(regionID, out GridRegion rinfo);
            return rinfo;
        }

        public GridRegion Get(UUID scopeID, ulong handle, out bool inCache)
        {
            inCache = TryGet(handle, out GridRegion rinfo);
            return rinfo;
        }

        public GridRegion Get(UUID scopeID, string name, out bool inCache)
        {
            inCache = TryGet(name, out GridRegion rinfo);
            return rinfo;
        }

        public GridRegion Get(UUID scopeID, uint x, uint y, out bool inCache)
        {
            inCache = TryGet(x, y, out GridRegion rinfo);
            return rinfo;
        }

        public bool TryGet(UUID regionID, out GridRegion rinfo)
        {
            /*
            if (disposed)
            {
                rinfo = null;
                return false;
            }
            */

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                return _byUUID.TryGetValue(regionID, out rinfo);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        public bool TryGet(ulong handle, out GridRegion rinfo)
        {
            /*
            if (disposed)
            {
                rinfo = null;
                return false;
            }
            */

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                handle &= HANDLEMASK;
                if (_byHandler.TryGetValue(handle, out rinfo))
                    return true;

                return _innerHandles.TryGetValue(handle, out rinfo);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        public bool TryGet(string name, out GridRegion rinfo)
        {
            /*
            if (disposed)
            {
                rinfo = null;
                return false;
            }
            */

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                return _byName.TryGetValue(name.ToLowerInvariant(), out rinfo);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        public bool TryGet(uint x, uint y, out GridRegion rinfo)
        {
            /*
            if (disposed)
            {
                rinfo = null;
                return false;
            }
            */

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                ulong handle = x & HANDLECOORDMASK;
                handle <<= 32;
                handle |= y & HANDLECOORDMASK;

                if (_byHandler.TryGetValue(handle, out rinfo))
                    return true;

                if (!_innerHandles.TryGetValue(handle, out rinfo))
                    return false;

                // extra check, possible redundant
                int test = rinfo.RegionLocX;
                if (x < test)
                {
                    rinfo = null;
                    return false;
                }
                test += rinfo.RegionSizeX;
                if (x >= test)
                {
                    rinfo = null;
                    return false;
                }
                test = rinfo.RegionLocY;
                if (y < test)
                {
                    rinfo = null;
                    return false;
                }
                test += rinfo.RegionSizeY;
                if (y < test)
                    return true;

                rinfo = null;
                return false;
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        private void PurgeCache(object ignored)
        {
            //if(disposed || _expireControl.Count == 0)
            if (_expireControl.Count == 0)
                return;

            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }

                int now = (int)(Util.GetTimeStamp() - starttimeS);
                List<ulong> toexpire = new List<ulong>(_expireControl.Count);
                foreach (KeyValuePair<ulong, int> kvp in _expireControl)
                {
                    if (kvp.Value < now)
                        toexpire.Add(kvp.Key);
                }

                if (toexpire.Count == 0)
                    return;

                ulong h;
                for (int i = 0; i < toexpire.Count; i++)
                {
                    h = toexpire[i];
                    if (_byHandler.TryGetValue(h, out GridRegion r))
                    {
                        _byName.Remove(r.RegionName.ToLowerInvariant());
                        _byUUID.Remove(r.RegionID);
                        removeFromInner(r);
                        _byHandler.Remove(h);
                    }
                    _expireControl.Remove(h);
                }
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
                if (_timer != null)
                    _timer.Change(CACHE_PURGE_TIME, Timeout.Infinite);
            }
        }

        private void addToInner(GridRegion region)
        {
            int rsx = region.RegionSizeX;
            int rsy = region.RegionSizeY;

            if (rsx < 257 && rsy < 257)
                return;

            rsx >>= 8;
            rsy >>= 8;

            ulong handle = region.RegionHandle & HANDLEMASK;
            fastRegionHandle fh = new fastRegionHandle(handle);
            uint startY = fh.y;
            for (int i = 0; i < rsx; i++)
            {
                for (int j = 0; j < rsy; j++)
                {
                    _innerHandles[fh.toHandle()] = region;
                    fh.y += 256;
                }

                fh.y = startY;
                fh.x += 256;
            }
        }

        private void removeFromInner(GridRegion region)
        {
            int rsx = region.RegionSizeX;
            int rsy = region.RegionSizeY;

            if (rsx < 257 && rsy < 257)
                return;

            rsx >>= 8;
            rsy >>= 8;
            ulong handle = region.RegionHandle & HANDLEMASK;
            fastRegionHandle fh = new fastRegionHandle(handle);
            uint startY = fh.y;
            for (int i = 0; i < rsx; i++)
            {
                for (int j = 0; j < rsy; j++)
                {
                    _innerHandles.Remove(fh.toHandle());
                    fh.y += 256;
                }

                fh.y = startY;
                fh.x += 256;
            }
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 8, Pack = 8)]
    public class fastRegionHandle
    {
        [FieldOffset(0)] public ulong handle;
        [FieldOffset(0)] public uint y;
        [FieldOffset(4)] public uint x;

        public fastRegionHandle(ulong h)
        {
            handle = h;
            if (!BitConverter.IsLittleEndian)
            {
                x = (uint)(handle >> 32);
                y = (uint)handle;
            }
            y &= 0xffffff00;
            x &= 0xffffff00;
        }

        public fastRegionHandle(uint px, uint py)
        {
            y = py & 0xffffff00;
            x = px & 0xffffff00;
        }

        public ulong toHandle()
        {
            if (BitConverter.IsLittleEndian)
                return handle;
            else
                return  (ulong)x << 32 | y ;
        }

        public static bool operator ==(fastRegionHandle value1, fastRegionHandle value2)
        {
            return value1.handle == value2.handle;
        }
        public static bool operator !=(fastRegionHandle value1, fastRegionHandle value2)
        {
            return value1.handle != value2.handle;
        }
        public override int GetHashCode()
        {
            return handle.GetHashCode();
        }
        public override bool Equals(object obj)
        {
            if(obj == null)
                return false;
            fastRegionHandle p = obj as fastRegionHandle;
            return p.handle == handle;
        }
    }
}
