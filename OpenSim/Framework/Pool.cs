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

namespace OpenSim.Framework
{
    /// <summary>
    /// Naive pool implementation.
    /// </summary>
    /// <remarks>
    /// Currently assumes that objects are in a useable state when returned.
    /// </remarks>
    public class Pool<T>
    {
        /// <summary>
        /// Number of objects in the pool.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_pool)
                    return _pool.Count;
            }
        }

        private readonly Stack<T> _pool;

        /// <summary>
        /// Maximum pool size.  Beyond this, any returned objects are not pooled.
        /// </summary>
        private readonly int _maxPoolSize;

        private readonly Func<T> _createFunction;

        public Pool(Func<T> createFunction, int maxSize)
        {
            _maxPoolSize = maxSize;
            _createFunction = createFunction;
            _pool = new Stack<T>(_maxPoolSize);
        }

        public T GetObject()
        {
            lock (_pool)
            {
                if (_pool.Count > 0)
                    return _pool.Pop();
                else
                    return _createFunction();
            }
        }

        public void ReturnObject(T obj)
        {
            lock (_pool)
            {
                if (_pool.Count >= _maxPoolSize)
                    return;
                else
                    _pool.Push(obj);
            }
        }
    }
}