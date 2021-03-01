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
using System.Collections.Generic;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.ScriptEngine.Yengine
{
    public partial class XMRInstance
    {
        /************************************************************************************\
         * This module contains these externally useful methods:                            *
         *   PostEvent() - queues an event to script and wakes script thread to process it  *
         *   RunOne() - runs script for a time slice or until it volunteers to give up cpu  *
         *   CallSEH() - runs in the microthread to call the event handler                  *
        \************************************************************************************/

        /**
         * @brief This can be called in any thread (including the script thread itself)
         *        to queue event to script for processing.
         */
        public void PostEvent(EventParams evt)
        {
            if(!_eventCodeMap.TryGetValue(evt.EventName, out ScriptEventCode evc))
                return;

             // Put event on end of event queue.
            bool startIt = false;
            bool wakeIt = false;
            lock(_QueueLock)
            {
                bool construct = _IState == XMRInstState.CONSTRUCT;

                 // Ignore event if we don't even have such an handler in any state.
                 // We can't be state-specific here because state might be different
                 // by the time this event is dequeued and delivered to the script.
                if(!construct &&                      // make sure _HaveEventHandlers is filled in 
                        (uint)evc < (uint)_HaveEventHandlers.Length &&
                        !_HaveEventHandlers[(int)evc])  // don't bother if we don't have such a handler in any state
                    return;

                // Not running means we ignore any incoming events.
                // But queue if still constructing because _Running is not yet valid.

                if(!_Running && !construct)
                {
                    if(_IState == XMRInstState.SUSPENDED)
                    {
                        if(evc == ScriptEventCode.state_entry && _EventQueue.Count == 0)
                        {
                            LinkedListNode<EventParams> llns = new LinkedListNode<EventParams>(evt);
                            _EventQueue.AddFirst(llns);
                        }
                    }
                    return;
                }

                if(_minEventDelay != 0)
                {
                    switch (evc)
                    {
                        // ignore some events by time set by llMinEventDelay
                        case ScriptEventCode.collision:
                        case ScriptEventCode.land_collision:
                        case ScriptEventCode.listen:
                        case ScriptEventCode.not_at_target:
                        case ScriptEventCode.not_at_rot_target:
                        case ScriptEventCode.no_sensor:
                        case ScriptEventCode.sensor:
                        case ScriptEventCode.timer:
                        case ScriptEventCode.touch:
                        {
                            double now = Util.GetTimeStamp();
                            if (now < _nextEventTime)
                                return;
                            _nextEventTime = now + _minEventDelay;
                            break;
                        }
                        case ScriptEventCode.changed:
                        {
                            const int canignore = ~(CHANGED_SCALE | CHANGED_POSITION);
                            int change = (int)evt.Params[0];
                            if(change == 0) // what?
                                return;
                            if((change & canignore) == 0)
                            {
                                double now = Util.GetTimeStamp();
                                if (now < _nextEventTime)
                                    return;
                                _nextEventTime = now + _minEventDelay;
                            }
                            break;
                        }
                        default:
                            break;
                    }
                }

                 // Only so many of each event type allowed to queue.
                if((uint)evc < (uint)_EventCounts.Length)
                {
                    if(evc == ScriptEventCode.timer)
                    {
                        if(_EventCounts[(int)evc] >= 1)
                            return;
                    }
                    else if(_EventCounts[(int)evc] >= MAXEVENTQUEUE)
                        return;

                    _EventCounts[(int)evc]++;
                }

                 // Put event on end of instance's event queue.
                LinkedListNode<EventParams> lln = new LinkedListNode<EventParams>(evt);
                switch(evc)
                {
                     // These need to go first.  The only time we manually
                     // queue them is for the default state_entry() and we
                     // need to make sure they go before any attach() events
                     // so the heapLimit value gets properly initialized.
                    case ScriptEventCode.state_entry:
                        _EventQueue.AddFirst(lln);
                        break;

                     // The attach event sneaks to the front of the queue.
                     // This is needed for quantum limiting to work because
                     // we want the attach(NULL_KEY) event to come in front
                     // of all others so the _DetachQuantum won't run out
                     // before attach(NULL_KEY) is executed.
                    case ScriptEventCode.attach:
                        if(evt.Params[0].ToString() == UUID.Zero.ToString())
                        {
                            LinkedListNode<EventParams> lln2 = null;
                            for(lln2 = _EventQueue.First; lln2 != null; lln2 = lln2.Next)
                            {
                                EventParams evt2 = lln2.Value;
                                _eventCodeMap.TryGetValue(evt2.EventName, out ScriptEventCode evc2);
                                if(evc2 != ScriptEventCode.state_entry && evc2 != ScriptEventCode.attach)
                                    break;
                            }
                            if(lln2 == null)
                                _EventQueue.AddLast(lln);
                            else
                                _EventQueue.AddBefore(lln2, lln);

                             // If we're detaching, limit the qantum. This will also
                             // cause the script to self-suspend after running this
                             // event
                            _DetachReady.Reset();
                            _DetachQuantum = 100;
                        }
                        else
                            _EventQueue.AddLast(lln);

                        break;

                     // All others just go on end in the order queued.
                    default:
                        _EventQueue.AddLast(lln);
                        break;
                }

                 // If instance is idle (ie, not running or waiting to run),
                 // flag it to be on _StartQueue as we are about to do so.
                 // Flag it now before unlocking so another thread won't try
                 // to do the same thing right now.
                 // Dont' flag it if it's still suspended!
                if(_IState == XMRInstState.IDLE && !_Suspended)
                {
                    _IState = XMRInstState.ONSTARTQ;
                    startIt = true;
                }

                 // If instance is sleeping (ie, possibly in xmrEventDequeue),
                 // wake it up if event is in the mask.
                if(_SleepUntil > DateTime.UtcNow && !_Suspended)
                {
                    int evc1 = (int)evc;
                    int evc2 = evc1 - 32;
                    if((uint)evc1 < (uint)32 && ((_SleepEventMask1 >> evc1) & 1) != 0 ||
                            (uint)evc2 < (uint)32 && ((_SleepEventMask2 >> evc2) & 1) != 0)
                        wakeIt = true;
                }
            }

             // If transitioned from IDLE->ONSTARTQ, actually go insert it
             // on _StartQueue and give the RunScriptThread() a wake-up.
            if(startIt)
                _Engine.QueueToStart(this);

             // Likewise, if the event mask triggered a wake, wake it up.
            if(wakeIt)
            {
                _SleepUntil = DateTime.MinValue;
                _Engine.WakeFromSleep(this);
            }
        }

        public void CancelEvent(string eventName)
        {
            if (!_eventCodeMap.TryGetValue(eventName, out ScriptEventCode evc))
                return;

            lock (_QueueLock)
            {
                if(_EventQueue.Count == 0)
                    return;

                LinkedListNode<EventParams> lln2 = null;
                for (lln2 = _EventQueue.First; lln2 != null; lln2 = lln2.Next)
                {
                    EventParams evt2 = lln2.Value;
                    if(evt2.EventName.Equals(eventName))
                    {
                        _EventQueue.Remove(lln2);
                        if (evc >= 0 && _EventCounts[(int)evc] > 0)
                            _EventCounts[(int)evc]--;
                    }
                }
            }
        }

         // This is called in the script thread to step script until it calls
         // CheckRun().  It returns what the instance's next state should be,
         // ONSLEEPQ, ONYIELDQ, SUSPENDED or FINISHED.
        public XMRInstState RunOne()
        {
            DateTime now = DateTime.UtcNow;
            _SliceStart = Util.GetTimeStampMS();

             // If script has called llSleep(), don't do any more until time is up.
            _RunOnePhase = "check _SleepUntil";
            if(_SleepUntil > now)
            {
                _RunOnePhase = "return is sleeping";
                return XMRInstState.ONSLEEPQ;
            }

             // Also, someone may have called Suspend().
            _RunOnePhase = "check _SuspendCount";
            if(_SuspendCount > 0)
            {
                _RunOnePhase = "return is suspended";
                return XMRInstState.SUSPENDED;
            }

            // Make sure we aren't being migrated in or out and prevent that 
            // whilst we are in here.  If migration has it locked, don't call
            // back right away, delay a bit so we don't get in infinite loop.
            _RunOnePhase = "lock _RunLock";
            if(!Monitor.TryEnter(_RunLock))
            {
                _SleepUntil = now.AddMilliseconds(15);
                _RunOnePhase = "return was locked";
                return XMRInstState.ONSLEEPQ;
            }
            try
            {
                _RunOnePhase = "check entry invariants";
                CheckRunLockInvariants(true);
                Exception e = null;

                 // Maybe it has been Disposed()
                if(_Part == null || _Part.Inventory == null)
                {
                    _RunOnePhase = "runone saw it disposed";
                    return XMRInstState.DISPOSED;
                }

                if(!_Running)
                {
                    _RunOnePhase = "return is not running";
                    return XMRInstState.SUSPENDED;
                }

                 // Do some more of the last event if it didn't finish.
                if(this.eventCode != ScriptEventCode.None)
                {
                    lock(_QueueLock)
                    {
                        if(_DetachQuantum > 0 && --_DetachQuantum == 0)
                        {
                            _Suspended = true;
                            _DetachReady.Set();
                            _RunOnePhase = "detach quantum went zero";
                            CheckRunLockInvariants(true);
                            return XMRInstState.FINISHED;
                        }
                    }

                    _RunOnePhase = "resume old event handler";
                    _LastRanAt = now;
                    _InstEHSlice++;
                    callMode = CallMode_NORMAL;
                    e = ResumeEx();
                }

                 // Otherwise, maybe we can dequeue a new event and start 
                 // processing it.
                else
                {
                    _RunOnePhase = "lock event queue";
                    EventParams evt = null;
                    ScriptEventCode evc = ScriptEventCode.None;

                    lock(_QueueLock)
                    {

                         // We can't get here unless the script has been resumed
                         // after creation, then suspended again, and then had
                         // an event posted to it. We just pretend there is no
                         // event int he queue and let the normal mechanics
                         // carry out the suspension. A Resume will handle the
                         // restarting gracefully. This is taking the easy way
                         // out and may be improved in the future.

                        if(_Suspended)
                        {
                            _RunOnePhase = "_Suspended is set";
                            CheckRunLockInvariants(true);
                            return XMRInstState.FINISHED;
                        }

                        _RunOnePhase = "dequeue event";
                        if(_EventQueue.First != null)
                        {
                            evt = _EventQueue.First.Value;
                            _eventCodeMap.TryGetValue(evt.EventName, out evc);
                            if (_DetachQuantum > 0)
                            {
                                if(evc != ScriptEventCode.attach)
                                {
                                     // This is the case where the attach event
                                     // has completed and another event is queued
                                     // Stop it from running and suspend
                                    _Suspended = true;
                                    _DetachReady.Set();
                                    _DetachQuantum = 0;
                                    _RunOnePhase = "nothing to do #3";
                                    CheckRunLockInvariants(true);
                                    return XMRInstState.FINISHED;
                                }
                            }
                            _EventQueue.RemoveFirst();
                            if(evc >= 0)
                                _EventCounts[(int)evc]--;
                        }

                         // If there is no event to dequeue, don't run this script
                         // until another event gets queued.
                        if(evt == null)
                        {
                            if(_DetachQuantum > 0)
                            {
                                 // This will happen if the attach event has run
                                 // and exited with time slice left.
                                _Suspended = true;
                                _DetachReady.Set();
                                _DetachQuantum = 0;
                            }
                            _RunOnePhase = "nothing to do #4";
                            CheckRunLockInvariants(true);
                            return XMRInstState.FINISHED;
                        }
                    }

                     // Dequeued an event, so start it going until it either 
                     // finishes or it calls CheckRun().
                    _RunOnePhase = "start event handler";
                    _DetectParams = evt.DetectParams;
                    _LastRanAt = now;
                    _InstEHEvent++;
                    e = StartEventHandler(evc, evt.Params);
                }
                _RunOnePhase = "done running";
                _CPUTime += DateTime.UtcNow.Subtract(now).TotalMilliseconds;

                 // Maybe it puqued.
                if(e != null)
                {
                    _RunOnePhase = "handling exception " + e.Message;
                    HandleScriptException(e);
                    _RunOnePhase = "return had exception " + e.Message;
                    CheckRunLockInvariants(true);
                    return XMRInstState.FINISHED;
                }

                 // If event handler completed, get rid of detect params.
                if(this.eventCode == ScriptEventCode.None)
                    _DetectParams = null;

            }
            finally
            {
                _RunOnePhase += "; checking exit invariants and unlocking";
                CheckRunLockInvariants(false);
                Monitor.Exit(_RunLock);
            }

             // Cycle script through the yield queue and call it back asap.
            _RunOnePhase = "last return";
            return XMRInstState.ONYIELDQ;
        }

        /**
         * @brief Immediately after taking _RunLock or just before releasing it, check invariants.
         */
        private ScriptEventCode lastEventCode = ScriptEventCode.None;
        private bool lastActive = false;
        private string lastRunPhase = "";

        public void CheckRunLockInvariants(bool throwIt)
        {
             // If not executing any event handler, there shouldn't be any saved stack frames.
             // If executing an event handler, there should be some saved stack frames.
            bool active = stackFrames != null;
            ScriptEventCode ec = this.eventCode;
            if(ec == ScriptEventCode.None && active ||
                ec != ScriptEventCode.None && !active)
            {
                _log.Error("CheckRunLockInvariants: script=" + _DescName);
                _log.Error("CheckRunLockInvariants: eventcode=" + ec.ToString() + ", active=" + active.ToString());
                _log.Error("CheckRunLockInvariants: _RunOnePhase=" + _RunOnePhase);
                _log.Error("CheckRunLockInvariants: lastec=" + lastEventCode + ", lastAct=" + lastActive + ", lastPhase=" + lastRunPhase);
                if(throwIt)
                    throw new Exception("CheckRunLockInvariants: eventcode=" + ec.ToString() + ", active=" + active.ToString());
            }
            lastEventCode = ec;
            lastActive = active;
            lastRunPhase = _RunOnePhase;
        }

        /*
         * Start event handler.
         *
         * Input:
         *  newEventCode = code of event to be processed
         *  newEhArgs    = arguments for the event handler
         *
         * Caution:
         *  It is up to the caller to make sure ehArgs[] is correct for
         *  the particular event handler being called.  The first thing
         *  a script event handler method does is to unmarshall the args
         *  from ehArgs[] and will throw an array bounds or cast exception 
         *  if it can't.
         */
        private Exception StartEventHandler(ScriptEventCode newEventCode, object[] newEhArgs)
        {
             // We use this.eventCode == ScriptEventCode.None to indicate we are idle.
             // So trying to execute ScriptEventCode.None might make a mess.
            if(newEventCode == ScriptEventCode.None)
                return new Exception("Can't process ScriptEventCode.None");

             // Silly to even try if there is no handler defined for this event.
            if((int)newEventCode >= 0 && _ObjCode.scriptEventHandlerTable[this.stateCode, (int)newEventCode] == null)
                return null;

             // The microthread shouldn't be processing any event code.
             // These are assert checks so we throw them directly as exceptions.
            if(this.eventCode != ScriptEventCode.None)
                throw new Exception("still processing event " + this.eventCode.ToString());

             // Save eventCode so we know what event handler to run in the microthread.
             // And it also marks us busy so we can't be started again and this event lost.
            this.eventCode = newEventCode;
            this.ehArgs = newEhArgs;

             // This calls ScriptUThread.Main() directly, and returns when Main() [indirectly]
             // calls Suspend() or when Main() returns, whichever occurs first.
             // Setting stackFrames = null means run the event handler from the beginning
             // without doing any stack frame restores first.
            this.stackFrames = null;
            return StartEx();
        }

        /**
         * @brief There was an exception whilst starting/running a script event handler.
         *        Maybe we handle it directly or just print an error message.
         */
        private void HandleScriptException(Exception e)
        {
            // The script threw some kind of exception that was not caught at
            // script level, so the script is no longer running an event handler.

            ScriptEventCode curevent = eventCode;
            eventCode = ScriptEventCode.None;
            stackFrames = null;

            if(_Part == null || _Part.Inventory == null)
            {
                //we are gone and don't know it still
                _SleepUntil = DateTime.MaxValue;
                return;
            }

            if (e is ScriptDeleteException)
            {
                // Script did something like llRemoveInventory(llGetScriptName());
                // ... to delete itself from the object.
                _SleepUntil = DateTime.MaxValue;
                Verbose("[YEngine]: script self-delete {0}", _ItemID);
                _Part.Inventory.RemoveInventoryItem(_ItemID);
            }
            else if(e is ScriptDieException)
            {
                 // Script did an llDie()
                _RunOnePhase = "dying...";
                _SleepUntil = DateTime.MaxValue;
                _Engine.World.DeleteSceneObject(_Part.ParentGroup, false);
            }
            else if (e is ScriptResetException)
            {
                 // Script did an llResetScript().
                _RunOnePhase = "resetting...";
                ResetLocked("HandleScriptResetException");
            }
            else if (e is ScriptException)
            {
                // Some general script error.
                SendScriptErrorMessage(e, curevent);
            }
            else
            {
                // Some general script error.
                SendErrorMessage(e);
            }
        }

        private void SendScriptErrorMessage(Exception e, ScriptEventCode ev)
        {
            StringBuilder msg = new StringBuilder();
            bool toowner = false;
            msg.Append("YEngine: ");
            if (e.Message != null)
            {
                string text = e.Message;
                if (text.StartsWith("(OWNER)"))
                {
                    text = text.Substring(7);
                    toowner = true;
                }
                msg.Append(text);
            }

            msg.Append(" (script: ");
            msg.Append(_Item.Name);
            msg.Append(" event: ");
            msg.Append(ev.ToString());
            msg.Append(" primID: ");
            msg.Append(_Part.UUID.ToString());
            msg.Append(" at: <");
            Vector3 pos = _Part.AbsolutePosition;
            msg.Append((int)Math.Floor(pos.X));
            msg.Append(',');
            msg.Append((int)Math.Floor(pos.Y));
            msg.Append(',');
            msg.Append((int)Math.Floor(pos.Z));
            msg.Append(">) Script must be Reset to re-enable.\n");

            string msgst = msg.ToString();
            if (msgst.Length > 1000)
                msgst = msgst.Substring(0, 1000);

            if (toowner)
            {
                ScenePresence sp = _Engine.World.GetScenePresence(_Part.OwnerID);
                if (sp != null && !sp.IsNPC)
                    _Engine.World.SimChatToAgent(_Part.OwnerID, Utils.StringToBytes(msgst), 0x7FFFFFFF, _Part.AbsolutePosition,
                                                           _Part.Name, _Part.UUID, false);
            }
            else
                _Engine.World.SimChat(Utils.StringToBytes(msgst),
                                                           ChatTypeEnum.DebugChannel, 0x7FFFFFFF,
                                                           _Part.AbsolutePosition,
                                                           _Part.Name, _Part.UUID, false);
            _log.Debug(string.Format(
                "[SCRIPT ERROR]: {0} (at event {1}, part {2} {3} at {4} in {5}",
                e.Message == null? "" : e.Message,
                ev.ToString(),
                _Part.Name,
                _Part.UUID,
                _Part.AbsolutePosition,
                _Part.ParentGroup.Scene.Name));

            _SleepUntil = DateTime.MaxValue;
        }

        /**
         * @brief There was an exception running script event handler.
         *        Display error message and disable script (in a way
         *        that the script can be reset to be restarted).
         */
        private void SendErrorMessage(Exception e)
        {
            StringBuilder msg = new StringBuilder();

            msg.Append("[YEngine]: Exception while running ");
            msg.Append(_ItemID);
            msg.Append('\n');

             // Add exception message.
            string des = e.Message;
            des = des == null ? "" : ": " + des;
            msg.Append(e.GetType().Name + des + "\n");

             // Tell script owner what to do.
            msg.Append("Prim: <");
            msg.Append(_Part.Name);
            msg.Append(">, Script: <");
            msg.Append(_Item.Name);
            msg.Append(">, Location: ");
            msg.Append(_Engine.World.RegionInfo.RegionName);
            msg.Append(" <");
            Vector3 pos = _Part.AbsolutePosition;
            msg.Append((int)Math.Floor(pos.X));
            msg.Append(',');
            msg.Append((int)Math.Floor(pos.Y));
            msg.Append(',');
            msg.Append((int)Math.Floor(pos.Z));
            msg.Append(">\nScript must be Reset to re-enable.\n");

             // Display full exception message in log.
            _log.Info(msg.ToString() + XMRExceptionStackString(e), e);

             // Give script owner the stack dump.
            msg.Append(XMRExceptionStackString(e));

             // Send error message to owner.
             // Suppress internal code stack trace lines.
            string msgst = msg.ToString();
            if(!msgst.EndsWith("\n"))
                msgst += '\n';
            int j = 0;
            StringBuilder imstr = new StringBuilder();
            for(int i = 0; (i = msgst.IndexOf('\n', i)) >= 0; j = ++i)
            {
                string line = msgst.Substring(j, i - j);
                if(line.StartsWith("at "))
                {
                    if(line.StartsWith("at (wrapper"))
                        continue;  // at (wrapper ...
                    int k = line.LastIndexOf(".cs:");  // ... .cs:linenumber
                    if(int.TryParse(line.Substring(k + 4), out k))
                        continue;
                }
                this.llOwnerSay(line);
            }

            // Say script is sleeping for a very long time.
            // Reset() is able to cancel this sleeping.
            _SleepUntil = DateTime.MaxValue;
        }

        /**
         * @brief The user clicked the Reset Script button.
         *        We want to reset the script to a never-has-ever-run-before state.
         */
        public void Reset()
        {
        checkstate:
            XMRInstState iState = _IState;
            switch(iState)
            {
                 // If it's really being constructed now, that's about as reset as we get.
                case XMRInstState.CONSTRUCT:
                    return;

                 // If it's idle, that means it is ready to receive a new event.
                 // So we lock the event queue to prevent another thread from taking
                 // it out of idle, verify that it is still in idle then transition
                 // it to resetting so no other thread will touch it.
                case XMRInstState.IDLE:
                    lock(_QueueLock)
                    {
                        if(_IState == XMRInstState.IDLE)
                        {
                            _IState = XMRInstState.RESETTING;
                            break;
                        }
                    }
                    goto checkstate;

                 // If it's on the start queue, that means it is about to dequeue an
                 // event and start processing it.  So we lock the start queue so it
                 // can't be started and transition it to resetting so no other thread
                 // will touch it.
                case XMRInstState.ONSTARTQ:
                    lock(_Engine._StartQueue)
                    {
                        if(_IState == XMRInstState.ONSTARTQ)
                        {
                            _Engine._StartQueue.Remove(this);
                            _IState = XMRInstState.RESETTING;
                            break;
                        }
                    }
                    goto checkstate;

                 // If it's running, tell CheckRun() to suspend the thread then go back
                 // to see what it got transitioned to.
                case XMRInstState.RUNNING:
                    suspendOnCheckRunHold = true;
                    lock(_QueueLock)
                    {
                    }
                    goto checkstate;

                 // If it's sleeping, remove it from sleep queue and transition it to
                 // resetting so no other thread will touch it.
                case XMRInstState.ONSLEEPQ:
                    lock(_Engine._SleepQueue)
                    {
                        if(_IState == XMRInstState.ONSLEEPQ)
                        {
                            _Engine._SleepQueue.Remove(this);
                            _IState = XMRInstState.RESETTING;
                            break;
                        }
                    }
                    goto checkstate;

                 // It was just removed from the sleep queue and is about to be put
                 // on the yield queue (ie, is being woken up).
                 // Let that thread complete transition and try again.
                case XMRInstState.REMDFROMSLPQ:
                    Sleep(10);
                    goto checkstate;

                 // If it's yielding, remove it from yield queue and transition it to
                 // resetting so no other thread will touch it.
                case XMRInstState.ONYIELDQ:
                    lock(_Engine._YieldQueue)
                    {
                        if(_IState == XMRInstState.ONYIELDQ)
                        {
                            _Engine._YieldQueue.Remove(this);
                            _IState = XMRInstState.RESETTING;
                            break;
                        }
                    }
                    goto checkstate;

                 // If it just finished running something, let that thread transition it
                 // to its next state then check again.
                case XMRInstState.FINISHED:
                    Sleep(10);
                    goto checkstate;

                 // If it's disposed, that's about as reset as it gets.
                case XMRInstState.DISPOSED:
                    return;

                // Some other thread is already resetting it, let it finish.

                case XMRInstState.RESETTING:
                    return;

                case XMRInstState.SUSPENDED:
                    break;

                default:
                    throw new Exception("bad state");
            }

             // This thread transitioned the instance to RESETTING so reset it.
            lock(_RunLock)
            {
                CheckRunLockInvariants(true);

                // No other thread should have transitioned it from RESETTING.
                if (_IState != XMRInstState.SUSPENDED)
                {
                    if (_IState != XMRInstState.RESETTING)
                        throw new Exception("bad state");

                    _IState = XMRInstState.IDLE;
                }

                // Reset everything and queue up default's start_entry() event.
                ClearQueue();
                ResetLocked("external Reset");

                // Mark it idle now so it can get queued to process new stuff.

                CheckRunLockInvariants(true);
            }
        }

        private void ClearQueueExceptLinkMessages()
        {
            lock(_QueueLock)
            {
                EventParams[] linkMessages = new EventParams[_EventQueue.Count];
                int n = 0;
                foreach(EventParams evt2 in _EventQueue)
                {
                    if(evt2.EventName == "link_message")
                        linkMessages[n++] = evt2;
                }

                _EventQueue.Clear();
                for(int i = _EventCounts.Length; --i >= 0;)
                    _EventCounts[i] = 0;

                for(int i = 0; i < n; i++)
                    _EventQueue.AddLast(linkMessages[i]);

                _EventCounts[(int)ScriptEventCode.link_message] = n;
            }
        }

        private void ClearQueue()
        {
            lock(_QueueLock)
            {
                _EventQueue.Clear();               // no events queued
                for(int i = _EventCounts.Length; --i >= 0;)
                    _EventCounts[i] = 0;
            }
        }

        /**
         * @brief The script called llResetScript() while it was running and
         *        has suspended.  We want to reset the script to a never-has-
         *        ever-run-before state.
         *
         *        Caller must have _RunLock locked so we know script isn't
         *        running.
         */
        private void ResetLocked(string from)
        {
            _RunOnePhase = "ResetLocked: releasing controls";
            ReleaseControlsOrPermissions(true);
            _Part.CollisionSound = UUID.Zero;

            if (_XMRLSLApi != null)
                _XMRLSLApi.llResetTime();

            _RunOnePhase = "ResetLocked: removing script";
            IUrlModule urlModule = _Engine.World.RequestModuleInterface<IUrlModule>();
            if(urlModule != null)
                urlModule.ScriptRemoved(_ItemID);

            AsyncCommandManager.RemoveScript(_Engine, _LocalID, _ItemID);

            _RunOnePhase = "ResetLocked: clearing current event";
            this.eventCode = ScriptEventCode.None;  // not processing an event
            _DetectParams = null;                  // not processing an event
            _SleepUntil = DateTime.MinValue;     // not doing llSleep()
            _ResetCount++;                        // has been reset once more

            _localsHeapUsed = 0;
            _arraysHeapUsed = 0;
            glblVars.Clear();

             // Tell next call to 'default state_entry()' to reset all global
             // vars to their initial values.
            doGblInit = true;

            // Throw away all its stack frames. 
            // If the script is resetting itself, there shouldn't be any stack frames. 
            // If the script is being reset by something else, we throw them away cuz we want to start from the beginning of an event handler. 
            stackFrames = null;

             // Set script to 'default' state and queue call to its 
             // 'state_entry()' event handler.
            _RunOnePhase = "ResetLocked: posting default:state_entry() event";
            stateCode = 0;
            _Part.RemoveScriptTargets(_ItemID);
            _Part.SetScriptEvents(_ItemID, GetStateEventFlags(0));
            PostEvent(new EventParams("state_entry",
                                      zeroObjectArray,
                                      zeroDetectParams));

             // Tell CheckRun() to let script run.
            suspendOnCheckRunHold = false;
            suspendOnCheckRunTemp = false;
            _RunOnePhase = "ResetLocked: reset complete";
        }

        private void ReleaseControlsOrPermissions(bool fullPermissions)
        {
            if(_Part != null && _Part.TaskInventory != null)
            {
                int permsMask;
                UUID permsGranter;
                _Part.TaskInventory.LockItemsForWrite(true);
                if (!_Part.TaskInventory.TryGetValue(_ItemID, out TaskInventoryItem item))
                {
                    _Part.TaskInventory.LockItemsForWrite(false);
                    return;
                }
                permsGranter = item.PermsGranter;
                permsMask = item.PermsMask;
                if(fullPermissions)
                {
                    item.PermsGranter = UUID.Zero;
                    item.PermsMask = 0;
                }
                else
                    item.PermsMask = permsMask & ~(ScriptBaseClass.PERMISSION_TAKE_CONTROLS | ScriptBaseClass.PERMISSION_CONTROL_CAMERA);
                _Part.TaskInventory.LockItemsForWrite(false);

                if ((permsMask & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) != 0)
                {
                    ScenePresence presence = _Engine.World.GetScenePresence(permsGranter);
                    if (presence != null)
                        presence.UnRegisterControlEventsToScript(_LocalID, _ItemID);
                }
            }
        }

        /**
         * @brief The script code should call this routine whenever it is
         *        convenient to perform a migation or switch microthreads.
         */
        public override void CheckRunWork()
        {
            if(!suspendOnCheckRunHold && !suspendOnCheckRunTemp)
            {
                if(Util.GetTimeStampMS() - _SliceStart < 60.0)
                    return;
                suspendOnCheckRunTemp = true;
            }
            _CheckRunPhase = "entered";

             // Stay stuck in this loop as long as something wants us suspended.
            while(suspendOnCheckRunHold || suspendOnCheckRunTemp)
            {
                _CheckRunPhase = "top of while";
                suspendOnCheckRunTemp = false;

                switch(this.callMode)
                {
                    // Now we are ready to suspend or resume.
                    case CallMode_NORMAL:
                        _CheckRunPhase = "suspending";
                        callMode = XMRInstance.CallMode_SAVE;
                        stackFrames = null;
                        throw new StackHibernateException(); // does not return

                    // We get here when the script state has been read in by MigrateInEventHandler().
                    // Since the stack is completely restored at this point, any subsequent calls
                    // within the functions should do their normal processing instead of trying to 
                    // restore their state.

                    // the stack has been restored as a result of calling ResumeEx()
                    // tell script code to process calls normally
                    case CallMode_RESTORE:
                        this.callMode = CallMode_NORMAL;
                        break;

                    default:
                        throw new Exception("callMode=" + callMode);
                }

                _CheckRunPhase = "resumed";
            }

            _CheckRunPhase = "returning";

             // Upon return from CheckRun() it should always be the case that the script is
             // going to process calls normally, neither saving nor restoring stack frame state.
            if(callMode != CallMode_NORMAL)
                throw new Exception("bad callMode " + callMode);
        }

        /**
         * @brief Allow script to dequeue events.
         */
        public void ResumeIt()
        {
            lock(_QueueLock)
            {
                _SuspendCount = 0;
                _Suspended = false;
                _DetachQuantum = 0;
                _DetachReady.Set();
                if (_EventQueue != null &&
                    _EventQueue.First != null &&
                    _IState == XMRInstState.IDLE)
                {
                    _IState = XMRInstState.ONSTARTQ;
                    _Engine.QueueToStart(this);
                }
                _HasRun = true;
            }
        }

        /**
         * @brief Block script from dequeuing events.
         */
        public void SuspendIt()
        {
            lock(_QueueLock)
            {
                _SuspendCount = 1;
                _Suspended = true;
            }
        }
    }

    /**
     * @brief Thrown by CheckRun() to unwind the script stack, capturing frames to
     *        instance.stackFrames as it unwinds.  We don't want scripts to be able
     *        to intercept this exception as it would block the stack capture
     *        functionality.
     */
    public class StackCaptureException: Exception, IXMRUncatchable
    {
    }
}
