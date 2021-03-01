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
using System.Linq;
using System.Reflection;
using System.Timers;
using System.IO;
using System.Collections.Generic;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Timer = System.Timers.Timer;
using Mono.Addins;

namespace OpenSim.Region.CoreModules.World.Region
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RestartModule")]
    public class RestartModule : INonSharedRegionModule, IRestartModule
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Scene _Scene;
        protected Timer _CountdownTimer = null;
        protected DateTime _RestartBegin;
        protected List<int> _Alerts;
        protected string _Message;
        protected UUID _Initiator;
        protected bool _Notice = false;
        protected IDialogModule _DialogModule = null;
        protected string _MarkerPath = string.Empty;
        private int[] _CurrentAlerts = null;
        protected bool _shortCircuitDelays = false;
        protected bool _rebootAll = false;

        public void Initialise(IConfigSource config)
        {
            IConfig restartConfig = config.Configs["RestartModule"];
            if (restartConfig != null)
            {
                _MarkerPath = restartConfig.GetString("MarkerPath", string.Empty);
            }
            IConfig startupConfig = config.Configs["Startup"];
            _shortCircuitDelays = startupConfig.GetBoolean("SkipDelayOnEmptyRegion", false);
            _rebootAll = startupConfig.GetBoolean("InworldRestartShutsDown", false);
        }

        public void AddRegion(Scene scene)
        {
            if (!string.IsNullOrEmpty(_MarkerPath))
                File.Delete(Path.Combine(_MarkerPath,
                        scene.RegionInfo.RegionID.ToString()));

            _Scene = scene;

            scene.RegisterModuleInterface<IRestartModule>(this);
            MainConsole.Instance.Commands.AddCommand("Regions",
                    false, "region restart bluebox",
                    "region restart bluebox <message> <delta seconds>+",
                    "Schedule a region restart",
                    "Schedule a region restart after a given number of seconds.  If one delta is given then the region is restarted in delta seconds time.  A time to restart is sent to users in the region as a dismissable bluebox notice.  If multiple deltas are given then a notice is sent when we reach each delta.",
                    HandleRegionRestart);

            MainConsole.Instance.Commands.AddCommand("Regions",
                    false, "region restart notice",
                    "region restart notice <message> <delta seconds>+",
                    "Schedule a region restart",
                    "Schedule a region restart after a given number of seconds.  If one delta is given then the region is restarted in delta seconds time.  A time to restart is sent to users in the region as a transient notice.  If multiple deltas are given then a notice is sent when we reach each delta.",
                    HandleRegionRestart);

            MainConsole.Instance.Commands.AddCommand("Regions",
                    false, "region restart abort",
                    "region restart abort [<message>]",
                    "Abort a region restart", HandleRegionRestart);
        }

        public void RegionLoaded(Scene scene)
        {
            _DialogModule = _Scene.RequestModuleInterface<IDialogModule>();
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name => "RestartModule";

        public Type ReplaceableInterface => typeof(IRestartModule);

        public TimeSpan TimeUntilRestart => DateTime.Now - _RestartBegin;

        public void ScheduleRestart(UUID initiator, string message, int[] alerts, bool notice)
        {
            if (_CountdownTimer != null)
            {
                _CountdownTimer.Stop();
                _CountdownTimer = null;
            }

            if (alerts == null)
            {
                CreateMarkerFile();
                _Scene.RestartNow();
                return;
            }

            _Message = message;
            _Initiator = initiator;
            _Notice = notice;
            _CurrentAlerts = alerts;
            _Alerts = new List<int>(alerts);
            _Alerts.Sort();
            _Alerts.Reverse();

            if (_Alerts[0] == 0)
            {
                CreateMarkerFile();
                _Scene.RestartNow();
                return;
            }

            int nextInterval = DoOneNotice(true);

            SetTimer(nextInterval);
        }

        public int DoOneNotice(bool sendOut)
        {
            if (_Alerts.Count == 0 || _Alerts[0] == 0)
            {
                CreateMarkerFile();
                _Scene.RestartNow();
                return 0;
            }

            int nextAlert = 0;
            while (_Alerts.Count > 1)
            {
                if (_Alerts[1] == _Alerts[0])
                {
                    _Alerts.RemoveAt(0);
                    continue;
                }
                nextAlert = _Alerts[1];
                break;
            }

            int currentAlert = _Alerts[0];

            _Alerts.RemoveAt(0);

            if (sendOut)
            {
                int minutes = currentAlert / 60;
                string currentAlertString = string.Empty;
                if (minutes > 0)
                {
                    if (minutes == 1)
                        currentAlertString += "1 minute";
                    else
                        currentAlertString += string.Format("{0} minutes", minutes);
                    if (currentAlert % 60 != 0)
                        currentAlertString += " and ";
                }
                if (currentAlert % 60 != 0)
                {
                    int seconds = currentAlert % 60;
                    if (seconds == 1)
                        currentAlertString += "1 second";
                    else
                        currentAlertString += string.Format("{0} seconds", seconds);
                }

                string msg = string.Format(_Message, currentAlertString);

                if (_DialogModule != null && !string.IsNullOrEmpty(msg))
                {
                    if (_Notice)
                        _DialogModule.SendGeneralAlert(msg);
                    else
                        _DialogModule.SendNotificationToUsersInRegion(_Initiator, "System", msg);
                }
            }

            return currentAlert - nextAlert;
        }

        public void SetTimer(int intervalSeconds)
        {
            if (intervalSeconds > 0)
            {
                _CountdownTimer = new Timer
                {
                    AutoReset = false,
                    Interval = intervalSeconds * 1000
                };
                _CountdownTimer.Elapsed += OnTimer;
                _CountdownTimer.Start();
            }
            else if (_CountdownTimer != null)
            {
                _CountdownTimer.Stop();
                _CountdownTimer = null;
            }
            else
            {
                _log.WarnFormat(
                    "[RESTART MODULE]: Tried to set restart timer to {0} in {1}, which is not a valid interval",
                    intervalSeconds, _Scene.Name);
            }
        }

        private void OnTimer(object source, ElapsedEventArgs e)
        {
            int nextInterval = DoOneNotice(true);
            if (_shortCircuitDelays)
            {
                if (CountAgents() == 0)
                {
                    _Scene.RestartNow();
                    return;
                }
            }

            SetTimer(nextInterval);
        }

        public void DelayRestart(int seconds, string message)
        {
            if (_CountdownTimer == null)
                return;

            _CountdownTimer.Stop();
            _CountdownTimer = null;

            _Alerts = new List<int>(_CurrentAlerts);
            _Alerts.Add(seconds);
            _Alerts.Sort();
            _Alerts.Reverse();

            int nextInterval = DoOneNotice(false);

            SetTimer(nextInterval);
        }

        public void AbortRestart(string message)
        {
            if (_CountdownTimer != null)
            {
                _CountdownTimer.Stop();
                _CountdownTimer = null;
                if (_DialogModule != null && !string.IsNullOrEmpty(message))
                    _DialogModule.SendNotificationToUsersInRegion(UUID.Zero, "System", message);
                    //_DialogModule.SendGeneralAlert(message);
            }
            if (!string.IsNullOrEmpty(_MarkerPath))
                File.Delete(Path.Combine(_MarkerPath,
                        _Scene.RegionInfo.RegionID.ToString()));
        }

        private void HandleRegionRestart(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene is Scene))
                return;

            if (MainConsole.Instance.ConsoleScene != _Scene)
                return;

            if (args.Length < 5)
            {
                if (args.Length > 2)
                {
                    if (args[2] == "abort")
                    {
                        string msg = string.Empty;
                        if (args.Length > 3)
                            msg = args[3];

                        AbortRestart(msg);

                        MainConsole.Instance.Output("Region restart aborted");
                        return;
                    }
                }

                MainConsole.Instance.Output("Error: restart region <mode> <name> <delta seconds>+");
                return;
            }

            bool notice = false;
            if (args[2] == "notice")
                notice = true;

            List<int> times = new List<int>();
            for (int i = 4 ; i < args.Length ; i++)
                times.Add(Convert.ToInt32(args[i]));

            MainConsole.Instance.Output(
                "Region {0} scheduled for restart in {1} seconds", _Scene.Name, times.Sum());

            ScheduleRestart(UUID.Zero, args[3], times.ToArray(), notice);
        }

        protected void CreateMarkerFile()
        {
            if (string.IsNullOrEmpty(_MarkerPath))
                return;

            string path = Path.Combine(_MarkerPath, _Scene.RegionInfo.RegionID.ToString());
            try
            {
                string pidstring = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                FileStream fs = File.Create(path);
                System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
                byte[] buf = enc.GetBytes(pidstring);
                fs.Write(buf, 0, buf.Length);
                fs.Close();
            }
            catch (Exception)
            {
            }
        }

        int CountAgents()
        {
            _log.Info("[RESTART MODULE]: Counting affected avatars");
            int agents = 0;

            if (_rebootAll)
            {
                foreach (Scene s in SceneManager.Instance.Scenes)
                {
                    foreach (ScenePresence sp in s.GetScenePresences())
                    {
                        if (!sp.IsChildAgent && !sp.IsNPC)
                            agents++;
                    }
                }
            }
            else
            {
                foreach (ScenePresence sp in _Scene.GetScenePresences())
                {
                    if (!sp.IsChildAgent && !sp.IsNPC)
                        agents++;
                }
            }

            _log.InfoFormat("[RESTART MODULE]: Avatars in region: {0}", agents);

            return agents;
        }
    }
}
