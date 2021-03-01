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

using System;
using System.Threading;
using System.Collections.Generic;
using Timer = System.Threading.Timer ;

namespace OpenSim.Framework
{
    public sealed class ExpiringKey<Tkey1> : IDisposable
    {
        private const int MINEXPIRECHECK = 500;

        private Timer _purgeTimer;
        private ReaderWriterLockSlim _rwLock;
        private readonly Dictionary<Tkey1, int> _dictionary;
        private readonly double _startTS;
        private readonly int _expire;

        public ExpiringKey()
        {
            _dictionary = new Dictionary<Tkey1, int>();
            _rwLock = new ReaderWriterLockSlim();
            _expire = MINEXPIRECHECK;
            _startTS = Util.GetTimeStampMS();
        }

        public ExpiringKey(int expireCheckTimeinMS)
        {
            _dictionary = new Dictionary<Tkey1, int>();
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

        ~ExpiringKey()
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

                if (_dictionary.Count == 0)
                {
                    DisposeTimer();
                    return;
                }

                int now = (int)(Util.GetTimeStampMS() - _startTS);
                List<Tkey1> expired = new List<Tkey1>(_dictionary.Count);
                foreach(KeyValuePair<Tkey1,int> kvp in _dictionary)
                {
                    int expire = kvp.Value;
                    if (expire > 0 && expire < now)
                        expired.Add(kvp.Key);
                }

                if (expired.Count > 0)
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

                        foreach (Tkey1 key in expired)
                            _dictionary.Remove(key);
                    }
                    finally
                    {
                        if (gotWriteLock)
                            _rwLock.ExitWriteLock();
                    }
                    if (_dictionary.Count == 0)
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

        public void Add(Tkey1 key)
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

                _dictionary[key] = now;
                CheckTimer();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public void Add(Tkey1 key, int expireMS)
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

                _dictionary[key] = now;
                CheckTimer();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public bool Remove(Tkey1 key)
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
                success = _dictionary.Remove(key);
                if(_dictionary.Count == 0)
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
                _dictionary.Clear();
                DisposeTimer();
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitWriteLock();
            }
        }

        public int Count => _dictionary.Count;

        public bool ContainsKey(Tkey1 key)
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
                return _dictionary.ContainsKey(key);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }
        }

        public bool ContainsKey(Tkey1 key, int expireMS)
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
                if (_dictionary.ContainsKey(key))
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
                        if (expireMS > 0)
                        {
                            expireMS = expireMS > _expire ? expireMS : _expire;
                            now = (int)(Util.GetTimeStampMS() - _startTS) + expireMS;
                        }
                        else
                            now = int.MinValue;

                        _dictionary[key] = now;
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
                    _rwLock.EnterUpgradeableReadLock();
            }
        }

        public bool TryGetValue(Tkey1 key, out int value)
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

                success = _dictionary.TryGetValue(key, out value);
            }
            finally
            {
                if (gotLock)
                    _rwLock.ExitReadLock();
            }

            return success;
        }
    }
}