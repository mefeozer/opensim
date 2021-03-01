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
using System.Linq;
using System.Reflection;
using System.Security.Policy;
using System.Text;
using System.Threading;
using System.Xml;
using OpenMetaverse;
using log4net;
using Nini.Config;
using Amib.Threading;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared;
using OpenSim.Region.ScriptEngine.Shared.CodeTools;
using OpenSim.Region.ScriptEngine.Shared.Instance;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.Api.Plugins;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;
using OpenSim.Region.ScriptEngine.XEngine.ScriptBase;
using Timer = OpenSim.Region.ScriptEngine.Shared.Api.Plugins.Timer;

using ScriptCompileQueue = OpenSim.Framework.LocklessQueue<object[]>;

namespace OpenSim.Region.ScriptEngine.XEngine
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "XEngine")]
    public class XEngine : INonSharedRegionModule, IScriptModule, IScriptEngine
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Control the printing of certain debug messages.
        /// </summary>
        /// <remarks>
        /// If DebugLevel >= 1, then we log every time that a script is started.
        /// </remarks>
        public int DebugLevel { get; set; }

        /// <summary>
        /// A parameter to allow us to notify the log if at least one script has a compilation that is not compatible
        /// with ScriptStopStrategy.
        /// </summary>
        public bool HaveNotifiedLogOfScriptStopMismatch { get; private set; }

        private SmartThreadPool _ThreadPool;
        private int _MaxScriptQueue;
        private Scene _Scene;
        private IConfig _ScriptConfig = null;
        private IConfigSource _ConfigSource = null;
        private ICompiler _Compiler;
        private int _MinThreads;
        private int _MaxThreads;

        /// <summary>
        /// Amount of time to delay before starting.
        /// </summary>
        private int _StartDelay;

        /// <summary>
        /// Are we stopping scripts co-operatively by inserting checks in them at C# compile time (true) or aborting
        /// their threads (false)?
        /// </summary>
        private bool _coopTermination;

        private int _IdleTimeout;
        private int _StackSize;
        private int _SleepTime;
        private int _SaveTime;
        private ThreadPriority _Prio;
        private bool _Enabled = false;
        private bool _InitialStartup = true;
        private int _ScriptFailCount; // Number of script fails since compile queue was last empty
        private string _ScriptErrorMessage;
        private bool _AppDomainLoading;
        private bool _AttachmentsDomainLoading;
        private readonly Dictionary<UUID,ArrayList> _ScriptErrors =
                new Dictionary<UUID,ArrayList>();

        // disable warning: need to keep a reference to XEngine.EventManager
        // alive to avoid it being garbage collected
#pragma warning disable 414
        private EventManager _EventManager;
