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
using System.Collections.Concurrent;

using OpenSim.Framework;

namespace OpenSim.Region.Framework.Scenes
{
    public class PriorityQueue
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public delegate bool UpdatePriorityHandler(ref int priority, ISceneEntity entity);

        /// <summary>
        /// Total number of queues (priorities) available
        /// </summary>

        public const int NumberOfQueues = 13; // includes immediate queues, _queueCounts need to be set acording

        /// <summary>
        /// Number of queuest (priorities) that are processed immediately
        /// </summary.
        public const int NumberOfImmediateQueues = 2;
        // first queues are immediate, so no counts
        private static readonly int[] _queueCounts = {0, 0, 8, 8, 5, 4, 3, 2, 1, 1, 1, 1, 1 };
        // this is                     ava, ava, attach, <10m, 20,40,80,160m,320,640,1280, +

        private PriorityMinHeap[] _heaps = new PriorityMinHeap[NumberOfQueues];
        private ConcurrentDictionary<uint, EntityUpdate> _lookupTable;

        // internal state used to ensure the deqeues are spread across the priority
        // queues "fairly". queuecounts is the amount to pull from each queue in
        // each pass. weighted towards the higher priority queues
        private int _nextQueue = 0;
        private int _countFromQueue = 0;

        // next request is a counter of the number of updates queued, it provides
        // a total ordering on the updates coming through the queue and is more
        // lightweight (and more discriminating) than tick count
        private ulong _nextRequest = 0;

        /// <summary>
        /// Lock for enqueue and dequeue operations on the priority queue
        /// </summary>
        private readonly object _syncRoot = new object();
        public object SyncRoot => _syncRoot;

        #region constructor
        public PriorityQueue(int capacity)
        {
            capacity /= 4;
            for (int i = 0; i < _heaps.Length; ++i)
                _heaps[i] = new PriorityMinHeap(capacity);

            _lookupTable = new ConcurrentDictionary<uint, EntityUpdate>();
            _nextQueue = NumberOfImmediateQueues;
            _countFromQueue = _queueCounts[_nextQueue];
        }
#endregion Constructor

#region PublicMethods
        public void Close()
        {
            for (int i = 0; i < _heaps.Length; ++i)
            {
                _heaps[i].Clear();
                _heaps[i] = null;
            }

            _heaps = null;
            _lookupTable.Clear();
            _lookupTable = null;
        }

        /// <summary>
        /// Return the number of items in the queues
        /// </summary>
        public int Count => _lookupTable.Count;

        /// <summary>
        /// Enqueue an item into the specified priority queue
        /// </summary>
        public bool Enqueue(int pqueue, EntityUpdate value)
        {
            ulong entry;
            EntityUpdate existentup;

            uint localid = value.Entity.LocalId;
            if (_lookupTable.TryGetValue(localid, out existentup))
            {
                int eqqueue = existentup.PriorityQueue;

                existentup.UpdateFromNew(value, pqueue);
                value.Free();

                if (pqueue != eqqueue)
                {
                    _heaps[eqqueue].RemoveAt(existentup.PriorityQueueIndex);
                    _heaps[pqueue].Add(existentup);
                }
                return true;
            }

            entry = _nextRequest++;
            value.Update(pqueue, entry);

            _heaps[pqueue].Add(value);
            _lookupTable[localid] = value;
            return true;
        }

        public void Remove(List<uint> ids)
        {
            EntityUpdate lookup;
            foreach (uint localid in ids)
            {
                if (_lookupTable.TryRemove(localid, out lookup))
                {
                    _heaps[lookup.PriorityQueue].RemoveAt(lookup.PriorityQueueIndex);
                    lookup.Free();
                }
            }
        }

        /// <summary>
        /// Remove an item from one of the queues. Specifically, it removes the
        /// oldest item from the next queue in order to provide fair access to
        /// all of the queues
        /// </summary>
        public bool TryDequeue(out EntityUpdate value)
        {
            // If there is anything in immediate queues, return it first no
            // matter what else. Breaks fairness. But very useful.

            for (int iq = 0; iq < NumberOfImmediateQueues; iq++)
            {
                if (_heaps[iq].Count > 0)
                {
                    value = _heaps[iq].RemoveNext();
                    return _lookupTable.TryRemove(value.Entity.LocalId, out value);
                }
            }

            // To get the fair queing, we cycle through each of the
            // queues when finding an element to dequeue.
            // We pull (NumberOfQueues - QueueIndex) items from each queue in order
            // to give lower numbered queues a higher priority and higher percentage
            // of the bandwidth.

            PriorityMinHeap curheap = _heaps[_nextQueue];
            // Check for more items to be pulled from the current queue
            if (_countFromQueue > 0 && curheap.Count > 0)
            {
                --_countFromQueue;

                value = curheap.RemoveNext();
                return _lookupTable.TryRemove(value.Entity.LocalId, out value);
            }

            // Find the next non-immediate queue with updates in it
            for (int i = NumberOfImmediateQueues; i < NumberOfQueues; ++i)
            {
                _nextQueue++;
                if(_nextQueue >= NumberOfQueues)
                    _nextQueue = NumberOfImmediateQueues;
 
                curheap = _heaps[_nextQueue];
                if (curheap.Count == 0)
                    continue;

                _countFromQueue = _queueCounts[_nextQueue];
                --_countFromQueue;

                value = curheap.RemoveNext();
                return _lookupTable.TryRemove(value.Entity.LocalId, out value);
            }

            value = null;
            return false;
        }

