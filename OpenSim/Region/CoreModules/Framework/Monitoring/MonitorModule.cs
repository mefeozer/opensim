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
using System.Net;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.Framework.Monitoring.Alerts;
using OpenSim.Region.CoreModules.Framework.Monitoring.Monitors;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.Framework.Monitoring
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "MonitorModule")]
    public class MonitorModule : INonSharedRegionModule
    {
        /// <summary>
        /// Is this module enabled?
        /// </summary>
        public bool Enabled { get; private set; }

        private Scene _scene;

        /// <summary>
        /// These are monitors where we know the static details in advance.
        /// </summary>
        /// <remarks>
        /// Dynamic monitors also exist (we don't know any of the details of what stats we get back here)
        /// but these are currently hardcoded.
        /// </remarks>
        private readonly List<IMonitor> _staticMonitors = new List<IMonitor>();

        private readonly List<IAlert> _alerts = new List<IAlert>();
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public MonitorModule()
        {
            Enabled = true;
        }

        #region Implementation of INonSharedRegionModule

        public void Initialise(IConfigSource source)
        {
            IConfig cnfg = source.Configs["Monitoring"];

            if (cnfg != null)
                Enabled = cnfg.GetBoolean("Enabled", true);

            if (!Enabled)
                return;

        }

        public void AddRegion(Scene scene)
        {
            if (!Enabled)
                return;

            _scene = scene;

            _scene.AddCommand("General", this, "monitor report",
                               "monitor report",
                               "Returns a variety of statistics about the current region and/or simulator",
                               DebugMonitors);

            MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler("/monitorstats/" + _scene.RegionInfo.RegionID, StatsPage));
            MainServer.Instance.AddSimpleStreamHandler(new SimpleStreamHandler(
                "/monitorstats/" + Uri.EscapeDataString(_scene.RegionInfo.RegionName), StatsPage));

            AddMonitors();
            RegisterStatsManagerRegionStatistics();
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Enabled)
                return;

            MainServer.Instance.RemoveHTTPHandler("GET", "/monitorstats/" + _scene.RegionInfo.RegionID);
            MainServer.Instance.RemoveHTTPHandler("GET", "/monitorstats/" + Uri.EscapeDataString(_scene.RegionInfo.RegionName));

            UnRegisterStatsManagerRegionStatistics();

            _scene = null;
        }

        public void Close()
        {
        }

        public string Name => "Region Health Monitoring Module";

        public void RegionLoaded(Scene scene)
        {
        }

        public Type ReplaceableInterface => null;

        #endregion

        public void AddMonitors()
        {
            _staticMonitors.Add(new AgentCountMonitor(_scene));
            _staticMonitors.Add(new ChildAgentCountMonitor(_scene));
            _staticMonitors.Add(new GCMemoryMonitor());
            _staticMonitors.Add(new ObjectCountMonitor(_scene));
            _staticMonitors.Add(new PhysicsFrameMonitor(_scene));
            _staticMonitors.Add(new PhysicsUpdateFrameMonitor(_scene));
            _staticMonitors.Add(new PWSMemoryMonitor());
            _staticMonitors.Add(new ThreadCountMonitor());
            _staticMonitors.Add(new TotalFrameMonitor(_scene));
            _staticMonitors.Add(new EventFrameMonitor(_scene));
            _staticMonitors.Add(new LandFrameMonitor(_scene));
            _staticMonitors.Add(new LastFrameTimeMonitor(_scene));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "TimeDilationMonitor",
                    "Time Dilation",
                    m => m.Scene.StatsReporter.LastReportedSimStats[0],
                    m => m.GetValue().ToString()));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "SimFPSMonitor",
                    "Sim FPS",
                    m => m.Scene.StatsReporter.LastReportedSimStats[1],
                    m => string.Format("{0}", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "PhysicsFPSMonitor",
                    "Physics FPS",
                    m => m.Scene.StatsReporter.LastReportedSimStats[2],
                    m => string.Format("{0}", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "AgentUpdatesPerSecondMonitor",
                    "Agent Updates",
                    m => m.Scene.StatsReporter.LastReportedSimStats[3],
                    m => string.Format("{0} per second", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "ActiveObjectCountMonitor",
                    "Active Objects",
                    m => m.Scene.StatsReporter.LastReportedSimStats[7],
                    m => string.Format("{0}", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "ActiveScriptsMonitor",
                    "Active Scripts",
                    m => m.Scene.StatsReporter.LastReportedSimStats[19],
                    m => string.Format("{0}", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "ScriptEventsPerSecondMonitor",
                    "Script Events",
                    m => m.Scene.StatsReporter.LastReportedSimStats[23],
                    m => string.Format("{0} per second", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "InPacketsPerSecondMonitor",
                    "In Packets",
                    m => m.Scene.StatsReporter.LastReportedSimStats[13],
                    m => string.Format("{0} per second", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "OutPacketsPerSecondMonitor",
                    "Out Packets",
                    m => m.Scene.StatsReporter.LastReportedSimStats[14],
                    m => string.Format("{0} per second", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "UnackedBytesMonitor",
                    "Unacked Bytes",
                    m => m.Scene.StatsReporter.LastReportedSimStats[15],
                    m => string.Format("{0}", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "PendingDownloadsMonitor",
                    "Pending Downloads",
                    m => m.Scene.StatsReporter.LastReportedSimStats[17],
                    m => string.Format("{0}", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "PendingUploadsMonitor",
                    "Pending Uploads",
                    m => m.Scene.StatsReporter.LastReportedSimStats[18],
                    m => string.Format("{0}", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "TotalFrameTimeMonitor",
                    "Total Frame Time",
                    m => m.Scene.StatsReporter.LastReportedSimStats[8],
                    m => string.Format("{0} ms", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "NetFrameTimeMonitor",
                    "Net Frame Time",
                    m => m.Scene.StatsReporter.LastReportedSimStats[9],
                    m => string.Format("{0} ms", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "PhysicsFrameTimeMonitor",
                    "Physics Frame Time",
                    m => m.Scene.StatsReporter.LastReportedSimStats[10],
                    m => string.Format("{0} ms", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "SimulationFrameTimeMonitor",
                    "Simulation Frame Time",
                    m => m.Scene.StatsReporter.LastReportedSimStats[12],
                    m => string.Format("{0} ms", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "AgentFrameTimeMonitor",
                    "Agent Frame Time",
                    m => m.Scene.StatsReporter.LastReportedSimStats[16],
                    m => string.Format("{0} ms", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "ImagesFrameTimeMonitor",
                    "Images Frame Time",
                    m => m.Scene.StatsReporter.LastReportedSimStats[11],
                    m => string.Format("{0} ms", m.GetValue())));

            _staticMonitors.Add(
                new GenericMonitor(
                    _scene,
                    "SpareFrameTimeMonitor",
                    "Spare Frame Time",
                    m => m.Scene.StatsReporter.LastReportedSimStats[38],
                    m => string.Format("{0} ms", m.GetValue())));

            _alerts.Add(new DeadlockAlert(_staticMonitors.Find(x => x is LastFrameTimeMonitor) as LastFrameTimeMonitor));

            foreach (IAlert alert in _alerts)
            {
                alert.OnTriggerAlert += OnTriggerAlert;
            }
        }

        public void DebugMonitors(string module, string[] args)
        {
            foreach (IMonitor monitor in _staticMonitors)
            {
                MainConsole.Instance.Output(
                    "[MONITOR MODULE]: {0} reports {1} = {2}",
                    _scene.RegionInfo.RegionName, monitor.GetFriendlyName(), monitor.GetFriendlyValue());
            }

            foreach (KeyValuePair<string, float> tuple in _scene.StatsReporter.GetExtraSimStats())
            {
                MainConsole.Instance.Output(
                    "[MONITOR MODULE]: {0} reports {1} = {2}",
                    _scene.RegionInfo.RegionName, tuple.Key, tuple.Value);
            }
        }

        public void TestAlerts()
        {
            foreach (IAlert alert in _alerts)
            {
                alert.Test();
            }
        }

        public void StatsPage(IOSHttpRequest request, IOSHttpResponse response)
        {
            response.KeepAlive = false;
            if(request.HttpMethod != "GET")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            // If request was for a specific monitor
            // eg url/?monitor=Monitor.Name
            if (request.QueryAsDictionary.TryGetValue("monitor", out string monID))
            {
                foreach (IMonitor monitor in _staticMonitors)
                {
                    string elemName = monitor.ToString();
                    if (elemName.StartsWith(monitor.GetType().Namespace))
                        elemName = elemName.Substring(monitor.GetType().Namespace.Length + 1);

                    if (elemName == monID || monitor.ToString() == monID)
                    {
                        response.RawBuffer = Util.UTF8.GetBytes(monitor.GetValue().ToString());
                        response.StatusCode = (int)HttpStatusCode.OK;
                        return;
                    }
                }

                // FIXME: Arguably this should also be done with dynamic monitors but I'm not sure what the above code
                // is even doing.  Why are we inspecting the type of the monitor???

                // No monitor with that name
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            string xml = "<data>";
            foreach (IMonitor monitor in _staticMonitors)
            {
                string elemName = monitor.GetName();
                xml += "<" + elemName + ">" + monitor.GetValue().ToString() + "</" + elemName + ">";
//                _log.DebugFormat("[MONITOR MODULE]: {0} = {1}", elemName, monitor.GetValue());
            }

            foreach (KeyValuePair<string, float> tuple in _scene.StatsReporter.GetExtraSimStats())
            {
                xml += "<" + tuple.Key + ">" + tuple.Value + "</" + tuple.Key + ">";
            }

            xml += "</data>";

            response.RawBuffer = Util.UTF8.GetBytes(xml);
            response.ContentType = "text/xml";
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        void OnTriggerAlert(System.Type reporter, string reason, bool fatal)
        {
            _log.Error("[Monitor] " + reporter.Name + " for " + _scene.RegionInfo.RegionName + " reports " + reason + " (Fatal: " + fatal + ")");
        }

        private readonly List<Stat> registeredStats = new List<Stat>();
        private void MakeStat(string pName, string pUnitName, Action<Stat> act)
        {
            Stat tempStat = new Stat(pName, pName, pName, pUnitName, "scene", _scene.RegionInfo.RegionName, StatType.Pull, act, StatVerbosity.Info);
            StatsManager.RegisterStat(tempStat);
            registeredStats.Add(tempStat);
        }
        private void RegisterStatsManagerRegionStatistics()
        {
            MakeStat("RootAgents", "avatars", (s) => { s.Value = _scene.SceneGraph.GetRootAgentCount(); });
            MakeStat("ChildAgents", "avatars", (s) => { s.Value = _scene.SceneGraph.GetChildAgentCount(); });
            MakeStat("TotalPrims", "objects", (s) => { s.Value = _scene.SceneGraph.GetTotalObjectsCount(); });
            MakeStat("ActivePrims", "objects", (s) => { s.Value = _scene.SceneGraph.GetActiveObjectsCount(); });
            MakeStat("ActiveScripts", "scripts", (s) => { s.Value = _scene.SceneGraph.GetActiveScriptsCount(); });

            MakeStat("TimeDilation", "sec/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[0]; });
            MakeStat("SimFPS", "fps", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[1]; });
            MakeStat("PhysicsFPS", "fps", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[2]; });
            MakeStat("AgentUpdates", "updates/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[3]; });
            MakeStat("FrameTime", "ms/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[8]; });
            MakeStat("NetTime", "ms/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[9]; });
            MakeStat("OtherTime", "ms/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[12]; });
            MakeStat("PhysicsTime", "ms/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[10]; });
            MakeStat("AgentTime", "ms/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[16]; });
            MakeStat("ImageTime", "ms/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[11]; });
            MakeStat("ScriptLines", "lines/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[20]; });
            MakeStat("SimSpareMS", "ms/sec", (s) => { s.Value = _scene.StatsReporter.LastReportedSimStats[21]; });
        }

        private void UnRegisterStatsManagerRegionStatistics()
        {
            foreach (Stat stat in registeredStats)
            {
                StatsManager.DeregisterStat(stat);
                stat.Dispose();
            }
            registeredStats.Clear();
        }

    }
}