#pragma warning restore 414
        private IXmlRpcRouter _XmlRpcRouter;
        private int _EventLimit;
        private bool _KillTimedOutScripts;

        /// <summary>
        /// Number of milliseconds we will wait for a script event to complete on script stop before we forcibly abort
        /// its thread.
        /// </summary>
        /// <remarks>
        /// It appears that if a script thread is aborted whilst it is holding ReaderWriterLockSlim (possibly the write
        /// lock) then the lock is not properly released.  This causes mono 2.6, 2.10 and possibly
        /// later to crash, sometimes with symptoms such as a leap to 100% script usage and a vm thead dump showing
        /// all threads waiting on release of ReaderWriterLockSlim write thread which none of the threads listed
        /// actually hold.
        ///
        /// Pausing for event completion reduces the risk of this happening.  However, it may be that aborting threads
        /// is not a mono issue per se but rather a risky activity in itself in an AppDomain that is not immediately
        /// shutting down.
        /// </remarks>
        private int _WaitForEventCompletionOnScriptStop = 1000;

        private string _ScriptEnginesPath = null;

        private readonly ExpiringCache<UUID, bool> _runFlags = new ExpiringCache<UUID, bool>();

        /// <summary>
        /// Is the entire simulator in the process of shutting down?
        /// </summary>
        private bool _SimulatorShuttingDown;

        private static readonly List<XEngine> _ScriptEngines =
                new List<XEngine>();

        // Maps the local id to the script inventory items in it

        private readonly Dictionary<uint, List<UUID> > _PrimObjects =
                new Dictionary<uint, List<UUID> >();

        // Maps the UUID above to the script instance

        private readonly Dictionary<UUID, IScriptInstance> _Scripts =
                new Dictionary<UUID, IScriptInstance>();

        // Maps the asset ID to the assembly

        private readonly Dictionary<UUID, string> _Assemblies =
                new Dictionary<UUID, string>();

        private readonly Dictionary<string, int> _AddingAssemblies =
                new Dictionary<string, int>();

        // This will list AppDomains by script asset

        private readonly Dictionary<UUID, AppDomain> _AppDomains =
                new Dictionary<UUID, AppDomain>();

        // List the scripts running in each appdomain

        private readonly Dictionary<UUID, List<UUID> > _DomainScripts =
                new Dictionary<UUID, List<UUID> >();

        private readonly ScriptCompileQueue _CompileQueue = new ScriptCompileQueue();
        IWorkItemResult _CurrentCompile = null;
        private readonly Dictionary<UUID, ScriptCompileInfo> _CompileDict = new Dictionary<UUID, ScriptCompileInfo>();

        private ScriptEngineConsoleCommands _consoleCommands;

        public string ScriptEngineName => "XEngine";

        public string ScriptClassName { get; private set; }

        public string ScriptBaseClassName { get; private set; }

        public ParameterInfo[] ScriptBaseClassParameters { get; private set; }

        public string[] ScriptReferencedAssemblies { get; private set; }

        public Scene World => _Scene;

        public static List<XEngine> ScriptEngines => _ScriptEngines;

        public IScriptModule ScriptModule => this;

        // private struct RezScriptParms
        // {
        //     uint LocalID;
        //     UUID ItemID;
        //     string Script;
        // }

        public IConfig Config => _ScriptConfig;

        public string ScriptEnginePath => _ScriptEnginesPath;

        public IConfigSource ConfigSource => _ConfigSource;

        private class ScriptCompileInfo
        {
            public readonly List<EventParams> eventList = new List<EventParams>();
        }

        /// <summary>
        /// Event fired after the script engine has finished removing a script.
        /// </summary>
        public event ScriptRemoved OnScriptRemoved;

        /// <summary>
        /// Event fired after the script engine has finished removing a script from an object.
        /// </summary>
        public event ObjectRemoved OnObjectRemoved;

        public void Initialise(IConfigSource configSource)
        {
            if (configSource.Configs["XEngine"] == null)
                return;

            _ScriptConfig = configSource.Configs["XEngine"];
            _ConfigSource = configSource;

            string rawScriptStopStrategy = _ScriptConfig.GetString("ScriptStopStrategy", "co-op");

            _log.InfoFormat("[XEngine]: Script stop strategy is {0}", rawScriptStopStrategy);

            if (rawScriptStopStrategy == "co-op")
            {
                _coopTermination = true;
                ScriptClassName = "XEngineScript";
                ScriptBaseClassName = typeof(XEngineScriptBase).FullName;
                ScriptBaseClassParameters = typeof(XEngineScriptBase).GetConstructor(new Type[] { typeof(WaitHandle) }).GetParameters();
                ScriptReferencedAssemblies = new string[] { Path.GetFileName(typeof(XEngineScriptBase).Assembly.Location) };
            }
            else
            {
                ScriptClassName = "Script";
                ScriptBaseClassName = typeof(ScriptBaseClass).FullName;
            }

//            Console.WriteLine("ASSEMBLY NAME: {0}", ScriptReferencedAssemblies[0]);
        }

        public void AddRegion(Scene scene)
        {
            if (_ScriptConfig == null)
                return;

            _ScriptFailCount = 0;
            _ScriptErrorMessage = string.Empty;

            _Enabled = _ScriptConfig.GetBoolean("Enabled", true);

            if (!_Enabled)
                return;

            AppDomain.CurrentDomain.AssemblyResolve +=
                OnAssemblyResolve;

            _Scene = scene;
            _log.InfoFormat("[XEngine]: Initializing scripts in region {0}", _Scene.RegionInfo.RegionName);

            _MinThreads = _ScriptConfig.GetInt("MinThreads", 2);
            _MaxThreads = _ScriptConfig.GetInt("MaxThreads", 100);
            _IdleTimeout = _ScriptConfig.GetInt("IdleTimeout", 60);
            string priority = _ScriptConfig.GetString("Priority", "BelowNormal");
            _StartDelay = _ScriptConfig.GetInt("StartDelay", 15000);
            _MaxScriptQueue = _ScriptConfig.GetInt("MaxScriptEventQueue",300);
            _StackSize = _ScriptConfig.GetInt("ThreadStackSize", 262144);
            _SleepTime = _ScriptConfig.GetInt("MaintenanceInterval", 10) * 1000;
            _AppDomainLoading = _ScriptConfig.GetBoolean("AppDomainLoading", false);
            _AttachmentsDomainLoading = _ScriptConfig.GetBoolean("AttachmentsDomainLoading", false);
            _EventLimit = _ScriptConfig.GetInt("EventLimit", 30);
            _KillTimedOutScripts = _ScriptConfig.GetBoolean("KillTimedOutScripts", false);
            _SaveTime = _ScriptConfig.GetInt("SaveInterval", 120) * 1000;
            _WaitForEventCompletionOnScriptStop
                = _ScriptConfig.GetInt("WaitForEventCompletionOnScriptStop", _WaitForEventCompletionOnScriptStop);

            _ScriptEnginesPath = _ScriptConfig.GetString("ScriptEnginesPath", "ScriptEngines");

            _Prio = ThreadPriority.BelowNormal;
            switch (priority)
            {
                case "Lowest":
                    _Prio = ThreadPriority.Lowest;
                    break;
                case "BelowNormal":
                    _Prio = ThreadPriority.BelowNormal;
                    break;
                case "Normal":
                    _Prio = ThreadPriority.Normal;
                    break;
                case "AboveNormal":
                    _Prio = ThreadPriority.AboveNormal;
                    break;
                case "Highest":
                    _Prio = ThreadPriority.Highest;
                    break;
                default:
                    _log.ErrorFormat("[XEngine] Invalid thread priority: '{0}'. Assuming BelowNormal", priority);
                    break;
            }

            lock (_ScriptEngines)
            {
                _ScriptEngines.Add(this);
            }

            // Needs to be here so we can queue the scripts that need starting
            //
            _Scene.EventManager.OnRezScript += OnRezScript;

            // Complete basic setup of the thread pool
            //
            SetupEngine(_MinThreads, _MaxThreads, _IdleTimeout, _Prio,
                        _MaxScriptQueue, _StackSize);

            _Scene.StackModuleInterface<IScriptModule>(this);

            _XmlRpcRouter = _Scene.RequestModuleInterface<IXmlRpcRouter>();
            if (_XmlRpcRouter != null)
            {
                OnScriptRemoved += _XmlRpcRouter.ScriptRemoved;
                OnObjectRemoved += _XmlRpcRouter.ObjectRemoved;
            }

            _consoleCommands = new ScriptEngineConsoleCommands(this);
            _consoleCommands.RegisterCommands();

            MainConsole.Instance.Commands.AddCommand(
                "Scripts", false, "xengine status", "xengine status", "Show status information",
                "Show status information on the script engine.",
                HandleShowStatus);

            MainConsole.Instance.Commands.AddCommand(
                "Scripts", false, "scripts show", "scripts show [<script-item-uuid>+]", "Show script information",
                "Show information on all scripts known to the script engine.\n"
                    + "If one or more <script-item-uuid>s are given then only information on that script will be shown.",
                HandleShowScripts);

            MainConsole.Instance.Commands.AddCommand(
                "Scripts", false, "show scripts", "show scripts [<script-item-uuid>+]", "Show script information",
                "Synonym for scripts show command", HandleShowScripts);

            MainConsole.Instance.Commands.AddCommand(
                "Scripts", false, "scripts suspend", "scripts suspend [<script-item-uuid>+]", "Suspends all running scripts",
                "Suspends all currently running scripts.  This only suspends event delivery, it will not suspend a"
                    + " script that is currently processing an event.\n"
                    + "Suspended scripts will continue to accumulate events but won't process them.\n"
                    + "If one or more <script-item-uuid>s are given then only that script will be suspended.  Otherwise, all suitable scripts are suspended.",
                 (module, cmdparams) => HandleScriptsAction(cmdparams, HandleSuspendScript));

            MainConsole.Instance.Commands.AddCommand(
                "Scripts", false, "scripts resume", "scripts resume [<script-item-uuid>+]", "Resumes all suspended scripts",
                "Resumes all currently suspended scripts.\n"
                    + "Resumed scripts will process all events accumulated whilst suspended.\n"
                    + "If one or more <script-item-uuid>s are given then only that script will be resumed.  Otherwise, all suitable scripts are resumed.",
                (module, cmdparams) => HandleScriptsAction(cmdparams, HandleResumeScript));

            MainConsole.Instance.Commands.AddCommand(
                "Scripts", false, "scripts stop", "scripts stop [<script-item-uuid>+]", "Stops all running scripts",
                "Stops all running scripts.\n"
                    + "If one or more <script-item-uuid>s are given then only that script will be stopped.  Otherwise, all suitable scripts are stopped.",
                (module, cmdparams) => HandleScriptsAction(cmdparams, HandleStopScript));

            MainConsole.Instance.Commands.AddCommand(
                "Scripts", false, "scripts start", "scripts start [<script-item-uuid>+]", "Starts all stopped scripts",
                "Starts all stopped scripts.\n"
                    + "If one or more <script-item-uuid>s are given then only that script will be started.  Otherwise, all suitable scripts are started.",
                (module, cmdparams) => HandleScriptsAction(cmdparams, HandleStartScript));

            MainConsole.Instance.Commands.AddCommand(
                "Debug", false, "debug scripts log", "debug scripts log <item-id> <log-level>", "Extra debug logging for a particular script.",
                "Activates or deactivates extra debug logging for the given script.\n"
                    + "Level == 0, deactivate extra debug logging.\n"
                    + "Level >= 1, log state changes.\n"
                    + "Level >= 2, log event invocations.\n",
                HandleDebugScriptLogCommand);

            MainConsole.Instance.Commands.AddCommand(
                "Debug", false, "debug xengine log", "debug xengine log [<level>]",
                "Turn on detailed xengine debugging.",
                  "If level <= 0, then no extra logging is done.\n"
                + "If level >= 1, then we log every time that a script is started.",
                HandleDebugLevelCommand);
        }

        private void HandleDebugScriptLogCommand(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == _Scene))
                return;

            if (args.Length != 5)
            {
                MainConsole.Instance.Output("Usage: debug script log <item-id> <log-level>");
                return;
            }

            UUID itemId;

            if (!ConsoleUtil.TryParseConsoleUuid(MainConsole.Instance, args[3], out itemId))
                return;

            int newLevel;

            if (!ConsoleUtil.TryParseConsoleInt(MainConsole.Instance, args[4], out newLevel))
                return;

            IScriptInstance si;

            lock (_Scripts)
            {
                // XXX: We can't give the user feedback on a bad item id because this may apply to a different script
                // engine
                if (!_Scripts.TryGetValue(itemId, out si))
                    return;
            }

            si.DebugLevel = newLevel;
            MainConsole.Instance.Output("Set debug level of {0} {1} to {2}", si.ScriptName, si.ItemID, newLevel);
        }

        /// <summary>
        /// Change debug level
        /// </summary>
        /// <param name="module"></param>
        /// <param name="args"></param>
        private void HandleDebugLevelCommand(string module, string[] args)
        {
            if (args.Length >= 4)
            {
                int newDebug;
                if (ConsoleUtil.TryParseConsoleNaturalInt(MainConsole.Instance, args[3], out newDebug))
                {
                    DebugLevel = newDebug;
                    MainConsole.Instance.Output("Debug level set to {0} in XEngine for region {1}", newDebug, _Scene.Name);
                }
            }
            else if (args.Length == 3)
            {
                MainConsole.Instance.Output("Current debug level is {0}", DebugLevel);
            }
            else
            {
                MainConsole.Instance.Output("Usage: debug xengine log <level>");
            }
        }

        /// <summary>
        /// Parse the raw item id into a script instance from the command params if it's present.
        /// </summary>
        /// <param name="cmdparams"></param>
        /// <param name="instance"></param>
        /// <param name="comparer">Basis on which to sort output.  Can be null if no sort needs to take place</param>
        private void HandleScriptsAction(string[] cmdparams, Action<IScriptInstance> action)
        {
            HandleScriptsAction<object>(cmdparams, action, null);
        }

        /// <summary>
        /// Parse the raw item id into a script instance from the command params if it's present.
        /// </summary>
        /// <param name="cmdparams"></param>
        /// <param name="instance"></param>
        /// <param name="keySelector">Basis on which to sort output.  Can be null if no sort needs to take place</param>
        private void HandleScriptsAction<TKey>(
            string[] cmdparams, Action<IScriptInstance> action, System.Func<IScriptInstance, TKey> keySelector)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == _Scene))
                return;

            lock (_Scripts)
            {
                string rawItemId;
                UUID itemId = UUID.Zero;

                if (cmdparams.Length == 2)
                {
                    IEnumerable<IScriptInstance> scripts = _Scripts.Values;

                    if (keySelector != null)
                        scripts = scripts.OrderBy<IScriptInstance, TKey>(keySelector);

                    foreach (IScriptInstance instance in scripts)
                        action(instance);

                    return;
                }

                for (int i = 2; i < cmdparams.Length; i++)
                {
                    rawItemId = cmdparams[i];

                    if (!UUID.TryParse(rawItemId, out itemId))
                    {
                        MainConsole.Instance.Output("ERROR: {0} is not a valid UUID", rawItemId);
                        continue;
                    }

                    if (itemId != UUID.Zero)
                    {
                        IScriptInstance instance = GetInstance(itemId);
                        if (instance == null)
                        {
                            // Commented out for now since this will cause false reports on simulators with more than
                            // one scene where the current command line set region is 'root' (which causes commands to
                            // go to both regions... (sigh)
    //                        MainConsole.Instance.OutputFormat("Error - No item found with id {0}", itemId);
                            continue;
                        }
                        else
                        {
                            action(instance);
                        }
                    }
                }
            }
        }

        private void HandleShowStatus(string module, string[] cmdparams)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == _Scene))
                return;

            MainConsole.Instance.Output(GetStatusReport());
        }

        public string GetStatusReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Status of XEngine instance for {0}\n", _Scene.RegionInfo.RegionName);

            long scriptsLoaded, eventsQueued = 0, eventsProcessed = 0;

            lock (_Scripts)
            {
                scriptsLoaded = _Scripts.Count;

                foreach (IScriptInstance si in _Scripts.Values)
                {
                    eventsQueued += si.EventsQueued;
                    eventsProcessed += si.EventsProcessed;
                }
            }

            sb.AppendFormat("Scripts loaded             : {0}\n", scriptsLoaded);
            sb.AppendFormat("Scripts waiting for load   : {0}\n", _CompileQueue.Count);
            sb.AppendFormat("Max threads                : {0}\n", _ThreadPool.MaxThreads);
            sb.AppendFormat("Min threads                : {0}\n", _ThreadPool.MinThreads);
            sb.AppendFormat("Allocated threads          : {0}\n", _ThreadPool.ActiveThreads);
            sb.AppendFormat("In use threads             : {0}\n", _ThreadPool.InUseThreads);
            sb.AppendFormat("Work items waiting         : {0}\n", _ThreadPool.WaitingCallbacks);
