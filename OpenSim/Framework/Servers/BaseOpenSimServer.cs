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
using System.Reflection;
using System.Text;
using System.Threading;
using System.Timers;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using log4net;
using OpenMetaverse;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers.HttpServer;
using Timer = System.Timers.Timer;
using Nini.Config;

namespace OpenSim.Framework.Servers
{
    /// <summary>
    /// Common base for the main OpenSimServers (user, grid, inventory, region, etc)
    /// </summary>
    public abstract class BaseOpenSimServer : ServerBase
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Used by tests to suppress Environment.Exit(0) so that post-run operations are possible.
        /// </summary>
        public bool SuppressExit { get; set; }

        /// <summary>
        /// This will control a periodic log printout of the current 'show stats' (if they are active) for this
        /// server.
        /// </summary>

        private int _periodDiagnosticTimerMS = 60 * 60 * 1000;
        private readonly Timer _periodicDiagnosticsTimer = new Timer(60 * 60 * 1000);

        /// <summary>
        /// Random uuid for private data
        /// </summary>
        protected string _osSecret = string.Empty;

        protected BaseHttpServer _httpServer;
        public BaseHttpServer HttpServer => _httpServer;

        public BaseOpenSimServer() : base()
        {
            // Random uuid for private data
            _osSecret = UUID.Random().ToString();
        }

        private static bool _NoVerifyCertChain = false;
        private static bool _NoVerifyCertHostname = false;

        public static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            if (_NoVerifyCertChain)
                sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateChainErrors;
 
            if (_NoVerifyCertHostname)
                sslPolicyErrors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;

            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            return false;
        }
        /// <summary>
        /// Must be overriden by child classes for their own server specific startup behaviour.
        /// </summary>
        protected virtual void StartupSpecific()
        {
            StatsManager.SimExtraStats = new SimExtraStatsCollector();
            RegisterCommonCommands();
            RegisterCommonComponents(Config);

            IConfig startupConfig = Config.Configs["Startup"];

            _NoVerifyCertChain = startupConfig.GetBoolean("NoVerifyCertChain", _NoVerifyCertChain);
            _NoVerifyCertHostname = startupConfig.GetBoolean("NoVerifyCertHostname", _NoVerifyCertHostname);
            ServicePointManager.ServerCertificateValidationCallback = ValidateServerCertificate;

            int logShowStatsSeconds = startupConfig.GetInt("LogShowStatsSeconds", _periodDiagnosticTimerMS / 1000);
            _periodDiagnosticTimerMS = logShowStatsSeconds * 1000;
            _periodicDiagnosticsTimer.Elapsed += new ElapsedEventHandler(LogDiagnostics);
            if (_periodDiagnosticTimerMS != 0)
            {
                _periodicDiagnosticsTimer.Interval = _periodDiagnosticTimerMS;
                _periodicDiagnosticsTimer.Enabled = true;
            }
        }

        protected override void ShutdownSpecific()
        {
            Watchdog.Enabled = false;
            base.ShutdownSpecific();
            
            MainServer.Stop();

            Thread.Sleep(500);
            Util.StopThreadPool();
            WorkManager.Stop();

            RemovePIDFile();

            _log.Info("[SHUTDOWN]: Shutdown processing on main thread complete.  Exiting...");

           if (!SuppressExit)
                Environment.Exit(0);
        }

        /// <summary>
        /// Provides a list of help topics that are available.  Overriding classes should append their topics to the
        /// information returned when the base method is called.
        /// </summary>
        ///
        /// <returns>
        /// A list of strings that represent different help topics on which more information is available
        /// </returns>
        protected virtual List<string> GetHelpTopics() { return new List<string>(); }

        /// <summary>
        /// Print statistics to the logfile, if they are active
        /// </summary>
        protected void LogDiagnostics(object source, ElapsedEventArgs e)
        {
            StringBuilder sb = new StringBuilder("DIAGNOSTICS\n\n");
            sb.Append(GetUptimeReport());
            sb.Append(StatsManager.SimExtraStats.Report());
            sb.Append(Environment.NewLine);
            sb.Append(GetThreadsReport());

            _log.Debug(sb);
        }

        /// <summary>
        /// Performs initialisation of the scene, such as loading configuration from disk.
        /// </summary>
        public virtual void Startup()
        {
            _log.Info("[STARTUP]: Beginning startup processing");

            _log.Info("[STARTUP]: version: " + _version + Environment.NewLine);
            // clr version potentially is more confusing than helpful, since it doesn't tell us if we're running under Mono/MS .NET and
            // the clr version number doesn't match the project version number under Mono.
            //_log.Info("[STARTUP]: Virtual machine runtime version: " + Environment.Version + Environment.NewLine);
            _log.InfoFormat(
                "[STARTUP]: Operating system version: {0}, .NET platform {1}, {2}-bit\n",
                Environment.OSVersion, Environment.OSVersion.Platform, Environment.Is64BitProcess ? "64" : "32");

            // next code can be changed on .net 4.7.x
            if(Util.IsWindows())
                _log.InfoFormat("[STARTUP]: Processor Architecture: {0}({1})",
                    System.Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE", EnvironmentVariableTarget.Machine),
                    BitConverter.IsLittleEndian ?"le":"be");

            // on other platforms we need to wait for .net4.7.1
            try
            {
                StartupSpecific();
            }
            catch(Exception e)
            {
                _log.Fatal("Fatal error: " + e.ToString());
                Environment.Exit(1);
            }

            //TimeSpan timeTaken = DateTime.Now - _startuptime;

//            MainConsole.Instance.OutputFormat(
//                "PLEASE WAIT FOR LOGINS TO BE ENABLED ON REGIONS ONCE SCRIPTS HAVE STARTED.  Non-script portion of startup took {0}m {1}s.",
//                timeTaken.Minutes, timeTaken.Seconds);
        }

        public string osSecret =>
            // Secret uuid for the simulator
            _osSecret;

        public string StatReport(IOSHttpRequest httpRequest)
        {
            // If we catch a request for "callback", wrap the response in the value for jsonp
            if (httpRequest.Query.ContainsKey("callback"))
            {
                return httpRequest.Query["callback"].ToString() + "(" + StatsManager.SimExtraStats.XReport((DateTime.Now - _startuptime).ToString() , _version) + ");";
            }
            else
            {
                return StatsManager.SimExtraStats.XReport((DateTime.Now - _startuptime).ToString() , _version);
            }
        }
    }
}
