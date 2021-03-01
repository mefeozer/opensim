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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using OpenMetaverse;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.ScriptEngine.Shared.CodeTools;
using OpenSim.Region.ScriptEngine.Interfaces;

using System.Diagnostics; //for [DebuggerNonUserCode]

namespace OpenSim.Region.ScriptEngine.Shared.Instance
{
    public class ScriptInstance : MarshalByRefObject, IScriptInstance
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public bool StatePersistedHere => _AttachedAvatar == UUID.Zero;

        /// <summary>
        /// The current work item if an event for this script is running or waiting to run,
        /// </summary>
        /// <remarks>
        /// Null if there is no running or waiting to run event.  Must be changed only under an EventQueue lock.
        /// </remarks>
        private IScriptWorkItem _CurrentWorkItem;

        private IScript _Script;
        private DetectParams[] _DetectParams;
        private bool _TimerQueued;
        private DateTime _EventStart;
        private bool _InEvent;
        private string _assemblyPath;
        private string _dataPath;
        private string _CurrentEvent = string.Empty;
        private bool _InSelfDelete;
        private readonly int _MaxScriptQueue;
        private bool _SaveState;
        private int _ControlEventsInQueue;
        private int _LastControlLevel;
        private bool _CollisionInQueue;
        private bool _StateChangeInProgress;

        // The following is for setting a minimum delay between events
        private double _minEventDelay;

        private long _eventDelayTicks;
        private long _nextEventTimeTicks;
        private bool _startOnInit = true;
        private UUID _AttachedAvatar;
        private StateSource _stateSource;
        private readonly bool _postOnRez;
        private bool _startedFromSavedState;
        private UUID _CurrentStateHash;
        private readonly UUID _RegionID;

        public int DebugLevel { get; set; }

        public WaitHandle CoopWaitHandle { get; private set; }
        public Stopwatch ExecutionTimer { get; }

        public Dictionary<KeyValuePair<int, int>, KeyValuePair<int, int>> LineMap { get; set; }

        private readonly Dictionary<string,IScriptApi> _Apis = new Dictionary<string,IScriptApi>();

        public object[] PluginData = new object[0];

        /// <summary>
        /// Used by llMinEventDelay to suppress events happening any faster than this speed.
        /// This currently restricts all events in one go. Not sure if each event type has
        /// its own check so take the simple route first.
        /// </summary>
        public double MinEventDelay
        {
            get => _minEventDelay;
            set
            {
                if (value > 0.001)
                    _minEventDelay = value;
                else
                    _minEventDelay = 0.0;

                _eventDelayTicks = (long)(_minEventDelay * 10000000L);
                _nextEventTimeTicks = DateTime.Now.Ticks;
            }
        }

        public bool Running
        {
            get => _running;

            set
            {
                _running = value;
                if (_running)
                    StayStopped = false;
            }
        }
        private bool _running;

        public bool Suspended
        {
            get => _Suspended;

            set
            {
                // Need to do this inside a lock in order to avoid races with EventProcessor()
                lock (_Script)
                {
                    bool wasSuspended = _Suspended;
                    _Suspended = value;

                    if (wasSuspended && !_Suspended)
                    {
                        lock (EventQueue)
                        {
                            // Need to place ourselves back in a work item if there are events to process
                            if (EventQueue.Count > 0 && Running && !ShuttingDown)
                                _CurrentWorkItem = Engine.QueueEventHandler(this);
                        }
                    }
                }
            }
        }
        private bool _Suspended;

        public bool ShuttingDown { get; set; }

        public string State { get; set; }

        public bool StayStopped { get; set; }

        public IScriptEngine Engine { get; }

        public UUID AppDomain { get; set; }

        public SceneObjectPart Part { get; }

        public string PrimName { get; }

        public string ScriptName { get; }

        public UUID ItemID { get; }

        public UUID ObjectID => Part.UUID;

        public uint LocalID => Part.LocalId;

        public UUID RootObjectID => Part.ParentGroup.UUID;

        public uint RootLocalID => Part.ParentGroup.LocalId;

        public UUID AssetID { get; }

        public Queue EventQueue { get; }

        public long EventsQueued
        {
            get
            {
                lock (EventQueue)
                    return EventQueue.Count;
            }
        }

        public long EventsProcessed { get; private set; }

        public int StartParam { get; set; }

        public TaskInventoryItem ScriptTask { get; }

        public DateTime TimeStarted { get; private set; }

        public MetricsCollectorTime ExecutionTime { get; }

        private static readonly int MeasurementWindow = 30 * 1000;   // show the *recent* time used by the script, to find currently active scripts

        private bool _coopTermination;

        private EventWaitHandle _coopSleepHandle;

        public void ClearQueue()
        {
            _TimerQueued = false;
            _StateChangeInProgress = false;
            EventQueue.Clear();
        }

