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
using System.Xml;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Yengine
{
    public partial class XMRInstance
    {
        /****************************************************************************\
         *  The only method of interest to outside this module is the Initializer.  *
         *                                                                          *
         *  The rest of this module contains support routines for the Initializer.  *
        \****************************************************************************/

        /**
         * @brief Initializer, loads script in memory and all ready for running.
         * @param engine = YEngine instance this is part of
         * @param scriptBasePath = directory name where files are
         * @param stackSize = number of bytes to allocate for stacks
         * @param errors = return compiler errors in this array
         * @param forceRecomp = force recompile
         * Throws exception if any error, so it was successful if it returns.
         */
        public void Initialize(Yengine engine, string scriptBasePath,
                               int stackSize, int heapSize, ArrayList errors)
        {
            if(stackSize < 16384)
                stackSize = 16384;
            if(heapSize < 16384)
                heapSize = 16384;

            // Save all call parameters in instance vars for easy access.
            _Engine = engine;
            _ScriptBasePath = scriptBasePath;
            _StackSize = stackSize;
            _StackLeft = stackSize;
            _HeapSize = heapSize;
            _localsHeapUsed = 0;
            _arraysHeapUsed = 0;
            _CompilerErrors = errors;
            _StateFileName = GetStateFileName(scriptBasePath, _ItemID);

            // Not in any XMRInstQueue.
            _NextInst = this;
            _PrevInst = this;

            // Set up list of API calls it has available.
            // This also gets the API modules ready to accept setup data, such as
            // active listeners being restored.
            IScriptApi scriptApi;
            ApiManager am = new ApiManager();
            foreach(string api in am.GetApis())
            {
                // Instantiate the API for this script instance.
                if(api != "LSL")
                    scriptApi = am.CreateApi(api);
                else
                    scriptApi = _XMRLSLApi = new XMRLSL_Api();

                // Connect it up to the instance.
                InitScriptApi(engine, api, scriptApi);
            }

            _XMRLSLApi.InitXMRLSLApi(this);

            // Get object loaded, compiling script and reading .state file as
            // necessary to restore the state.
            suspendOnCheckRunHold = true;
            InstantiateScript();
            _SourceCode = null;
            if(_ObjCode == null)
                throw new ArgumentNullException("_ObjCode");
            if(_ObjCode.scriptEventHandlerTable == null)
                throw new ArgumentNullException("_ObjCode.scriptEventHandlerTable");

            suspendOnCheckRunHold = false;
            suspendOnCheckRunTemp = false;
        }

        private void InitScriptApi(Yengine engine, string api, IScriptApi scriptApi)
        {
            // Set up _ApiManager_<APINAME> = instance pointer.
            engine._XMRInstanceApiCtxFieldInfos[api].SetValue(this, scriptApi);

            // Initialize the API instance.
            scriptApi.Initialize(_Engine, _Part, _Item);
            this.InitApi(api, scriptApi);
        }


        /*
         * Get script object code loaded in memory and all ready to run,
         * ready to resume it from where the .state file says it was last
         */
        private void InstantiateScript()
        {
            bool compiledIt = false;
            ScriptObjCode objCode;

            // If source code string is empty, use the asset ID as the object file name.
            // Allow lines of // comments at the beginning (for such as engine selection).
            int i, j, len;
            if(_SourceCode == null)
                _SourceCode = string.Empty;
            for(len = _SourceCode.Length; len > 0; --len)
            {
                if(_SourceCode[len - 1] > ' ')
                    break;
            }
            for(i = 0; i < len; i++)
            {
                char c = _SourceCode[i];
                if(c <= ' ')
                    continue;
                if(c != '/')
                    break;
                if(i + 1 >= len || _SourceCode[i + 1] != '/')
                    break;
                i = _SourceCode.IndexOf('\n', i);
                if(i < 0)
                    i = len - 1;
            }
            if(i >= len)
            {
                // Source consists of nothing but // comments and whitespace,
                // or we are being forced to use the asset-id as the key, to
                // open an already existing object code file.
                _ScriptObjCodeKey = _Item.AssetID.ToString();
                if(i >= len)
                    _SourceCode = "";
            }
            else
            {
                // Make up dictionary key for the object code.
                // Use the same object code for identical source code
                // regardless of asset ID, so we don't care if they
                // copy scripts or not.
                byte[] scbytes = System.Text.Encoding.UTF8.GetBytes(_SourceCode);
                StringBuilder sb = new StringBuilder((256 + 5) / 6);
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                    ByteArrayToSixbitStr(sb, sha.ComputeHash(scbytes));
                _ScriptObjCodeKey = sb.ToString();

                // But source code can be just a sixbit string itself
                // that identifies an already existing object code file.
                if(len - i == _ScriptObjCodeKey.Length)
                {
                    for(j = len; --j >= i;)
                    {
                        if(sixbit.IndexOf(_SourceCode[j]) < 0)
                            break;
                    }
                    if(j < i)
                    {
                        _ScriptObjCodeKey = _SourceCode.Substring(i, len - i);
                        _SourceCode = "";
                    }
                }
            }

            // There may already be an ScriptObjCode struct in memory that
            // we can use.  If not, try to compile it.
            lock(_CompileLock)
            {
                if(!_CompiledScriptObjCode.TryGetValue(_ScriptObjCodeKey, out objCode) || _ForceRecomp)
                {
                    objCode = TryToCompile();
                    compiledIt = true;
                }

                // Loaded successfully, increment reference count.
                // If we just compiled it though, reset count to 0 first as
                // this is the one-and-only existance of this objCode struct,
                // and we want any old ones for this source code to be garbage
                // collected.

                if(compiledIt)
                {
                    _CompiledScriptObjCode[_ScriptObjCodeKey] = objCode;
                    objCode.refCount = 0;
                }
                objCode.refCount++;

                // Now set up to decrement ref count on dispose.
                _ObjCode = objCode;
            }

            try
            {

                // Fill in script instance from object code
                // Script instance is put in a "never-ever-has-run-before" state.
                LoadObjCode();

                // Fill in script intial state
                // - either as loaded from a .state file
                // - or initial default state_entry() event
                LoadInitialState();
            }
            catch
            {

                // If any error loading, decrement object code reference count.
                DecObjCodeRefCount();
                throw;
            }
        }

        private const string sixbit = "0123456789_abcdefghijklmnopqrstuvwxyz@ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private static void ByteArrayToSixbitStr(StringBuilder sb, byte[] bytes)
        {
            int bit = 0;
            int val = 0;
            foreach(byte b in bytes)
            {
                val |= (int)((uint)b << bit);
                bit += 8;
                while(bit >= 6)
                {
                    sb.Append(sixbit[val & 63]);
                    val >>= 6;
                    bit -= 6;
                }
            }
            if(bit > 0)
                sb.Append(sixbit[val & 63]);
        }

        // Try to create object code from source code
        // If error, just throw exception
        private ScriptObjCode TryToCompile()
        {
            _CompilerErrors.Clear();

            // If object file exists, create ScriptObjCode directly from that.
            // Otherwise, compile the source to create object file then create
            // ScriptObjCode from that.
            string assetID = _Item.AssetID.ToString();
            _CameFrom = "asset://" + assetID;
            ScriptObjCode objCode = Compile();
            if(_CompilerErrors.Count != 0)
                throw new Exception("compilation errors");

            if(objCode == null)
                throw new Exception("compilation failed");

            return objCode;
        }

        /*
         * Retrieve source from asset server.
         */
        private string FetchSource(string cameFrom)
        {
            _log.Debug("[YEngine]: fetching source " + cameFrom);
            if(!cameFrom.StartsWith("asset://"))
                throw new Exception("unable to retrieve source from " + cameFrom);

            string assetID = cameFrom.Substring(8);
            AssetBase asset = _Engine.World.AssetService.Get(assetID);
            if(asset == null)
                throw new Exception("source not found " + cameFrom);

            string source = Encoding.UTF8.GetString(asset.Data);
            if(EmptySource(source))
                throw new Exception("fetched source empty " + cameFrom);

            return source;
        }

        /*
         * Fill in script object initial contents.
         * Set the initial state to "default".
         */
        private void LoadObjCode()
        {
            // Script must leave this much stack remaining on calls to CheckRun().
            this.stackLimit = _StackSize / 2;

            // This is how many total heap bytes script is allowed to use.
            this.heapLimit = _HeapSize;

            // Allocate global variable arrays.
            this.glblVars.AllocVarArrays(_ObjCode.glblSizes);

            // Script can handle these event codes.
            _HaveEventHandlers = new bool[_ObjCode.scriptEventHandlerTable.GetLength(1)];
            for(int i = _ObjCode.scriptEventHandlerTable.GetLength(0); --i >= 0;)
            {
                for(int j = _ObjCode.scriptEventHandlerTable.GetLength(1); --j >= 0;)
                {
                    if(_ObjCode.scriptEventHandlerTable[i, j] != null)
                    {
                        _HaveEventHandlers[j] = true;
                    }
                }
            }
        }

        /*
         *  LoadInitialState()
         *      if no state XML file exists for the asset,
         *          post initial default state events
         *      else
         *          try to restore from .state file
         *  If any error, throw exception
         */
        private void LoadInitialState()
        {
            // If no .state file exists, start from default state
            // Otherwise, read initial state from the .state file

            if(!File.Exists(_StateFileName))
            {
                _Running = true;                  // event processing is enabled
                eventCode = ScriptEventCode.None;  // not processing any event

                // default state_entry() must initialize global variables
                doGblInit = true;
                stateCode = 0;

                PostEvent(new EventParams("state_entry",
                                          zeroObjectArray,
                                          zeroDetectParams));
            }
            else
            {
                try
                {
                    string xml;
                    using (FileStream fs = File.Open(_StateFileName,
                                          FileMode.Open,
                                          FileAccess.Read))
                    {
                        using(StreamReader ss = new StreamReader(fs))
                            xml = ss.ReadToEnd();
                    }

                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(xml);
                    LoadScriptState(doc);
                }
                catch
                {
                    File.Delete(_StateFileName);

                    _Running = true;                  // event processing is enabled
                    eventCode = ScriptEventCode.None;  // not processing any event

                    // default state_entry() must initialize global variables
                    glblVars.AllocVarArrays(_ObjCode.glblSizes); // reset globals
                    doGblInit = true;
                    stateCode = 0;

                    PostEvent(new EventParams("state_entry",
                                              zeroObjectArray,
                                              zeroDetectParams));
                }
            }

             // Post event(s) saying what caused the script to start.
            if(_PostOnRez)
            {
                PostEvent(new EventParams("on_rez",
                          new object[] { _StartParam },
                          zeroDetectParams));
            }

            switch(_StateSource)
            {
                case StateSource.AttachedRez:
                    PostEvent(new EventParams("attach",
                              new object[] { _Part.ParentGroup.AttachedAvatar.ToString() }, 
                              zeroDetectParams));
                    break;

                case StateSource.PrimCrossing:
                    PostEvent(new EventParams("changed",
                              sbcCR,
                              zeroDetectParams));
                    break;

                case StateSource.Teleporting:
                    PostEvent(new EventParams("changed",
                              sbcCR,
                              zeroDetectParams));
                    PostEvent(new EventParams("changed",
                              sbcCT,
                              zeroDetectParams));
                    break;

                case StateSource.RegionStart:
                    PostEvent(new EventParams("changed",
                              sbcCRS,
                              zeroDetectParams));
                    break;
            }
        }

        private static readonly object[] sbcCRS = new object[] { ScriptBaseClass.CHANGED_REGION_START };
        private static readonly object[] sbcCR = new object[] { ScriptBaseClass.CHANGED_REGION };
        private static readonly object[] sbcCT = new object[] { ScriptBaseClass.CHANGED_TELEPORT };

        /**
         * @brief Save compilation error messages for later retrieval
         *        via GetScriptErrors().
         */
        private void ErrorHandler(Token token, string message)
        {
            if(token != null)
            {
                string srcloc = token.SrcLoc;
                if(srcloc.StartsWith(_CameFrom))
                    srcloc = srcloc.Substring(_CameFrom.Length);

                _CompilerErrors.Add(srcloc + " Error: " + message);
            }
            else if(message != null)
                _CompilerErrors.Add("(0,0) Error: " + message);
            else
                _CompilerErrors.Add("(0,0) Error compiling, see exception in log");
        }

        /**
         * @brief Load script state from the given XML doc into the script memory
         *  <ScriptState Engine="YEngine" Asset=...>
         *      <Running>...</Running>
         *      <DoGblInit>...</DoGblInit>
         *      <Permissions granted=... mask=... />
         *      RestoreDetectParams()
         *      <Plugins>
         *          ExtractXMLObjectArray("plugin")
         *      </Plugins>
         *      <Snapshot>
         *          MigrateInEventHandler()
         *      </Snapshot>
         *  </ScriptState>
         */
        private void LoadScriptState(XmlDocument doc)
        {

            // Everything we know is enclosed in <ScriptState>...</ScriptState>
            XmlElement scriptStateN = (XmlElement)doc.SelectSingleNode("ScriptState");
            if(scriptStateN == null)
                throw new Exception("no <ScriptState> tag");

            XmlElement XvariablesN = null;
            string sen = scriptStateN.GetAttribute("Engine");
            if(sen == null || sen != _Engine.ScriptEngineName)
            {
                XvariablesN = (XmlElement)scriptStateN.SelectSingleNode("Variables");
                if(XvariablesN == null)
                    throw new Exception("<ScriptState> missing Engine=\"YEngine\" attribute");
                processXstate(doc);
                return;
            }

            // AssetID is unique for the script source text so make sure the
            // state file was written for that source file
            string assetID = scriptStateN.GetAttribute("Asset");
            if(assetID != _Item.AssetID.ToString())
                throw new Exception("<ScriptState> assetID mismatch");

            // Also match the sourceHash in case script was
            // loaded via 'xmroption fetchsource' and has changed
            string sourceHash = scriptStateN.GetAttribute("SourceHash");
            if(sourceHash == null || sourceHash != _ObjCode.sourceHash)
                throw new Exception("<ScriptState> SourceHash mismatch");

            // Get various attributes
            XmlElement runningN = (XmlElement)scriptStateN.SelectSingleNode("Running");
            _Running = bool.Parse(runningN.InnerText);

            XmlElement doGblInitN = (XmlElement)scriptStateN.SelectSingleNode("DoGblInit");
            doGblInit = bool.Parse(doGblInitN.InnerText);

            if (_XMRLSLApi != null)
            {
                XmlElement scpttimeN = (XmlElement)scriptStateN.SelectSingleNode("scrpTime");
                if (scpttimeN != null && double.TryParse(scpttimeN.InnerText, out double t))
                {
                    _XMRLSLApi.SetLSLTimer(Util.GetTimeStampMS() - t);
                }
            }

            double minEventDelay = 0.0;
            XmlElement minEventDelayN = (XmlElement)scriptStateN.SelectSingleNode("mEvtDly");
            if (minEventDelayN != null)
                minEventDelay = double.Parse(minEventDelayN.InnerText);

            // get values used by stuff like llDetectedGrab, etc.
            DetectParams[] detParams = RestoreDetectParams(scriptStateN.SelectSingleNode("DetectArray"));

            // Restore queued events
            LinkedList<EventParams> eventQueue = RestoreEventQueue(scriptStateN.SelectSingleNode("EventQueue"));

            // Restore timers and listeners
            XmlElement pluginN = (XmlElement)scriptStateN.SelectSingleNode("Plugins");
            object[] pluginData = ExtractXMLObjectArray(pluginN, "plugin");

            // Script's global variables and stack contents
            XmlElement snapshotN = (XmlElement)scriptStateN.SelectSingleNode("Snapshot");

            byte[] data = Convert.FromBase64String(snapshotN.InnerText);
            using(MemoryStream ms = new MemoryStream())
            {
                ms.Write(data, 0, data.Length);
                ms.Seek(0, SeekOrigin.Begin);
                MigrateInEventHandler(ms);
            }

            XmlElement permissionsN = (XmlElement)scriptStateN.SelectSingleNode("Permissions");
            _Item.PermsGranter = new UUID(permissionsN.GetAttribute("granter"));
            _Item.PermsMask = Convert.ToInt32(permissionsN.GetAttribute("mask"));
            _Part.Inventory.UpdateInventoryItem(_Item, false, false);

            // Restore event queues, preserving any events that queued
            // whilst we were restoring the state
            lock (_QueueLock)
            {
                _DetectParams = detParams;
                foreach(EventParams evt in _EventQueue)
                    eventQueue.AddLast(evt);

                _EventQueue = eventQueue;
                for(int i = _EventCounts.Length; --i >= 0;)
                    _EventCounts[i] = 0;
                foreach(EventParams evt in _EventQueue)
                {
                    if(_eventCodeMap.TryGetValue(evt.EventName, out ScriptEventCode eventCode))
                        _EventCounts[(int)eventCode]++;
                }
            }

            // Requeue timer and listeners (possibly queuing new events)
            AsyncCommandManager.CreateFromData(_Engine,
                    _LocalID, _ItemID, _Part.UUID,
                    pluginData);

            MinEventDelay = minEventDelay;
        }

        private void processXstate(XmlDocument doc)
        {

            XmlNodeList rootL = doc.GetElementsByTagName("ScriptState");
            if (rootL.Count != 1)
                throw new Exception("Xstate <ScriptState> missing");

            XmlNode rootNode = rootL[0];
            if (rootNode == null)
                throw new Exception("Xstate root missing");

            string stateName = "";
            bool running = false;

            UUID permsGranter = UUID.Zero;
            int permsMask = 0;
            double minEventDelay = 0.0;
            object[] pluginData = new object[0];

            LinkedList<EventParams> eventQueue = new LinkedList<EventParams>();

            Dictionary<string, int> intNames = new Dictionary<string, int>();
            Dictionary<string, int> doubleNames = new Dictionary<string, int>();
            Dictionary<string, int> stringNames = new Dictionary<string, int>();
            Dictionary<string, int> vectorNames = new Dictionary<string, int>();
            Dictionary<string, int> rotationNames = new Dictionary<string, int>();
            Dictionary<string, int> listNames = new Dictionary<string, int>();

            int nn = _ObjCode.globalVarNames.Count;
            int[] ints = null;
            double[] doubles = null;
            string[] strings = null;
            LSL_Vector[] vectors = null;
            LSL_Rotation[] rotations = null;
            LSL_List[] lists = null;

            if (nn > 0)
            {
                if (_ObjCode.globalVarNames.ContainsKey("iarIntegers"))
                {
                    getvarNames(_ObjCode.globalVarNames["iarIntegers"], intNames);
                    ints = new int[_ObjCode.globalVarNames["iarIntegers"].Count];
                }
                if (_ObjCode.globalVarNames.ContainsKey("iarFloats"))
                {
                    getvarNames(_ObjCode.globalVarNames["iarFloats"], doubleNames);
                    doubles = new double[_ObjCode.globalVarNames["iarFloats"].Count];
                }
                if (_ObjCode.globalVarNames.ContainsKey("iarVectors"))
                {
                    getvarNames(_ObjCode.globalVarNames["iarVectors"], vectorNames);
                    vectors = new LSL_Vector[_ObjCode.globalVarNames["iarVectors"].Count];
                }
                if (_ObjCode.globalVarNames.ContainsKey("iarRotations"))
                {
                    getvarNames(_ObjCode.globalVarNames["iarRotations"], rotationNames);
                    rotations = new LSL_Rotation[_ObjCode.globalVarNames["iarRotations"].Count];
                }
                if (_ObjCode.globalVarNames.ContainsKey("iarStrings"))
                {
                    getvarNames(_ObjCode.globalVarNames["iarStrings"], stringNames);
                    strings = new string[_ObjCode.globalVarNames["iarStrings"].Count];
                }
                if (_ObjCode.globalVarNames.ContainsKey("iarLists"))
                {
                    getvarNames(_ObjCode.globalVarNames["iarLists"], listNames);
                    lists = new LSL_List[_ObjCode.globalVarNames["iarLists"].Count];
                }
            }

            int heapsz = 0;

            try
            {
                XmlNodeList partL = rootNode.ChildNodes;
                foreach (XmlNode part in partL)
                {
                    switch (part.Name)
                    {
                        case "State":
                            stateName = part.InnerText;
                            break;
                        case "Running":
                            running = bool.Parse(part.InnerText);
                            break;
                        case "Variables":
                            int indx;
                            XmlNodeList varL = part.ChildNodes;
                            foreach (XmlNode var in varL)
                            {
                                string varName;
                                object o = ReadXTypedValue(var, out varName);
                                Type otype = o.GetType();
                                if (otype == typeof(LSL_Integer))
                                {
                                    if (intNames.TryGetValue(varName, out indx))
                                        ints[indx] = (LSL_Integer)o;
                                    continue;
                                }
                                if (otype == typeof(LSL_Float))
                                {
                                    if (doubleNames.TryGetValue(varName, out indx))
                                        doubles[indx] = (LSL_Float)o;
                                    continue;
                                }
                                if (otype == typeof(LSL_String))
                                {
                                    if (stringNames.TryGetValue(varName, out indx))
                                    {
                                        strings[indx] = (LSL_String)o;
                                        heapsz += ((LSL_String)o).Length;
                                    }
                                    continue;
                                }
                                if (otype == typeof(LSL_Rotation))
                                {
                                    if (rotationNames.TryGetValue(varName, out indx))
                                        rotations[indx] = (LSL_Rotation)o;
                                    continue;
                                }
                                if (otype == typeof(LSL_Vector))
                                {
                                    if (vectorNames.TryGetValue(varName, out indx))
                                        vectors[indx] = (LSL_Vector)o;
                                    continue;
                                }
                                if (otype == typeof(LSL_Key))
                                {
                                    if (stringNames.TryGetValue(varName, out indx))
                                    {
                                        strings[indx] = (LSL_Key)o;
                                        heapsz += ((LSL_String)o).Length;
                                    }
                                    continue;
                                }
                                if (otype == typeof(UUID))
                                {
                                    if (stringNames.TryGetValue(varName, out indx))
                                    {
                                        LSL_String id = ((UUID)o).ToString();
                                        strings[indx] = id;
                                        heapsz += id.Length;
                                    }
                                    continue;
                                }
                                if (otype == typeof(LSL_List))
                                {
                                    if (listNames.TryGetValue(varName, out indx))
                                    {
                                        LSL_List lo = (LSL_List)o;
                                        lists[indx] = lo;
                                        heapsz += lo.Size;
                                    }
                                    continue;
                                }
                            }
                            break;
                        case "Queue":
                            XmlNodeList itemL = part.ChildNodes;
                            foreach (XmlNode item in itemL)
                            {
                                List<object> parms = new List<object>();
                                List<DetectParams> detected = new List<DetectParams>();

                                string eventName = item.Attributes.GetNamedItem("event").Value;
                                XmlNodeList eventL = item.ChildNodes;
                                foreach (XmlNode evt in eventL)
                                {
                                    switch (evt.Name)
                                    {
                                        case "Params":
                                            XmlNodeList prms = evt.ChildNodes;
                                            foreach (XmlNode pm in prms)
                                                parms.Add(ReadXTypedValue(pm));

                                            break;
                                        case "Detected":
                                            XmlNodeList detL = evt.ChildNodes;
                                            foreach (XmlNode det in detL)
                                            {
                                                string vect = det.Attributes.GetNamedItem("pos").Value;
                                                LSL_Vector v = new LSL_Vector(vect);

                                                int d_linkNum = 0;
                                                UUID d_group = UUID.Zero;
                                                string d_name = string.Empty;
                                                UUID d_owner = UUID.Zero;
                                                LSL_Vector d_position = new LSL_Vector();
                                                LSL_Rotation d_rotation = new LSL_Rotation();
                                                int d_type = 0;
                                                LSL_Vector d_velocity = new LSL_Vector();

                                                try
                                                {
                                                    string tmp;

                                                    tmp = det.Attributes.GetNamedItem("linkNum").Value;
                                                    int.TryParse(tmp, out d_linkNum);

                                                    tmp = det.Attributes.GetNamedItem("group").Value;
                                                    UUID.TryParse(tmp, out d_group);

                                                    d_name = det.Attributes.GetNamedItem("name").Value;

                                                    tmp = det.Attributes.GetNamedItem("owner").Value;
                                                    UUID.TryParse(tmp, out d_owner);

                                                    tmp = det.Attributes.GetNamedItem("position").Value;
                                                    d_position = new LSL_Types.Vector3(tmp);

                                                    tmp = det.Attributes.GetNamedItem("rotation").Value;
                                                    d_rotation = new LSL_Rotation(tmp);

                                                    tmp = det.Attributes.GetNamedItem("type").Value;
                                                    int.TryParse(tmp, out d_type);

                                                    tmp = det.Attributes.GetNamedItem("velocity").Value;
                                                    d_velocity = new LSL_Vector(tmp);
                                                }
                                                catch (Exception) // Old version XML
                                                {
                                                }

                                                UUID uuid = new UUID();
                                                UUID.TryParse(det.InnerText, out uuid);

                                                DetectParams d = new DetectParams
                                                {
                                                    Key = uuid,
                                                    OffsetPos = v,
                                                    LinkNum = d_linkNum,
                                                    Group = d_group,
                                                    Name = d_name,
                                                    Owner = d_owner,
                                                    Position = d_position,
                                                    Rotation = d_rotation,
                                                    Type = d_type,
                                                    Velocity = d_velocity
                                                };

                                                detected.Add(d);
                                            }
                                            break;
                                    }
                                }
                                EventParams ep = new EventParams(
                                        eventName, parms.ToArray(),
                                        detected.ToArray());
                                eventQueue.AddLast(ep);
                            }
                            break;
                        case "Plugins":
                            List<object> olist = new List<object>();
                            XmlNodeList itemLP = part.ChildNodes;
                            foreach (XmlNode item in itemLP)
                                olist.Add(ReadXTypedValue(item));
                            pluginData = olist.ToArray();
                            break;
                        case "Permissions":
                            string tmpPerm;
                            int mask = 0;
                            tmpPerm = part.Attributes.GetNamedItem("mask").Value;
                            if (tmpPerm != null)
                            {
                                int.TryParse(tmpPerm, out mask);
                                if (mask != 0)
                                {
                                    tmpPerm = part.Attributes.GetNamedItem("granter").Value;
                                    if (tmpPerm != null)
                                    {
                                        UUID granter = new UUID();
                                        UUID.TryParse(tmpPerm, out granter);
                                        if (granter != UUID.Zero)
                                        {
                                            permsMask = mask;
                                            permsGranter = granter;
                                        }
                                    }
                                }
                            }
                            break;
                        case "MinEventDelay":
                            double.TryParse(part.InnerText, out minEventDelay);
                            break;
                    }
                }
            }
            catch
            {
                throw new Exception("Xstate fail decode");
            }

            int k = 0;
            stateCode = 0;
            foreach (string sn in _ObjCode.stateNames)
            {
                if (stateName == sn)
                {
                    stateCode = k;
                    break;
                }
                k++;
            }
            eventCode = ScriptEventCode.None;
            _Running = running;
            doGblInit = false;

            _Item.PermsGranter = permsGranter;
            _Item.PermsMask = permsMask;
            _Part.Inventory.UpdateInventoryItem(_Item, false, false);

            lock (_RunLock)
            {
                glblVars.iarIntegers = ints;
                glblVars.iarFloats = doubles;
                glblVars.iarVectors = vectors;
                glblVars.iarRotations = rotations;
                glblVars.iarStrings = strings;
                glblVars.iarLists = lists;

                AddArraysHeapUse(heapsz);
                CheckRunLockInvariants(true);
            }

            lock (_QueueLock)
            {
                _DetectParams = null;
                foreach (EventParams evt in _EventQueue)
                    eventQueue.AddLast(evt);

                _EventQueue = eventQueue;
                for (int i = _EventCounts.Length; --i >= 0;)
                    _EventCounts[i] = 0;
                foreach (EventParams evt in _EventQueue)
                {
                    if(_eventCodeMap.TryGetValue(evt.EventName, out ScriptEventCode evtCode))
                        _EventCounts[(int)evtCode]++;
                }
            }

            AsyncCommandManager.CreateFromData(_Engine,
                     _LocalID, _ItemID, _Part.UUID, pluginData);

            MinEventDelay = minEventDelay;
        }

        private static void getvarNames(Dictionary<int, string> s, Dictionary<string, int> d)
        {
            foreach(KeyValuePair<int, string> kvp in s)
                d[kvp.Value] = kvp.Key;
        }

        private static LSL_Types.list ReadXList(XmlNode parent)
        {
            List<object> olist = new List<object>();

            XmlNodeList itemL = parent.ChildNodes;
            foreach (XmlNode item in itemL)
                olist.Add(ReadXTypedValue(item));

            return new LSL_Types.list(olist.ToArray());
        }

        private static object ReadXTypedValue(XmlNode tag, out string name)
        {
            name = tag.Attributes.GetNamedItem("name").Value;

            return ReadXTypedValue(tag);
        }

        private static object ReadXTypedValue(XmlNode tag)
        {
            object varValue;
            string assembly;

            string itemType = tag.Attributes.GetNamedItem("type").Value;

            if (itemType == "list")
                return ReadXList(tag);

            if (itemType == "OpenMetaverse.UUID")
            {
                UUID val = new UUID();
                UUID.TryParse(tag.InnerText, out val);

                return val;
            }

            Type itemT = Type.GetType(itemType);
            if (itemT == null)
            {
                object[] args =
                    new object[] { tag.InnerText };

                assembly = itemType + ", OpenSim.Region.ScriptEngine.Shared";
                itemT = Type.GetType(assembly);
                if (itemT == null)
                    return null;

                varValue = Activator.CreateInstance(itemT, args);

                if (varValue == null)
                    return null;
            }
            else
            {
                varValue = Convert.ChangeType(tag.InnerText, itemT);
            }
            return varValue;
        }

    /**
     * @brief Read llDetectedGrab, etc, values from XML
     *  <EventQueue>
     *      <DetectParams>...</DetectParams>
     *          .
     *          .
     *          .
     *  </EventQueue>
     */
    private LinkedList<EventParams> RestoreEventQueue(XmlNode eventsN)
        {
            LinkedList<EventParams> eventQueue = new LinkedList<EventParams>();
            if(eventsN != null)
            {
                XmlNodeList eventL = eventsN.SelectNodes("Event");
                foreach(XmlNode evnt in eventL)
                {
                    string name = ((XmlElement)evnt).GetAttribute("Name");
                    object[] parms = ExtractXMLObjectArray(evnt, "param");
                    DetectParams[] detects = RestoreDetectParams(evnt);

                    if(parms == null)
                        parms = zeroObjectArray;
                    if(detects == null)
                        detects = zeroDetectParams;

                    EventParams evt = new EventParams(name, parms, detects);
                    eventQueue.AddLast(evt);
                }
            }
            return eventQueue;
        }

        /**
         * @brief Read llDetectedGrab, etc, values from XML
         *  <DetectArray>
         *      <DetectParams>...</DetectParams>
         *          .
         *          .
         *          .
         *  </DetectArray>
         */
        private DetectParams[] RestoreDetectParams(XmlNode detectedN)
        {
            if(detectedN == null)
                return null;

            List<DetectParams> detected = new List<DetectParams>();
            XmlNodeList detectL = detectedN.SelectNodes("DetectParams");

            DetectParams detprm = new DetectParams();
            foreach(XmlNode detxml in detectL)
            {
                try
                {
                    detprm.Group = new UUID(detxml.Attributes.GetNamedItem("group").Value);
                    detprm.Key = new UUID(detxml.Attributes.GetNamedItem("key").Value);
                    detprm.Owner = new UUID(detxml.Attributes.GetNamedItem("owner").Value);

                    detprm.LinkNum = int.Parse(detxml.Attributes.GetNamedItem("linkNum").Value);
                    detprm.Type = int.Parse(detxml.Attributes.GetNamedItem("type").Value);

                    detprm.Name = detxml.Attributes.GetNamedItem("name").Value;

                    detprm.OffsetPos = new LSL_Types.Vector3(detxml.Attributes.GetNamedItem("pos").Value);
                    detprm.Position = new LSL_Types.Vector3(detxml.Attributes.GetNamedItem("position").Value);
                    detprm.Velocity = new LSL_Types.Vector3(detxml.Attributes.GetNamedItem("velocity").Value);

                    detprm.Rotation = new LSL_Types.Quaternion(detxml.Attributes.GetNamedItem("rotation").Value);

                    detected.Add(detprm);
                    detprm = new DetectParams();
                }
                catch(Exception e)
                {
                    _log.Warn("[YEngine]: RestoreDetectParams bad XML: " + detxml.ToString());
                    _log.Warn("[YEngine]: ... " + e.ToString());
                }
            }

            return detected.ToArray();
        }

        /**
         * @brief Extract elements of an array of objects from an XML parent.
         *        Each element is of form <tag ...>...</tag>
         * @param parent = XML parent to extract them from
         * @param tag = what the value's tag is
         * @returns object array of the values
         */
        private static object[] ExtractXMLObjectArray(XmlNode parent, string tag)
        {
            List<object> olist = new List<object>();

            XmlNodeList itemL = parent.SelectNodes(tag);
            foreach(XmlNode item in itemL)
            {
                olist.Add(ExtractXMLObjectValue(item));
            }

            return olist.ToArray();
        }

        private static object ExtractXMLObjectValue(XmlNode item)
        {
            string itemType = item.Attributes.GetNamedItem("type").Value;

            if(itemType == "list")
            {
                return new LSL_List(ExtractXMLObjectArray(item, "item"));
            }

            if(itemType == "OpenMetaverse.UUID")
            {
                UUID val = new UUID();
                UUID.TryParse(item.InnerText, out val);
                return val;
            }

            Type itemT = Type.GetType(itemType);
            if(itemT == null)
            {
                object[] args = new object[] { item.InnerText };

                string assembly = itemType + ", OpenSim.Region.ScriptEngine.Shared";
                itemT = Type.GetType(assembly);
                if(itemT == null)
                {
                    return null;
                }
                return Activator.CreateInstance(itemT, args);
            }

            return Convert.ChangeType(item.InnerText, itemT);
        }

        /*
         * Migrate an event handler in from a stream.
         *
         * Input:
         *  stream = as generated by MigrateOutEventHandler()
         */
        private void MigrateInEventHandler(Stream stream)
        {
            int mv = stream.ReadByte();
            if(mv != migrationVersion)
                throw new Exception("incoming migration version " + mv + " but accept only " + migrationVersion);

            stream.ReadByte();  // ignored

            /*
             * Restore script variables and stack and other state from stream.
             * And it also marks us busy (by setting this.eventCode) so we can't be
             * started again and this event lost.  If it restores this.eventCode =
             * None, the the script was idle.
             */
            lock(_RunLock)
            {
                BinaryReader br = new BinaryReader(stream);
                this.MigrateIn(br);

                _RunOnePhase = "MigrateInEventHandler finished";
                CheckRunLockInvariants(true);
            }
        }
    }
}
