/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyrightD
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
using System.Reflection;
using System.Text;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.CoreModules.Framework
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "CapabilitiesModule")]
    public class CapabilitiesModule : INonSharedRegionModule, ICapabilitiesModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string _showCapsCommandFormat = "   {0,-38} {1,-60}\n";

        protected Scene _scene;

        /// <summary>
        /// Each agent has its own capabilities handler.
        /// </summary>
        protected Dictionary<uint, Caps> _capsObjects = new Dictionary<uint, Caps>();

        protected Dictionary<UUID, string> _capsPaths = new Dictionary<UUID, string>();

        protected Dictionary<UUID, Dictionary<ulong, string>> _childrenSeeds
            = new Dictionary<UUID, Dictionary<ulong, string>>();

        public void Initialise(IConfigSource source)
        {
        }

        public void AddRegion(Scene scene)
        {
            _scene = scene;
            _scene.RegisterModuleInterface<ICapabilitiesModule>(this);

            MainConsole.Instance.Commands.AddCommand(
                "Comms", false, "show caps list",
                "show caps list",
                "Shows list of registered capabilities for users.", HandleShowCapsListCommand);

            MainConsole.Instance.Commands.AddCommand(
                "Comms", false, "show caps stats by user",
                "show caps stats by user [<first-name> <last-name>]",
                "Shows statistics on capabilities use by user.",
                "If a user name is given, then prints a detailed breakdown of caps use ordered by number of requests received.",
                HandleShowCapsStatsByUserCommand);

            MainConsole.Instance.Commands.AddCommand(
                "Comms", false, "show caps stats by cap",
                "show caps stats by cap [<cap-name>]",
                "Shows statistics on capabilities use by capability.",
                "If a capability name is given, then prints a detailed breakdown of use by each user.",
                HandleShowCapsStatsByCapCommand);
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            _scene.UnregisterModuleInterface<ICapabilitiesModule>(this);
        }

        public void PostInitialise()
        {
        }

        public void Close() {}

        public string Name => "Capabilities Module";

        public Type ReplaceableInterface => null;

        public void CreateCaps(UUID agentId, uint circuitCode)
        {
            int ts = Util.EnvironmentTickCount();
/*  this as no business here...
 * must be done elsewhere ( and is )
            int flags = _scene.GetUserFlags(agentId);

            _log.ErrorFormat("[CreateCaps]: banCheck {0} ", Util.EnvironmentTickCountSubtract(ts));

            if (_scene.RegionInfo.EstateSettings.IsBanned(agentId, flags))
                return;
*/
            Caps caps;
            string capsObjectPath = GetCapsPath(agentId);

            lock (_capsObjects)
            {
                if (_capsObjects.ContainsKey(circuitCode))
                {
                    Caps oldCaps = _capsObjects[circuitCode];


                    if (capsObjectPath == oldCaps.CapsObjectPath)
                    {
//                        _log.WarnFormat(
//                           "[CAPS]: Reusing caps for agent {0} in region {1}.  Old caps path {2}, new caps path {3}. ",
//                            agentId, _scene.RegionInfo.RegionName, oldCaps.CapsObjectPath, capsObjectPath);
                        return;
                    }
                    else
                    {
                        // not reusing  add extra melanie cleanup
                        // Remove tge handlers. They may conflict with the
                        // new object created below
                        oldCaps.DeregisterHandlers();

                        // Better safe ... should not be needed but also
                        // no big deal
                        _capsObjects.Remove(circuitCode);
                    }
                }

//                _log.DebugFormat(
//                    "[CAPS]: Adding capabilities for agent {0} in {1} with path {2}",
//                    agentId, _scene.RegionInfo.RegionName, capsObjectPath);

                caps = new Caps(MainServer.Instance, _scene.RegionInfo.ExternalHostName,
                        MainServer.Instance == null ? 0: MainServer.Instance.Port,
                        capsObjectPath, agentId, _scene.RegionInfo.RegionName);

                _log.DebugFormat("[CreateCaps]: new caps agent {0}, circuit {1}, path {2}, time {3} ",agentId,
                    circuitCode,caps.CapsObjectPath, Util.EnvironmentTickCountSubtract(ts));

                _capsObjects[circuitCode] = caps;
            }
            _scene.EventManager.TriggerOnRegisterCaps(agentId, caps);
//            _log.ErrorFormat("[CreateCaps]: end {0} ", Util.EnvironmentTickCountSubtract(ts));

        }

        public void RemoveCaps(UUID agentId, uint circuitCode)
        {
            _log.DebugFormat("[CAPS]: Remove caps for agent {0} in region {1}", agentId, _scene.RegionInfo.RegionName);
            lock (_childrenSeeds)
            {
                if (_childrenSeeds.ContainsKey(agentId))
                {
                    _childrenSeeds.Remove(agentId);
                }
            }

            lock (_capsObjects)
            {
                if (_capsObjects.TryGetValue(circuitCode, out Caps cp))
                {
                    _scene.EventManager.TriggerOnDeregisterCaps(agentId, cp);
                    _capsObjects.Remove(circuitCode);
                    cp.Dispose();
                }
                else
                {
                    foreach (KeyValuePair<uint, Caps> kvp in _capsObjects)
                    {
                        if (kvp.Value.AgentID == agentId)
                        {
                            _scene.EventManager.TriggerOnDeregisterCaps(agentId, kvp.Value);
                            _capsObjects.Remove(kvp.Key);
                            kvp.Value.Dispose();
                            return;
                        }
                    }
                    _log.WarnFormat(
                        "[CAPS]: Received request to remove CAPS handler for root agent {0} in {1}, but no such CAPS handler found!",
                        agentId, _scene.RegionInfo.RegionName);
                }
            }
        }

        public Caps GetCapsForUser(uint circuitCode)
        {
            lock (_capsObjects)
            {
                if (_capsObjects.ContainsKey(circuitCode))
                {
                    return _capsObjects[circuitCode];
                }
            }

            return null;
        }

        public void ActivateCaps(uint circuitCode)
        {
            lock (_capsObjects)
            {
                if (_capsObjects.ContainsKey(circuitCode))
                {
                    _capsObjects[circuitCode].Activate();
                }
            }
        }

        public void SetAgentCapsSeeds(AgentCircuitData agent)
        {
            lock (_capsPaths)
                _capsPaths[agent.AgentID] = agent.CapsPath;

            lock (_childrenSeeds)
                _childrenSeeds[agent.AgentID]
                    = agent.ChildrenCapSeeds == null ? new Dictionary<ulong, string>() : agent.ChildrenCapSeeds;
        }

        public string GetCapsPath(UUID agentId)
        {
            lock (_capsPaths)
            {
                if (_capsPaths.ContainsKey(agentId))
                {
                    return _capsPaths[agentId];
                }
            }

            return null;
        }

        public Dictionary<ulong, string> GetChildrenSeeds(UUID agentID)
        {
            Dictionary<ulong, string> seeds = null;

            lock (_childrenSeeds)
                if (_childrenSeeds.TryGetValue(agentID, out seeds))
                    return seeds;

            return new Dictionary<ulong, string>();
        }

        public void DropChildSeed(UUID agentID, ulong handle)
        {
            Dictionary<ulong, string> seeds;

            lock (_childrenSeeds)
            {
                if (_childrenSeeds.TryGetValue(agentID, out seeds))
                {
                    seeds.Remove(handle);
                }
            }
        }

        public string GetChildSeed(UUID agentID, ulong handle)
        {
            Dictionary<ulong, string> seeds;
            string returnval;

            lock (_childrenSeeds)
            {
                if (_childrenSeeds.TryGetValue(agentID, out seeds))
                {
                    if (seeds.TryGetValue(handle, out returnval))
                        return returnval;
                }
            }

            return null;
        }

        public void SetChildrenSeed(UUID agentID, Dictionary<ulong, string> seeds)
        {
            //_log.DebugFormat(" !!! Setting child seeds in {0} to {1}", _scene.RegionInfo.RegionName, seeds.Count);

            lock (_childrenSeeds)
                _childrenSeeds[agentID] = seeds;
        }

        public void DumpChildrenSeeds(UUID agentID)
        {
            _log.Info("================ ChildrenSeed "+_scene.RegionInfo.RegionName+" ================");

            lock (_childrenSeeds)
            {
                foreach (KeyValuePair<ulong, string> kvp in _childrenSeeds[agentID])
                {
                    uint x, y;
                    Util.RegionHandleToRegionLoc(kvp.Key, out x, out y);
                    _log.Info(" >> "+x+", "+y+": "+kvp.Value);
                }
            }
        }

        private void HandleShowCapsListCommand(string module, string[] cmdParams)
        {
            if (SceneManager.Instance.CurrentScene != null && SceneManager.Instance.CurrentScene != _scene)
                return;

            StringBuilder capsReport = new StringBuilder();
            capsReport.AppendFormat("Region {0}:\n", _scene.RegionInfo.RegionName);

            lock (_capsObjects)
            {
                foreach (KeyValuePair<uint, Caps> kvp in _capsObjects)
                {
                    Caps caps = kvp.Value;
                    string name = string.Empty;
                    if(_scene.TryGetScenePresence(caps.AgentID, out ScenePresence sp) && sp!=null)
                        name = sp.Name;
                    capsReport.AppendFormat("** Circuit {0}; {1} {2}:\n", kvp.Key, caps.AgentID,name);

                    for (IDictionaryEnumerator kvp2 = caps.CapsHandlers.GetCapsDetails(false, null).GetEnumerator(); kvp2.MoveNext(); )
                    {
                        Uri uri = new Uri(kvp2.Value.ToString());
                        capsReport.AppendFormat(_showCapsCommandFormat, kvp2.Key, uri.PathAndQuery);
                    }

                    foreach (KeyValuePair<string, PollServiceEventArgs> kvp2 in caps.GetPollHandlers())
                        capsReport.AppendFormat(_showCapsCommandFormat, kvp2.Key, kvp2.Value.Url);

                    foreach (KeyValuePair<string, string> kvp3 in caps.ExternalCapsHandlers)
                        capsReport.AppendFormat(_showCapsCommandFormat, kvp3.Key, kvp3.Value);
                }
            }

            MainConsole.Instance.Output(capsReport.ToString());
        }

        private void HandleShowCapsStatsByCapCommand(string module, string[] cmdParams)
        {
            if (SceneManager.Instance.CurrentScene != null && SceneManager.Instance.CurrentScene != _scene)
                return;

            if (cmdParams.Length != 5 && cmdParams.Length != 6)
            {
                MainConsole.Instance.Output("Usage: show caps stats by cap [<cap-name>]");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Region {0}:\n", _scene.Name);

            if (cmdParams.Length == 5)
            {
                BuildSummaryStatsByCapReport(sb);
            }
            else if (cmdParams.Length == 6)
            {
                BuildDetailedStatsByCapReport(sb, cmdParams[5]);
            }

            MainConsole.Instance.Output(sb.ToString());
        }

        private void BuildDetailedStatsByCapReport(StringBuilder sb, string capName)
        {
            /*
            sb.AppendFormat("Capability name {0}\n", capName);

            ConsoleDisplayTable cdt = new ConsoleDisplayTable();
            cdt.AddColumn("User Name", 34);
            cdt.AddColumn("Req Received", 12);
            cdt.AddColumn("Req Handled", 12);
            cdt.Indent = 2;

            Dictionary<string, int> receivedStats = new Dictionary<string, int>();
            Dictionary<string, int> handledStats = new Dictionary<string, int>();

            _scene.ForEachScenePresence(
                sp =>
                {
                    Caps caps = _scene.CapsModule.GetCapsForUser(sp.UUID);

                    if (caps == null)
                        return;

                    Dictionary<string, IRequestHandler> capsHandlers = caps.CapsHandlers.GetCapsHandlers();

                    IRequestHandler reqHandler;
                    if (capsHandlers.TryGetValue(capName, out reqHandler))
                    {
                        receivedStats[sp.Name] = reqHandler.RequestsReceived;
                        handledStats[sp.Name] = reqHandler.RequestsHandled;
                    }
                    else
                    {
                        PollServiceEventArgs pollHandler = null;
                        if (caps.TryGetPollHandler(capName, out pollHandler))
                        {
                            receivedStats[sp.Name] = pollHandler.RequestsReceived;
                            handledStats[sp.Name] = pollHandler.RequestsHandled;
                        }
                    }
                }
            );

            foreach (KeyValuePair<string, int> kvp in receivedStats.OrderByDescending(kp => kp.Value))
            {
                cdt.AddRow(kvp.Key, kvp.Value, handledStats[kvp.Key]);
            }

            sb.Append(cdt.ToString());
            */
        }

        private void BuildSummaryStatsByCapReport(StringBuilder sb)
        {
            /*
            ConsoleDisplayTable cdt = new ConsoleDisplayTable();
            cdt.AddColumn("Name", 34);
            cdt.AddColumn("Req Received", 12);
            cdt.AddColumn("Req Handled", 12);
            cdt.Indent = 2;

            Dictionary<string, int> receivedStats = new Dictionary<string, int>();
            Dictionary<string, int> handledStats = new Dictionary<string, int>();

            _scene.ForEachScenePresence(
                sp =>
                {
                    Caps caps = _scene.CapsModule.GetCapsForUser(sp.UUID);

                    if (caps == null)
                        return;

                    foreach (IRequestHandler reqHandler in caps.CapsHandlers.GetCapsHandlers().Values)
                    {
                        string reqName = reqHandler.Name ?? "";

                        if (!receivedStats.ContainsKey(reqName))
                        {
                            receivedStats[reqName] = reqHandler.RequestsReceived;
                            handledStats[reqName] = reqHandler.RequestsHandled;
                        }
                        else
                        {
                            receivedStats[reqName] += reqHandler.RequestsReceived;
                            handledStats[reqName] += reqHandler.RequestsHandled;
                        }
                    }

                    foreach (KeyValuePair<string, PollServiceEventArgs> kvp in caps.GetPollHandlers())
                    {
                        string name = kvp.Key;
                        PollServiceEventArgs pollHandler = kvp.Value;

                        if (!receivedStats.ContainsKey(name))
                        {
                            receivedStats[name] = pollHandler.RequestsReceived;
                            handledStats[name] = pollHandler.RequestsHandled;
                        }
                            else
                        {
                            receivedStats[name] += pollHandler.RequestsReceived;
                            handledStats[name] += pollHandler.RequestsHandled;
                        }
                    }
                }
            );

            foreach (KeyValuePair<string, int> kvp in receivedStats.OrderByDescending(kp => kp.Value))
                cdt.AddRow(kvp.Key, kvp.Value, handledStats[kvp.Key]);

            sb.Append(cdt.ToString());
            */
        }

        private void HandleShowCapsStatsByUserCommand(string module, string[] cmdParams)
        {
            /*
            if (SceneManager.Instance.CurrentScene != null && SceneManager.Instance.CurrentScene != _scene)
                return;

            if (cmdParams.Length != 5 && cmdParams.Length != 7)
            {
                MainConsole.Instance.Output("Usage: show caps stats by user [<first-name> <last-name>]");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendFormat("Region {0}:\n", _scene.Name);

            if (cmdParams.Length == 5)
            {
                BuildSummaryStatsByUserReport(sb);
            }
            else if (cmdParams.Length == 7)
            {
                string firstName = cmdParams[5];
                string lastName = cmdParams[6];

                ScenePresence sp = _scene.GetScenePresence(firstName, lastName);

                if (sp == null)
                    return;

                BuildDetailedStatsByUserReport(sb, sp);
            }

            MainConsole.Instance.Output(sb.ToString());
            */
        }

        private void BuildDetailedStatsByUserReport(StringBuilder sb, ScenePresence sp)
        {
            /*
            sb.AppendFormat("Avatar name {0}, type {1}\n", sp.Name, sp.IsChildAgent ? "child" : "root");

            ConsoleDisplayTable cdt = new ConsoleDisplayTable();
            cdt.AddColumn("Cap Name", 34);
            cdt.AddColumn("Req Received", 12);
            cdt.AddColumn("Req Handled", 12);
            cdt.Indent = 2;

            Caps caps = _scene.CapsModule.GetCapsForUser(sp.UUID);

            if (caps == null)
                return;

            List<CapTableRow> capRows = new List<CapTableRow>();

            foreach (IRequestHandler reqHandler in caps.CapsHandlers.GetCapsHandlers().Values)
                capRows.Add(new CapTableRow(reqHandler.Name, reqHandler.RequestsReceived, reqHandler.RequestsHandled));

            foreach (KeyValuePair<string, PollServiceEventArgs> kvp in caps.GetPollHandlers())
                capRows.Add(new CapTableRow(kvp.Key, kvp.Value.RequestsReceived, kvp.Value.RequestsHandled));

            foreach (CapTableRow ctr in capRows.OrderByDescending(ctr => ctr.RequestsReceived))
                cdt.AddRow(ctr.Name, ctr.RequestsReceived, ctr.RequestsHandled);

            sb.Append(cdt.ToString());
            */
        }

        private void BuildSummaryStatsByUserReport(StringBuilder sb)
        {
            /*
            ConsoleDisplayTable cdt = new ConsoleDisplayTable();
            cdt.AddColumn("Name", 32);
            cdt.AddColumn("Type", 5);
            cdt.AddColumn("Req Received", 12);
            cdt.AddColumn("Req Handled", 12);
            cdt.Indent = 2;

            _scene.ForEachScenePresence(
                sp =>
                {
                    Caps caps = _scene.CapsModule.GetCapsForUser(sp.UUID);

                    if (caps == null)
                        return;

                    Dictionary<string, IRequestHandler> capsHandlers = caps.CapsHandlers.GetCapsHandlers();

                    int totalRequestsReceived = 0;
                    int totalRequestsHandled = 0;

                    foreach (IRequestHandler reqHandler in capsHandlers.Values)
                    {
                        totalRequestsReceived += reqHandler.RequestsReceived;
                        totalRequestsHandled += reqHandler.RequestsHandled;
                    }

                    Dictionary<string, PollServiceEventArgs> capsPollHandlers = caps.GetPollHandlers();

                    foreach (PollServiceEventArgs handler in capsPollHandlers.Values)
                    {
                        totalRequestsReceived += handler.RequestsReceived;
                        totalRequestsHandled += handler.RequestsHandled;
                    }

                    cdt.AddRow(sp.Name, sp.IsChildAgent ? "child" : "root", totalRequestsReceived, totalRequestsHandled);
                }
            );

            sb.Append(cdt.ToString());
            */
        }

        private class CapTableRow
        {
            public string Name { get; }
            public int RequestsReceived { get; }
            public int RequestsHandled { get; }

            public CapTableRow(string name, int requestsReceived, int requestsHandled)
            {
                Name = name;
                RequestsReceived = requestsReceived;
                RequestsHandled = requestsHandled;
            }
        }
    }
}