        public ScriptInstance(
            IScriptEngine engine, SceneObjectPart part, TaskInventoryItem item,
            int startParam, bool postOnRez,
            int maxScriptQueue)
        {
            State = "default";
            EventQueue = new Queue(32);
            ExecutionTimer = new Stopwatch();

            Engine = engine;
            Part = part;
            ScriptTask = item;

            // This is currently only here to allow regression tests to get away without specifying any inventory
            // item when they are testing script logic that doesn't require an item.
            if (ScriptTask != null)
            {
                ScriptName = ScriptTask.Name;
                ItemID = ScriptTask.ItemID;
                AssetID = ScriptTask.AssetID;
            }

            PrimName = part.ParentGroup.Name;
            StartParam = startParam;
            _MaxScriptQueue = maxScriptQueue;
            _postOnRez = postOnRez;
            _AttachedAvatar = part.ParentGroup.AttachedAvatar;
            _RegionID = part.ParentGroup.Scene.RegionInfo.RegionID;

            _SaveState = StatePersistedHere;

            ExecutionTime = new MetricsCollectorTime(MeasurementWindow, 10);

//            _log.DebugFormat(
//                "[SCRIPT INSTANCE]: Instantiated script instance {0} (id {1}) in part {2} (id {3}) in object {4} attached avatar {5} in {6}",
//                ScriptTask.Name, ScriptTask.ItemID, Part.Name, Part.UUID, Part.ParentGroup.Name, _AttachedAvatar, Engine.World.Name);
        }

