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
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.Framework.Scenes;
using log4net;

// This class exists in the main app domain
//
namespace OpenSim.Region.ScriptEngine.Yengine
{
    /**
     * @brief Which queue it is in as far as running is concerned,
     *        ie, _StartQueue, _YieldQueue, _SleepQueue, etc.
     * Allowed transitions:
     *   Starts in CONSTRUCT when constructed
     *   CONSTRUCT->ONSTARTQ              : only by thread that constructed and compiled it
     *   IDLE->ONSTARTQ,RESETTING         : by any thread but must have _QueueLock when transitioning
     *   ONSTARTQ->RUNNING,RESETTING      : only by thread that removed it from _StartQueue
     *   ONYIELDQ->RUNNING,RESETTING      : only by thread that removed it from _YieldQueue
     *   ONSLEEPQ->REMDFROMSLPQ           : by any thread but must have _SleepQueue when transitioning
     *   REMDFROMSLPQ->ONYIELDQ,RESETTING : only by thread that removed it from _SleepQueue
     *   RUNNING->whatever1               : only by thread that transitioned it to RUNNING
     *                                      whatever1 = IDLE,ONSLEEPQ,ONYIELDQ,ONSTARTQ,SUSPENDED,FINISHED
     *   FINSHED->whatever2               : only by thread that transitioned it to FINISHED
     *                                      whatever2 = IDLE,ONSTARTQ,DISPOSED
     *   SUSPENDED->ONSTARTQ              : by any thread (NOT YET IMPLEMENTED, should be under some kind of lock?)
     *   RESETTING->ONSTARTQ              : only by the thread that transitioned it to RESETTING
     */
    public enum XMRInstState
    {
        CONSTRUCT,     // it is being constructed
        IDLE,          // nothing happening (finished last event and _EventQueue is empty)
        ONSTARTQ,      // inserted on _Engine._StartQueue
        RUNNING,       // currently being executed by RunOne()
        ONSLEEPQ,      // inserted on _Engine._SleepQueue
        REMDFROMSLPQ,  // removed from _SleepQueue but not yet on _YieldQueue
        ONYIELDQ,      // inserted on _Engine._YieldQueue
        FINISHED,      // just finished handling an event
        SUSPENDED,     // _SuspendCount > 0
        RESETTING,     // being reset via external call
        DISPOSED       // has been disposed
    }

    public partial class XMRInstance: XMRInstAbstract, IDisposable
    {
        /******************************************************************\
         *  This module contains the instance variables for XMRInstance.  *
        \******************************************************************/

        public const int MAXEVENTQUEUE = 64;

        public static readonly DetectParams[] zeroDetectParams = new DetectParams[0];
        public static readonly object[] zeroObjectArray = new object[0];

        public static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public XMRInstance _NextInst;  // used by XMRInstQueue
        public XMRInstance _PrevInst;

        // For a given _Item.AssetID, do we have the compiled object code and where
        // is it?
        public static object _CompileLock = new object();
        private static readonly Dictionary<string, ScriptObjCode> _CompiledScriptObjCode = new Dictionary<string, ScriptObjCode>();

        public XMRInstState _IState;

        public bool _ForceRecomp = false;
        public SceneObjectPart _Part = null;
        public uint _LocalID = 0;
        public TaskInventoryItem _Item = null;
        public UUID _ItemID;
        public UUID _PartUUID;
        private string _CameFrom;
        private string _ScriptObjCodeKey;

        private Yengine _Engine = null;
        private string _ScriptBasePath;
        private string _StateFileName;
        public string _SourceCode;
        public bool _PostOnRez;
        private DetectParams[] _DetectParams = null;
        public int _StartParam = 0;
        public StateSource _StateSource;
        public string _DescName;
        private bool[] _HaveEventHandlers;
        public int _StackSize;
        public int _HeapSize;
        private ArrayList _CompilerErrors;
        private DateTime _LastRanAt = DateTime.MinValue;
        private string _RunOnePhase = "hasn't run";
        private string _CheckRunPhase = "hasn't checked";
        public int _InstEHEvent = 0;  // number of events dequeued (StartEventHandler called)
        public int _InstEHSlice = 0;  // number of times handler timesliced (ResumeEx called)
        public double _CPUTime = 0;  // accumulated CPU time (milliseconds)
        public double _SliceStart = 0;  // when did current exec start

        // If code needs to have both _QueueLock and _RunLock,
        // be sure to lock _RunLock first then _QueueLock, as
        // that is the order used in RunOne().
        // These locks are currently separated to allow the script
        // to call API routines that queue events back to the script.
        // If we just had one lock, then the queuing would deadlock.

