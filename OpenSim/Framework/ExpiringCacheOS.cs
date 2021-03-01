/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * - Redistribution and use in source and binary forms, with or without
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.org nor the names
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

// this is a lighter alternative to libomv, no sliding option

using System;
using System.Threading;
using System.Collections.Generic;
using Timer = System.Threading.Timer;

namespace OpenSim.Framework
{
    public sealed class ExpiringCacheOS<TKey1, TValue1> : IDisposable
    {
        private const int MINEXPIRECHECK = 500;

        private Timer _purgeTimer;
        private ReaderWriterLockSlim _rwLock;
        private readonly Dictionary<TKey1, int> _expireControl;
        private readonly Dictionary<TKey1, TValue1> _values;
        private readonly double _startTS;
        private readonly int _expire;

        public ExpiringCacheOS()
        {
            _expireControl = new Dictionary<TKey1, int>();
            _values = new Dictionary<TKey1, TValue1>();
            _rwLock = new ReaderWriterLockSlim();
            _expire = MINEXPIRECHECK;
            _startTS = Util.GetTimeStampMS();
        }

        public ExpiringCacheOS(int expireCheckTimeinMS)
        {
            _expireControl = new Dictionary<TKey1, int>();
            _values = new Dictionary<TKey1, TValue1>();
            _rwLock = new ReaderWriterLockSlim();
            _startTS = Util.GetTimeStampMS();
            _expire = expireCheckTimeinMS > MINEXPIRECHECK ? _expire = expireCheckTimeinMS : MINEXPIRECHECK;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void CheckTimer()
        {
            if (_purgeTimer == null)
            {
                _purgeTimer = new Timer(Purge, null, _expire, Timeout.Infinite);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void DisposeTimer()
        {
            if (_purgeTimer != null)
            {
                _purgeTimer.Dispose();
                _purgeTimer = null;
            }
        }

        ~ExpiringCacheOS()
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
            if (_rwLock != null)
            {
                DisposeTimer();
                _rwLock.Dispose();
                _rwLock = null;
            }
        }

        private void Purge(object ignored)
        {
            bool gotLock = false;

            try
            {
                try { }
                finally
                {
                    _rwLock.EnterUpgradeableReadLock();
                    gotLock = true;
                }

                if (_expireControl.Count == 0)
                {
                    DisposeTimer();
                    return;
                }

                int now = (int)(Util.GetTimeStampMS() - _startTS);
                List<TKey1> expired = new List<TKey1>(_expireControl.Count);
                foreach(KeyValuePair<TKey1, int> kvp in _expireControl)
                {
                    int expire = kvp.Value;
                    if(expire > 0 && expire < now)
                        expired.Add(kvp.Key);
                }

                if(expired.Count > 0)
                {
                    bool gotWriteLock = false;
                    try
                    {
                        try { }
                        finally
                        {
                            _rwLock.EnterWriteLock();
                            gotWriteLock = true;
                        }

                        foreach (TKey1 key in expired)
                        {
                            _expireControl.Remove(key);
                            _values.Remove(key);
                        }
                    }
                    finally
                    {
                        if (gotWriteLock)
                            _rwLock.ExitWriteLock();
                    }
                    if (_expireControl.Count == 0)
                        DisposeTimer();
                    else
                        _purgeTimer.Change(_expire, Timeout.Infinite);
                }
                else
                    _purgeTimer.Change(_expire, Timeout.Infinite);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitUpgradeableReadLock();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdate(TKey1 key, TValue1 val)
        {
            Add(key, val);
        }

        public void Add(TKey1 key, TValue1 val)
        {
            bool gotLock = false;
            int now = (int)(Util.GetTimeStampMS() - _startTS) + _expire;

            try
            {
                try { }
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }

                _expireControl[key] = now;
                _values[key] = val;
                CheckTimer();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdate(TKey1 key, TValue1 val, int expireSeconds)
        {
            Add(key, val, expireSeconds * 1000);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void AddOrUpdate(TKey1 key, TValue1 val, double expireSeconds)
        {
            Add(key, val, (int)(expireSeconds * 1000));
        }

        public void Add(TKey1 key, TValue1 val, int expireMS)
        {
            bool gotLock = false;
            int now;
            if (expireMS > 0)
            {
                expireMS = expireMS > _expire ? expireMS : _expire;
                now = (int)(Util.GetTimeStampMS() - _startTS) + expireMS;
            }
            else
                now = int.MinValue;

            try
            {
                try { }
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }

                _expireControl[key] = now;
                _values[key] = val;
                CheckTimer();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public bool Remove(TKey1 key)
        {
            bool success;
            bool gotLock = false;

            try
            {
                try {}
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }
                success = _expireControl.Remove(key);
                success |= _values.Remove(key);
                if(_expireControl.Count == 0)
                    DisposeTimer();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }

            return success;
        }

        public void Clear()
        {
            bool gotLock = false;

            try
            {
                try {}
                finally
                {
                    _rwLock.EnterWriteLock();
                    gotLock = true;
                }
                DisposeTimer();
                _expireControl.Clear();
                _values.Clear();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public int Count => _expireControl.Count;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool Contains(TKey1 key)
        {
            return ContainsKey(key);
        }

        public bool ContainsKey(TKey1 key)
        {
            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }
                return _expireControl.ContainsKey(key);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool Contains(TKey1 key, int expireMS)
        {
            return ContainsKey(key, expireMS);
        }

        public bool ContainsKey(TKey1 key, int expireMS)
        {
            bool gotLock = false;
            try
            {
                try { }
                finally
                {
                    _rwLock.EnterUpgradeableReadLock();
                    gotLock = true;
                }
                if(_expireControl.ContainsKey(key))
                {
                    bool gotWriteLock = false;
                    try
                    {
                        try { }
                        finally
                        {
                            _rwLock.EnterWriteLock();
                            gotWriteLock = true;
                        }
                        int now;
                        if(expireMS > 0)
                        {
                            expireMS = expireMS > _expire ? expireMS : _expire;
                            now = (int)(Util.GetTimeStampMS() - _startTS) + expireMS;
                        }
                        else
                            now = int.MinValue;

                        _expireControl[key] = now;
                        return true;
                    }
                    finally
                    {
                        if (gotWriteLock)
                            _rwLock.ExitWriteLock();
                    }
                }
                return false;
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitUpgradeableReadLock();
            }
        }

        public bool TryGetValue(TKey1 key, out TValue1 value)
        {
            bool success;
            bool gotLock = false;

            try
            {
                try {}
                finally
                {
                    _rwLock.EnterReadLock();
                    gotLock = true;
                }

                success = _values.TryGetValue(key, out value);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }

            return success;
        }

        public bool TryGetValue(TKey1 key, int expireMS, out TValue1 value)
        {
            bool success;
            bool gotLock = false;

            try
            {
                try { }
                finally
                {
                    _rwLock.EnterUpgradeableReadLock();
                    gotLock = true;
                }

                success = _values.TryGetValue(key, out value);
                if(success)
                {
                    bool gotWriteLock = false;
                    try
                    {
                        try { }
                        finally
                        {
                            _rwLock.EnterWriteLock();
                            gotWriteLock = true;
                        }
                        int now;
                        if(expireMS > 0)
                        {
                            expireMS = expireMS > _expire ? expireMS : _expire;
                            now = (int)(Util.GetTimeStampMS() - _startTS) + expireMS;
                        }
                        else
                            now = int.MinValue;

                        _expireControl[key] = now;
                    }
                    finally
                    {
                        if (gotWriteLock)
                            _rwLock.ExitWriteLock();
                    }
                }
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitUpgradeableReadLock();
            }

            return success;
        }

        public ICollection<TValue1> Values
        {
            get
            {
                bool gotLock = false;
                try
                {
                    try { }
                    finally
                    {
                        _rwLock.EnterUpgradeableReadLock();
                        gotLock = true;
                    }
                    return _values.Values;
                }
                finally
                {
                    if (gotLock)
                        _rwLock.ExitUpgradeableReadLock();
                }
            }
        }

        public ICollection<TKey1> Keys
        {
            get
            {
                bool gotLock = false;
                try
                {
                    try { }
                    finally
                    {
                        _rwLock.EnterUpgradeableReadLock();
                        gotLock = true;
                    }
                    return _values.Keys;
                }
                finally
                {
                    if (gotLock)
                        _rwLock.ExitUpgradeableReadLock();
                }
            }
        }
    }
}