//            sb.AppendFormat("Assemblies loaded          : {0}\n", _Assemblies.Count);
            sb.AppendFormat("Events queued              : {0}\n", eventsQueued);
            sb.AppendFormat("Events processed           : {0}\n", eventsProcessed);

            SensorRepeat sr = AsyncCommandManager.GetSensorRepeatPlugin(this);
            sb.AppendFormat("Sensors                    : {0}\n", sr != null ? sr.SensorsCount : 0);

            Dataserver ds = AsyncCommandManager.GetDataserverPlugin(this);
            sb.AppendFormat("Dataserver requests        : {0}\n", ds != null ? ds.DataserverRequestsCount : 0);

            Timer t = AsyncCommandManager.GetTimerPlugin(this);
            sb.AppendFormat("Timers                     : {0}\n", t != null ? t.TimersCount : 0);

            Listener l = AsyncCommandManager.GetListenerPlugin(this);
            sb.AppendFormat("Listeners                  : {0}\n", l != null ? l.ListenerCount : 0);

            return sb.ToString();
        }

        public void HandleShowScripts(string module, string[] cmdparams)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == _Scene))
                return;

            if (cmdparams.Length == 2)
            {
                lock (_Scripts)
                {
                    MainConsole.Instance.Output(
                        "Showing {0} scripts in {1}", _Scripts.Count, _Scene.RegionInfo.RegionName);
                }
            }

            HandleScriptsAction<long>(cmdparams, HandleShowScript, si => si.EventsProcessed);
        }

        private void HandleShowScript(IScriptInstance instance)
        {
            SceneObjectPart sop = _Scene.GetSceneObjectPart(instance.ObjectID);
            string status;

            if (instance.ShuttingDown)
            {
                status = "shutting down";
            }
            else if (instance.Suspended)
            {
                status = "suspended";
            }
            else if (!instance.Running)
            {
                status = "stopped";
            }
            else
            {
                status = "running";
            }

            StringBuilder sb = new StringBuilder();

            sb.AppendFormat("Script name         : {0}\n", instance.ScriptName);
            sb.AppendFormat("Status              : {0}\n", status);
            sb.AppendFormat("Queued events       : {0}\n", instance.EventsQueued);
            sb.AppendFormat("Processed events    : {0}\n", instance.EventsProcessed);
            sb.AppendFormat("Item UUID           : {0}\n", instance.ItemID);
            sb.AppendFormat("Asset UUID          : {0}\n", instance.AssetID);
            sb.AppendFormat("Containing part name: {0}\n", instance.PrimName);
            sb.AppendFormat("Containing part UUID: {0}\n", instance.ObjectID);
            sb.AppendFormat("Position            : {0}\n", sop.AbsolutePosition);

            MainConsole.Instance.Output(sb.ToString());
        }

        private void HandleSuspendScript(IScriptInstance instance)
        {
            if (!instance.Suspended)
            {
                instance.Suspend();

                SceneObjectPart sop = _Scene.GetSceneObjectPart(instance.ObjectID);
                MainConsole.Instance.Output(
                    "Suspended {0}.{1}, item UUID {2}, prim UUID {3} @ {4}",
                    instance.PrimName, instance.ScriptName, instance.ItemID, instance.ObjectID, sop.AbsolutePosition);
            }
        }

        private void HandleResumeScript(IScriptInstance instance)
        {
            if (instance.Suspended)
            {
                instance.Resume();

                SceneObjectPart sop = _Scene.GetSceneObjectPart(instance.ObjectID);
                MainConsole.Instance.Output(
                    "Resumed {0}.{1}, item UUID {2}, prim UUID {3} @ {4}",
                    instance.PrimName, instance.ScriptName, instance.ItemID, instance.ObjectID, sop.AbsolutePosition);
            }
        }

        private void HandleStartScript(IScriptInstance instance)
        {
            if (!instance.Running)
            {
                instance.Start();

                SceneObjectPart sop = _Scene.GetSceneObjectPart(instance.ObjectID);
                MainConsole.Instance.Output(
                    "Started {0}.{1}, item UUID {2}, prim UUID {3} @ {4}",
                    instance.PrimName, instance.ScriptName, instance.ItemID, instance.ObjectID, sop.AbsolutePosition);
            }
        }

        private void HandleStopScript(IScriptInstance instance)
        {
            if (instance.Running)
            {
                instance.StayStopped = true;    // the script was stopped explicitly

                instance.Stop(0);

                SceneObjectPart sop = _Scene.GetSceneObjectPart(instance.ObjectID);
                MainConsole.Instance.Output(
                    "Stopped {0}.{1}, item UUID {2}, prim UUID {3} @ {4}",
                    instance.PrimName, instance.ScriptName, instance.ItemID, instance.ObjectID, sop.AbsolutePosition);
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_Enabled)
                return;

            lock (_Scripts)
            {
                _log.InfoFormat(
                    "[XEngine]: Shutting down {0} scripts in {1}", _Scripts.Count, _Scene.RegionInfo.RegionName);

                foreach (IScriptInstance instance in _Scripts.Values)
                {
                    // Force a final state save
                    //
                    try
                    {
                        if (instance.StatePersistedHere)
                            instance.SaveState();
                    }
                    catch (Exception e)
                    {
                        _log.Error(
                            string.Format(
                                "[XEngine]: Failed final state save for script {0}.{1}, item UUID {2}, prim UUID {3} in {4}.  Exception ",
                                instance.PrimName, instance.ScriptName, instance.ItemID, instance.ObjectID, World.Name)
                            , e);
                    }

                    // Clear the event queue and abort the instance thread
                    //
                    instance.Stop(0, true);

                    // Release events, timer, etc
                    //
                    instance.DestroyScriptInstance();

                    // Unload scripts and app domains.
                    // Must be done explicitly because they have infinite
                    // lifetime.
                    // However, don't bother to do this if the simulator is shutting
                    // down since it takes a long time with many scripts.
                    if (!_SimulatorShuttingDown)
                    {
                        _DomainScripts[instance.AppDomain].Remove(instance.ItemID);
                        if (_DomainScripts[instance.AppDomain].Count == 0)
                        {
                            _DomainScripts.Remove(instance.AppDomain);
                            UnloadAppDomain(instance.AppDomain);
                        }
                    }
                }

                _Scripts.Clear();
                _PrimObjects.Clear();
                _Assemblies.Clear();
                _DomainScripts.Clear();
            }
            lock (_ScriptEngines)
            {
                _ScriptEngines.Remove(this);
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            _EventManager = new EventManager(this);

            _Compiler = new Compiler(this);

            _Scene.EventManager.OnRemoveScript += OnRemoveScript;
            _Scene.EventManager.OnScriptReset += OnScriptReset;
            _Scene.EventManager.OnStartScript += OnStartScript;
            _Scene.EventManager.OnStopScript += OnStopScript;
            _Scene.EventManager.OnGetScriptRunning += OnGetScriptRunning;
            _Scene.EventManager.OnShutdown += OnShutdown;

            // If region ready has been triggered, then the region had no scripts to compile and completed its other
            // work.
            _Scene.EventManager.OnRegionReadyStatusChange += s => { if (s.Ready) _InitialStartup = false; };

            if (_SleepTime > 0)
            {
                _ThreadPool.QueueWorkItem(new WorkItemCallback(this.DoMaintenance),
                                           new object[]{ _SleepTime });
            }

            if (_SaveTime > 0)
            {
                _ThreadPool.QueueWorkItem(new WorkItemCallback(this.DoBackup),
                                           new object[] { _SaveTime });
            }
        }

        public void StartProcessing()
        {
            _ThreadPool.Start();
        }

        public void Close()
        {
            if (!_Enabled)
                return;

            lock (_ScriptEngines)
            {
                if (_ScriptEngines.Contains(this))
                    _ScriptEngines.Remove(this);
            }

            lock(_Scripts)
                _ThreadPool.Shutdown();
        }

        public object DoBackup(object o)
        {
            object[] p = (object[])o;
            int saveTime = (int)p[0];

            if (saveTime > 0)
                System.Threading.Thread.Sleep(saveTime);

//            _log.Debug("[XEngine] Backing up script states");

            List<IScriptInstance> instances = new List<IScriptInstance>();

            lock (_Scripts)
            {
                foreach (IScriptInstance instance in _Scripts.Values)
                {
                    if (instance.StatePersistedHere)
                    {
//                        _log.DebugFormat(
//                            "[XEngine]: Adding script {0}.{1}, item UUID {2}, prim UUID {3} in {4} for state persistence",
//                            instance.PrimName, instance.ScriptName, instance.ItemID, instance.ObjectID, World.Name);

                        instances.Add(instance);
                    }
                }
            }

            foreach (IScriptInstance i in instances)
            {
                try
                {
                    i.SaveState();
                }
                catch (Exception e)
                {
                    _log.Error(
                        string.Format(
                            "[XEngine]: Failed to save state of script {0}.{1}, item UUID {2}, prim UUID {3} in {4}.  Exception ",
                            i.PrimName, i.ScriptName, i.ItemID, i.ObjectID, World.Name)
                        , e);
                }
            }

            if (saveTime > 0)
                _ThreadPool.QueueWorkItem(new WorkItemCallback(this.DoBackup),
                                           new object[] { saveTime });

            return 0;
        }

        public void SaveAllState()
        {
            DoBackup(new object[] { 0 });
        }

        public object DoMaintenance(object p)
        {
            object[] parms = (object[])p;
            int sleepTime = (int)parms[0];

            foreach (IScriptInstance inst in _Scripts.Values)
            {
                if (inst.EventTime() > _EventLimit)
                {
                    inst.Stop(100);
                    if (!_KillTimedOutScripts)
                        inst.Start();
                }
            }

            System.Threading.Thread.Sleep(sleepTime);

            _ThreadPool.QueueWorkItem(new WorkItemCallback(this.DoMaintenance),
                                       new object[]{ sleepTime });

            return 0;
        }

        public Type ReplaceableInterface => null;

        public string Name => "XEngine";

        public void OnRezScript(uint localID, UUID itemID, string script, int startParam, bool postOnRez, string engine, int stateSource)
        {
//            _log.DebugFormat(
//                "[XEngine]: OnRezScript event triggered for script {0}, startParam {1}, postOnRez {2}, engine {3}, stateSource {4}, script\n{5}",
//                 itemID, startParam, postOnRez, engine, stateSource, script);

            if (script.StartsWith("//MRM:"))
                return;

            List<IScriptModule> engines = new List<IScriptModule>(_Scene.RequestModuleInterfaces<IScriptModule>());

            List<string> names = new List<string>();
            foreach (IScriptModule m in engines)
                names.Add(m.ScriptEngineName);

            int lineEnd = script.IndexOf('\n');

            if (lineEnd > 1)
            {
                string firstline = script.Substring(0, lineEnd).Trim();

                int colon = firstline.IndexOf(':');
                if (firstline.Length > 2 && firstline.Substring(0, 2) == "//" && colon != -1)
                {
                    string engineName = firstline.Substring(2, colon - 2);

                    if (names.Contains(engineName))
                    {
                        engine = engineName;
                        script = "//" + script.Substring(colon + 1);
                    }
                    else
                    {
                        if (engine == ScriptEngineName)
                        {
                            // If we are falling back on XEngine as the default engine, then only complain to the user
                            // if a script language has been explicitly set and it's one that we recognize or there are
                            // no non-whitespace characters after the colon.
                            //
                            // If the script is
                            // explicitly not allowed or the script is not in LSL then the user will be informed by a later compiler message.
                            //
                            // If the colon ends the line then we'll risk the false positive as this is more likely
                            // to signal a real scriptengine line where the user wants to use the default compile language.
                            //
                            // This avoids the overwhelming number of false positives where we're in this code because
                            // there's a colon in a comment in the first line of a script for entirely
                            // unrelated reasons (e.g. vim settings).
                            //
                            // TODO: A better fix would be to deprecate simple : detection and look for some less likely
                            // string to begin the comment (like #! in unix shell scripts).
                            bool warnRunningInXEngine = false;
                            string restOfFirstLine = firstline.Substring(colon + 1);

                            // FIXME: These are hardcoded because they are currently hardcoded in Compiler.cs
                            if (restOfFirstLine.StartsWith("c#")
                                || restOfFirstLine.StartsWith("vb")
                                || restOfFirstLine.StartsWith("lsl")
                                || restOfFirstLine.Length == 0)
                                warnRunningInXEngine = true;

                            if (warnRunningInXEngine)
                            {
                                SceneObjectPart part =
                                        _Scene.GetSceneObjectPart(
                                        localID);

                                TaskInventoryItem item =
                                        part.Inventory.GetInventoryItem(itemID);

                                ScenePresence presence =
                                        _Scene.GetScenePresence(
                                        item.OwnerID);

                                if (presence != null)
                                {
                                   presence.ControllingClient.SendAgentAlertMessage(
                                            "Selected engine unavailable. "+
                                            "Running script on "+
                                            ScriptEngineName,
                                            false);
                                }
                            }
                        }
                    }
                }
            }

            if (engine != ScriptEngineName)
                return;

            object[] parms = new object[]{localID, itemID, script, startParam, postOnRez, (StateSource)stateSource};

            if (stateSource == (int)StateSource.ScriptedRez)
            {
                lock (_CompileDict)
                {
//                    _log.DebugFormat("[XENGINE]: Set compile dict for {0}", itemID);
                    _CompileDict[itemID] = new ScriptCompileInfo();
                }

                DoOnRezScript(parms);
            }
            else
            {
                lock (_CompileDict)
                    _CompileDict[itemID] = new ScriptCompileInfo();
//                _log.DebugFormat("[XENGINE]: Set compile dict for {0} delayed", itemID);

                // This must occur after the _CompileDict so that an existing compile thread cannot hit the check
                // in DoOnRezScript() before _CompileDict has been updated.
                _CompileQueue.Enqueue(parms);

//                _log.DebugFormat("[XEngine]: Added script {0} to compile queue", itemID);

                // NOTE: Although we use a lockless queue, the lock here
                // is required. It ensures that there are never two
                // compile threads running, which, due to a race
                // conndition, might otherwise happen
                //
                lock (_CompileQueue)
                {
                    if (_CurrentCompile == null)
                        _CurrentCompile = _ThreadPool.QueueWorkItem(DoOnRezScriptQueue, null);
                }
            }
        }

        public object DoOnRezScriptQueue(object dummy)
        {
            try
            {
                if (_InitialStartup)
                {
                    // This delay exists to stop mono problems where script compilation and startup would stop the sim
                    // working properly for the session.
                    System.Threading.Thread.Sleep(_StartDelay);

                    _log.InfoFormat("[XEngine]: Performing initial script startup on {0}", _Scene.Name);
                }

                object[] o;

                int scriptsStarted = 0;

                while (_CompileQueue.Dequeue(out o))
                {
                    try
                    {
                        if (DoOnRezScript(o))
                        {
                            scriptsStarted++;

                            if (_InitialStartup)
                                if (scriptsStarted % 50 == 0)
                                    _log.InfoFormat(
                                        "[XEngine]: Started {0} scripts in {1}", scriptsStarted, _Scene.Name);
                        }
                    }
                    catch (System.Threading.ThreadAbortException) { }
                    catch (Exception e)
                    {
                        _log.Error(
                            string.Format(
                                "[XEngine]: Failure in DoOnRezScriptQueue() for item {0} in {1}.  Continuing.  Exception  ",
                                o[1], _Scene.Name),
                            e);
                    }
                }

                if (_InitialStartup)
                    _log.InfoFormat(
                        "[XEngine]: Completed starting {0} scripts on {1}", scriptsStarted, _Scene.Name);

            }
            catch (Exception e)
            {
                _log.Error(
                    string.Format("[XEngine]: Failure in DoOnRezScriptQueue() in {0}.  Exception  ", _Scene.Name), e);
            }
            finally
            {
                // FIXME: On failure we must trigger this even if the compile queue is not actually empty so that the
                // RegionReadyModule is not forever waiting.  This event really needs a different name.
                _Scene.EventManager.TriggerEmptyScriptCompileQueue(_ScriptFailCount,
                                                                    _ScriptErrorMessage);

                _ScriptFailCount = 0;
                _InitialStartup = false;

                // NOTE: Despite having a lockless queue, this lock is required
                // to make sure there is never no compile thread while there
                // are still scripts to compile. This could otherwise happen
                // due to a race condition
                //
                lock (_CompileQueue)
                {
                    _CurrentCompile = null;

                    // This is to avoid a situation where the _CompileQueue while loop above could complete but
                    // OnRezScript() place a new script on the queue and check _CurrentCompile = null before we hit
                    // this section.
                    if (_CompileQueue.Count > 0)
                        _CurrentCompile = _ThreadPool.QueueWorkItem(DoOnRezScriptQueue, null);
                }
            }

            return null;
        }

        private bool DoOnRezScript(object[] parms)
        {
            object[] p = parms;
            uint localID = (uint)p[0];
            UUID itemID = (UUID)p[1];
            string script =(string)p[2];
            int startParam = (int)p[3];
            bool postOnRez = (bool)p[4];
            StateSource stateSource = (StateSource)p[5];

//            _log.DebugFormat("[XEngine]: DoOnRezScript called for script {0}", itemID);

            lock (_CompileDict)
            {
                if (!_CompileDict.ContainsKey(itemID))
                    return false;
            }

            // Get the asset ID of the script, so we can check if we
            // already have it.

            // We must look for the part outside the _Scripts lock because GetSceneObjectPart later triggers the
            // _parts lock on SOG.  At the same time, a scene object that is being deleted will take the _parts lock
            // and then later on try to take the _scripts lock in this class when it calls OnRemoveScript()
            SceneObjectPart part = _Scene.GetSceneObjectPart(localID);
            if (part == null)
            {
                _log.ErrorFormat("[Script]: SceneObjectPart with localID {0} unavailable. Script NOT started.", localID);
                _ScriptErrorMessage += "SceneObjectPart unavailable. Script NOT started.\n";
                _ScriptFailCount++;
                lock (_CompileDict)
                    _CompileDict.Remove(itemID);
                return false;
            }

            TaskInventoryItem item = part.Inventory.GetInventoryItem(itemID);
            if (item == null)
            {
                _ScriptErrorMessage += "Can't find script inventory item.\n";
                _ScriptFailCount++;
                lock (_CompileDict)
                    _CompileDict.Remove(itemID);
                return false;
            }

            if (DebugLevel > 0)
                _log.DebugFormat(
                    "[XEngine]: Loading script {0}.{1}, item UUID {2}, prim UUID {3} @ {4}.{5}",
                    part.ParentGroup.RootPart.Name, item.Name, itemID, part.UUID,
                    part.ParentGroup.RootPart.AbsolutePosition, part.ParentGroup.Scene.RegionInfo.RegionName);

            UUID assetID = item.AssetID;

            ScenePresence presence = _Scene.GetScenePresence(item.OwnerID);

            string assemblyPath = "";

            Culture.SetCurrentCulture();

            Dictionary<KeyValuePair<int, int>, KeyValuePair<int, int>> linemap;

            lock (_ScriptErrors)
            {
                try
                {
                    lock (_AddingAssemblies)
                    {
                        _Compiler.PerformScriptCompile(script, assetID.ToString(), item.OwnerID, out assemblyPath, out linemap);

//                        _log.DebugFormat(
//                            "[XENGINE]: Found assembly path {0} onrez {1} in {2}",
//                            assemblyPath, item.ItemID, World.Name);

                        if (!_AddingAssemblies.ContainsKey(assemblyPath)) {
                            _AddingAssemblies[assemblyPath] = 1;
                        } else {
                            _AddingAssemblies[assemblyPath]++;
                        }
                    }

                    string[] warnings = _Compiler.GetWarnings();

                    if (warnings != null && warnings.Length != 0)
                    {
                        foreach (string warning in warnings)
                        {
                            if (!_ScriptErrors.ContainsKey(itemID))
                                _ScriptErrors[itemID] = new ArrayList();

                            _ScriptErrors[itemID].Add(warning);
    //                        try
    //                        {
    //                            // DISPLAY WARNING INWORLD
    //                            string text = "Warning:\n" + warning;
    //                            if (text.Length > 1000)
    //                                text = text.Substring(0, 1000);
    //                            if (!ShowScriptSaveResponse(item.OwnerID,
    //                                    assetID, text, true))
    //                            {
    //                                if (presence != null && (!postOnRez))
    //                                    presence.ControllingClient.SendAgentAlertMessage("Script saved with warnings, check debug window!", false);
    //
    //                                World.SimChat(Utils.StringToBytes(text),
    //                                              ChatTypeEnum.DebugChannel, 2147483647,
    //                                              part.AbsolutePosition,
    //                                              part.Name, part.UUID, false);
    //                            }
    //                        }
    //                        catch (Exception e2) // LEGIT: User Scripting
    //                        {
    //                            _log.Error("[XEngine]: " +
    //                                    "Error displaying warning in-world: " +
    //                                    e2.ToString());
    //                            _log.Error("[XEngine]: " +
    //                                    "Warning:\r\n" +
    //                                    warning);
    //                        }
                        }
                    }
                }
                catch (Exception e)
                {
//                    _log.ErrorFormat(
//                        "[XEngine]: Exception when rezzing script with item ID {0}, {1}{2}",
//                        itemID, e.Message, e.StackTrace);

    //                try
    //                {
                        if (!_ScriptErrors.ContainsKey(itemID))
                            _ScriptErrors[itemID] = new ArrayList();
                        // DISPLAY ERROR INWORLD
    //                    _ScriptErrorMessage += "Failed to compile script in object: '" + part.ParentGroup.RootPart.Name + "' Script name: '" + item.Name + "' Error message: " + e.Message.ToString();
    //
                        _ScriptFailCount++;
                        _ScriptErrors[itemID].Add(e.Message.ToString());
    //                    string text = "Error compiling script '" + item.Name + "':\n" + e.Message.ToString();
    //                    if (text.Length > 1000)
    //                        text = text.Substring(0, 1000);
    //                    if (!ShowScriptSaveResponse(item.OwnerID,
    //                            assetID, text, false))
    //                    {
    //                        if (presence != null && (!postOnRez))
    //                            presence.ControllingClient.SendAgentAlertMessage("Script saved with errors, check debug window!", false);
    //                        World.SimChat(Utils.StringToBytes(text),
    //                                      ChatTypeEnum.DebugChannel, 2147483647,
    //                                      part.AbsolutePosition,
    //                                      part.Name, part.UUID, false);
    //                    }
    //                }
    //                catch (Exception e2) // LEGIT: User Scripting
    //                {
    //                    _log.Error("[XEngine]: "+
    //                            "Error displaying error in-world: " +
    //                            e2.ToString());
    //                    _log.Error("[XEngine]: " +
    //                            "Errormessage: Error compiling script:\r\n" +
    //                            e.Message.ToString());
    //                }

                    lock (_CompileDict)
                        _CompileDict.Remove(itemID);
                    return false;
                }
            }

            ScriptInstance instance = null;
            lock (_Scripts)
            {
                // Create the object record
                if (!_Scripts.ContainsKey(itemID) || _Scripts[itemID].AssetID != assetID)
                {

                    bool attachDomains = _AttachmentsDomainLoading && part.ParentGroup.IsAttachmentCheckFull();
                    UUID appDomain = part.ParentGroup.RootPart.UUID;

                    if (!_AppDomains.ContainsKey(appDomain))
                    {
                        try
                        {
                            AppDomain sandbox;
                            if (_AppDomainLoading || attachDomains)
                            {
                                AppDomainSetup appSetup = new AppDomainSetup
                                {
                                    PrivateBinPath = Path.Combine(
                                    _ScriptEnginesPath,
                                    _Scene.RegionInfo.RegionID.ToString())
                                };

                                Evidence baseEvidence = AppDomain.CurrentDomain.Evidence;
                                Evidence evidence = new Evidence(baseEvidence);

                                sandbox = AppDomain.CreateDomain(
                                                _Scene.RegionInfo.RegionID.ToString(),
                                                evidence, appSetup);
                                sandbox.AssemblyResolve +=
                                    new ResolveEventHandler(
                                        AssemblyResolver.OnAssemblyResolve);
                            }
                            else
                            {
                                sandbox = AppDomain.CurrentDomain;
                            }

                            //PolicyLevel sandboxPolicy = PolicyLevel.CreateAppDomainLevel();
                            //AllMembershipCondition sandboxMembershipCondition = new AllMembershipCondition();
                            //PermissionSet sandboxPermissionSet = sandboxPolicy.GetNamedPermissionSet("Internet");
                            //PolicyStatement sandboxPolicyStatement = new PolicyStatement(sandboxPermissionSet);
                            //CodeGroup sandboxCodeGroup = new UnionCodeGroup(sandboxMembershipCondition, sandboxPolicyStatement);
                            //sandboxPolicy.RootCodeGroup = sandboxCodeGroup;
                            //sandbox.SetAppDomainPolicy(sandboxPolicy);

                            _AppDomains[appDomain] = sandbox;
                            _DomainScripts[appDomain] = new List<UUID>();
                        }
                        catch (Exception e)
                        {
                            _log.ErrorFormat("[XEngine] Exception creating app domain:\n {0}", e.ToString());
                            _ScriptErrorMessage += "Exception creating app domain:\n";
                            _ScriptFailCount++;
                            lock (_AddingAssemblies)
                            {
                                _AddingAssemblies[assemblyPath]--;
                            }
                            lock (_CompileDict)
                                _CompileDict.Remove(itemID);
                            return false;
                        }
                    }

                    _DomainScripts[appDomain].Add(itemID);

                    IScript scriptObj = null;
                    EventWaitHandle coopSleepHandle;
                    bool coopTerminationForThisScript;

                    // Set up assembly name to point to the appropriate scriptEngines directory
                    AssemblyName assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(assemblyPath))
                    {
                        CodeBase = Path.GetDirectoryName(assemblyPath)
                    };

                    if (_coopTermination)
                    {
                        try
                        {
                            coopSleepHandle = new XEngineEventWaitHandle(false, EventResetMode.AutoReset);

                            scriptObj
                                = (IScript)_AppDomains[appDomain].CreateInstanceAndUnwrap(
                                    assemblyName.FullName,
                                    "SecondLife.XEngineScript",
                                    false,
                                    BindingFlags.Default,
                                    null,
                                    new object[] { coopSleepHandle },
                                    null,
                                    null);

                            coopTerminationForThisScript = true;
                        }
                        catch (TypeLoadException)
                        {
                            coopSleepHandle = null;

                            try
                            {
                                scriptObj
                                    = (IScript)_AppDomains[appDomain].CreateInstanceAndUnwrap(
                                        assemblyName.FullName,
                                        "SecondLife.Script",
                                        false,
                                        BindingFlags.Default,
                                        null,
                                        null,
                                        null,
                                        null);
                            }
                            catch (Exception e2)
                            {
                                _log.Error(
                                    string.Format(
                                        "[XENGINE]: Could not load previous SecondLife.Script from assembly {0} in {1}.  Not starting.  Exception  ",
                                        assemblyName.FullName, World.Name),
                                    e2);

                                lock (_CompileDict)
                                    _CompileDict.Remove(itemID);
                                return false;
                            }

                            coopTerminationForThisScript = false;
                        }
                    }
                    else
                    {
                        try
                        {
                            scriptObj
                                = (IScript)_AppDomains[appDomain].CreateInstanceAndUnwrap(
                                    assemblyName.FullName,
                                    "SecondLife.Script",
                                    false,
                                    BindingFlags.Default,
                                    null,
                                    null,
                                    null,
                                    null);

                            coopSleepHandle = null;
                            coopTerminationForThisScript = false;
                        }
                        catch (TypeLoadException)
                        {
                            coopSleepHandle = new XEngineEventWaitHandle(false, EventResetMode.AutoReset);

                            try
                            {
                                scriptObj
                                    = (IScript)_AppDomains[appDomain].CreateInstanceAndUnwrap(
                                        assemblyName.FullName,
                                        "SecondLife.XEngineScript",
                                        false,
                                        BindingFlags.Default,
                                        null,
                                        new object[] { coopSleepHandle },
                                        null,
                                        null);
                            }
                            catch (Exception e2)
                            {
                                _log.Error(
                                    string.Format(
                                        "[XENGINE]: Could not load previous SecondLife.XEngineScript from assembly {0} in {1}.  Not starting.  Exception  ",
                                        assemblyName.FullName, World.Name),
                                    e2);

                                lock (_CompileDict)
                                    _CompileDict.Remove(itemID);
                                return false;
                            }

                            coopTerminationForThisScript = true;
                        }
                    }

                    if (_coopTermination != coopTerminationForThisScript && !HaveNotifiedLogOfScriptStopMismatch)
                    {
                        // Notify the log that there is at least one script compile that doesn't match the
                        // ScriptStopStrategy.  Operator has to manually delete old DLLs - we can't do this on Windows
                        // once the assembly has been loaded evne if the instantiation of a class was unsuccessful.
                        _log.WarnFormat(
                            "[XEngine]: At least one existing compiled script DLL in {0} has {1} as ScriptStopStrategy whereas config setting is {2}."
                            + "\nContinuing with script compiled strategy but to remove this message please set [XEngine] DeleteScriptsOnStartup = true for one simulator session to remove old script DLLs (script state will not be lost).",
                            World.Name, coopTerminationForThisScript ? "co-op" : "abort", _coopTermination ? "co-op" : "abort");

                        HaveNotifiedLogOfScriptStopMismatch = true;
                    }

                    instance = new ScriptInstance(this, part,
                                                  item,
                                                  startParam, postOnRez,
                                                  _MaxScriptQueue);

                    if(!instance.Load(scriptObj, coopSleepHandle, assemblyPath,
                            Path.Combine(ScriptEnginePath, World.RegionInfo.RegionID.ToString()), stateSource, coopTerminationForThisScript))
                    {
                        lock (_CompileDict)
                            _CompileDict.Remove(itemID);
                        return false;
                    }

//                    if (DebugLevel >= 1)
//                    _log.DebugFormat(
//                        "[XEngine] Loaded script {0}.{1}, item UUID {2}, prim UUID {3} @ {4}.{5}",
//                        part.ParentGroup.RootPart.Name, item.Name, itemID, part.UUID,
//                        part.ParentGroup.RootPart.AbsolutePosition, part.ParentGroup.Scene.RegionInfo.RegionName);

                    if (presence != null)
                    {
                        ShowScriptSaveResponse(item.OwnerID,
                                assetID, "Compile successful", true);
                    }

                    instance.AppDomain = appDomain;
                    instance.LineMap = linemap;

                    _Scripts[itemID] = instance;
                }
            }

            lock (_PrimObjects)
            {
                if (!_PrimObjects.ContainsKey(localID))
                    _PrimObjects[localID] = new List<UUID>();

                if (!_PrimObjects[localID].Contains(itemID))
                    _PrimObjects[localID].Add(itemID);
            }


            lock (_AddingAssemblies)
            {
                if (!_Assemblies.ContainsKey(assetID))
                    _Assemblies[assetID] = assemblyPath;

                _AddingAssemblies[assemblyPath]--;
            }

            if (instance != null)
            {
                instance.Init();
                lock (_CompileDict)
                {
                    foreach (EventParams pp in _CompileDict[itemID].eventList)
                        instance.PostEvent(pp);
                }
            }
            lock (_CompileDict)
                _CompileDict.Remove(itemID);

            bool runIt;
            if (_runFlags.TryGetValue(itemID, out runIt))
            {
                if (!runIt)
                    StopScript(itemID);
                _runFlags.Remove(itemID);
            }

            return true;
        }

        public void OnRemoveScript(uint localID, UUID itemID)
        {
            // If it's not yet been compiled, make sure we don't try
            lock (_CompileDict)
            {
                if (_CompileDict.ContainsKey(itemID))
                    _CompileDict.Remove(itemID);
            }

            IScriptInstance instance = null;
            lock (_Scripts)
            {
                // Do we even have it?
                if (!_Scripts.TryGetValue(itemID, out instance))
                    return;
                _Scripts.Remove(itemID);
            }

            instance.Stop(_WaitForEventCompletionOnScriptStop, true);

            lock (_PrimObjects)
            {
                // Remove the script from it's prim
                if (_PrimObjects.ContainsKey(localID))
                {
                    // Remove inventory item record
                    if (_PrimObjects[localID].Contains(itemID))
                        _PrimObjects[localID].Remove(itemID);

                    // If there are no more scripts, remove prim
                    if (_PrimObjects[localID].Count == 0)
                        _PrimObjects.Remove(localID);
                }
            }

            if (instance.StatePersistedHere)
                instance.RemoveState();

            instance.DestroyScriptInstance();

            _DomainScripts[instance.AppDomain].Remove(instance.ItemID);
            if (_DomainScripts[instance.AppDomain].Count == 0)
            {
                _DomainScripts.Remove(instance.AppDomain);
                UnloadAppDomain(instance.AppDomain);
            }

            OnObjectRemoved?.Invoke(instance.ObjectID);
            OnScriptRemoved?.Invoke(itemID);
        }

        public void OnScriptReset(uint localID, UUID itemID)
        {
            ResetScript(itemID);
        }

        public void OnStartScript(uint localID, UUID itemID)
        {
            StartScript(itemID);
        }

        public void OnStopScript(uint localID, UUID itemID)
        {
            StopScript(itemID);
        }

        private void CleanAssemblies()
        {
            List<UUID> assetIDList = new List<UUID>(_Assemblies.Keys);

            foreach (IScriptInstance i in _Scripts.Values)
            {
                if (assetIDList.Contains(i.AssetID))
                    assetIDList.Remove(i.AssetID);
            }

            lock (_AddingAssemblies)
            {
                foreach (UUID assetID in assetIDList)
                {
                    // Do not remove assembly files if another instance of the script
                    // is currently initialising
                    if (!_AddingAssemblies.ContainsKey(_Assemblies[assetID])
                        || _AddingAssemblies[_Assemblies[assetID]] == 0)
                    {
//                        _log.DebugFormat("[XEngine] Removing unreferenced assembly {0}", _Assemblies[assetID]);
                        try
                        {
                            if (File.Exists(_Assemblies[assetID]))
                                File.Delete(_Assemblies[assetID]);

                            if (File.Exists(_Assemblies[assetID]+".text"))
                                File.Delete(_Assemblies[assetID]+".text");

                            if (File.Exists(_Assemblies[assetID]+".mdb"))
                                File.Delete(_Assemblies[assetID]+".mdb");

                            if (File.Exists(_Assemblies[assetID]+".map"))
                                File.Delete(_Assemblies[assetID]+".map");
                        }
                        catch (Exception)
                        {
                        }
                        _Assemblies.Remove(assetID);
                    }
                }
            }
        }

        private void UnloadAppDomain(UUID id)
        {
            if (_AppDomains.ContainsKey(id))
            {
                AppDomain domain = _AppDomains[id];
                _AppDomains.Remove(id);

                if (domain != AppDomain.CurrentDomain)
                    AppDomain.Unload(domain);
                domain = null;
                // _log.DebugFormat("[XEngine] Unloaded app domain {0}", id.ToString());
            }
        }

        //
        // Start processing
        //
        private void SetupEngine(int minThreads, int maxThreads,
                                 int idleTimeout, ThreadPriority threadPriority,
                                 int maxScriptQueue, int stackSize)
        {
            _MaxScriptQueue = maxScriptQueue;

            STPStartInfo startInfo = new STPStartInfo
            {
                ThreadPoolName = "XEngine",
                IdleTimeout = idleTimeout * 1000, // convert to seconds as stated in .ini
                MaxWorkerThreads = maxThreads,
                MinWorkerThreads = minThreads,
                ThreadPriority = threadPriority
            };
            ;
            startInfo.MaxStackSize = stackSize;
            startInfo.StartSuspended = true;

            _ThreadPool = new SmartThreadPool(startInfo);
        }

        //
        // Used by script instances to queue event handler jobs
        //
        public IScriptWorkItem QueueEventHandler(object parms)
        {
            return new XWorkItem(_ThreadPool.QueueWorkItem(
                                     new WorkItemCallback(this.ProcessEventHandler),
                                     parms));
        }

        /// <summary>
        /// Process a previously posted script event.
        /// </summary>
        /// <param name="parms"></param>
        /// <returns></returns>
        private object ProcessEventHandler(object parms)
        {
            Culture.SetCurrentCulture();

            IScriptInstance instance = (ScriptInstance) parms;

//            _log.DebugFormat("[XEngine]: Processing event for {0}", instance);

            return instance.EventProcessor();
        }

        /// <summary>
        /// Post event to an entire prim
        /// </summary>
        /// <param name="localID"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        public bool PostObjectEvent(uint localID, EventParams p)
        {
            bool result = false;
            List<UUID> uuids = null;

            lock (_PrimObjects)
            {
                if (!_PrimObjects.TryGetValue(localID, out uuids))
                    return false;

                foreach (UUID itemID in uuids)
                {
                    IScriptInstance instance = null;
                    try
                    {
                        _Scripts.TryGetValue(itemID, out instance);
                    }
                    catch { /* ignore race conditions */ }

                    if (instance != null)
                    {
                        instance.PostEvent(p);
                        result = true;
                    }
                    else
                    {
                        lock (_CompileDict)
                        {
                            if (_CompileDict.ContainsKey(itemID))
                            {
                                _CompileDict[itemID].eventList.Add(p);
                                result = true;
                            }
                        }
                    }
                }
            }

            return result;
        }

        public void CancelScriptEvent(UUID itemID, string eventName)
        {
        }

        /// <summary>
        /// Post an event to a single script
        /// </summary>
        /// <param name="itemID"></param>
        /// <param name="p"></param>
        /// <returns></returns>
        public bool PostScriptEvent(UUID itemID, EventParams p)
        {
            if (_Scripts.TryGetValue(itemID, out IScriptInstance instance))
            {
                instance?.PostEvent(p);
                return true;
            }
            lock (_CompileDict)
            {
                if (_CompileDict.ContainsKey(itemID))
                {
                    _CompileDict[itemID].eventList.Add(p);
                    return true;
                }
            }
            return false;
        }

        public bool PostScriptEvent(UUID itemID, string name, object[] p)
        {
            object[] lsl_p = new object[p.Length];
            for (int i = 0; i < p.Length ; i++)
            {
                if (p[i] is int)
                    lsl_p[i] = new LSL_Types.LSLInteger((int)p[i]);
                else if (p[i] is string)
                    lsl_p[i] = new LSL_Types.LSLString((string)p[i]);
                else if (p[i] is Vector3)
                    lsl_p[i] = new LSL_Types.Vector3((Vector3)p[i]);
                else if (p[i] is Quaternion)
                    lsl_p[i] = new LSL_Types.Quaternion((Quaternion)p[i]);
                else if (p[i] is float)
                    lsl_p[i] = new LSL_Types.LSLFloat((float)p[i]);
                else
                    lsl_p[i] = p[i];
            }

            return PostScriptEvent(itemID, new EventParams(name, lsl_p, new DetectParams[0]));
        }

        public bool PostObjectEvent(UUID itemID, string name, object[] p)
        {
            SceneObjectPart part = _Scene.GetSceneObjectPart(itemID);
            if (part == null)
                return false;

            object[] lsl_p = new object[p.Length];
            for (int i = 0; i < p.Length ; i++)
            {
                if (p[i] is int)
                    lsl_p[i] = new LSL_Types.LSLInteger((int)p[i]);
                else if (p[i] is string)
                    lsl_p[i] = new LSL_Types.LSLString((string)p[i]);
                else if (p[i] is Vector3)
                    lsl_p[i] = new LSL_Types.Vector3((Vector3)p[i]);
                else if (p[i] is Quaternion)
                    lsl_p[i] = new LSL_Types.Quaternion((Quaternion)p[i]);
                else if (p[i] is float)
                    lsl_p[i] = new LSL_Types.LSLFloat((float)p[i]);
                else
                    lsl_p[i] = p[i];
            }

            return PostObjectEvent(part.LocalId, new EventParams(name, lsl_p, new DetectParams[0]));
        }

        public Assembly OnAssemblyResolve(object sender,
                                          ResolveEventArgs args)
        {
            if (!(sender is System.AppDomain))
                return null;

            string[] pathList = new string[] {"bin", _ScriptEnginesPath,
                                              Path.Combine(_ScriptEnginesPath,
                                                           _Scene.RegionInfo.RegionID.ToString())};

            string assemblyName = args.Name;
            if (assemblyName.IndexOf(",") != -1)
                assemblyName = args.Name.Substring(0, args.Name.IndexOf(","));

            foreach (string s in pathList)
            {
                string path = Path.Combine(Directory.GetCurrentDirectory(),
                                           Path.Combine(s, assemblyName))+".dll";

//                Console.WriteLine("[XEngine]: Trying to resolve {0}", path);

                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }

            return null;
        }

        private IScriptInstance GetInstance(UUID itemID)
        {
            lock (_Scripts)
            {
                if (_Scripts.TryGetValue(itemID, out IScriptInstance instance))
                    return instance;
            }
            return null;
        }

        public void SetScriptState(UUID itemID, bool running, bool self)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance != null)
            {
                if (running)
                        instance.Start();
                else
                {
                    if(self)
                    {
                        instance.Running = false;
                        throw new EventAbortException();
                    }
                    else
                        instance.Stop(100);
                }
            }
        }

        public bool GetScriptState(UUID itemID)
        {
            IScriptInstance instance = GetInstance(itemID);
            return instance != null && instance.Running;
        }

        public void ApiResetScript(UUID itemID)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance != null)
                instance.ApiResetScript();

            // Send the new number of threads that are in use by the thread
            // pool, I believe that by adding them to the locations where the
            // script is changing states that I will catch all changes to the
            // thread pool
            _Scene.setThreadCount(_ThreadPool.InUseThreads);
        }

        public void ResetScript(UUID itemID)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance != null)
                instance.ResetScript(_WaitForEventCompletionOnScriptStop);

            // Send the new number of threads that are in use by the thread
            // pool, I believe that by adding them to the locations where the
            // script is changing states that I will catch all changes to the
            // thread pool
            _Scene.setThreadCount(_ThreadPool.InUseThreads);
        }

        public void StartScript(UUID itemID)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance != null)
                instance.Start();
            else
                _runFlags.AddOrUpdate(itemID, true, 240);

            // Send the new number of threads that are in use by the thread
            // pool, I believe that by adding them to the locations where the
            // script is changing states that I will catch all changes to the
            // thread pool
            _Scene.setThreadCount(_ThreadPool.InUseThreads);
        }

        public void StopScript(UUID itemID)
        {
            IScriptInstance instance = GetInstance(itemID);

            if (instance != null)
            {
                lock (instance.EventQueue)
                    instance.StayStopped = true;    // the script was stopped explicitly

                instance.Stop(_WaitForEventCompletionOnScriptStop);
            }
            else
            {
//                _log.DebugFormat("[XENGINE]: Could not find script with ID {0} to stop in {1}", itemID, World.Name);
                _runFlags.AddOrUpdate(itemID, false, 240);
            }

            // Send the new number of threads that are in use by the thread
            // pool, I believe that by adding them to the locations where the
            // script is changing states that I will catch all changes to the
            // thread pool
            _Scene.setThreadCount(_ThreadPool.InUseThreads);
        }

        public DetectParams GetDetectParams(UUID itemID, int idx)
        {
            IScriptInstance instance = GetInstance(itemID);
            return instance != null ? instance.GetDetectParams(idx) : null;
        }

        public void SetMinEventDelay(UUID itemID, double delay)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance != null)
                instance.MinEventDelay = delay;
        }

        public UUID GetDetectID(UUID itemID, int idx)
        {
            IScriptInstance instance = GetInstance(itemID);
            return instance != null ? instance.GetDetectID(idx) : UUID.Zero;
        }

        public void SetState(UUID itemID, string newState)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance == null)
                return;
            instance.SetState(newState);
        }

        public int GetStartParameter(UUID itemID)
        {
            IScriptInstance instance = GetInstance(itemID);
            return instance == null ? 0 : instance.StartParam;
        }

        public void OnShutdown()
        {
            _SimulatorShuttingDown = true;

            List<IScriptInstance> instances = new List<IScriptInstance>();

            lock (_Scripts)
            {
                foreach (IScriptInstance instance in _Scripts.Values)
                    instances.Add(instance);
            }

            foreach (IScriptInstance i in instances)
            {
                // Stop the script, even forcibly if needed. Then flag
                // it as shutting down and restore the previous run state
                // for serialization, so the scripts don't come back
                // dead after region restart
                //
                bool prevRunning = i.Running;
                i.Stop(50);
                i.ShuttingDown = true;
                i.Running = prevRunning;
            }

            DoBackup(new object[] {0});
        }

        public IScriptApi GetApi(UUID itemID, string name)
        {
            IScriptInstance instance = GetInstance(itemID);
            return instance == null ? null : instance.GetApi(name);
        }

        public void OnGetScriptRunning(IClientAPI controllingClient, UUID objectID, UUID itemID)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance == null)
                return;
            IEventQueue eq = World.RequestModuleInterface<IEventQueue>();
            if (eq == null)
            {
                controllingClient.SendScriptRunningReply(objectID, itemID,
                        GetScriptState(itemID));
            }
            else
            {
                eq.ScriptRunningEvent(objectID, itemID, GetScriptState(itemID), controllingClient.AgentId);
            }
        }

        public string GetXMLState(UUID itemID)
        {
//            _log.DebugFormat("[XEngine]: Getting XML state for script instance {0}", itemID);

            IScriptInstance instance = GetInstance(itemID);
            if (instance == null)
            {
//                _log.DebugFormat("[XEngine]: Found no script instance for {0}, returning empty string", itemID);
                return "";
            }

            string xml = instance.GetXMLState();

            XmlDocument sdoc = new XmlDocument();

            bool loadedState = true;
            try
            {
                sdoc.LoadXml(xml);
            }
            catch (System.Xml.XmlException)
            {
                loadedState = false;
            }

            XmlNodeList rootL = null;
            XmlNode rootNode = null;
            if (loadedState)
            {
                rootL = sdoc.GetElementsByTagName("ScriptState");
                rootNode = rootL[0];
            }

            // Create <State UUID="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx">
            XmlDocument doc = new XmlDocument();
            XmlElement stateData = doc.CreateElement("", "State", "");
            XmlAttribute stateID = doc.CreateAttribute("", "UUID", "");
            stateID.Value = itemID.ToString();
            stateData.Attributes.Append(stateID);
            XmlAttribute assetID = doc.CreateAttribute("", "Asset", "");
            assetID.Value = instance.AssetID.ToString();
            stateData.Attributes.Append(assetID);
            XmlAttribute engineName = doc.CreateAttribute("", "Engine", "");
            engineName.Value = ScriptEngineName;
            stateData.Attributes.Append(engineName);
            doc.AppendChild(stateData);

            XmlNode xmlstate = null;

            // Add <ScriptState>...</ScriptState>
            if (loadedState)
            {
                xmlstate = doc.ImportNode(rootNode, true);
            }
            else
            {
                xmlstate = doc.CreateElement("", "ScriptState", "");
            }

            stateData.AppendChild(xmlstate);

            string assemName = instance.GetAssemblyName();

            string fn = Path.GetFileName(assemName);

            string assem = string.Empty;
            string assemNameText = assemName + ".text";

            if (File.Exists(assemNameText))
            {
                FileInfo tfi = new FileInfo(assemNameText);

                if (tfi != null)
                {
                    byte[] tdata = new byte[tfi.Length];

                    try
                    {
                        using (FileStream tfs = File.Open(assemNameText,
                                FileMode.Open, FileAccess.Read))
                        {
                            tfs.Read(tdata, 0, tdata.Length);
                        }

                        assem = Encoding.ASCII.GetString(tdata);
                    }
                    catch (Exception e)
                    {
                         _log.ErrorFormat(
                            "[XEngine]: Unable to open script textfile {0}{1}, reason: {2}",
                            assemName, ".text", e.Message);
                    }
                }
            }
            else
            {
                FileInfo fi = new FileInfo(assemName);

                if (fi != null)
                {
                    byte[] data = new byte[fi.Length];

                    try
                    {
                        using (FileStream fs = File.Open(assemName, FileMode.Open, FileAccess.Read))
                        {
                            fs.Read(data, 0, data.Length);
                        }

                        assem = System.Convert.ToBase64String(data);
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat(
                            "[XEngine]: Unable to open script assembly {0}, reason: {1}", assemName, e.Message);
                    }
                }
            }

            string map = string.Empty;

            if (File.Exists(fn + ".map"))
            {
                using (FileStream mfs = File.Open(fn + ".map", FileMode.Open, FileAccess.Read))
                {
                    using (StreamReader msr = new StreamReader(mfs))
                    {
                        map = msr.ReadToEnd();
                    }
                }
            }

            XmlElement assemblyData = doc.CreateElement("", "Assembly", "");
            XmlAttribute assemblyName = doc.CreateAttribute("", "Filename", "");

            assemblyName.Value = fn;
            assemblyData.Attributes.Append(assemblyName);

            assemblyData.InnerText = assem;

            stateData.AppendChild(assemblyData);

            XmlElement mapData = doc.CreateElement("", "LineMap", "");
            XmlAttribute mapName = doc.CreateAttribute("", "Filename", "");

            mapName.Value = fn + ".map";
            mapData.Attributes.Append(mapName);

            mapData.InnerText = map;

            stateData.AppendChild(mapData);

            // _log.DebugFormat("[XEngine]: Got XML state for {0}", itemID);

            return doc.InnerXml;
        }

        private bool ShowScriptSaveResponse(UUID ownerID, UUID assetID, string text, bool compiled)
        {
            return false;
        }

        public bool SetXMLState(UUID itemID, string xml)
        {
//            _log.DebugFormat("[XEngine]: Writing state for script item with ID {0}", itemID);

            if (string.IsNullOrEmpty(xml))
                return false;

            XmlDocument doc = new XmlDocument();

            try
            {
                doc.LoadXml(xml);
            }
            catch (Exception)
            {
                _log.Error("[XEngine]: Exception decoding XML data from region transfer");
                return false;
            }

            XmlNodeList rootL = doc.GetElementsByTagName("State");
            if (rootL.Count < 1)
                return false;

            XmlElement rootE = (XmlElement)rootL[0];

            if (rootE.GetAttribute("Engine") != ScriptEngineName)
                return false;

//          On rez from inventory, that ID will have changed. It was only
//          advisory anyway. So we don't check it anymore.
//
//            if (rootE.GetAttribute("UUID") != itemID.ToString())
//                return;

            XmlNodeList stateL = rootE.GetElementsByTagName("ScriptState");

            if (stateL.Count != 1)
                return false;

            XmlElement stateE = (XmlElement)stateL[0];

            if (World._trustBinaries)
            {
                XmlNodeList assemL = rootE.GetElementsByTagName("Assembly");

                if (assemL.Count != 1)
                    return false;

                XmlElement assemE = (XmlElement)assemL[0];

                string fn = assemE.GetAttribute("Filename");
                string base64 = assemE.InnerText;

                string path = Path.Combine(_ScriptEnginesPath, World.RegionInfo.RegionID.ToString());
                path = Path.Combine(path, fn);

                if (!File.Exists(path))
                {
                    byte[] filedata = Convert.FromBase64String(base64);

                    try
                    {
                        using (FileStream fs = File.Create(path))
                        {
//                            _log.DebugFormat("[XEngine]: Writing assembly file {0}", path);

                            fs.Write(filedata, 0, filedata.Length);
                        }
                    }
                    catch (IOException ex)
                    {
                        // if there already exists a file at that location, it may be locked.
                        _log.ErrorFormat("[XEngine]: Error whilst writing assembly file {0}, {1}", path, ex.Message);
                    }

                    string textpath = path + ".text";
                    try
                    {
                        using (FileStream fs = File.Create(textpath))
                        {
                            using (StreamWriter sw = new StreamWriter(fs))
                            {
//                                _log.DebugFormat("[XEngine]: Writing .text file {0}", textpath);

                                sw.Write(base64);
                            }
                        }
                    }
                    catch (IOException ex)
                    {
                        // if there already exists a file at that location, it may be locked.
                        _log.ErrorFormat("[XEngine]: Error whilst writing .text file {0}, {1}", textpath, ex.Message);
                    }
                }

                XmlNodeList mapL = rootE.GetElementsByTagName("LineMap");
                if (mapL.Count > 0)
                {
                    XmlElement mapE = (XmlElement)mapL[0];

                    string mappath = Path.Combine(_ScriptEnginesPath, World.RegionInfo.RegionID.ToString());
                    mappath = Path.Combine(mappath, mapE.GetAttribute("Filename"));

                    try
                    {
                        using (FileStream mfs = File.Create(mappath))
                        {
                            using (StreamWriter msw = new StreamWriter(mfs))
                            {
    //                            _log.DebugFormat("[XEngine]: Writing linemap file {0}", mappath);

                                msw.Write(mapE.InnerText);
                            }
                        }
                    }
                    catch (IOException ex)
                    {
                        // if there already exists a file at that location, it may be locked.
                        _log.Error(
                            string.Format("[XEngine]: Linemap file {0} could not be written.  Exception  ", mappath), ex);
                    }
                }
            }

            string statepath = Path.Combine(_ScriptEnginesPath, World.RegionInfo.RegionID.ToString());
            statepath = Path.Combine(statepath, itemID.ToString() + ".state");

            try
            {
                using (FileStream sfs = File.Create(statepath))
                {
                    using (StreamWriter ssw = new StreamWriter(sfs))
                    {
//                        _log.DebugFormat("[XEngine]: Writing state file {0}", statepath);

                        ssw.Write(stateE.OuterXml);
                    }
                }
            }
            catch (IOException ex)
            {
                // if there already exists a file at that location, it may be locked.
                _log.ErrorFormat("[XEngine]: Error whilst writing state file {0}, {1}", statepath, ex.Message);
            }

//            _log.DebugFormat(
//                "[XEngine]: Wrote state for script item with ID {0} at {1} in {2}", itemID, statepath, _Scene.Name);

            return true;
        }

        public ArrayList GetScriptErrors(UUID itemID)
        {
            System.Threading.Thread.Sleep(1000);

            lock (_ScriptErrors)
            {
                if (_ScriptErrors.ContainsKey(itemID))
                {
                    ArrayList ret = _ScriptErrors[itemID];
                    _ScriptErrors.Remove(itemID);
                    return ret;
                }
                return new ArrayList();
            }
        }

        public Dictionary<uint, float> GetObjectScriptsExecutionTimes()
        {
            Dictionary<uint, float> topScripts = new Dictionary<uint, float>();

            lock (_Scripts)
            {
                foreach (IScriptInstance si in _Scripts.Values)
                {
                    if (!topScripts.ContainsKey(si.RootLocalID))
                        topScripts[si.RootLocalID] = 0;

                    topScripts[si.RootLocalID] += GetExectionTime(si);
                }
            }

            return topScripts;
        }

        public float GetScriptExecutionTime(List<UUID> itemIDs)
        {
            if (itemIDs == null|| itemIDs.Count == 0)
            {
                return 0.0f;
            }
            float time = 0.0f;
            IScriptInstance si;
            // Calculate the time for all scripts that this engine is executing
            // Ignore any others
            foreach (UUID id in itemIDs)
            {
                si = GetInstance(id);
                if (si != null && si.Running)
                {
                    time += GetExectionTime(si);
                }
            }
            return time;
        }

        public int GetScriptsMemory(List<UUID> itemIDs)
        {
            return 0;
        }

        private float GetExectionTime(IScriptInstance si)
        {
            return (float)si.ExecutionTime.GetSumTime().TotalMilliseconds;
        }

        public bool SuspendScript(UUID itemID)
        {
            //            _log.DebugFormat("[XEngine]: Received request to suspend script with ID {0}", itemID);
            _Scene.setThreadCount(_ThreadPool.InUseThreads);
            IScriptInstance instance = GetInstance(itemID);
            if (instance == null)
                return false;

           instance.Suspend();
           return true;
        }

        public bool ResumeScript(UUID itemID)
        {
            //            _log.DebugFormat("[XEngine]: Received request to resume script with ID {0}", itemID);

            _Scene.setThreadCount(_ThreadPool.InUseThreads);

            IScriptInstance instance = GetInstance(itemID);
            if (instance != null)
            {
                instance.Resume();
                return true;
            }
            return false;
        }

        public bool HasScript(UUID itemID, out bool running)
        {
            running = true;

            IScriptInstance instance = GetInstance(itemID);
            if (instance == null)
                return false;

            running = instance.Running;
            return true;
        }

        public void SleepScript(UUID itemID, int delay)
        {
            IScriptInstance instance = GetInstance(itemID);
            if (instance == null)
                return;

            instance.ExecutionTimer.Stop();
            try
            {
                if (instance.CoopWaitHandle != null)
                {
                    if (instance.CoopWaitHandle.WaitOne(delay))
                        throw new ScriptCoopStopException();
                }
                else
                {
                    Thread.Sleep(delay);
                }
            }
            finally
            {
                instance.ExecutionTimer.Start();
            }
        }

        public ICollection<ScriptTopStatsData> GetTopObjectStats(float mintime, int minmemory, out float totaltime, out float totalmemory)
        {
            Dictionary<uint, ScriptTopStatsData> topScripts = new Dictionary<uint, ScriptTopStatsData>();
            totalmemory = 0;
            totaltime = 0;
            lock (_Scripts)
            {
                foreach (IScriptInstance si in _Scripts.Values)
                {
                    float time = GetExectionTime(si);
                    totaltime += time;
                    if(time > mintime)
                    {
                        ScriptTopStatsData sd;
                        if (topScripts.TryGetValue(si.RootLocalID, out sd))
                            sd.time += time;
                        else
                        {
                            sd = new ScriptTopStatsData
                            {
                                localID = si.RootLocalID,
                                time = time
                            };
                            topScripts[si.RootLocalID] = sd;
                        }
                    }
                }
            }
            return topScripts.Values;
        }

    }
}
