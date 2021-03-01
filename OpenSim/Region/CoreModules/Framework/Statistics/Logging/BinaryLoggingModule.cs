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
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.Framework.Statistics.Logging
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "BinaryLoggingModule")]
    public class BinaryLoggingModule : INonSharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected bool _collectStats;
        protected Scene _scene = null;

        public string Name => "Binary Statistics Logging Module";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            try
            {
                IConfig statConfig = source.Configs["Statistics.Binary"];
                if (statConfig != null && statConfig.Contains("enabled") && statConfig.GetBoolean("enabled"))
                {
                    if (statConfig.Contains("collect_region_stats"))
                    {
                        if (statConfig.GetBoolean("collect_region_stats"))
                        {
                            _collectStats = true;
                        }
                    }
                    if (statConfig.Contains("region_stats_period_seconds"))
                    {
                        _statLogPeriod = TimeSpan.FromSeconds(statConfig.GetInt("region_stats_period_seconds"));
                    }
                    if (statConfig.Contains("stats_dir"))
                    {
                        _statsDir = statConfig.GetString("stats_dir");
                    }
                }
            }
            catch
            {
                // if it doesn't work, we don't collect anything
            }
        }

        public void AddRegion(Scene scene)
        {
            _scene = scene;
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (_collectStats)
                _scene.StatsReporter.OnSendStatsResult += LogSimStats;
        }

        public void Close()
        {
        }

        public class StatLogger
        {
            public DateTime StartTime;
            public string Path;
            public System.IO.BinaryWriter Log;
        }

        static StatLogger _statLog = null;
        static TimeSpan _statLogPeriod = TimeSpan.FromSeconds(300);
        static string _statsDir = string.Empty;
        static readonly object _statLockObject = new object();

        private void LogSimStats(SimStats stats)
        {
            SimStatsPacket pack = new SimStatsPacket
            {
                Region = new SimStatsPacket.RegionBlock
                {
                    RegionX = stats.RegionX,
                    RegionY = stats.RegionY,
                    RegionFlags = stats.RegionFlags,
                    ObjectCapacity = stats.ObjectCapacity
                },
                //pack.Region = //stats.RegionBlock;
                Stat = stats.StatsBlock
            };
            pack.Header.Reliable = false;

            // note that we are inside the reporter lock when called
            DateTime now = DateTime.Now;

            // hide some time information into the packet
            pack.Header.Sequence = (uint)now.Ticks;

            lock (_statLockObject) // _statLog is shared so make sure there is only executer here
            {
                try
                {
                    if (_statLog == null || now > _statLog.StartTime + _statLogPeriod)
                    {
                        // First log file or time has expired, start writing to a new log file
                        if (_statLog != null && _statLog.Log != null)
                        {
                            _statLog.Log.Close();
                        }
                        _statLog = new StatLogger
                        {
                            StartTime = now,
                            Path = (_statsDir.Length > 0 ? _statsDir + System.IO.Path.DirectorySeparatorChar.ToString() : "")
                                + string.Format("stats-{0}.log", now.ToString("yyyyMMddHHmmss"))
                        };
                        _statLog.Log = new BinaryWriter(File.Open(_statLog.Path, FileMode.Append, FileAccess.Write));
                    }

                    // Write the serialized data to disk
                    if (_statLog != null && _statLog.Log != null)
                        _statLog.Log.Write(pack.ToBytes());
                }
                catch (Exception ex)
                {
                    _log.Error("statistics gathering failed: " + ex.Message, ex);
                    if (_statLog != null && _statLog.Log != null)
                    {
                        _statLog.Log.Close();
                    }
                    _statLog = null;
                }
            }
            return;
        }
    }
}
