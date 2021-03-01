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

namespace OpenSim.Region.ScriptEngine.Yengine
{
    /**
     * @brief Implements a queue of XMRInstance's.
     *        Do our own queue to avoid shitty little mallocs.
     *
     * Note: looping inst._NextInst and _PrevInst back to itself
     *       when inst is removed from a queue is purely for debug.
     */
    public class XMRInstQueue
    {
        private XMRInstance _Head = null;
        private XMRInstance _Tail = null;

        /**
         * @brief Insert instance at head of queue (in front of all others)
         * @param inst = instance to insert
         */
        public void InsertHead(XMRInstance inst)
        {
            if(inst._PrevInst != inst || inst._NextInst != inst)
                throw new Exception("already in list");

            inst._PrevInst = null;
            if((inst._NextInst = _Head) == null)
                _Tail = inst;
            else
                _Head._PrevInst = inst;

            _Head = inst;
        }

        /**
         * @brief Insert instance at tail of queue (behind all others)
         * @param inst = instance to insert
         */
        public void InsertTail(XMRInstance inst)
        {
            if(inst._PrevInst != inst || inst._NextInst != inst)
                throw new Exception("already in list");

            inst._NextInst = null;
            if((inst._PrevInst = _Tail) == null)
                _Head = inst;
            else
                _Tail._NextInst = inst;

            _Tail = inst;
        }

        /**
         * @brief Insert instance before another element in queue
         * @param inst  = instance to insert
         * @param after = element that is to come after one being inserted
         */
        public void InsertBefore(XMRInstance inst, XMRInstance after)
        {
            if(inst._PrevInst != inst || inst._NextInst != inst)
                throw new Exception("already in list");

            if(after == null)
                InsertTail(inst);
            else
            {
                inst._NextInst = after;
                inst._PrevInst = after._PrevInst;
                if(inst._PrevInst == null)
                    _Head = inst;
                else
                    inst._PrevInst._NextInst = inst;
                after._PrevInst = inst;
            }
        }

        /**
         * @brief Peek to see if anything in queue
         * @returns first XMRInstance in queue but doesn't remove it
         *          null if queue is empty
         */
        public XMRInstance PeekHead()
        {
            return _Head;
        }

        /**
         * @brief Remove first element from queue, if any
         * @returns null if queue is empty
         *          else returns first element in queue and removes it
         */
        public XMRInstance RemoveHead()
        {
            XMRInstance inst = _Head;
            if(inst != null)
            {
                if((_Head = inst._NextInst) == null)
                    _Tail = null;
                else
                    _Head._PrevInst = null;

                inst._NextInst = inst;
                inst._PrevInst = inst;
            }
            return inst;
        }

        /**
         * @brief Remove last element from queue, if any
         * @returns null if queue is empty
         *          else returns last element in queue and removes it
         */
        public XMRInstance RemoveTail()
        {
            XMRInstance inst = _Tail;
            if(inst != null)
            {
                if((_Tail = inst._PrevInst) == null)
                    _Head = null;
                else
                    _Tail._NextInst = null;

                inst._NextInst = inst;
                inst._PrevInst = inst;
            }
            return inst;
        }

        /**
         * @brief Remove arbitrary element from queue, if any
         * @param inst = element to remove (assumed to be in the queue)
         * @returns with element removed
         */
        public void Remove(XMRInstance inst)
        {
            XMRInstance next = inst._NextInst;
            XMRInstance prev = inst._PrevInst;
            if(prev == inst || next == inst)
                throw new Exception("not in a list");

            if(next == null)
            {
                if(_Tail != inst)
                    throw new Exception("not in this list");

                _Tail = prev;
            }
            else
                next._PrevInst = prev;

            if(prev == null)
            {
                if(_Head != inst)
                    throw new Exception("not in this list");

                _Head = next;
            }
            else
                prev._NextInst = next;

            inst._NextInst = inst;
            inst._PrevInst = inst;
        }
    }
}