        /// <summary>
        /// Load the script from an assembly into an AppDomain.
        /// </summary>
        /// <param name='dom'></param>
        /// <param name='assembly'></param>
        /// <param name='dataPath'>
        /// Path for all script associated data (state, etc.).  In a multi-region set up
        /// with all scripts loading into the same AppDomain this may not be the same place as the DLL itself.
        /// </param>
        /// <param name='stateSource'></param>
        /// <returns>false if load failed, true if suceeded</returns>
        public bool Load(
            IScript script, EventWaitHandle coopSleepHandle, string assemblyPath,
            string dataPath, StateSource stateSource, bool coopTermination)
        {
            _Script = script;
            _coopSleepHandle = coopSleepHandle;
            _assemblyPath = assemblyPath;
            _dataPath = dataPath;
            _stateSource = stateSource;
            _coopTermination = coopTermination;

            if (_coopTermination)
                CoopWaitHandle = coopSleepHandle;
            else
                CoopWaitHandle = null;

            ApiManager am = new ApiManager();

            foreach (string api in am.GetApis())
            {
                _Apis[api] = am.CreateApi(api);
                _Apis[api].Initialize(Engine, Part, ScriptTask);
            }

            try
            {
                foreach (KeyValuePair<string,IScriptApi> kv in _Apis)
                {
                    _Script.InitApi(kv.Key, kv.Value);
                }
            }
            catch (Exception e)
            {
                _log.ErrorFormat(
                    "[SCRIPT INSTANCE]: Not starting script {0} (id {1}) in part {2} (id {3}) in object {4} in {5}.  Error initializing script instance.  Exception {6}{7}",
                    ScriptTask.Name, ScriptTask.ItemID, Part.Name, Part.UUID, Part.ParentGroup.Name, Engine.World.Name, e.Message, e.StackTrace);

                return false;
            }

            // For attachments, XEngine saves the state into a .state file when XEngine.SetXMLState() is called.
            string savedState = Path.Combine(_dataPath, ItemID.ToString() + ".state");

            if (File.Exists(savedState))
            {
                //                _log.DebugFormat(
                //                    "[SCRIPT INSTANCE]: Found state for script {0} for {1} ({2}) at {3} in {4}",
                //                    ItemID, savedState, Part.Name, Part.ParentGroup.Name, Part.ParentGroup.Scene.Name);

                string xml = string.Empty;

                try
                {
                    FileInfo fi = new FileInfo(savedState);
                    int size = (int)fi.Length;
                    if (size < 512000)
                    {
                        using (FileStream fs = File.Open(savedState,
                                                         FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            byte[] data = new byte[size];
                            fs.Read(data, 0, size);

                            xml = Encoding.UTF8.GetString(data);

                            ScriptSerializer.Deserialize(xml, this);

                            AsyncCommandManager.CreateFromData(Engine,
                                                               LocalID, ItemID, ObjectID,
                                                               PluginData);

                            // _log.DebugFormat("[Script] Successfully retrieved state for script {0}.{1}", PrimName, _ScriptName);


                            if (!Running)
                                _startOnInit = false;

                            Running = false;

                            // we get new rez events on sim restart, too
                            // but if there is state, then we fire the change
                            // event

                            // We loaded state, don't force a re-save
                            _SaveState = false;
                            _startedFromSavedState = true;
                        }

                        // If this script is in an attachment then we no longer need the state file.
                        if (!StatePersistedHere)
                            RemoveState();
                    }
                    //                    else
                    //                    {
                    //                        _log.WarnFormat(
                    //                            "[SCRIPT INSTANCE]: Not starting script {0} (id {1}) in part {2} (id {3}) in object {4} in {5}.  Unable to load script state file {6}.  Memory limit exceeded.",
                    //                            ScriptTask.Name, ScriptTask.ItemID, Part.Name, Part.UUID, Part.ParentGroup.Name, Engine.World.Name, savedState);
                    //                    }
                }
                catch (Exception e)
                {
                    _log.ErrorFormat(
                        "[SCRIPT INSTANCE]: Not starting script {0} (id {1}) in part {2} (id {3}) in object {4} in {5}.  Unable to load script state file {6}.  XML is {7}.  Exception {8}{9}",
                        ScriptTask.Name, ScriptTask.ItemID, Part.Name, Part.UUID, Part.ParentGroup.Name, Engine.World.Name, savedState, xml, e.Message, e.StackTrace);
                }
            }
            //            else
            //            {
            //                _log.DebugFormat(
            //                    "[SCRIPT INSTANCE]: Did not find state for script {0} for {1} ({2}) at {3} in {4}",
            //                    ItemID, savedState, Part.Name, Part.ParentGroup.Name, Part.ParentGroup.Scene.Name);
            //            }
            try
            {
                Part.SetScriptEvents(ItemID, _Script.GetStateEventFlags(State));
            }
            catch
            {
                _log.ErrorFormat("[SCRIPT INSTANCE]: failed to SetScriptEvents {0}", ItemID);
            }

            return true;
        }

        public void Init()
        {
            if (ShuttingDown)
                return;

            if (_startedFromSavedState)
            {
                if (_startOnInit)
                    Start();
                if (_postOnRez)
                {
                    PostEvent(new EventParams("on_rez",
                        new object[] {new LSL_Types.LSLInteger(StartParam)}, new DetectParams[0]));
                }
                if (_stateSource == StateSource.AttachedRez)
                {
                    PostEvent(new EventParams("attach",
                        new object[] { new LSL_Types.LSLString(_AttachedAvatar.ToString()) }, new DetectParams[0]));
                }
                else if (_stateSource == StateSource.RegionStart)
                {
                    //_log.Debug("[Script] Posted changed(CHANGED_REGION_RESTART) to script");
                    PostEvent(new EventParams("changed",
                        new object[] { new LSL_Types.LSLInteger((int)Changed.REGION_RESTART) }, new DetectParams[0]));
                }
                else if (_stateSource == StateSource.PrimCrossing || _stateSource == StateSource.Teleporting)
                {
                    // CHANGED_REGION
                    PostEvent(new EventParams("changed",
                        new object[] { new LSL_Types.LSLInteger((int)Changed.REGION) }, new DetectParams[0]));

                    // CHANGED_TELEPORT
                    if (_stateSource == StateSource.Teleporting)
                        PostEvent(new EventParams("changed",
                            new object[] { new LSL_Types.LSLInteger((int)Changed.TELEPORT) }, new DetectParams[0]));
                }
            }
            else
            {
                if (_startOnInit)
                    Start();
                PostEvent(new EventParams("state_entry",
                                          new object[0], new DetectParams[0]));
                if (_postOnRez)
                {
                    PostEvent(new EventParams("on_rez",
                        new object[] {new LSL_Types.LSLInteger(StartParam)}, new DetectParams[0]));
                }

                if (_stateSource == StateSource.AttachedRez)
                {
                    PostEvent(new EventParams("attach",
                        new object[] { new LSL_Types.LSLString(_AttachedAvatar.ToString()) }, new DetectParams[0]));
                }
            }
        }

        private void ReleaseControlsorPermissions(bool fullpermissions)
        {
            SceneObjectPart part = Engine.World.GetSceneObjectPart(LocalID);

            if (part != null && part.TaskInventory != null)
            {
                int permsMask;
                UUID permsGranter;
                part.TaskInventory.LockItemsForWrite(true);
                if (!part.TaskInventory.TryGetValue(ItemID, out TaskInventoryItem item))
                {
                    part.TaskInventory.LockItemsForWrite(false);
                    return;
                }
                permsGranter = item.PermsGranter;
                permsMask = item.PermsMask;
                if(fullpermissions)
                {
                    item.PermsGranter = UUID.Zero;
                    item.PermsMask = 0;
                }
                else
                    item.PermsMask = permsMask & ~(ScriptBaseClass.PERMISSION_TAKE_CONTROLS | ScriptBaseClass.PERMISSION_CONTROL_CAMERA);
                part.TaskInventory.LockItemsForWrite(false);

                if ((permsMask & ScriptBaseClass.PERMISSION_TAKE_CONTROLS) != 0)
                {
                    ScenePresence presence = Engine.World.GetScenePresence(permsGranter);
                    if (presence != null)
                        presence.UnRegisterControlEventsToScript(LocalID, ItemID);
                }
            }
        }

        public void DestroyScriptInstance()
        {
            ReleaseControlsorPermissions(false);
            AsyncCommandManager.RemoveScript(Engine, LocalID, ItemID);
            SceneObjectPart part = Engine.World.GetSceneObjectPart(LocalID);
            if (part != null)
                part.RemoveScriptEvents(ItemID);
        }

        public void RemoveState()
        {
            string savedState = Path.Combine(_dataPath, ItemID.ToString() + ".state");

//            _log.DebugFormat(
//                "[SCRIPT INSTANCE]: Deleting state {0} for script {1} (id {2}) in part {3} (id {4}) in object {5} in {6}.",
//                savedState, ScriptTask.Name, ScriptTask.ItemID, Part.Name, Part.UUID, Part.ParentGroup.Name, Engine.World.Name);

            try
            {
                File.Delete(savedState);
            }
            catch (Exception e)
            {
                _log.Warn(
                    string.Format(
                        "[SCRIPT INSTANCE]: Could not delete script state {0} for script {1} (id {2}) in part {3} (id {4}) in object {5} in {6}.  Exception  ",
                        savedState, ScriptTask.Name, ScriptTask.ItemID, Part.Name, Part.UUID, Part.ParentGroup.Name, Engine.World.Name),
                    e);
            }
        }

        public void VarDump(Dictionary<string, object> vars)
        {
            // _log.Info("Variable dump for script "+ ItemID.ToString());
            // foreach (KeyValuePair<string, object> v in vars)
            // {
                // _log.Info("Variable: "+v.Key+" = "+v.Value.ToString());
            // }
        }

        public void Start()
        {
            lock (EventQueue)
            {
                if (Running)
                    return;

                Running = true;

                TimeStarted = DateTime.Now;

                // Note: we don't reset ExecutionTime. The reason is that runaway scripts are stopped and restarted
                // automatically, and we *do* want to show that they had high CPU in that case. If we had reset
                // ExecutionTime here then runaway scripts, paradoxically, would never show up in the "Top Scripts" dialog.

                if (EventQueue.Count > 0)
                {
                    if (_CurrentWorkItem == null)
                        _CurrentWorkItem = Engine.QueueEventHandler(this);
                    // else
                        // _log.Error("[Script] Tried to start a script that was already queued");
                }
            }
        }

        public bool Stop(int timeout, bool clearEventQueue = false)
        {
            if (DebugLevel >= 1)
                _log.DebugFormat(
                    "[SCRIPT INSTANCE]: Stopping script {0} {1} in {2} {3} with timeout {4} {5} {6}",
                    ScriptName, ItemID, PrimName, ObjectID, timeout, _InSelfDelete, DateTime.Now.Ticks);

            IScriptWorkItem workItem;

            lock (EventQueue)
            {
                if (clearEventQueue)
                    ClearQueue();

                if (!Running)
                    return true;

                // If we're not running or waiting to run an event then we can safely stop.
                if (_CurrentWorkItem == null)
                {
                    Running = false;
                    return true;
                }

                // If we are waiting to run an event then we can try to cancel it.
                if (_CurrentWorkItem.Cancel())
                {
                    _CurrentWorkItem = null;
                    Running = false;
                    return true;
                }

                workItem = _CurrentWorkItem;
                Running = false;
            }

            // Wait for the current event to complete.
            if (!_InSelfDelete)
            {
                if (!_coopTermination)
                {
                    // If we're not co-operative terminating then try and wait for the event to complete before stopping
                    if (workItem.Wait(timeout))
                        return true;
                }
                else
                {
                    if (DebugLevel >= 1)
                        _log.DebugFormat(
                            "[SCRIPT INSTANCE]: Co-operatively stopping script {0} {1} in {2} {3}",
                            ScriptName, ItemID, PrimName, ObjectID);

                    // This will terminate the event on next handle check by the script.
                    _coopSleepHandle.Set();

                    // For now, we will wait forever since the event should always cleanly terminate once LSL loop
                    // checking is implemented.  May want to allow a shorter timeout option later.
                    if (workItem.Wait(Timeout.Infinite))
                    {
                        if (DebugLevel >= 1)
                            _log.DebugFormat(
                                "[SCRIPT INSTANCE]: Co-operatively stopped script {0} {1} in {2} {3}",
                                ScriptName, ItemID, PrimName, ObjectID);

                        return true;
                    }
                }
            }

            lock (EventQueue)
            {
                workItem = _CurrentWorkItem;
            }

            if (workItem == null)
                return true;

            // If the event still hasn't stopped and we the stop isn't the result of script or object removal, then
            // forcibly abort the work item (this aborts the underlying thread).
            // Co-operative termination should never reach this point.
            if (!_InSelfDelete)
            {
                _log.DebugFormat(
                    "[SCRIPT INSTANCE]: Aborting unstopped script {0} {1} in prim {2}, localID {3}, timeout was {4} ms",
                    ScriptName, ItemID, PrimName, LocalID, timeout);

                workItem.Abort();
            }

            lock (EventQueue)
            {
                _CurrentWorkItem = null;
            }

            return true;
        }

        [DebuggerNonUserCode] //Prevents the debugger from farting in this function
        public void SetState(string state)
        {
            if (state == State)
                return;

            EventParams lastTimerEv = null;

            lock (EventQueue)
            {
                // Remove all queued events, remembering the last timer event
                while (EventQueue.Count > 0)
                {
                    EventParams tempv = (EventParams)EventQueue.Dequeue();
                    if (tempv.EventName == "timer") lastTimerEv = tempv;
                }

                // Post events
                PostEvent(new EventParams("state_exit", new object[0],
                                           new DetectParams[0]));
                PostEvent(new EventParams("state", new object[] { state },
                                           new DetectParams[0]));
                PostEvent(new EventParams("state_entry", new object[0],
                                           new DetectParams[0]));

                // Requeue the timer event after the state changing events
                if (lastTimerEv != null) EventQueue.Enqueue(lastTimerEv);

                // This will stop events from being queued and processed
                // until the new state is started
                _StateChangeInProgress = true;
            }

            throw new EventAbortException();
        }

        /// <summary>
        /// Post an event to this script instance.
        /// </summary>
        /// <remarks>
        /// The request to run the event is sent
        /// </remarks>
        /// <param name="data"></param>
        public void PostEvent(EventParams data)
        {
//            _log.DebugFormat("[Script] Posted event {2} in state {3} to {0}.{1}",
//                        PrimName, ScriptName, data.EventName, State);

            if (!Running)
                return;

            // If min event delay is set then ignore any events untill the time has expired
            // This currently only allows 1 event of any type in the given time period.
            // This may need extending to allow for a time for each individual event type.
            if (_eventDelayTicks != 0 && 
                    data.EventName != "state" && data.EventName != "state_entry" && data.EventName != "state_exit"
                    && data.EventName != "run_time_permissions" && data.EventName != "http_request" && data.EventName != "link_message")
            {
                if (DateTime.Now.Ticks < _nextEventTimeTicks)
                    return;
                _nextEventTimeTicks = DateTime.Now.Ticks + _eventDelayTicks;
            }

            lock (EventQueue)
            {
                // The only events that persist across state changes are timers
                if (_StateChangeInProgress && data.EventName != "timer")
                    return;

                if (EventQueue.Count >= _MaxScriptQueue)
                    return;

                if (data.EventName == "timer")
                {
                    if (_TimerQueued)
                        return;
                    _TimerQueued = true;
                }

                if (data.EventName == "control")
                {
                    int held = ((LSL_Types.LSLInteger)data.Params[1]).value;
                    // int changed = ((LSL_Types.LSLInteger)data.Params[2]).value;

                    // If the last message was a 0 (nothing held)
                    // and this one is also nothing held, drop it
                    //
                    if (_LastControlLevel == held && held == 0)
                        return;

                    // If there is one or more queued, then queue
                    // only changed ones, else queue unconditionally
                    //
                    if (_ControlEventsInQueue > 0)
                    {
                        if (_LastControlLevel == held)
                            return;
                    }

                    _LastControlLevel = held;
                    _ControlEventsInQueue++;
                }

                if (data.EventName == "collision")
                {
                    if (_CollisionInQueue)
                        return;
                    if (data.DetectParams == null)
                        return;

                    _CollisionInQueue = true;
                }

                EventQueue.Enqueue(data);

                if (_CurrentWorkItem == null)
                {
                    _CurrentWorkItem = Engine.QueueEventHandler(this);
                }
            }
        }

        /// <summary>
        /// Process the next event queued for this script
        /// </summary>
        /// <returns></returns>
        public object EventProcessor()
        {
            // We check here as the thread stopping this instance from running may itself hold the _Script lock.
            if (!Running)
                return 0;

            lock (_Script)
            {
//                _log.DebugFormat("[XEngine]: EventProcessor() invoked for {0}.{1}", PrimName, ScriptName);

                if (Suspended)
                    return 0;

                ExecutionTimer.Restart();

                try
                {
                    return EventProcessorInt();
                }
                finally
                {
                    ExecutionTimer.Stop();
                    ExecutionTime.AddSample(ExecutionTimer);
                    Part.ParentGroup.Scene.AddScriptExecutionTime(ExecutionTimer.ElapsedTicks);
                }
            }
        }

        private object EventProcessorInt()
        {
            EventParams data = null;

            lock (EventQueue)
            {
                data = (EventParams)EventQueue.Dequeue();
                if (data == null)
                {
                    // check if a null event was enqueued or if its really empty
                    if (EventQueue.Count > 0 && Running && !ShuttingDown && !_InSelfDelete)
                    {
                        _CurrentWorkItem = Engine.QueueEventHandler(this);
                    }
                    else
                    {
                        _CurrentWorkItem = null;
                    }
                    return 0;
                }

                if (data.EventName == "timer")
                    _TimerQueued = false;
                if (data.EventName == "control")
                {
                    if (_ControlEventsInQueue > 0)
                        _ControlEventsInQueue--;
                }
                if (data.EventName == "collision")
                    _CollisionInQueue = false;
            }

            if (DebugLevel >= 2)
                _log.DebugFormat(
                    "[SCRIPT INSTANCE]: Processing event {0} for {1}/{2}({3})/{4}({5}) @ {6}/{7}",
                    data.EventName,
                    ScriptName,
                    Part.Name,
                    Part.LocalId,
                    Part.ParentGroup.Name,
                    Part.ParentGroup.UUID,
                    Part.AbsolutePosition,
                    Part.ParentGroup.Scene.Name);

            _DetectParams = data.DetectParams;

            if (data.EventName == "state") // Hardcoded state change
            {
                State = data.Params[0].ToString();

                if (DebugLevel >= 1)
                    _log.DebugFormat(
                        "[SCRIPT INSTANCE]: Changing state to {0} for {1}/{2}({3})/{4}({5}) @ {6}/{7}",
                        State,
                        ScriptName,
                        Part.Name,
                        Part.LocalId,
                        Part.ParentGroup.Name,
                        Part.ParentGroup.UUID,
                        Part.AbsolutePosition,
                        Part.ParentGroup.Scene.Name);
                AsyncCommandManager.StateChange(Engine,
                    LocalID, ItemID);
                // we are effectively in the new state now, so we can resume queueing
                // and processing other non-timer events
                _StateChangeInProgress = false;

                Part.RemoveScriptTargets(ItemID);
                Part.SetScriptEvents(ItemID, _Script.GetStateEventFlags(State));
            }
            else
            {
                Exception e = null;

                if (Engine.World.PipeEventsForScript(LocalID) ||
                    data.EventName == "control") // Don't freeze avies!
                {
                    //                        _log.DebugFormat("[Script] Delivered event {2} in state {3} to {0}.{1}",
                    //                                PrimName, ScriptName, data.EventName, State);

                    try
                    {
                        _CurrentEvent = data.EventName;
                        _EventStart = DateTime.UtcNow;
                        _InEvent = true;

                        try
                        {
                            _Script.ExecuteEvent(State, data.EventName, data.Params);
                        }
                        finally
                        {
                            _InEvent = false;
                            _CurrentEvent = string.Empty;
                            lock (EventQueue)
                                _CurrentWorkItem = null; // no longer in a event that can be canceled
                        }

                        if (_SaveState)
                        {
                            // This will be the very first event we deliver
                            // (state_entry) in default state
                            //
                            SaveState();

                            _SaveState = false;
                        }
                    }
                    catch (Exception exx)
                    {
                        e = exx;
                    }

                    if(e != null)
                    {
                        //                            _log.DebugFormat(
                        //                                "[SCRIPT] Exception in script {0} {1}: {2}{3}",
                        //                                ScriptName, ItemID, e.Message, e.StackTrace);

                        if ((!(e is TargetInvocationException)
                            || !(e.InnerException is SelfDeleteException)
                            && !(e.InnerException is ScriptDeleteException)
                            && !(e.InnerException is ScriptCoopStopException))
                            && !(e is ThreadAbortException))
                        {
                            try
                            {
                                if(e.InnerException != null && e.InnerException is ScriptException)
                                {
                                    bool toowner = false;
                                    string text = e.InnerException.Message;
                                    if(text.StartsWith("(OWNER)"))
                                    {
                                        text = text.Substring(7);
                                        toowner = true;
                                    }
                                    text +=     "(script: " + ScriptName +
                                                " event: " + data.EventName +
                                                " primID:" + Part.UUID.ToString() +
                                                " at " + Part.AbsolutePosition + ")";
                                    if (text.Length > 1000)
                                        text = text.Substring(0, 1000);
                                    if (toowner)
                                    {
                                        ScenePresence sp = Engine.World.GetScenePresence(Part.OwnerID);
                                        if (sp != null && !sp.IsNPC)
                                            Engine.World.SimChatToAgent(Part.OwnerID, Utils.StringToBytes(text), 0x7FFFFFFF, Part.AbsolutePosition,
                                                                                   Part.Name, Part.UUID, false);
                                    }
                                    else
                                        Engine.World.SimChat(Utils.StringToBytes(text),
                                                           ChatTypeEnum.DebugChannel, 2147483647,
                                                           Part.AbsolutePosition,
                                                           Part.Name, Part.UUID, false);
                                    _log.Debug(string.Format(
                                        "[SCRIPT ERROR]: {0} (at event {1}, part {2} {3} at {4} in {5}",
                                        e.InnerException.Message,
                                        data.EventName,
                                        PrimName,
                                        Part.UUID,
                                        Part.AbsolutePosition,
                                        Part.ParentGroup.Scene.Name));

                                }
                                else
                                {

                                    // DISPLAY ERROR INWORLD
                                    string text = FormatException(e);

                                    if (text.Length > 1000)
                                        text = text.Substring(0, 1000);
                                    Engine.World.SimChat(Utils.StringToBytes(text),
                                                           ChatTypeEnum.DebugChannel, 2147483647,
                                                           Part.AbsolutePosition,
                                                           Part.Name, Part.UUID, false);


                                    _log.Debug(string.Format(
                                        "[SCRIPT ERROR]: Runtime error in script {0} (event {1}), part {2} {3} at {4} in {5} ",
                                        ScriptName,
                                        data.EventName,
                                        PrimName,
                                        Part.UUID,
                                        Part.AbsolutePosition,
                                        Part.ParentGroup.Scene.Name),
                                        e);
                                }
                            }
                            catch (Exception)
                            {
                            }
                        }
                        else if (e is TargetInvocationException && e.InnerException is SelfDeleteException)
                        {
                            _InSelfDelete = true;
                            Engine.World.DeleteSceneObject(Part.ParentGroup, false);
                        }
                        else if (e is TargetInvocationException && e.InnerException is ScriptDeleteException)
                        {
                            _InSelfDelete = true;
                            Part.Inventory.RemoveInventoryItem(ItemID);
                        }
                        else if (e is TargetInvocationException && e.InnerException is ScriptCoopStopException)
                        {
                            if (DebugLevel >= 1)
                                _log.DebugFormat(
                                    "[SCRIPT INSTANCE]: Script {0}.{1} in event {2}, state {3} stopped co-operatively.",
                                    PrimName, ScriptName, data.EventName, State);
                        }
                    }
                }
            }

            // If there are more events and we are currently running and not shutting down, then ask the
            // script engine to run the next event.
            lock (EventQueue)
            {
                // Increase processed events counter and prevent wrap;
                if (++EventsProcessed == 1000000)
                    EventsProcessed = 100000;

                if (EventsProcessed % 100000 == 0 && DebugLevel > 0)
                {
                    _log.DebugFormat("[SCRIPT INSTANCE]: Script \"{0}\" (Object \"{1}\" {2} @ {3}.{4}, Item ID {5}, Asset {6}) in event {7}: processed {8:n0} script events",
                                    ScriptTask.Name,
                                    Part.ParentGroup.Name, Part.ParentGroup.UUID, Part.ParentGroup.AbsolutePosition, Part.ParentGroup.Scene.Name,
                                    ScriptTask.ItemID, ScriptTask.AssetID, data.EventName, EventsProcessed);
                }

                if (EventQueue.Count > 0 && Running && !ShuttingDown && !_InSelfDelete)
                {
                    _CurrentWorkItem = Engine.QueueEventHandler(this);
                }
                else
                {
                    _CurrentWorkItem = null;
                }
            }

            _DetectParams = null;

            return 0;
        }

        public int EventTime()
        {
            if (!_InEvent)
                return 0;

            return (DateTime.UtcNow - _EventStart).Seconds;
        }

        public void ResetScript(int timeout)
        {
            if (_Script == null)
                return;

            bool running = Running;

            RemoveState();
            ReleaseControlsorPermissions(true);

            Stop(timeout);
            AsyncCommandManager.RemoveScript(Engine, LocalID, ItemID);

            _TimerQueued = false;
            _StateChangeInProgress = false;
            EventQueue.Clear();

            _Script.ResetVars();
            StartParam = 0;
            State = "default";

            SceneObjectPart part = Engine.World.GetSceneObjectPart(LocalID);
            if (part == null)
                return;

            part.CollisionSound = UUID.Zero;
            part.RemoveScriptTargets(ItemID);
            part.SetScriptEvents(ItemID, _Script.GetStateEventFlags(State));
            if (running)
                Start();

            _SaveState = StatePersistedHere;

            PostEvent(new EventParams("state_entry",
                    new object[0], new DetectParams[0]));
        }

        [DebuggerNonUserCode] //Stops the VS debugger from farting in this function
        public void ApiResetScript()
        {
            // bool running = Running;

            RemoveState();
            ReleaseControlsorPermissions(true);

            AsyncCommandManager.RemoveScript(Engine, LocalID, ItemID);

            _TimerQueued = false;
            _StateChangeInProgress = false;
            EventQueue.Clear();
            _Script.ResetVars();
            string oldState = State;
            StartParam = 0;
            State = "default";

            SceneObjectPart part = Engine.World.GetSceneObjectPart(LocalID);
            if(part != null)
            {
                part.CollisionSound = UUID.Zero;
                part.RemoveScriptTargets(ItemID);
                part.SetScriptEvents(ItemID, _Script.GetStateEventFlags(State));
            }
            if (_CurrentEvent != "state_entry" || oldState != "default")
            {
                _SaveState = StatePersistedHere;
                PostEvent(new EventParams("state_entry",
                        new object[0], new DetectParams[0]));
                throw new EventAbortException();
            }
        }

        public Dictionary<string, object> GetVars()
        {
            if (_Script != null)
                return _Script.GetVars();
            else
                return new Dictionary<string, object>();
        }

        public void SetVars(Dictionary<string, object> vars)
        {
//            foreach (KeyValuePair<string, object> kvp in vars)
//                _log.DebugFormat("[SCRIPT INSTANCE]: Setting var {0}={1}", kvp.Key, kvp.Value);

            _Script.SetVars(vars);
        }

        public DetectParams GetDetectParams(int idx)
        {
            if (_DetectParams == null)
                return null;
            if (idx < 0 || idx >= _DetectParams.Length)
                return null;

            return _DetectParams[idx];
        }

        public UUID GetDetectID(int idx)
        {
            if (_DetectParams == null)
                return UUID.Zero;
            if (idx < 0 || idx >= _DetectParams.Length)
                return UUID.Zero;

            return _DetectParams[idx].Key;
        }

        public void SaveState()
        {
            if (!Running && !StayStopped)
                return;

            // We cannot call this inside the EventQueue lock since it will currently take AsyncCommandManager.staticLock.
            // This may already be held by AsyncCommandManager.DoOneCmdHandlerPass() which in turn can take EventQueue
            // lock via ScriptInstance.PostEvent().
            PluginData = AsyncCommandManager.GetSerializationData(Engine, ItemID);

            // We need to lock here to avoid any race with a thread that is removing this script.
            lock (EventQueue)
            {
                // Check again to avoid a race with a thread in Stop()
                if (!Running && !StayStopped)
                    return;

                // If we're currently in an event, just tell it to save upon return
                //
                if (_InEvent)
                {
                    _SaveState = true;
                    return;
                }

    //            _log.DebugFormat(
    //                "[SCRIPT INSTANCE]: Saving state for script {0} (id {1}) in part {2} (id {3}) in object {4} in {5}",
    //                ScriptTask.Name, ScriptTask.ItemID, Part.Name, Part.UUID, Part.ParentGroup.Name, Engine.World.Name);

                string xml = ScriptSerializer.Serialize(this);

                // Compare hash of the state we just just created with the state last written to disk
                // If the state is different, update the disk file.
                UUID hash = UUID.Parse(Utils.MD5String(xml));

                if (hash != _CurrentStateHash)
                {
                    try
                    {
                        using (FileStream fs = File.Create(Path.Combine(_dataPath, ItemID.ToString() + ".state")))
                        {
                            byte[] buf = Util.UTF8NoBomEncoding.GetBytes(xml);
                            fs.Write(buf, 0, buf.Length);
                        }
                    }
                    catch(Exception)
                    {
                        // _log.Error("Unable to save xml\n"+e.ToString());
                    }
                    //if (!File.Exists(Path.Combine(Path.GetDirectoryName(assembly), ItemID.ToString() + ".state")))
                    //{
                    //    throw new Exception("Completed persistence save, but no file was created");
                    //}
                    _CurrentStateHash = hash;
                }

                StayStopped = false;
            }
        }

        public IScriptApi GetApi(string name)
        {
            if (_Apis.ContainsKey(name))
            {
//                _log.DebugFormat("[SCRIPT INSTANCE]: Found api {0} in {1}@{2}", name, ScriptName, PrimName);

                return _Apis[name];
            }

//            _log.DebugFormat("[SCRIPT INSTANCE]: Did not find api {0} in {1}@{2}", name, ScriptName, PrimName);

            return null;
        }

        public override string ToString()
        {
            return string.Format("{0} {1} on {2}", ScriptName, ItemID, PrimName);
        }

        string FormatException(Exception e)
        {
            if (e.InnerException == null) // Not a normal runtime error
                return e.ToString();

            string message = "Runtime error:\n" + e.InnerException.StackTrace;
            string[] lines = message.Split(new char[] {'\n'});

            foreach (string line in lines)
            {
                if (line.Contains("SecondLife.Script"))
                {
                    int idx = line.IndexOf(':');
                    if (idx != -1)
                    {
                        string val = line.Substring(idx+1);
                        int lineNum = 0;
                        if (int.TryParse(val, out lineNum))
                        {
                            KeyValuePair<int, int> pos =
                                    Compiler.FindErrorPosition(
                                    lineNum, 0, LineMap);

                            int scriptLine = pos.Key;
                            int col = pos.Value;
                            if (scriptLine == 0)
                                scriptLine++;
                            if (col == 0)
                                col++;
                            message = string.Format("Runtime error:\n" +
                                    "({0}): {1}", scriptLine - 1,
                                    e.InnerException.Message);

                            return message;
                        }
                    }
                }
            }

            // _log.ErrorFormat("Scripting exception:");
            // _log.ErrorFormat(e.ToString());

            return e.ToString();
        }

        public string GetAssemblyName()
        {
            return _assemblyPath;
        }

        public string GetXMLState()
        {
            bool run = Running;
            Stop(100);
            Running = run;

            // We should not be doing this, but since we are about to
            // dispose this, it really doesn't make a difference
            // This is meant to work around a Windows only race
            //
            _InEvent = false;

            // Force an update of the in-memory plugin data
            //
            PluginData = AsyncCommandManager.GetSerializationData(Engine, ItemID);

            return ScriptSerializer.Serialize(this);
        }

        public UUID RegionID => _RegionID;

        public void Suspend()
        {
            Suspended = true;
        }

        public void Resume()
        {
            Suspended = false;
        }
    }

    /// <summary>
    /// Xengine event wait handle.
    /// </summary>
    /// <remarks>
    /// This class exists becase XEngineScriptBase gets a reference to this wait handle.  We need to make sure that
    /// when scripts are running in different AppDomains the lease does not expire.
    /// FIXME: Like LSL_Api, etc., this effectively leaks memory since the GC will never collect it.  To avoid this,
    /// proper remoting sponsorship needs to be implemented across the board.
    /// </remarks>
    public class XEngineEventWaitHandle : EventWaitHandle
    {
        public XEngineEventWaitHandle(bool initialState, EventResetMode mode) : base(initialState, mode) {}

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }
}
