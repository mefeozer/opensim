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

using OpenSim.Framework.Monitoring;
using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenSim.Region.ScriptEngine.Yengine
{

    public partial class Yengine
    {
        private int _WakeUpOne = 0;
        public object _WakeUpLock = new object();

        private readonly Dictionary<int, XMRInstance> _RunningInstances = new Dictionary<int, XMRInstance>();

        private bool _SuspendScriptThreadFlag = false;
        private bool _WakeUpThis = false;
        public DateTime _LastRanAt = DateTime.MinValue;
        public long _ScriptExecTime = 0;

        [ThreadStatic]
        private static int _ScriptThreadTID;

        public static bool IsScriptThread => _ScriptThreadTID != 0;

        public void StartThreadWorker(int i, ThreadPriority priority, string sceneName)
        {
            Thread thd;
            if(i >= 0)
                thd = Yengine.StartMyThread(RunScriptThread, "YScript" + i.ToString() + " (" + sceneName +")", priority);
            else
                thd = Yengine.StartMyThread(RunScriptThread, "YScript", priority);
            lock(_WakeUpLock)
                _RunningInstances.Add(thd.ManagedThreadId, null);
        }

        public void StopThreadWorkers()
        {
            lock(_WakeUpLock)
            {
                while(_RunningInstances.Count != 0)
                {
                    Monitor.PulseAll(_WakeUpLock);
                    Monitor.Wait(_WakeUpLock, Watchdog.DEFAULT_WATCHDOG_TIMEOUT_MS / 2);
                }
            }
        }

        /**
         * @brief Something was just added to the Start or Yield queue so
         *        wake one of the RunScriptThread() instances to run it.
         */
        public void WakeUpOne()
        {
            lock(_WakeUpLock)
            {
                _WakeUpOne++;
                Monitor.Pulse(_WakeUpLock);
            }
        }

        public void SuspendThreads()
        {
            lock(_WakeUpLock)
            {
                _SuspendScriptThreadFlag = true;
                Monitor.PulseAll(_WakeUpLock);
            }
        }

        public void ResumeThreads()
        {
            lock(_WakeUpLock)
            {
                _SuspendScriptThreadFlag = false;
                Monitor.PulseAll(_WakeUpLock);
            }
        }

        /**
         * @brief Thread that runs the scripts.
         *
         *        There are NUMSCRIPTHREADWKRS of these.
         *        Each sits in a loop checking the Start and Yield queues for 
         *        a script to run and calls the script as a microthread.
         */
        private void RunScriptThread()
        {
            int tid = System.Threading.Thread.CurrentThread.ManagedThreadId;
            ThreadStart thunk;
            XMRInstance inst;
            bool didevent;
            _ScriptThreadTID = tid;

            while(!_Exiting)
            {
                Yengine.UpdateMyThread();

                lock(_WakeUpLock)
                {
                    // Maybe there are some scripts waiting to be migrated in or out.
                    thunk = null;
                    if(_ThunkQueue.Count > 0)
                        thunk = _ThunkQueue.Dequeue();

                    // Handle 'xmr resume/suspend' commands.
                    else if(_SuspendScriptThreadFlag && !_Exiting)
                    {
                        Monitor.Wait(_WakeUpLock, Watchdog.DEFAULT_WATCHDOG_TIMEOUT_MS / 2);
                        Yengine.UpdateMyThread();
                        continue;
                    }
                }

                if(thunk != null)
                {
                    thunk();
                    continue;
                }

                if(_StartProcessing)
                {
                    // If event just queued to any idle scripts
                    // start them right away.  But only start so
                    // many so we can make some progress on yield
                    // queue.
                    int numStarts;
                    didevent = false;
                    for(numStarts = 5; numStarts >= 0; --numStarts)
                    {
                        lock(_StartQueue)
                            inst = _StartQueue.RemoveHead();

                        if(inst == null)
                            break;
                        if (inst._IState == XMRInstState.SUSPENDED)
                            continue;
                        if (inst._IState != XMRInstState.ONSTARTQ)
                            throw new Exception("bad state");
                        RunInstance(inst, tid);
                        if(_SuspendScriptThreadFlag || _Exiting)
                            continue;
                        didevent = true;
                    }

                    // If there is something to run, run it
                    // then rescan from the beginning in case
                    // a lot of things have changed meanwhile.
                    //
                    // These are considered lower priority than
                    // _StartQueue as they have been taking at
                    // least one quantum of CPU time and event
                    // handlers are supposed to be quick.
                    lock(_YieldQueue)
                        inst = _YieldQueue.RemoveHead();

                    if(inst != null)
                    {
                        if (inst._IState == XMRInstState.SUSPENDED)
                            continue;
                        if (inst._IState != XMRInstState.ONYIELDQ)
                            throw new Exception("bad state");
                        RunInstance(inst, tid);
                        continue;
                    }

                    // If we left something dangling in the _StartQueue or _YieldQueue, go back to check it.
                    if(didevent)
                        continue;
                }

                // Nothing to do, sleep.
                lock(_WakeUpLock)
                {
                    if(!_WakeUpThis && _WakeUpOne <= 0 && !_Exiting)
                        Monitor.Wait(_WakeUpLock, Watchdog.DEFAULT_WATCHDOG_TIMEOUT_MS / 2);

                    _WakeUpThis = false;
                    if(_WakeUpOne > 0 && --_WakeUpOne > 0)
                        Monitor.Pulse(_WakeUpLock);
                }
            }
            lock(_WakeUpLock)
                _RunningInstances.Remove(tid);

            Yengine.MyThreadExiting();
        }

        /**
         * @brief A script instance was just removed from the Start or Yield Queue.
         *        So run it for a little bit then stick in whatever queue it should go in.
         */
        private void RunInstance(XMRInstance inst, int tid)
        {
            _LastRanAt = DateTime.UtcNow;
            _ScriptExecTime -= (long)(_LastRanAt - DateTime.MinValue).TotalMilliseconds;
            inst._IState = XMRInstState.RUNNING;

            lock(_WakeUpLock)
                _RunningInstances[tid] = inst;

            XMRInstState newIState = inst.RunOne();

            lock(_WakeUpLock)
                _RunningInstances[tid] = null;

            HandleNewIState(inst, newIState);
            _ScriptExecTime += (long)(DateTime.UtcNow - DateTime.MinValue).TotalMilliseconds;
        }
    }
}