        public bool TryOrderedDequeue(out EntityUpdate value)
        {
            for (int iq = 0; iq < NumberOfQueues; ++iq)
            {
                PriorityMinHeap curheap = _heaps[iq];
                if (curheap.Count > 0)
                {
                    value = curheap.RemoveNext();
                    return _lookupTable.TryRemove(value.Entity.LocalId, out value);
                }
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Reapply the prioritization function to each of the updates currently
        /// stored in the priority queues.
        /// </summary
        public void Reprioritize(UpdatePriorityHandler handler)
        {
            int pqueue = 0;
            foreach (EntityUpdate currentEU in _lookupTable.Values)
            {
                if (handler(ref pqueue, currentEU.Entity))
                {
                    // unless the priority queue has changed, there is no need to modify
                    // the entry
                    if (pqueue != currentEU.PriorityQueue)
                    {
                        _heaps[currentEU.PriorityQueue].RemoveAt(currentEU.PriorityQueueIndex);
                        currentEU.PriorityQueue = pqueue;
                        _heaps[pqueue].Add(currentEU);
                    }
                }
                else
                {
                    break;
                }
            }
        }

        /// <summary>
        /// </summary>
        public override string ToString()
        {
            string s = "";
            for (int i = 0; i < NumberOfQueues; i++)
                s += string.Format("{0,7} ", _heaps[i].Count);
            return s;
        }

#endregion PublicMethods
    }

    public class PriorityMinHeap
    {
        public const int MIN_CAPACITY = 16;

        private EntityUpdate[] _items;
        private int _size;
        private readonly int minCapacity;

        public PriorityMinHeap(int _capacity)
        {
            minCapacity = MIN_CAPACITY;
            _items = new EntityUpdate[_capacity];
            _size = 0;
        }

        public int Count => _size;

        private bool BubbleUp(int index)
        {
            EntityUpdate tmp;
            EntityUpdate item = _items[index];
            ulong itemEntryOrder = item.EntryOrder;
            int current, parent;

            for (current = index, parent = (current - 1) / 2;
                    current > 0 && _items[parent].EntryOrder > itemEntryOrder;
                    current = parent, parent = (current - 1) / 2)
            {
                tmp = _items[parent];
                tmp.PriorityQueueIndex = current;
                _items[current] = tmp;
            }

            if (current != index)
            {
                item.PriorityQueueIndex = current;
                _items[current] = item;
                return true;
            }
            return false;
        }

        private void BubbleDown(int index)
        {
            if(_size < 2)
                return;

            EntityUpdate childItem;
            EntityUpdate childItemR;
            EntityUpdate item = _items[index];

            ulong itemEntryOrder = item.EntryOrder;
            int current;
            int child;
            int childlimit = _size - 1;

            for (current = index, child = 2 * current + 1;
                        current < _size / 2;
                        current = child, child = 2 * current + 1)
            {
                childItem = _items[child];
                if (child < childlimit)
                {
                    childItemR = _items[child + 1];

                    if(childItem.EntryOrder > childItemR.EntryOrder)
                    {
                        childItem = childItemR;
                        ++child;
                    }
                }
                if (childItem.EntryOrder >= itemEntryOrder)
                    break;

                childItem.PriorityQueueIndex = current;
                _items[current] = childItem;
            }

            if (current != index)
            {
                item.PriorityQueueIndex = current;
                _items[current] = item;
            }
        }

        public void Add(EntityUpdate value)
        {
            if (_size == _items.Length)
            {
                int newcapacity = (int)(_items.Length * 200L / 100L);
                if (newcapacity < _items.Length + MIN_CAPACITY)
                    newcapacity = _items.Length + MIN_CAPACITY;
                Array.Resize<EntityUpdate>(ref _items, newcapacity);
            }

            value.PriorityQueueIndex = _size;
            _items[_size] = value;

            BubbleUp(_size);
            ++_size;
        }

        public void Clear()
        {
            for (int index = 0; index < _size; ++index)
                _items[index].Free();
            _size = 0;
        }

        public void RemoveAt(int index)
        {
            if (_size == 0)
                throw new InvalidOperationException("Heap is empty");
            if (index >= _size)
                throw new ArgumentOutOfRangeException("index");

            --_size;
            if (_size > 0)
            {
                if (index != _size)
                {
                    EntityUpdate tmp = _items[_size];
                    tmp.PriorityQueueIndex = index;
                    _items[index] = tmp;

                    _items[_size] = null;
                    if (!BubbleUp(index))
                        BubbleDown(index);
                }
            }
            else if (_items.Length > 4 * minCapacity)
                _items = new EntityUpdate[minCapacity];
        }

        public EntityUpdate RemoveNext()
        {
            if (_size == 0)
                throw new InvalidOperationException("Heap is empty");

            EntityUpdate item = _items[0];
            --_size;
            if (_size > 0)
            {
                EntityUpdate tmp = _items[_size];
                tmp.PriorityQueueIndex = 0;
                _items[0] = tmp;
                _items[_size] = null;

                BubbleDown(0);
            }
            else if (_items.Length > 4 * minCapacity)
                _items = new EntityUpdate[minCapacity];

            return item;
        }

        public bool Remove(EntityUpdate value)
        {
            int index = value.PriorityQueueIndex;
            if (index != -1)
            {
                RemoveAt(index);
                return true;
            }
            return false;
        }
    }
}
