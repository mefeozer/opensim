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
using System.IO;
using System.Text;
using OpenMetaverse;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.Framework.Scenes;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

// This class exists in the main app domain
//
namespace OpenSim.Region.ScriptEngine.Yengine
{
    public partial class XMRInstance
    {

        private bool _disposed;
        // In case Dispose() doesn't get called, we want to be sure to clean
        // up.  This makes sure we decrement _CompiledScriptRefCount.
        ~XMRInstance()
        {
            Dispose(false);
        }

        /**
         * @brief Clean up stuff.
         *        We specifically leave _DescName intact for 'xmr ls' command.
         */
        public void Dispose()
        {
            Dispose(true);
            //GC.SuppressFinalize(this);
        }

        public void Dispose(bool fromdispose)
        {
            if (_disposed)
                return;

            // Tell script stop executing next time it calls CheckRun().
            suspendOnCheckRunHold = true;

            // Don't send us any more events.
            lock (_RunLock)
            {
                if(_Part != null)
                {
                    AsyncCommandManager.RemoveScript(_Engine, _LocalID, _ItemID);
                    _Part = null;
                }
            }

             // Let script methods get garbage collected if no one else is using
             // them.
            DecObjCodeRefCount();
            _disposed = true;
        }

        private void DecObjCodeRefCount()
        {
            if(_ObjCode != null)
            {
                lock(_CompileLock)
                {
                    ScriptObjCode objCode;

                    if(_CompiledScriptObjCode.TryGetValue(_ScriptObjCodeKey, out objCode) &&
                        objCode == _ObjCode &&
                        --objCode.refCount == 0)
                    {
                        _CompiledScriptObjCode.Remove(_ScriptObjCodeKey);
                    }
                }
                _ObjCode = null;
            }
        }

        public void Verbose(string format, params object[] args)
        {
            if(_Engine._Verbose)
                _log.DebugFormat(format, args);
        }

        // Called by 'xmr top' console command
        // to dump this script's state to console
        //  Sacha 
        public void RunTestTop()
        {
            if(_InstEHSlice > 0)
            {
                Console.WriteLine(_DescName);
                Console.WriteLine("    _LocalID       = " + _LocalID);
                Console.WriteLine("    _ItemID        = " + _ItemID);
                Console.WriteLine("    _Item.AssetID  = " + _Item.AssetID);
                Console.WriteLine("    _StartParam    = " + _StartParam);
                Console.WriteLine("    _PostOnRez     = " + _PostOnRez);
                Console.WriteLine("    _StateSource   = " + _StateSource);
                Console.WriteLine("    _SuspendCount  = " + _SuspendCount);
                Console.WriteLine("    _SleepUntil    = " + _SleepUntil);
                Console.WriteLine("    _IState        = " + _IState.ToString());
                Console.WriteLine("    _StateCode     = " + GetStateName(stateCode));
                Console.WriteLine("    eventCode       = " + eventCode.ToString());
                Console.WriteLine("    _LastRanAt     = " + _LastRanAt.ToString());
                Console.WriteLine("    heapUsed/Limit  = " + xmrHeapUsed() + "/" + heapLimit);
                Console.WriteLine("    _InstEHEvent   = " + _InstEHEvent.ToString());
                Console.WriteLine("    _InstEHSlice   = " + _InstEHSlice.ToString());
            }
        }

        // Called by 'xmr ls' console command
        // to dump this script's state to console
        public string RunTestLs(bool flagFull)
        {
            if(flagFull)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(_DescName);
                sb.AppendLine("    _LocalID            = " + _LocalID);
                sb.AppendLine("    _ItemID             = " + _ItemID + "  (.state file)");
                sb.AppendLine("    _Item.AssetID       = " + _Item.AssetID);
                sb.AppendLine("    _Part.WorldPosition = " + _Part.GetWorldPosition());
                sb.AppendLine("    _ScriptObjCodeKey   = " + _ScriptObjCodeKey + "  (source text)");
                sb.AppendLine("    _StartParam         = " + _StartParam);
                sb.AppendLine("    _PostOnRez          = " + _PostOnRez);
                sb.AppendLine("    _StateSource        = " + _StateSource);
                sb.AppendLine("    _SuspendCount       = " + _SuspendCount);
                sb.AppendLine("    _SleepUntil         = " + _SleepUntil);
                sb.AppendLine("    _SleepEvMask1       = 0x" + _SleepEventMask1.ToString("X"));
                sb.AppendLine("    _SleepEvMask2       = 0x" + _SleepEventMask2.ToString("X"));
                sb.AppendLine("    _IState             = " + _IState.ToString());
                sb.AppendLine("    _StateCode          = " + GetStateName(stateCode));
                sb.AppendLine("    eventCode            = " + eventCode.ToString());
                sb.AppendLine("    _LastRanAt          = " + _LastRanAt.ToString());
                sb.AppendLine("    _RunOnePhase        = " + _RunOnePhase);
                sb.AppendLine("    suspOnCkRunHold      = " + suspendOnCheckRunHold);
                sb.AppendLine("    suspOnCkRunTemp      = " + suspendOnCheckRunTemp);
                sb.AppendLine("    _CheckRunPhase      = " + _CheckRunPhase);
                sb.AppendLine("    heapUsed/Limit       = " + xmrHeapUsed() + "/" + heapLimit);
                sb.AppendLine("    _InstEHEvent        = " + _InstEHEvent.ToString());
                sb.AppendLine("    _InstEHSlice        = " + _InstEHSlice.ToString());
                sb.AppendLine("    _CPUTime            = " + _CPUTime);
                sb.AppendLine("    callMode             = " + callMode);
                lock(_QueueLock)
                {
                    sb.AppendLine("    _Running            = " + _Running);
                    foreach(EventParams evt in _EventQueue)
                    {
                        sb.AppendLine("        evt.EventName        = " + evt.EventName);
                    }
                }
                return sb.ToString();
            }
            else
            {
                return string.Format("{0} {1} {2} {3} {4} {5}",
                        _ItemID,
                        _CPUTime.ToString("F3").PadLeft(9),
                        _InstEHEvent.ToString().PadLeft(9),
                        _IState.ToString().PadRight(10),
                        _Part.GetWorldPosition().ToString().PadRight(32),
                        _DescName);
            }
        }