        // guards _DetachQuantum, _EventQueue, _EventCounts, _Running, _Suspended
        public object _QueueLock = new object();

        // true iff allowed to accept new events
        public bool _Running = true;

        // queue of events that haven't been acted upon yet
        public LinkedList<EventParams> _EventQueue = new LinkedList<EventParams>();

        // number of events of each code currently in _EventQueue.
        private readonly int[] _EventCounts = new int[(int)ScriptEventCode.Size];

        // locked whilst running on the microthread stack (or about to run on it or just ran on it)
        private readonly object _RunLock = new object();

        // script won't step while > 0.  bus-atomic updates only.
        private int _SuspendCount = 0;

        // don't run any of script until this time
        // or until one of these events are queued
        public DateTime _SleepUntil = DateTime.MinValue;
        public int _SleepEventMask1 = 0;
        public int _SleepEventMask2 = 0;

        private XMRLSL_Api _XMRLSLApi;

        /*
         * Makes sure migration data version is same on both ends.
         */
        public static byte migrationVersion = 11;

        // Incremented each time script gets reset.
        public int _ResetCount = 0;

        // Scripts start suspended now. This means that event queues will
        // accept events, but will not actually run them until the core
        // tells it it's OK. This is needed to prevent loss of link messages
        // in complex objects, where no event can be allowed to run until
        // all possible link message receivers' queues are established.
        // Guarded by _QueueLock.
        public bool _Suspended = true;

        // We really don't want to save state for a script that hasn't had
        // a chance to run, because it's state will be blank. That would
        // cause attachment state loss.
        public bool _HasRun = false;

        // When llDie is executed within the attach(NULL_KEY) event of
        // a script being detached to inventory, the DeleteSceneObject call
        // it causes will delete the script instances before their state can
        // be saved. Therefore, the instance needs to know that it's being
        // detached to inventory, rather than to ground.
        // Also, the attach(NULL_KEY) event needs to run with priority, and
        // it also needs to have a limited quantum.
        // If this is nonzero, we're detaching to inventory.
        // Guarded by _QueueLock.
        private int _DetachQuantum = 0;

        // Finally, we need to wait until the quantum is done, or the script
        // suspends itself. This should be efficient, so we use an event
        // for it instead of spinning busy.
        // It's born ready, but will be reset when the detach is posted.
        // It will then be set again on suspend/completion
        private readonly ManualResetEvent _DetachReady = new ManualResetEvent(true);

        // llmineventdelay support
        double _minEventDelay = 0.0;
        double _nextEventTime = 0.0;

        private static readonly Dictionary<string, ScriptEventCode> _eventCodeMap = new Dictionary<string, ScriptEventCode>()
        {
            {"attach", ScriptEventCode.attach},
            {"at_rot_target", ScriptEventCode.at_rot_target},
            {"at_target", ScriptEventCode.at_target},
            {"collision", ScriptEventCode.collision},
            {"collision_end", ScriptEventCode.collision_end},
            {"collision_start", ScriptEventCode.collision_start},
            {"control", ScriptEventCode.control},
            {"dataserver", ScriptEventCode.dataserver},
            {"email", ScriptEventCode.email},
            {"http_response", ScriptEventCode.http_response},
            {"land_collision", ScriptEventCode.land_collision},
            {"land_collision_end", ScriptEventCode.land_collision_end},
            {"land_collision_start", ScriptEventCode.land_collision_start},
            {"listen", ScriptEventCode.listen},
            {"money", ScriptEventCode.money},
            {"moving_end", ScriptEventCode.moving_end},
            {"moving_start", ScriptEventCode.moving_start},
            {"not_at_rot_target", ScriptEventCode.not_at_rot_target},
            {"not_at_target", ScriptEventCode.not_at_target},
            {"remote_data", ScriptEventCode.remote_data},
            {"run_time_permissions", ScriptEventCode.run_time_permissions},
            {"state_entry", ScriptEventCode.state_entry},
            {"state_exit", ScriptEventCode.state_exit},
            {"timer", ScriptEventCode.timer},
            {"touch", ScriptEventCode.touch},
            {"touch_end", ScriptEventCode.touch_end},
            {"touch_start", ScriptEventCode.touch_start},
            {"transaction_result", ScriptEventCode.transaction_result},
            {"object_rez", ScriptEventCode.object_rez},
            {"changed", ScriptEventCode.changed},
            {"link_message", ScriptEventCode.link_message},
            {"no_sensor", ScriptEventCode.no_sensor},
            {"on_rez", ScriptEventCode.on_rez},
            {"sensor", ScriptEventCode.sensor},
            {"http_request", ScriptEventCode.http_request},
            {"path_update", ScriptEventCode.path_update}
        };
    }
}
