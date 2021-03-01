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
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Mono.Data.SqliteClient;
using Mono.Addins;

using Caps = OpenSim.Framework.Capabilities.Caps;

using OSD = OpenMetaverse.StructuredData.OSD;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

namespace OpenSim.Region.UserStatistics
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WebStatsModule")]
    public class WebStatsModule : ISharedRegionModule
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static SqliteConnection dbConn;

        /// <summary>
        /// User statistics sessions keyed by agent ID
        /// </summary>
        private readonly Dictionary<UUID, UserSession> _sessions = new Dictionary<UUID, UserSession>();

        private readonly List<Scene> _scenes = new List<Scene>();
        private readonly Dictionary<string, IStatsController> reports = new Dictionary<string, IStatsController>();
        private readonly Dictionary<UUID, USimStatsData> _simstatsCounters = new Dictionary<UUID, USimStatsData>();
        private const int updateStatsMod = 6;
        private int updateLogMod = 1;
        private volatile int updateLogCounter = 0;
        private volatile int concurrencyCounter = 0;
        private bool enabled = false;
        private string _loglines = string.Empty;
        private volatile int lastHit = 12000;

        #region ISharedRegionModule

        public virtual void Initialise(IConfigSource config)
        {
            IConfig cnfg = config.Configs["WebStats"];

            if (cnfg != null)
                enabled = cnfg.GetBoolean("enabled", false);
        }

        public virtual void PostInitialise()
        {
            if (!enabled)
                return;

            if (Util.IsWindows())
                Util.LoadArchSpecificWindowsDll("sqlite3.dll");

            //IConfig startupConfig = config.Configs["Startup"];

            dbConn = new SqliteConnection("URI=file:LocalUserStatistics.db,version=3");
            dbConn.Open();
            CreateTables(dbConn);

            Prototype_distributor protodep = new Prototype_distributor();
            Updater_distributor updatedep = new Updater_distributor();
            ActiveConnectionsAJAX ajConnections = new ActiveConnectionsAJAX();
            SimStatsAJAX ajSimStats = new SimStatsAJAX();
            LogLinesAJAX ajLogLines = new LogLinesAJAX();
            Default_Report defaultReport = new Default_Report();
            Clients_report clientReport = new Clients_report();
            Sessions_Report sessionsReport = new Sessions_Report();

            reports.Add("prototype.js", protodep);
            reports.Add("updater.js", updatedep);
            reports.Add("activeconnectionsajax.html", ajConnections);
            reports.Add("simstatsajax.html", ajSimStats);
            reports.Add("activelogajax.html", ajLogLines);
            reports.Add("default.report", defaultReport);
            reports.Add("clients.report", clientReport);
            reports.Add("sessions.report", sessionsReport);

            reports.Add("sim.css", new Prototype_distributor("sim.css"));
            reports.Add("sim.html", new Prototype_distributor("sim.html"));
            reports.Add("jquery.js", new Prototype_distributor("jquery.js"));

            ////
            // Add Your own Reports here (Do Not Modify Lines here Devs!)
            ////

            ////
            // End Own reports section
            ////

            MainServer.Instance.AddHTTPHandler("/SStats", HandleStatsRequest);
            MainServer.Instance.AddHTTPHandler("/VS", HandleUnknownCAPSRequest);
        }

        public virtual void AddRegion(Scene scene)
        {
            if (!enabled)
                return;

            lock (_scenes)
            {
                _scenes.Add(scene);
                updateLogMod = _scenes.Count * 2;

                _simstatsCounters.Add(scene.RegionInfo.RegionID, new USimStatsData(scene.RegionInfo.RegionID));

                scene.EventManager.OnRegisterCaps += OnRegisterCaps;
                scene.EventManager.OnDeregisterCaps += OnDeRegisterCaps;
                scene.EventManager.OnClientClosed += OnClientClosed;
                scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
                scene.StatsReporter.OnSendStatsResult += ReceiveClassicSimStatsPacket;
            }
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!enabled)
                return;

            lock (_scenes)
            {
                _scenes.Remove(scene);
                updateLogMod = _scenes.Count * 2;
                _simstatsCounters.Remove(scene.RegionInfo.RegionID);
            }
        }

        public virtual void Close()
        {
            if (!enabled)
                return;

            dbConn.Close();
            dbConn.Dispose();
            _sessions.Clear();
            _scenes.Clear();
            reports.Clear();
            _simstatsCounters.Clear();
        }

        public virtual string Name => "ViewerStatsModule";

        public Type ReplaceableInterface => null;

        #endregion

        private void ReceiveClassicSimStatsPacket(SimStats stats)
        {
            if (!enabled)
                return;

            try
            {
                // Ignore the update if there's a report running right now
                // ignore the update if there hasn't been a hit in 30 seconds.
                if (concurrencyCounter > 0 || System.Environment.TickCount - lastHit > 30000)
                    return;

                // We will conduct this under lock so that fields such as updateLogCounter do not potentially get
                // confused if a scene is removed.
                // XXX: Possibly the scope of this lock could be reduced though it's not critical.
                lock (_scenes)
                {
                    if (updateLogMod != 0 && updateLogCounter++ % updateLogMod == 0)
                    {
                        _loglines = readLogLines(10);

                        if (updateLogCounter > 10000)
                            updateLogCounter = 1;
                    }

                    USimStatsData ss = _simstatsCounters[stats.RegionUUID];

                    if (++ss.StatsCounter % updateStatsMod == 0)
                    {
                        ss.ConsumeSimStats(stats);
                    }
                }
            }
            catch (KeyNotFoundException)
            {
            }
        }

        private Hashtable HandleUnknownCAPSRequest(Hashtable request)
        {
            //string regpath = request["uri"].ToString();
            int response_code = 200;
            string contenttype = "text/html";
            UpdateUserStats(ParseViewerStats(request["body"].ToString(), UUID.Zero), dbConn);
            Hashtable responsedata = new Hashtable();

            responsedata["int_response_code"] = response_code;
            responsedata["content_type"] = contenttype;
            responsedata["keepalive"] = false;
            responsedata["str_response_string"] = string.Empty;
            return responsedata;
        }

        private Hashtable HandleStatsRequest(Hashtable request)
        {
            lastHit = System.Environment.TickCount;
            Hashtable responsedata = new Hashtable();
            string regpath = request["uri"].ToString();
            int response_code = 404;
            string contenttype = "text/html";
            bool jsonFormatOutput = false;

            string strOut = string.Empty;

            // The request patch should be "/SStats/reportName" where 'reportName'
            // is one of the names added to the 'reports' hashmap.
            regpath = regpath.Remove(0, 8);
            if (regpath.Length == 0) regpath = "default.report";
            if (reports.ContainsKey(regpath))
            {
                IStatsController rep = reports[regpath];
                Hashtable repParams = new Hashtable();

                if (request.ContainsKey("json"))
                    jsonFormatOutput = true;

                if (request.ContainsKey("requestvars"))
                    repParams["RequestVars"] = request["requestvars"];
                else
                    repParams["RequestVars"] = new Hashtable();

                if (request.ContainsKey("querystringkeys"))
                    repParams["QueryStringKeys"] = request["querystringkeys"];
                else
                    repParams["QueryStringKeys"] = new string[0];


                repParams["DatabaseConnection"] = dbConn;
                repParams["Scenes"] = _scenes;
                repParams["SimStats"] = _simstatsCounters;
                repParams["LogLines"] = _loglines;
                repParams["Reports"] = reports;

                concurrencyCounter++;

                if (jsonFormatOutput)
                {
                    strOut = rep.RenderJson(rep.ProcessModel(repParams));
                    contenttype = "text/json";
                }
                else
                {
                    strOut = rep.RenderView(rep.ProcessModel(repParams));
                }

                if (regpath.EndsWith("js"))
                {
                    contenttype = "text/javascript";
                }

                if (regpath.EndsWith("css"))
                {
                    contenttype = "text/css";
                }

                concurrencyCounter--;

                response_code = 200;
            }
            else
            {
                strOut = MainServer.Instance.GetHTTP404();
            }

            responsedata["int_response_code"] = response_code;
            responsedata["content_type"] = contenttype;
            responsedata["keepalive"] = false;
            responsedata["str_response_string"] = strOut;

            return responsedata;
        }

        private void CreateTables(SqliteConnection db)
        {
            using (SqliteCommand createcmd = new SqliteCommand(SQL_STATS_TABLE_CREATE, db))
            {
                createcmd.ExecuteNonQuery();
            }
        }

        private void OnRegisterCaps(UUID agentID, Caps caps)
        {
//            _log.DebugFormat("[WEB STATS MODULE]: OnRegisterCaps: agentID {0} caps {1}", agentID, caps);

            string capsPath = "/" + UUID.Random();
            caps.RegisterHandler(
                "ViewerStats",
                new RestStreamHandler(
                    "POST",
                    capsPath,
                    (request, path, param, httpRequest, httpResponse)
                        => ViewerStatsReport(request, path, param, agentID, caps),
                    "ViewerStats",
                    agentID.ToString()));
        }

        private void OnDeRegisterCaps(UUID agentID, Caps caps)
        {
        }

        protected virtual void AddEventHandlers()
        {
            lock (_scenes)
            {
                updateLogMod = _scenes.Count * 2;
                foreach (Scene scene in _scenes)
                {
                    scene.EventManager.OnRegisterCaps += OnRegisterCaps;
                    scene.EventManager.OnDeregisterCaps += OnDeRegisterCaps;
                    scene.EventManager.OnClientClosed += OnClientClosed;
                    scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
                }
            }
        }

        private void OnMakeRootAgent(ScenePresence agent)
        {
//            _log.DebugFormat(
//                "[WEB STATS MODULE]: Looking for session {0} for {1} in {2}",
//                agent.ControllingClient.SessionId, agent.Name, agent.Scene.Name);

            lock (_sessions)
            {
                UserSession uid;

                if (!_sessions.ContainsKey(agent.UUID))
                {
                    UserSessionData usd = UserSessionUtil.newUserSessionData();
                    uid = new UserSession
                    {
                        name_f = agent.Firstname,
                        name_l = agent.Lastname,
                        session_data = usd
                    };

                    _sessions.Add(agent.UUID, uid);
                }
                else
                {
                    uid = _sessions[agent.UUID];
                }

                uid.region_id = agent.Scene.RegionInfo.RegionID;
                uid.session_id = agent.ControllingClient.SessionId;
            }
        }

        private void OnClientClosed(UUID agentID, Scene scene)
        {
            lock (_sessions)
            {
                if (_sessions.ContainsKey(agentID) && _sessions[agentID].region_id == scene.RegionInfo.RegionID)
                {
                    _sessions.Remove(agentID);
                }
            }
        }

        private string readLogLines(int amount)
        {
            Encoding encoding = Encoding.ASCII;
            int sizeOfChar = encoding.GetByteCount("\n");
            byte[] buffer = encoding.GetBytes("\n");
            string logfile = Util.logFile();
            FileStream fs = new FileStream(logfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long tokenCount = 0;
            long endPosition = fs.Length / sizeOfChar;

            for (long position = sizeOfChar; position < endPosition; position += sizeOfChar)
            {
                fs.Seek(-position, SeekOrigin.End);
                fs.Read(buffer, 0, buffer.Length);

                if (encoding.GetString(buffer) == "\n")
                {
                    tokenCount++;
                    if (tokenCount == amount)
                    {
                        byte[] returnBuffer = new byte[fs.Length - fs.Position];
                        fs.Read(returnBuffer, 0, returnBuffer.Length);
                        fs.Close();
                        fs.Dispose();
                        return encoding.GetString(returnBuffer);
                    }
                }
            }

            // handle case where number of tokens in file is less than numberOfTokens
            fs.Seek(0, SeekOrigin.Begin);
            buffer = new byte[fs.Length];
            fs.Read(buffer, 0, buffer.Length);
            fs.Close();
            fs.Dispose();
            return encoding.GetString(buffer);
        }

        /// <summary>
        /// Callback for a viewerstats cap
        /// </summary>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        private string ViewerStatsReport(string request, string path, string param,
                                      UUID agentID, Caps caps)
        {
//            _log.DebugFormat("[WEB STATS MODULE]: Received viewer starts report from {0}", agentID);

            UpdateUserStats(ParseViewerStats(request, agentID), dbConn);

            return string.Empty;
        }

        private UserSession ParseViewerStats(string request, UUID agentID)
        {
            UserSession uid = new UserSession();
            UserSessionData usd;
            OSD message = OSDParser.DeserializeLLSDXml(request);
            OSDMap mmap;

            lock (_sessions)
            {
                if (agentID != UUID.Zero)
                {
                    if (!_sessions.ContainsKey(agentID))
                    {
                        _log.WarnFormat("[WEB STATS MODULE]: no session for stat disclosure for agent {0}", agentID);
                        return new UserSession();
                    }

                    uid = _sessions[agentID];

//                    _log.DebugFormat("[WEB STATS MODULE]: Got session {0} for {1}", uid.session_id, agentID);
                }
                else
                {
                    // parse through the beginning to locate the session
                    if (message.Type != OSDType.Map)
                        return new UserSession();

                    mmap = (OSDMap)message;
                    {
                        UUID sessionID = mmap["session_id"].AsUUID();

                        if (sessionID == UUID.Zero)
                            return new UserSession();


                        // search through each session looking for the owner
                        foreach (UUID usersessionid in _sessions.Keys)
                        {
                            // got it!
                            if (_sessions[usersessionid].session_id == sessionID)
                            {
                                agentID = usersessionid;
                                uid = _sessions[usersessionid];
                                break;
                            }

                        }

                        // can't find a session
                        if (agentID == UUID.Zero)
                        {
                            return new UserSession();
                        }
                    }
                }
            }

            usd = uid.session_data;

            if (message.Type != OSDType.Map)
                return new UserSession();

            mmap = (OSDMap)message;
            {
                if (mmap["agent"].Type != OSDType.Map)
                    return new UserSession();
                OSDMap agent_map = (OSDMap)mmap["agent"];
                usd.agent_id = agentID;
                usd.name_f = uid.name_f;
                usd.name_l = uid.name_l;
                usd.region_id = uid.region_id;
                usd.a_language = agent_map["language"].AsString();
                usd.me_use = (float)agent_map["me_use"].AsReal();
                usd.meters_traveled = (float)agent_map["meters_traveled"].AsReal();
                usd.regions_visited = agent_map["regions_visited"].AsInteger();
                usd.run_time = (float)agent_map["run_time"].AsReal();
                usd.start_time = (float)agent_map["start_time"].AsReal();
                usd.client_version = agent_map["version"].AsString();

                UserSessionUtil.UpdateMultiItems(ref usd, agent_map["agents_in_view"].AsInteger(),
                                                 (float)agent_map["ping"].AsReal(),
                                                 (float)agent_map["si_fps"].AsReal(),
                                                 (float)agent_map["fps"].AsReal());

                if (mmap["downloads"].Type != OSDType.Map)
                    return new UserSession();
                OSDMap downloads_map = (OSDMap)mmap["downloads"];
                usd.d_object_kb = (float)downloads_map["object_kbytes"].AsReal();
                usd.d_texture_kb = (float)downloads_map["texture_kbytes"].AsReal();
                usd.d_world_kb = (float)downloads_map["workd_kbytes"].AsReal();

//                _log.DebugFormat("[WEB STATS MODULE]: mmap[\"session_id\"] = [{0}]", mmap["session_id"].AsUUID());

                usd.session_id = mmap["session_id"].AsUUID();

                if (mmap["system"].Type != OSDType.Map)
                    return new UserSession();
                OSDMap syste_map = (OSDMap)mmap["system"];

                usd.s_cpu = syste_map["cpu"].AsString();
                usd.s_gpu = syste_map["gpu"].AsString();
                usd.s_os = syste_map["os"].AsString();
                usd.s_ram = syste_map["ram"].AsInteger();

                if (mmap["stats"].Type != OSDType.Map)
                    return new UserSession();

                OSDMap stats_map = (OSDMap)mmap["stats"];
                {

                    if (stats_map["failures"].Type != OSDType.Map)
                        return new UserSession();
                    OSDMap stats_failures = (OSDMap)stats_map["failures"];
                    usd.f_dropped = stats_failures["dropped"].AsInteger();
                    usd.f_failed_resends = stats_failures["failed_resends"].AsInteger();
                    usd.f_invalid = stats_failures["invalid"].AsInteger();
                    usd.f_resent = stats_failures["resent"].AsInteger();
                    usd.f_send_packet = stats_failures["send_packet"].AsInteger();

                    if (stats_map["net"].Type != OSDType.Map)
                        return new UserSession();
                    OSDMap stats_net = (OSDMap)stats_map["net"];
                    {
                        if (stats_net["in"].Type != OSDType.Map)
                            return new UserSession();

                        OSDMap net_in = (OSDMap)stats_net["in"];
                        usd.n_in_kb = (float)net_in["kbytes"].AsReal();
                        usd.n_in_pk = net_in["packets"].AsInteger();

                        if (stats_net["out"].Type != OSDType.Map)
                            return new UserSession();
                        OSDMap net_out = (OSDMap)stats_net["out"];

                        usd.n_out_kb = (float)net_out["kbytes"].AsReal();
                        usd.n_out_pk = net_out["packets"].AsInteger();
                    }
                }
            }

            uid.session_data = usd;
            _sessions[agentID] = uid;

//            _log.DebugFormat(
//                "[WEB STATS MODULE]: Parse data for {0} {1}, session {2}", uid.name_f, uid.name_l, uid.session_id);

            return uid;
        }

        private void UpdateUserStats(UserSession uid, SqliteConnection db)
        {
//            _log.DebugFormat(
//                "[WEB STATS MODULE]: Updating user stats for {0} {1}, session {2}", uid.name_f, uid.name_l, uid.session_id);

            if (uid.session_id == UUID.Zero)
                return;

            lock (db)
            {
                using (SqliteCommand updatecmd = new SqliteCommand(SQL_STATS_TABLE_INSERT, db))
                {
                    updatecmd.Parameters.Add(new SqliteParameter(":session_id", uid.session_data.session_id.ToString()));
                    updatecmd.Parameters.Add(new SqliteParameter(":agent_id", uid.session_data.agent_id.ToString()));
                    updatecmd.Parameters.Add(new SqliteParameter(":region_id", uid.session_data.region_id.ToString()));
                    updatecmd.Parameters.Add(new SqliteParameter(":last_updated", (int) uid.session_data.last_updated));
                    updatecmd.Parameters.Add(new SqliteParameter(":remote_ip", uid.session_data.remote_ip));
                    updatecmd.Parameters.Add(new SqliteParameter(":name_f", uid.session_data.name_f));
                    updatecmd.Parameters.Add(new SqliteParameter(":name_l", uid.session_data.name_l));
                    updatecmd.Parameters.Add(new SqliteParameter(":avg_agents_in_view", uid.session_data.avg_agents_in_view));
                    updatecmd.Parameters.Add(new SqliteParameter(":min_agents_in_view",
                                                                 (int) uid.session_data.min_agents_in_view));
                    updatecmd.Parameters.Add(new SqliteParameter(":max_agents_in_view",
                                                                 (int) uid.session_data.max_agents_in_view));
                    updatecmd.Parameters.Add(new SqliteParameter(":mode_agents_in_view",
                                                                 (int) uid.session_data.mode_agents_in_view));
                    updatecmd.Parameters.Add(new SqliteParameter(":avg_fps", uid.session_data.avg_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":min_fps", uid.session_data.min_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":max_fps", uid.session_data.max_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":mode_fps", uid.session_data.mode_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":a_language", uid.session_data.a_language));
                    updatecmd.Parameters.Add(new SqliteParameter(":me_use", uid.session_data.me_use));
                    updatecmd.Parameters.Add(new SqliteParameter(":meters_traveled", uid.session_data.meters_traveled));
                    updatecmd.Parameters.Add(new SqliteParameter(":avg_ping", uid.session_data.avg_ping));
                    updatecmd.Parameters.Add(new SqliteParameter(":min_ping", uid.session_data.min_ping));
                    updatecmd.Parameters.Add(new SqliteParameter(":max_ping", uid.session_data.max_ping));
                    updatecmd.Parameters.Add(new SqliteParameter(":mode_ping", uid.session_data.mode_ping));
                    updatecmd.Parameters.Add(new SqliteParameter(":regions_visited", uid.session_data.regions_visited));
                    updatecmd.Parameters.Add(new SqliteParameter(":run_time", uid.session_data.run_time));
                    updatecmd.Parameters.Add(new SqliteParameter(":avg_si_fps", uid.session_data.avg_si_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":min_si_fps", uid.session_data.min_si_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":max_si_fps", uid.session_data.max_si_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":mode_si_fps", uid.session_data.mode_si_fps));
                    updatecmd.Parameters.Add(new SqliteParameter(":start_time", uid.session_data.start_time));
                    updatecmd.Parameters.Add(new SqliteParameter(":client_version", uid.session_data.client_version));
                    updatecmd.Parameters.Add(new SqliteParameter(":s_cpu", uid.session_data.s_cpu));
                    updatecmd.Parameters.Add(new SqliteParameter(":s_gpu", uid.session_data.s_gpu));
                    updatecmd.Parameters.Add(new SqliteParameter(":s_os", uid.session_data.s_os));
                    updatecmd.Parameters.Add(new SqliteParameter(":s_ram", uid.session_data.s_ram));
                    updatecmd.Parameters.Add(new SqliteParameter(":d_object_kb", uid.session_data.d_object_kb));
                    updatecmd.Parameters.Add(new SqliteParameter(":d_texture_kb", uid.session_data.d_texture_kb));
                    updatecmd.Parameters.Add(new SqliteParameter(":d_world_kb", uid.session_data.d_world_kb));
                    updatecmd.Parameters.Add(new SqliteParameter(":n_in_kb", uid.session_data.n_in_kb));
                    updatecmd.Parameters.Add(new SqliteParameter(":n_in_pk", uid.session_data.n_in_pk));
                    updatecmd.Parameters.Add(new SqliteParameter(":n_out_kb", uid.session_data.n_out_kb));
                    updatecmd.Parameters.Add(new SqliteParameter(":n_out_pk", uid.session_data.n_out_pk));
                    updatecmd.Parameters.Add(new SqliteParameter(":f_dropped", uid.session_data.f_dropped));
                    updatecmd.Parameters.Add(new SqliteParameter(":f_failed_resends", uid.session_data.f_failed_resends));
                    updatecmd.Parameters.Add(new SqliteParameter(":f_invalid", uid.session_data.f_invalid));
                    updatecmd.Parameters.Add(new SqliteParameter(":f_off_circuit", uid.session_data.f_off_circuit));
                    updatecmd.Parameters.Add(new SqliteParameter(":f_resent", uid.session_data.f_resent));
                    updatecmd.Parameters.Add(new SqliteParameter(":f_send_packet", uid.session_data.f_send_packet));

//                        StringBuilder parameters = new StringBuilder();
//                        SqliteParameterCollection spc = updatecmd.Parameters;
//                        foreach (SqliteParameter sp in spc)
//                            parameters.AppendFormat("{0}={1},", sp.ParameterName, sp.Value);
//
//                        _log.DebugFormat("[WEB STATS MODULE]: Parameters {0}", parameters);

//                    _log.DebugFormat("[WEB STATS MODULE]: Database stats update for {0}", uid.session_data.agent_id);

                    updatecmd.ExecuteNonQuery();
                }
            }
        }

        #region SQL
        private const string SQL_STATS_TABLE_CREATE = @"CREATE TABLE IF NOT EXISTS stats_session_data (
               session_id VARCHAR(36) NOT NULL PRIMARY KEY,
               agent_id VARCHAR(36) NOT NULL DEFAULT '',
               region_id VARCHAR(36) NOT NULL DEFAULT '',
               last_updated INT NOT NULL DEFAULT '0',
               remote_ip VARCHAR(16) NOT NULL DEFAULT '',
               name_f VARCHAR(50) NOT NULL DEFAULT '',
               name_l VARCHAR(50) NOT NULL DEFAULT '',
               avg_agents_in_view FLOAT NOT NULL DEFAULT '0',
               min_agents_in_view INT NOT NULL DEFAULT '0',
               max_agents_in_view INT NOT NULL DEFAULT '0',
               mode_agents_in_view INT NOT NULL DEFAULT '0',
               avg_fps FLOAT NOT NULL DEFAULT '0',
               min_fps FLOAT NOT NULL DEFAULT '0',
               max_fps FLOAT NOT NULL DEFAULT '0',
               mode_fps FLOAT NOT NULL DEFAULT '0',
               a_language VARCHAR(25) NOT NULL DEFAULT '',
               me_use FLOAT NOT NULL DEFAULT '0',
               meters_traveled FLOAT NOT NULL DEFAULT '0',
               avg_ping FLOAT NOT NULL DEFAULT '0',
               min_ping FLOAT NOT NULL DEFAULT '0',
               max_ping FLOAT NOT NULL DEFAULT '0',
               mode_ping FLOAT NOT NULL DEFAULT '0',
               regions_visited INT NOT NULL DEFAULT '0',
               run_time FLOAT NOT NULL DEFAULT '0',
               avg_si_fps FLOAT NOT NULL DEFAULT '0',
               min_si_fps FLOAT NOT NULL DEFAULT '0',
               max_si_fps FLOAT NOT NULL DEFAULT '0',
               mode_si_fps FLOAT NOT NULL DEFAULT '0',
               start_time FLOAT NOT NULL DEFAULT '0',
               client_version VARCHAR(255) NOT NULL DEFAULT '',
               s_cpu VARCHAR(255) NOT NULL DEFAULT '',
               s_gpu VARCHAR(255) NOT NULL DEFAULT '',
               s_os VARCHAR(2255) NOT NULL DEFAULT '',
               s_ram INT NOT NULL DEFAULT '0',
               d_object_kb FLOAT NOT NULL DEFAULT '0',
               d_texture_kb FLOAT NOT NULL DEFAULT '0',
               d_world_kb FLOAT NOT NULL DEFAULT '0',
               n_in_kb FLOAT NOT NULL DEFAULT '0',
               n_in_pk INT NOT NULL DEFAULT '0',
               n_out_kb FLOAT NOT NULL DEFAULT '0',
               n_out_pk INT NOT NULL DEFAULT '0',
               f_dropped INT NOT NULL DEFAULT '0',
               f_failed_resends INT NOT NULL DEFAULT '0',
               f_invalid INT NOT NULL DEFAULT '0',
               f_off_circuit INT NOT NULL DEFAULT '0',
               f_resent INT NOT NULL DEFAULT '0',
               f_send_packet INT NOT NULL DEFAULT '0'
            );";

        private const string SQL_STATS_TABLE_INSERT = @"INSERT OR REPLACE INTO stats_session_data (
