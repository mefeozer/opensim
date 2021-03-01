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

namespace OpenSim.Framework
{
    /// <summary>
    /// Stores two synchronized collections: a mutable dictionary and an
    /// immutable array. Slower inserts/removes than a normal dictionary,
    /// but provides safe iteration while maintaining fast hash lookups
    /// </summary>
    /// <typeparam name="TKey">Key type to use for hash lookups</typeparam>
    /// <typeparam name="TValue">Value type to store</typeparam>
    public sealed class MapAndArray<TKey, TValue>
    {
        private Dictionary<TKey, TValue> _dict;
        private TValue[] _array;

        /// <summary>Number of values currently stored in the collection</summary>
        public int Count => _dict.Count;

        /// <summary>NOTE: This collection is thread safe. You do not need to
        /// acquire a lock to add, remove, or enumerate entries. This
        /// synchronization object should only be locked for larger
        /// transactions</summary>
        private readonly object _syncRoot = new object();
        public object SyncRoot => _syncRoot;

        /// <summary>
        /// Constructor
        /// </summary>
        public MapAndArray()
        {
            _dict = new Dictionary<TKey, TValue>();
            _array = null;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="capacity">Initial capacity of the dictionary</param>
        public MapAndArray(int capacity)
        {
            _dict = new Dictionary<TKey, TValue>(capacity);
            _array = null;
        }

        /// <summary>
        /// Adds a key/value pair to the collection, or updates an existing key
        /// with a new value
        /// </summary>
        /// <param name="key">Key to add or update</param>
        /// <param name="value">Value to add</param>
        /// <returns>True if a new key was added, false if an existing key was
        /// updated</returns>
        public bool AddOrReplace(TKey key, TValue value)
        {
            lock (_syncRoot)
            {
                bool containedKey = _dict.ContainsKey(key);

                _dict[key] = value;
                _array = null;

                return !containedKey;
            }
        }

        /// <summary>
        /// Adds a key/value pair to the collection. This will throw an
        /// exception if the key is already present in the collection
        /// </summary>
        /// <param name="key">Key to add or update</param>
        /// <param name="value">Value to add</param>
        /// <returns>Index of the inserted item</returns>
        public int Add(TKey key, TValue value)
        {
            lock (_syncRoot)
            {
                _dict.Add(key, value);
                _array = null;
                return _dict.Count;
            }
        }

        /// <summary>
        /// Removes a key/value pair from the collection
        /// </summary>
        /// <param name="key">Key to remove</param>
        /// <returns>True if the key was found and removed, otherwise false</returns>
        public bool Remove(TKey key)
        {
            lock (_syncRoot)
            {
                bool removed = _dict.Remove(key);
                _array = null;
                return removed;
            }
        }

        /// <summary>
        /// Determines whether the collections contains a specified key
        /// </summary>
        /// <param name="key">Key to search for</param>
        /// <returns>True if the key was found, otherwise false</returns>
        public bool ContainsKey(TKey key)
        {
            lock (_syncRoot)
                return _dict.ContainsKey(key);
        }

        /// <summary>
        /// Gets the value associated with the specified key
        /// </summary>
        /// <param name="key">Key of the value to get</param>
        /// <param name="value">Will contain the value associated with the
        /// given key if the key is found. If the key is not found it will
        /// contain the default value for the type of the value parameter</param>
        /// <returns>True if the key was found and a value was retrieved,
        /// otherwise false</returns>
        public bool TryGetValue(TKey key, out TValue value)
        {
            lock (_syncRoot)
                return _dict.TryGetValue(key, out value);
        }

        /// <summary>
        /// Clears all key/value pairs from the collection
        /// </summary>
        public void Clear()
        {
            lock (_syncRoot)
            {
                _dict = new Dictionary<TKey, TValue>();
                _array = null;
            }
        }

        /// <summary>
        /// Gets a reference to the immutable array of values stored in this
        /// collection. This array is thread safe for iteration
        /// </summary>
        /// <returns>A thread safe reference ton an array of all of the stored
        /// values</returns>
        public TValue[] GetArray()
        {
            lock (_syncRoot)
            {
                if (_array == null)
                {
                    if(_dict.Count == 0)
                        return new TValue[0];
                    _array = new TValue[_dict.Count];
                    _dict.Values.CopyTo(_array, 0);
                }
                return _array;
            }
        }
    }
}