        /**
         * @brief For a given stateCode, get a mask of the low 32 event codes
         *        that the state has handlers defined for.
         */
        public ulong GetStateEventFlags(int state)
        {
            if(state < 0 ||
                state >= _ObjCode.scriptEventHandlerTable.GetLength(0))
            {
                return 0;
            }

            ulong flags = 0;
            for(int i = 0; i <(int)ScriptEventCode.Size; i++)
            {
                if(_ObjCode.scriptEventHandlerTable[state, i] != null)
                {
                    flags |= 1ul << i;
                }
            }
            return flags;
        }

        /**
         * @brief Get the .state file name.
         */
        public static string GetStateFileName(string scriptBasePath, UUID itemID)
        {
            return GetScriptFileName(scriptBasePath, itemID.ToString() + ".state");
        }

        public string GetScriptFileName(string filename)
        {
            return GetScriptFileName(_ScriptBasePath, filename);
        }

        public string GetScriptILFileName(string filename)
        {
            string path = Path.Combine(_ScriptBasePath, "DebugIL");
            Directory.CreateDirectory(path);
            return Path.Combine(path, filename);
        }

        public static string GetScriptFileName(string scriptBasePath, string filename)
        {
             // Get old path, ie, all files lumped in a single huge directory.
            string oldPath = Path.Combine(scriptBasePath, filename);

             // Get new path, ie, files split up based on first 2 chars of name.
             //           string subdir = filename.Substring (0, 2);
             //           filename = filename.Substring (2);
            string subdir = filename.Substring(0, 1);
            filename = filename.Substring(1);
            scriptBasePath = Path.Combine(scriptBasePath, subdir);
            Directory.CreateDirectory(scriptBasePath);
            string newPath = Path.Combine(scriptBasePath, filename);

             // If file exists only in old location, move to new location.
             // If file exists in both locations, delete old location.
            if(File.Exists(oldPath))
            {
                if(File.Exists(newPath))
                {
                    File.Delete(oldPath);
                }
                else
                {
                    File.Move(oldPath, newPath);
                }
            }

             // Always return new location.
            return newPath;
        }

        /**
         * @brief Decode state code (int) to state name (string).
         */
        public string GetStateName(int stateCode)
        {
            try
            {
                return _ObjCode.stateNames[stateCode];
            }
            catch
            {
                return stateCode.ToString();
            }
        }

        /**
         * @brief various gets & sets.
         */
        public int StartParam
        {
            get => _StartParam;
            set => _StartParam = value;
        }

        public double MinEventDelay
        {
            get => _minEventDelay;
            set
            {
                if (value > 0.001)
                    _minEventDelay = value;
                else
                    _minEventDelay = 0.0;

                _nextEventTime = 0.0; // reset it
            }
        }


        public SceneObjectPart SceneObject => _Part;

        public DetectParams[] DetectParams
        {
            get => _DetectParams;
            set => _DetectParams = value;
        }

        public UUID ItemID => _ItemID;

        public UUID AssetID => _Item.AssetID;

        public bool Running
        {
            get => _Running;
            set
            {
                lock(_QueueLock)
                {
                    _Running = value;
                    if(value)
                    {
                        if (_IState == XMRInstState.SUSPENDED && _SuspendCount == 0)
                        {
                            if(eventCode != ScriptEventCode.None)
                            {
                                _IState = XMRInstState.ONYIELDQ;
                                _Engine.QueueToYield(this);
                            }
                            else if (_EventQueue != null && _EventQueue.First != null)
                            {
                                _IState = XMRInstState.ONSTARTQ;
                                _Engine.QueueToStart(this);
                            }
                            else
                                _IState = XMRInstState.IDLE;
                        }
                        //else if(_SuspendCount != 0)
                        //    _IState = XMRInstState.IDLE;
                    }
                    else
                    {
                        if(_IState == XMRInstState.ONSLEEPQ)
                        {
                            _Engine.RemoveFromSleep(this);
                            _IState = XMRInstState.SUSPENDED;
                        }
                        EmptyEventQueues();
                    }
                }
            }
        }

        /**
         * @brief Empty out the event queues.
         *        Assumes caller has the _QueueLock locked.
         */
        public void EmptyEventQueues()
        {
            _EventQueue.Clear();
            for(int i = _EventCounts.Length; --i >= 0;)
                _EventCounts[i] = 0;
        }

        /**
         * @brief Convert an LSL vector to an Openmetaverse vector.
         */
        public static OpenMetaverse.Vector3 LSLVec2OMVec(LSL_Vector lslVec)
        {
            return new OpenMetaverse.Vector3((float)lslVec.x, (float)lslVec.y, (float)lslVec.z);
        }

        /**
         * @brief Extract an integer from an element of an LSL_List.
         */
        public static int ListInt(object element)
        {
            if(element is LSL_Integer)
            {
                return (int)(LSL_Integer)element;
            }
            return (int)element;
        }

        /**
         * @brief Extract a string from an element of an LSL_List.
         */
        public static string ListStr(object element)
        {
            if(element is LSL_String)
            {
                return (string)(LSL_String)element;
            }
            return (string)element;
        }
    }
}