session_id, agent_id, region_id, last_updated, remote_ip, name_f, name_l, avg_agents_in_view, min_agents_in_view, max_agents_in_view,
mode_agents_in_view, avg_fps, min_fps, max_fps, mode_fps, a_language, me_use, meters_traveled, avg_ping, min_ping, max_ping, mode_ping,
regions_visited, run_time, avg_si_fps, min_si_fps, max_si_fps, mode_si_fps, start_time, client_version, s_cpu, s_gpu, s_os, s_ram,
d_object_kb, d_texture_kb, d_world_kb, n_in_kb, n_in_pk, n_out_kb, n_out_pk, f_dropped, f_failed_resends, f_invalid, f_off_circuit,
f_resent, f_send_packet
)
VALUES
(
:session_id, :agent_id, :region_id, :last_updated, :remote_ip, :name_f, :name_l, :avg_agents_in_view, :min_agents_in_view, :max_agents_in_view,
:mode_agents_in_view, :avg_fps, :min_fps, :max_fps, :mode_fps, :a_language, :me_use, :meters_traveled, :avg_ping, :min_ping, :max_ping, :mode_ping,
:regions_visited, :run_time, :avg_si_fps, :min_si_fps, :max_si_fps, :mode_si_fps, :start_time, :client_version, :s_cpu, :s_gpu, :s_os, :s_ram,
:d_object_kb, :d_texture_kb, :d_world_kb, :n_in_kb, :n_in_pk, :n_out_kb, :n_out_pk, :f_dropped, :f_failed_resends, :f_invalid, :f_off_circuit,
:f_resent, :f_send_packet
)
";

    #endregion

    }

    public static class UserSessionUtil
    {
        public static UserSessionData newUserSessionData()
        {
            UserSessionData obj = ZeroSession(new UserSessionData());
            return obj;
        }

        public static void UpdateMultiItems(ref UserSessionData s, int agents_in_view, float ping, float si_fps, float fps)
        {
            // don't insert zero values here or it'll skew the statistics.
            if (agents_in_view == 0 && fps == 0 && si_fps == 0 && ping == 0)
                return;
            s._agents_in_view.Add(agents_in_view);
            s._fps.Add(fps);
            s._si_fps.Add(si_fps);
            s._ping.Add(ping);

            int[] __agents_in_view = s._agents_in_view.ToArray();

            s.avg_agents_in_view = ArrayAvg_i(__agents_in_view);
            s.min_agents_in_view = ArrayMin_i(__agents_in_view);
            s.max_agents_in_view = ArrayMax_i(__agents_in_view);
            s.mode_agents_in_view = ArrayMode_i(__agents_in_view);

            float[] __fps = s._fps.ToArray();
            s.avg_fps = ArrayAvg_f(__fps);
            s.min_fps = ArrayMin_f(__fps);
            s.max_fps = ArrayMax_f(__fps);
            s.mode_fps = ArrayMode_f(__fps);

            float[] __si_fps = s._si_fps.ToArray();
            s.avg_si_fps = ArrayAvg_f(__si_fps);
            s.min_si_fps = ArrayMin_f(__si_fps);
            s.max_si_fps = ArrayMax_f(__si_fps);
            s.mode_si_fps = ArrayMode_f(__si_fps);

            float[] __ping = s._ping.ToArray();
            s.avg_ping = ArrayAvg_f(__ping);
            s.min_ping = ArrayMin_f(__ping);
            s.max_ping = ArrayMax_f(__ping);
            s.mode_ping = ArrayMode_f(__ping);
        }

        #region Statistics

        public static int ArrayMin_i(int[] arr)
        {
            int cnt = arr.Length;
            if (cnt == 0)
                return 0;

            Array.Sort(arr);
            return arr[0];
        }

        public static int ArrayMax_i(int[] arr)
        {
            int cnt = arr.Length;
            if (cnt == 0)
                return 0;

            Array.Sort(arr);
            return arr[cnt-1];
        }

        public static float ArrayMin_f(float[] arr)
        {
            int cnt = arr.Length;
            if (cnt == 0)
                return 0;

            Array.Sort(arr);
            return arr[0];
        }

        public static float ArrayMax_f(float[] arr)
        {
            int cnt = arr.Length;
            if (cnt == 0)
                return 0;

            Array.Sort(arr);
            return arr[cnt - 1];
        }

        public static float ArrayAvg_i(int[] arr)
        {
            int cnt = arr.Length;

            if (cnt == 0)
                return 0;

            float result = arr[0];

            for (int i = 1; i < cnt; i++)
                result += arr[i];

            return result / cnt;
        }

        public static float ArrayAvg_f(float[] arr)
        {
            int cnt = arr.Length;

            if (cnt == 0)
                return 0;

            float result = arr[0];

            for (int i = 1; i < cnt; i++)
                result += arr[i];

            return result / cnt;
        }

        public static float ArrayMode_f(float[] arr)
        {
            List<float> mode = new List<float>();

            float[] srtArr = new float[arr.Length];
            float[,] freq = new float[arr.Length, 2];
            Array.Copy(arr, srtArr, arr.Length);
            Array.Sort(srtArr);

            float tmp = srtArr[0];
            int index = 0;
            int i = 0;
            while (i < srtArr.Length)
            {
                freq[index, 0] = tmp;

                while (tmp == srtArr[i])
                {
                    freq[index, 1]++;
                    i++;

                    if (i > srtArr.Length - 1)
                        break;
                }

                if (i < srtArr.Length)
                {
                    tmp = srtArr[i];
                    index++;
                }

            }

            Array.Clear(srtArr, 0, srtArr.Length);

            for (i = 0; i < srtArr.Length; i++)
                srtArr[i] = freq[i, 1];

            Array.Sort(srtArr);

            if (srtArr[srtArr.Length - 1] == 0 || srtArr[srtArr.Length - 1] == 1)
                return 0;

            float freqtest = (float)freq.Length / freq.Rank;

            for (i = 0; i < freqtest; i++)
            {
                if (freq[i, 1] == srtArr[index])
                    mode.Add(freq[i, 0]);

            }

            return mode.ToArray()[0];
        }

        public static int ArrayMode_i(int[] arr)
        {
            List<int> mode = new List<int>();

            int[] srtArr = new int[arr.Length];
            int[,] freq = new int[arr.Length, 2];
            Array.Copy(arr, srtArr, arr.Length);
            Array.Sort(srtArr);

            int tmp = srtArr[0];
            int index = 0;
            int i = 0;
            while (i < srtArr.Length)
            {
                freq[index, 0] = tmp;

                while (tmp == srtArr[i])
                {
                    freq[index, 1]++;
                    i++;

                    if (i > srtArr.Length - 1)
                        break;
                }

                if (i < srtArr.Length)
                {
                    tmp = srtArr[i];
                    index++;
                }

            }

            Array.Clear(srtArr, 0, srtArr.Length);

            for (i = 0; i < srtArr.Length; i++)
                srtArr[i] = freq[i, 1];

            Array.Sort(srtArr);

            if (srtArr[srtArr.Length - 1] == 0 || srtArr[srtArr.Length - 1] == 1)
                return 0;

            float freqtest = (float)freq.Length / freq.Rank;

            for (i = 0; i < freqtest; i++)
            {
                if (freq[i, 1] == srtArr[index])
                    mode.Add(freq[i, 0]);

            }

            return mode.ToArray()[0];
        }

        #endregion

        private static UserSessionData ZeroSession(UserSessionData s)
        {
            s.session_id = UUID.Zero;
            s.agent_id = UUID.Zero;
            s.region_id = UUID.Zero;
            s.last_updated = Util.UnixTimeSinceEpoch();
            s.remote_ip = "";
            s.name_f = "";
            s.name_l = "";
            s.avg_agents_in_view = 0;
            s.min_agents_in_view = 0;
            s.max_agents_in_view = 0;
            s.mode_agents_in_view = 0;
            s.avg_fps = 0;
            s.min_fps = 0;
            s.max_fps = 0;
            s.mode_fps = 0;
            s.a_language = "";
            s.me_use = 0;
            s.meters_traveled = 0;
            s.avg_ping = 0;
            s.min_ping = 0;
            s.max_ping = 0;
            s.mode_ping = 0;
            s.regions_visited = 0;
            s.run_time = 0;
            s.avg_si_fps = 0;
            s.min_si_fps = 0;
            s.max_si_fps = 0;
            s.mode_si_fps = 0;
            s.start_time = 0;
            s.client_version = "";
            s.s_cpu = "";
            s.s_gpu = "";
            s.s_os = "";
            s.s_ram = 0;
            s.d_object_kb = 0;
            s.d_texture_kb = 0;
            s.d_world_kb = 0;
            s.n_in_kb = 0;
            s.n_in_pk = 0;
            s.n_out_kb = 0;
            s.n_out_pk = 0;
            s.f_dropped = 0;
            s.f_failed_resends = 0;
            s.f_invalid = 0;
            s.f_off_circuit = 0;
            s.f_resent = 0;
            s.f_send_packet = 0;
            s._ping = new List<float>();
            s._fps = new List<float>();
            s._si_fps = new List<float>();
            s._agents_in_view = new List<int>();
            return s;
        }
    }
    #region structs

    public class UserSession
    {
        public UUID session_id;
        public UUID region_id;
        public string name_f;
        public string name_l;
        public UserSessionData session_data;
    }

    public struct UserSessionData
    {
        public UUID session_id;
        public UUID agent_id;
        public UUID region_id;
        public float last_updated;
        public string remote_ip;
        public string name_f;
        public string name_l;
        public float avg_agents_in_view;
        public float min_agents_in_view;
        public float max_agents_in_view;
        public float mode_agents_in_view;
        public float avg_fps;
        public float min_fps;
        public float max_fps;
        public float mode_fps;
        public string a_language;
        public float me_use;
        public float meters_traveled;
        public float avg_ping;
        public float min_ping;
        public float max_ping;
        public float mode_ping;
        public int regions_visited;
        public float run_time;
        public float avg_si_fps;
        public float min_si_fps;
        public float max_si_fps;
        public float mode_si_fps;
        public float start_time;
        public string client_version;
        public string s_cpu;
        public string s_gpu;
        public string s_os;
        public int s_ram;
        public float d_object_kb;
        public float d_texture_kb;
        public float d_world_kb;
        public float n_in_kb;
        public int n_in_pk;
        public float n_out_kb;
        public int n_out_pk;
        public int f_dropped;
        public int f_failed_resends;
        public int f_invalid;
        public int f_off_circuit;
        public int f_resent;
        public int f_send_packet;
        public List<float> _ping;
        public List<float> _fps;
        public List<float> _si_fps;
        public List<int> _agents_in_view;
    }

    #endregion

    public class USimStatsData
    {
        private UUID _regionID = UUID.Zero;
        private volatile int _statcounter = 0;
        private volatile float _timeDilation;
        private volatile float _simFps;
        private volatile float _physicsFps;
        private volatile float _agentUpdates;
        private volatile float _rootAgents;
        private volatile float _childAgents;
        private volatile float _totalPrims;
        private volatile float _activePrims;
        private volatile float _totalFrameTime;
        private volatile float _netFrameTime;
        private volatile float _physicsFrameTime;
        private volatile float _otherFrameTime;
        private volatile float _imageFrameTime;
        private volatile float _inPacketsPerSecond;
        private volatile float _outPacketsPerSecond;
        private volatile float _unackedBytes;
        private volatile float _agentFrameTime;
        private volatile float _pendingDownloads;
        private volatile float _pendingUploads;
        private volatile float _activeScripts;
        private volatile float _scriptLinesPerSecond;

        public UUID RegionId => _regionID;
        public int StatsCounter { get => _statcounter;
            set => _statcounter = value;
        }
        public float TimeDilation => _timeDilation;
        public float SimFps => _simFps;
        public float PhysicsFps => _physicsFps;
        public float AgentUpdates => _agentUpdates;
        public float RootAgents => _rootAgents;
        public float ChildAgents => _childAgents;
        public float TotalPrims => _totalPrims;
        public float ActivePrims => _activePrims;
        public float TotalFrameTime => _totalFrameTime;
        public float NetFrameTime => _netFrameTime;
        public float PhysicsFrameTime => _physicsFrameTime;
        public float OtherFrameTime => _otherFrameTime;
        public float ImageFrameTime => _imageFrameTime;
        public float InPacketsPerSecond => _inPacketsPerSecond;
        public float OutPacketsPerSecond => _outPacketsPerSecond;
        public float UnackedBytes => _unackedBytes;
        public float AgentFrameTime => _agentFrameTime;
        public float PendingDownloads => _pendingDownloads;
        public float PendingUploads => _pendingUploads;
        public float ActiveScripts => _activeScripts;
        public float ScriptLinesPerSecond => _scriptLinesPerSecond;

        public USimStatsData(UUID pRegionID)
        {
            _regionID = pRegionID;
        }

        public void ConsumeSimStats(SimStats stats)
        {
            _regionID = stats.RegionUUID;
            _timeDilation = stats.StatsBlock[0].StatValue;
            _simFps = stats.StatsBlock[1].StatValue;
            _physicsFps = stats.StatsBlock[2].StatValue;
            _agentUpdates = stats.StatsBlock[3].StatValue;
            _rootAgents = stats.StatsBlock[4].StatValue;
            _childAgents = stats.StatsBlock[5].StatValue;
            _totalPrims = stats.StatsBlock[6].StatValue;
            _activePrims = stats.StatsBlock[7].StatValue;
            _totalFrameTime = stats.StatsBlock[8].StatValue;
            _netFrameTime = stats.StatsBlock[9].StatValue;
            _physicsFrameTime = stats.StatsBlock[10].StatValue;
            _otherFrameTime = stats.StatsBlock[11].StatValue;
            _imageFrameTime = stats.StatsBlock[12].StatValue;
            _inPacketsPerSecond = stats.StatsBlock[13].StatValue;
            _outPacketsPerSecond = stats.StatsBlock[14].StatValue;
            _unackedBytes = stats.StatsBlock[15].StatValue;
            _agentFrameTime = stats.StatsBlock[16].StatValue;
            _pendingDownloads = stats.StatsBlock[17].StatValue;
            _pendingUploads = stats.StatsBlock[18].StatValue;
            _activeScripts = stats.StatsBlock[19].StatValue;
            _scriptLinesPerSecond = stats.ExtraStatsBlock[0].StatValue;
        }
    }
}