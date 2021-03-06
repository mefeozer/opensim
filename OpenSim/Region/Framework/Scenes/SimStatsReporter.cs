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
using System.Threading;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.Framework.Scenes
{
    /// <summary>
    /// Collect statistics from the scene to send to the client and for access by other monitoring tools.
    /// </summary>
    /// <remarks>
    /// FIXME: This should be a monitoring region module
    /// </remarks>
    public class SimStatsReporter
    {
        private static readonly log4net.ILog _log
            = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string LastReportedObjectUpdateStatName = "LastReportedObjectUpdates";
        public const string SlowFramesStatName = "SlowFrames";

        public delegate void SendStatResult(SimStats stats);

        public delegate void YourStatsAreWrong();

        public event SendStatResult OnSendStatsResult;

        public event YourStatsAreWrong OnStatsIncorrect;

        private SendStatResult handlerSendStatResult;

        private YourStatsAreWrong handlerStatsIncorrect;

        // Determines the size of the array that is used to collect StatBlocks
        // for sending viewer compatible stats must be conform with sb array filling below
        private const int _statisticViewerArraySize = 38;
        // size of LastReportedSimFPS with extra stats.
        private const int _statisticExtraArraySize = (int)(Stats.SimExtraCountEnd - Stats.SimExtraCountStart);

        /// <summary>
        /// These are the IDs of stats sent in the StatsPacket to the viewer.
        /// </summary>
        /// <remarks>
        /// Some of these are not relevant to OpenSimulator since it is architected differently to other simulators
        /// (e.g. script instructions aren't executed as part of the frame loop so 'script time' is tricky).
        /// </remarks>
        public enum Stats : uint
        {
// viewers defined IDs
            TimeDilation = 0,
            SimFPS = 1,
            PhysicsFPS = 2,
            AgentUpdates = 3,
            FrameMS = 4,
            NetMS = 5,
            OtherMS = 6,
            PhysicsMS = 7,
            AgentMS = 8,
            ImageMS = 9,
            ScriptMS = 10,
            TotalPrim = 11,
            ActivePrim = 12,
            Agents = 13,
            ChildAgents = 14,
            ActiveScripts = 15,
            LSLScriptLinesPerSecond = 16, // viewers don't like this anymore
            InPacketsPerSecond = 17,
            OutPacketsPerSecond = 18,
            PendingDownloads = 19,
            PendingUploads = 20,
            VirtualSizeKb = 21,
            ResidentSizeKb = 22,
            PendingLocalUploads = 23,
            UnAckedBytes = 24,
            PhysicsPinnedTasks = 25,
            PhysicsLodTasks = 26,
            SimPhysicsStepMs = 27,
            SimPhysicsShapeMs = 28,
            SimPhysicsOtherMs = 29,
            SimPhysicsMemory = 30,
            ScriptEps = 31,
            SimSpareMs = 32,
            SimSleepMs = 33,
            SimIoPumpTime = 34,
            SimPCTSscriptsRun = 35,
            SimRegionIdle = 36, // dataserver only
            SimRegionIdlePossible  = 37, // dataserver only
            SimAIStepTimeMS = 38,
            SimSkippedSillouet_PS  = 39,
            SimSkippedCharsPerC  = 40,

// extra stats IDs irrelevant, just far from viewer defined ones
            SimExtraCountStart = 1000,

            internalLSLScriptLinesPerSecond = 1000,
            FrameDilation2 = 1001,
            UsersLoggingIn = 1002,
            TotalGeoPrim = 1003,
            TotalMesh = 1004,
            ThreadCount = 1005,

            SimExtraCountEnd = 1006
        }

        /// <summary>
        /// This is for llGetRegionFPS
        /// </summary>
        public float LastReportedSimFPS => lastReportedSimFPS;

        /// <summary>
        /// Number of object updates performed in the last stats cycle
        /// </summary>
        /// <remarks>
        /// This isn't sent out to the client but it is very useful data to detect whether viewers are being sent a
        /// large number of object updates.
        /// </remarks>
        public float LastReportedObjectUpdates { get; private set; }

        public float[] LastReportedSimStats => lastReportedSimStats;

        /// <summary>
        /// Number of frames that have taken longer to process than Scene.MIN_FRAME_TIME
        /// </summary>
        public Stat SlowFramesStat { get; }

        /// <summary>
        /// The threshold at which we log a slow frame.
        /// </summary>
        public int SlowFramesStatReportThreshold { get; }

        /// <summary>
        /// Extra sim statistics that are used by monitors but not sent to the client.
        /// </summary>
        /// <value>
        /// The keys are the stat names.
        /// </value>
        private readonly Dictionary<string, float> _lastReportedExtraSimStats = new Dictionary<string, float>();

        // Sending a stats update every 3 seconds-
        private int _statsUpdatesEveryMS = 3000;
        private double _lastUpdateTS;
        private double _prevFrameStatsTS;
        private double _FrameStatsTS;
        private float _timeDilation;
        private int _fps;

        private readonly object _statsLock = new object();
        private readonly object _statsFrameLock = new object();

        /// <summary>
        /// Parameter to adjust reported scene fps
        /// </summary>
        /// <remarks>
        /// The close we have to a frame rate as expected by viewers, users and scripts
        /// is heartbeat rate.
        /// heartbeat rate default value is very diferent from the expected one
        /// and can be changed from region to region acording to its specific simulation needs
        /// since this creates incompatibility with expected values,
        /// this scale factor can be used to normalize values to a Virtual FPS.
        /// original decision was to use a value of 55fps for all opensim
        /// corresponding, with default heartbeat rate, to a value of 5.
        /// </remarks>
        private readonly float _statisticsFPSfactor = 5.0f;
        private readonly float _targetFrameTime = 0.1f;
        // saved last reported value so there is something available for llGetRegionFPS
        private float lastReportedSimFPS;
        private readonly float[] lastReportedSimStats = new float[_statisticExtraArraySize + _statisticViewerArraySize];
        private float _pfps;

        /// <summary>
        /// Number of agent updates requested in this stats cycle
        /// </summary>
        private int _agentUpdates;

        /// <summary>
        /// Number of object updates requested in this stats cycle
        /// </summary>
        private int _objectUpdates;

        private float _frameMS;

        private float _netMS;
        private float _agentMS;
        private float _physicsMS;
        private float _imageMS;
        private float _otherMS;
        private float _sleeptimeMS;
        private float _scriptTimeMS;

        private int _rootAgents;
        private int _childAgents;
        private int _numPrim;
        private int _numGeoPrim;
        private int _numMesh;
        private int _inPacketsPerSecond;
        private int _outPacketsPerSecond;
        private int _activePrim;
        private int _unAckedBytes;
        private int _pendingDownloads;
        private readonly int _pendingUploads = 0;  // FIXME: Not currently filled in
        private int _activeScripts;
        private int _scriptLinesPerSecond;
        private int _scriptEventsPerSecond;

        private readonly int _objectCapacity = 45000;

         // The current number of users attempting to login to the region
        private int _usersLoggingIn;

        // The last reported value of threads from the SmartThreadPool inside of
        // XEngine
        private int _inUseThreads;

        private readonly Scene _scene;

        private readonly RegionInfo ReportingRegion;

        private readonly System.Timers.Timer _report = new System.Timers.Timer();

        private IEstateModule estateModule;

         public SimStatsReporter(Scene scene)
        {
            _scene = scene;

            ReportingRegion = scene.RegionInfo;

            if(scene.Normalized55FPS)
                _statisticsFPSfactor = 55.0f * _scene.FrameTime;
            else
                _statisticsFPSfactor = 1.0f;

            _targetFrameTime = 1000.0f * _scene.FrameTime /  _statisticsFPSfactor;

            _objectCapacity = scene.RegionInfo.ObjectCapacity;
            _report.AutoReset = true;
            _report.Interval = _statsUpdatesEveryMS;
            _report.Elapsed += TriggerStatsHeartbeat;
            _report.Enabled = true;

            _lastUpdateTS = Util.GetTimeStampMS();
            _FrameStatsTS = _lastUpdateTS;
            _prevFrameStatsTS = _lastUpdateTS;

            if (StatsManager.SimExtraStats != null)
                OnSendStatsResult += StatsManager.SimExtraStats.ReceiveClassicSimStatsPacket;

            /// At the moment, we'll only report if a frame is over 120% of target, since commonly frames are a bit
            /// longer than ideal (which in itself is a concern).
            SlowFramesStatReportThreshold = (int)Math.Ceiling(_scene.FrameTime * 1000 * 1.2);

            SlowFramesStat
                = new Stat(
                    "SlowFrames",
                    "Slow Frames",
                    "Number of frames where frame time has been significantly longer than the desired frame time.",
                    " frames",
                    "scene",
                    _scene.Name,
                    StatType.Push,
                    null,
                    StatVerbosity.Info);

            StatsManager.RegisterStat(SlowFramesStat);
        }


        public void Close()
        {
            _report.Elapsed -= TriggerStatsHeartbeat;
            _report.Close();
        }

        /// <summary>
        /// Sets the number of milliseconds between stat updates.
        /// </summary>
        /// <param name='ms'></param>
        public void SetUpdateMS(int ms)
        {
            _statsUpdatesEveryMS = ms;
            _report.Interval = _statsUpdatesEveryMS;
        }

        private void TriggerStatsHeartbeat(object sender, EventArgs args)
        {
            try
            {
                statsHeartBeat(sender, args);
            }
            catch (Exception e)
            {
                _log.Warn(string.Format(
                    "[SIM STATS REPORTER] Update for {0} failed with exception ",
                    _scene.RegionInfo.RegionName), e);
            }
        }

        private void statsHeartBeat(object sender, EventArgs e)
        {
              if (!_scene.Active)
                return;

            // dont do it if if still been done

            if(Monitor.TryEnter(_statsLock))
            {
                // _log.Debug("Firing Stats Heart Beat");

                SimStatsPacket.StatBlock[] sb = new SimStatsPacket.StatBlock[_statisticViewerArraySize];
                SimStatsPacket.StatBlock[] sbex = new SimStatsPacket.StatBlock[_statisticExtraArraySize];
                SimStatsPacket.RegionBlock rb = new SimStatsPacket.RegionBlock();
                uint regionFlags = 0;

                try
                {
                    if (estateModule == null)
                        estateModule = _scene.RequestModuleInterface<IEstateModule>();
                    regionFlags = estateModule != null ? estateModule.GetRegionFlags() : 0;
                }
                catch (Exception)
                {
                    // leave region flags at 0
                }

#region various statistic googly moogly
                double timeTmp = _lastUpdateTS;
                _lastUpdateTS = Util.GetTimeStampMS();
                float updateElapsed = (float)((_lastUpdateTS - timeTmp)/1000.0);

                // factor to consider updates integration time
                float updateTimeFactor = 1.0f / updateElapsed;


                // scene frame stats
                float reportedFPS;
                float physfps;
                float timeDilation;
                float agentMS;
                float physicsMS;
                float otherMS;
                float sleeptime;
                float scriptTimeMS;
                float totalFrameTime;

                float invFrameElapsed;

                // get a copy under lock and reset
                lock(_statsFrameLock)
                {
                    timeDilation   = _timeDilation;
                    reportedFPS    = _fps;
                    physfps        = _pfps;
                    agentMS        = _agentMS;
                    physicsMS      = _physicsMS;
                    otherMS        = _otherMS;
                    sleeptime      = _sleeptimeMS;
                    scriptTimeMS   = _scriptTimeMS;
                    totalFrameTime = _frameMS;
                    // still not inv
                    invFrameElapsed = (float)((_FrameStatsTS - _prevFrameStatsTS) / 1000.0);

                    ResetFrameStats();
                }

                if (invFrameElapsed / updateElapsed < 0.8)
                   // scene is in trouble, its account of time is most likely wrong
                   // can even be in stall
                   invFrameElapsed = updateTimeFactor;
                else
                    invFrameElapsed = 1.0f / invFrameElapsed;

                float perframefactor;
                if (reportedFPS <= 0)
                {
                   reportedFPS = 0.0f;
                   physfps = 0.0f;
                   perframefactor = 1.0f;
                   timeDilation = 0.0f;
                }
                else
                {
                   timeDilation /= reportedFPS;
                   reportedFPS *=  _statisticsFPSfactor;
                   perframefactor = 1.0f / (float)reportedFPS;
                   reportedFPS *= invFrameElapsed;
                   physfps *= invFrameElapsed  * _statisticsFPSfactor;
                }

                // some engines track frame time with error related to the simulation step size
                if(physfps > reportedFPS)
                    physfps = reportedFPS;

                // save the reported value so there is something available for llGetRegionFPS
                lastReportedSimFPS = reportedFPS;

                // scale frame stats

                totalFrameTime *= perframefactor;
                sleeptime      *= perframefactor;
                otherMS        *= perframefactor;
                physicsMS      *= perframefactor;
                agentMS        *= perframefactor;
                scriptTimeMS   *= perframefactor;

                // estimate spare time
                float sparetime;
                sparetime      = _targetFrameTime - (physicsMS + agentMS + otherMS);

                if (sparetime < 0)
                    sparetime = 0;
                 else if (sparetime > totalFrameTime)
                        sparetime = totalFrameTime;

#endregion

                _rootAgents = _scene.SceneGraph.GetRootAgentCount();
                _childAgents = _scene.SceneGraph.GetChildAgentCount();
                _numPrim = _scene.SceneGraph.GetTotalObjectsCount();
                _numGeoPrim = _scene.SceneGraph.GetTotalPrimObjectsCount();
                _numMesh = _scene.SceneGraph.GetTotalMeshObjectsCount();
                _activePrim = _scene.SceneGraph.GetActiveObjectsCount();
                _activeScripts = _scene.SceneGraph.GetActiveScriptsCount();
                _scriptLinesPerSecond = _scene.SceneGraph.GetScriptLPS();

                 // FIXME: Checking for stat sanity is a complex approach.  What we really need to do is fix the code
                // so that stat numbers are always consistent.
                CheckStatSanity();

                for (int i = 0; i < _statisticViewerArraySize; i++)
                {
                    sb[i] = new SimStatsPacket.StatBlock();
                }

                sb[0].StatID = (uint) Stats.TimeDilation;
                sb[0].StatValue = float.IsNaN(timeDilation) ? 0.0f : (float)Math.Round(timeDilation,3);

                sb[1].StatID = (uint) Stats.SimFPS;
                sb[1].StatValue = (float)Math.Round(reportedFPS,1);;

                sb[2].StatID = (uint) Stats.PhysicsFPS;
                sb[2].StatValue =  (float)Math.Round(physfps,1);

                sb[3].StatID = (uint) Stats.AgentUpdates;
                sb[3].StatValue = _agentUpdates * updateTimeFactor;

                sb[4].StatID = (uint) Stats.Agents;
                sb[4].StatValue = _rootAgents;

                sb[5].StatID = (uint) Stats.ChildAgents;
                sb[5].StatValue = _childAgents;

                sb[6].StatID = (uint) Stats.TotalPrim;
                sb[6].StatValue = _numPrim;

                sb[7].StatID = (uint) Stats.ActivePrim;
                sb[7].StatValue = _activePrim;

                sb[8].StatID = (uint)Stats.FrameMS;
                sb[8].StatValue = totalFrameTime;

                sb[9].StatID = (uint)Stats.NetMS;
                sb[9].StatValue = _netMS * perframefactor;

                sb[10].StatID = (uint)Stats.PhysicsMS;
                sb[10].StatValue = physicsMS;

                sb[11].StatID = (uint)Stats.ImageMS ;
                sb[11].StatValue = _imageMS * perframefactor;

                sb[12].StatID = (uint)Stats.OtherMS;
                sb[12].StatValue = otherMS;

                sb[13].StatID = (uint)Stats.InPacketsPerSecond;
                sb[13].StatValue = (float)Math.Round(_inPacketsPerSecond * updateTimeFactor);

                sb[14].StatID = (uint)Stats.OutPacketsPerSecond;
                sb[14].StatValue = (float)Math.Round(_outPacketsPerSecond * updateTimeFactor);

                sb[15].StatID = (uint)Stats.UnAckedBytes;
                sb[15].StatValue = _unAckedBytes;

                sb[16].StatID = (uint)Stats.AgentMS;
                sb[16].StatValue = agentMS;

                sb[17].StatID = (uint)Stats.PendingDownloads;
                sb[17].StatValue = _pendingDownloads;

                sb[18].StatID = (uint)Stats.PendingUploads;
                sb[18].StatValue = _pendingUploads;

                sb[19].StatID = (uint)Stats.ActiveScripts;
                sb[19].StatValue = _activeScripts;

                sb[20].StatID = (uint)Stats.SimSleepMs;
                sb[20].StatValue = sleeptime;

                sb[21].StatID = (uint)Stats.SimSpareMs;
                sb[21].StatValue = sparetime;

                //  this should came from phys engine
                sb[22].StatID = (uint)Stats.SimPhysicsStepMs;
                sb[22].StatValue = 20;

                // send the ones we dont have as zeros, to clean viewers state
                // specially arriving from regions with wrond IDs in use

                sb[23].StatID = (uint)Stats.VirtualSizeKb;
                sb[23].StatValue = 0;

                sb[24].StatID = (uint)Stats.ResidentSizeKb;
                sb[24].StatValue = 0;

                sb[25].StatID = (uint)Stats.PendingLocalUploads;
                sb[25].StatValue = 0;

                sb[26].StatID = (uint)Stats.PhysicsPinnedTasks;
                sb[26].StatValue = 0;

                sb[27].StatID = (uint)Stats.PhysicsLodTasks;
                sb[27].StatValue = 0;

                sb[28].StatID = (uint)Stats.ScriptEps; // we actually have this, but not messing array order AGAIN
                sb[28].StatValue = (float)Math.Round(_scriptEventsPerSecond * updateTimeFactor);

                sb[29].StatID = (uint)Stats.SimAIStepTimeMS;
                sb[29].StatValue = 0;

                sb[30].StatID = (uint)Stats.SimIoPumpTime;
                sb[30].StatValue = 0;

                sb[31].StatID = (uint)Stats.SimPCTSscriptsRun;
                sb[31].StatValue = 0;

                sb[32].StatID = (uint)Stats.SimRegionIdle;
                sb[32].StatValue = 0;

                sb[33].StatID = (uint)Stats.SimRegionIdlePossible;
                sb[33].StatValue = 0;

                sb[34].StatID = (uint)Stats.SimSkippedSillouet_PS;
                sb[34].StatValue = 0;

                sb[35].StatID = (uint)Stats.SimSkippedCharsPerC;
                sb[35].StatValue = 0;

                sb[36].StatID = (uint)Stats.SimPhysicsMemory;
                sb[36].StatValue = 0;

                sb[37].StatID = (uint)Stats.ScriptMS;
                sb[37].StatValue = scriptTimeMS;

                for (int i = 0; i < _statisticViewerArraySize; i++)
                {
                    lastReportedSimStats[i] = sb[i].StatValue;
                }


                // add extra stats for internal use

                for (int i = 0; i < _statisticExtraArraySize; i++)
                {
                    sbex[i] = new SimStatsPacket.StatBlock();
                }

                sbex[0].StatID = (uint)Stats.LSLScriptLinesPerSecond;
                sbex[0].StatValue = _scriptLinesPerSecond * updateTimeFactor;
                lastReportedSimStats[38] = _scriptLinesPerSecond * updateTimeFactor;

                sbex[1].StatID = (uint)Stats.FrameDilation2;
                sbex[1].StatValue = float.IsNaN(timeDilation) ? 0.1f : timeDilation;
                lastReportedSimStats[39] = float.IsNaN(timeDilation) ? 0.1f : timeDilation;

                sbex[2].StatID = (uint)Stats.UsersLoggingIn;
                sbex[2].StatValue = _usersLoggingIn;
                lastReportedSimStats[40] = _usersLoggingIn;

                sbex[3].StatID = (uint)Stats.TotalGeoPrim;
                sbex[3].StatValue = _numGeoPrim;
                lastReportedSimStats[41] = _numGeoPrim;

                sbex[4].StatID = (uint)Stats.TotalMesh;
                sbex[4].StatValue = _numMesh;
                lastReportedSimStats[42] = _numMesh;

                sbex[5].StatID = (uint)Stats.ThreadCount;
                sbex[5].StatValue = _inUseThreads;
                lastReportedSimStats[43] = _inUseThreads;

                SimStats simStats
                    = new SimStats(
                        ReportingRegion.RegionLocX, ReportingRegion.RegionLocY, regionFlags, (uint)_objectCapacity,
                        rb, sb, sbex, _scene.RegionInfo.originRegionID);

                 handlerSendStatResult = OnSendStatsResult;
                if (handlerSendStatResult != null)
                {
                    handlerSendStatResult(simStats);
                }

                // Extra statistics that aren't currently sent to clients
                if (_scene.PhysicsScene != null)
                {
                    lock (_lastReportedExtraSimStats)
                    {
                        _lastReportedExtraSimStats[LastReportedObjectUpdateStatName] = _objectUpdates * updateTimeFactor;
                        _lastReportedExtraSimStats[SlowFramesStat.ShortName] = (float)SlowFramesStat.Value;

                        Dictionary<string, float> physicsStats = _scene.PhysicsScene.GetStats();

                        if (physicsStats != null)
                        {
                            foreach (KeyValuePair<string, float> tuple in physicsStats)
                            {
                                // FIXME: An extremely dirty hack to divide MS stats per frame rather than per second
                                // Need to change things so that stats source can indicate whether they are per second or
                                // per frame.
                                if (tuple.Key.EndsWith("MS"))
                                    _lastReportedExtraSimStats[tuple.Key] = tuple.Value * perframefactor;
                                else
                                    _lastReportedExtraSimStats[tuple.Key] = tuple.Value * updateTimeFactor;
                            }
                        }
                    }
                }

//                LastReportedObjectUpdates = _objectUpdates / _statsUpdateFactor;
                ResetValues();
                Monitor.Exit(_statsLock);
            }
        }

        private void ResetValues()
        {
            _agentUpdates = 0;
            _objectUpdates = 0;
            _unAckedBytes = 0;
            _scriptEventsPerSecond = 0;

            _netMS = 0;
            _imageMS = 0;
        }


        internal void CheckStatSanity()
        {
            if (_rootAgents < 0 || _childAgents < 0)
            {
                handlerStatsIncorrect = OnStatsIncorrect;
                if (handlerStatsIncorrect != null)
                {
                    handlerStatsIncorrect();
                }
            }
            if (_rootAgents == 0 && _childAgents == 0)
            {
                _unAckedBytes = 0;
            }
        }

        # region methods called from Scene

        public void AddFrameStats(float _timeDilation, float _physicsFPS, float _agentMS,
                             float _physicsMS, float _otherMS , float _sleepMS,
                             float _frameMS, float _scriptTimeMS)
        {
            lock(_statsFrameLock)
            {
                _fps++;
                _timeDilation += _timeDilation;
                _pfps         += _physicsFPS;
                _agentMS      += _agentMS;
                _physicsMS    += _physicsMS;
                _otherMS      += _otherMS;
                _sleeptimeMS  += _sleepMS;
                _frameMS      += _frameMS;
                _scriptTimeMS += _scriptTimeMS;

                if (_frameMS > SlowFramesStatReportThreshold)
                    SlowFramesStat.Value++;

                _FrameStatsTS = Util.GetTimeStampMS();
            }
        }

        private void ResetFrameStats()
        {
            _fps          = 0;
            _timeDilation = 0.0f;
            _pfps         = 0.0f;
            _agentMS      = 0.0f;
            _physicsMS    = 0.0f;
            _otherMS      = 0.0f;
            _sleeptimeMS  = 0.0f;
            _frameMS      = 0.0f;
            _scriptTimeMS = 0.0f;

            _prevFrameStatsTS = _FrameStatsTS;
        }

        public void AddObjectUpdates(int numUpdates)
        {
            _objectUpdates += numUpdates;
        }

        public void AddAgentUpdates(int numUpdates)
        {
            _agentUpdates += numUpdates;
        }

        public void AddInPackets(int numPackets)
        {
            _inPacketsPerSecond = numPackets;
        }

        public void AddOutPackets(int numPackets)
        {
            _outPacketsPerSecond = numPackets;
        }

        public void AddunAckedBytes(int numBytes)
        {
            _unAckedBytes += numBytes;
            if (_unAckedBytes < 0) _unAckedBytes = 0;
        }


        public void addNetMS(float ms)
        {
            _netMS += ms;
        }

        public void addImageMS(float ms)
        {
            _imageMS += ms;
        }

        public void AddPendingDownloads(int count)
        {
            _pendingDownloads += count;

            if (_pendingDownloads < 0)
                _pendingDownloads = 0;

            //_log.InfoFormat("[stats]: Adding {0} to pending downloads to make {1}", count, _pendingDownloads);
        }

        public void addScriptEvents(int count)
        {
            _scriptEventsPerSecond += count;
        }

        public void AddPacketsStats(int inPackets, int outPackets, int unAckedBytes)
        {
            AddInPackets(inPackets);
            AddOutPackets(outPackets);
            AddunAckedBytes(unAckedBytes);
        }

        public void UpdateUsersLoggingIn(bool isLoggingIn)
        {
            // Determine whether the user has started logging in or has completed
            // logging into the region
            if (isLoggingIn)
            {
                // The user is starting to login to the region so increment the
                // number of users attempting to login to the region
                _usersLoggingIn++;
            }
            else
            {
                // The user has finished logging into the region so decrement the
                // number of users logging into the region
                _usersLoggingIn--;
            }
        }

        public void SetThreadCount(int inUseThreads)
        {
            // Save the new number of threads to our member variable to send to
            // the extra stats collector
            _inUseThreads = inUseThreads;
        }

        #endregion

        public Dictionary<string, float> GetExtraSimStats()
        {
            lock (_lastReportedExtraSimStats)
                return new Dictionary<string, float>(_lastReportedExtraSimStats);
        }
    }
}
