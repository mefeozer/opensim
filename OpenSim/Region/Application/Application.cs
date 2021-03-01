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
using System.Net;
using System.Reflection;
using log4net;
using log4net.Config;
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim
{
    /// <summary>
    /// Starting class for the OpenSimulator Region
    /// </summary>
    public class Application
    {
        /// <summary>
        /// Text Console Logger
        /// </summary>
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Save Crashes in the bin/crashes folder.  Configurable with _crashDir
        /// </summary>
        public static bool _saveCrashDumps = false;

        /// <summary>
        /// Directory to save crash reports to.  Relative to bin/
        /// </summary>
        public static string _crashDir = "crashes";

        /// <summary>
        /// Instance of the OpenSim class.  This could be OpenSim or OpenSimBackground depending on the configuration
        /// </summary>
        protected static OpenSimBase _sim = null;

        //could move our main function into OpenSimMain and kill this class
        public static void Main(string[] args)
        {
            // First line, hook the appdomain to the crash reporter
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(CurrentDomain_UnhandledException);

            Culture.SetCurrentCulture();
            Culture.SetDefaultCurrentCulture();
            SetupServicePointManager();

            // Add the arguments supplied when running the application to the configuration
            ArgvConfigSource configSource = new ArgvConfigSource(args);
            ConfigureLogger(configSource);

            _log.InfoFormat(
                "[OPENSIM MAIN]: System Locale is {0}", System.Threading.Thread.CurrentThread.CurrentCulture);
            if (!Util.IsWindows())
            {
                string monoThreadsPerCpu = Environment.GetEnvironmentVariable("MONO_THREADS_PER_CPU");
                _log.InfoFormat(
                    "[OPENSIM MAIN]: Environment variable MONO_THREADS_PER_CPU is {0}", monoThreadsPerCpu ?? "unset");
            }

            SetThreadCounts();
            LogEnvironmentSupport();

            _log.InfoFormat("Default culture changed to {0}", Culture.GetDefaultCurrentCulture().DisplayName);

            AddConfigSourceAliases(configSource);
            AddConfigSourceSwitches(configSource);

            configSource.AddConfig("StandAlone");
            configSource.AddConfig("Network");

            // Check if we're running in the background or not
            bool background = configSource.Configs["Startup"].GetBoolean("background", false);

            // Check if we're saving crashes
            _saveCrashDumps = configSource.Configs["Startup"].GetBoolean("save_crashes", false);

            // load Crash directory config
            _crashDir = configSource.Configs["Startup"].GetString("crash_dir", _crashDir);

            if (background)
            {
                _sim = new OpenSimBackground(configSource);
                _sim.Startup();
            }
            else
            {
                _sim = new OpenSim(configSource);

                _sim.Startup();

                while (true)
                {
                    try
                    {
                        // Block thread here for input
                        MainConsole.Instance.Prompt();
                    }
                    catch (Exception e)
                    {
                        _log.ErrorFormat("Command error: {0}", e);
                    }
                }
            }
        }

        private static void SetupServicePointManager()
        {
            ServicePointManager.DefaultConnectionLimit = 32;
            ServicePointManager.MaxServicePointIdleTime = 30000;

            try { ServicePointManager.DnsRefreshTimeout = 5000; } catch { }
            ServicePointManager.Expect100Continue = true; // needed now to suport auto redir without writecache
            ServicePointManager.UseNagleAlgorithm = false;
        }

        private static void ConfigureLogger(ArgvConfigSource configSource)
        {
            // Configure Log4Net
            configSource.AddSwitch("Startup", "logconfig");
            string logConfigFile = configSource.Configs["Startup"].GetString("logconfig", string.Empty);
            if (!string.IsNullOrEmpty(logConfigFile))
            {
                XmlConfigurator.Configure(new FileInfo(logConfigFile));
                _log.InfoFormat("[OPENSIM MAIN]: configured log4net using \"{0}\" as configuration file",
                                 logConfigFile);
            }
            else
            {
                XmlConfigurator.Configure();
                _log.Info("[OPENSIM MAIN]: configured log4net using default OpenSim.exe.config");
            }
        }

        private static void LogEnvironmentSupport()
        {
            // Check if the system is compatible with OpenSimulator.
            // Ensures that the minimum system requirements are met
            string supported = string.Empty;
            if (Util.IsEnvironmentSupported(ref supported))
            {
                _log.Info("[OPENSIM MAIN]: Environment is supported by OpenSimulator.");
            }
            else
            {
                _log.Warn("[OPENSIM MAIN]: Environment is not supported by OpenSimulator (" + supported + ")\n");
            }
        }

        private static void AddConfigSourceSwitches(ArgvConfigSource configSource)
        {
            configSource.AddSwitch("Startup", "background");
            configSource.AddSwitch("Startup", "inifile");
            configSource.AddSwitch("Startup", "inimaster");
            configSource.AddSwitch("Startup", "inidirectory");
            configSource.AddSwitch("Startup", "physics");
            configSource.AddSwitch("Startup", "gui");
            configSource.AddSwitch("Startup", "console");
            configSource.AddSwitch("Startup", "save_crashes");
            configSource.AddSwitch("Startup", "crash_dir");
        }

        private static void AddConfigSourceAliases(ArgvConfigSource configSource)
        {
            configSource.Alias.AddAlias("On", true);
            configSource.Alias.AddAlias("Off", false);
            configSource.Alias.AddAlias("True", true);
            configSource.Alias.AddAlias("False", false);
            configSource.Alias.AddAlias("Yes", true);
            configSource.Alias.AddAlias("No", false);
        }

        private static void SetThreadCounts()
        {
            System.Threading.ThreadPool.GetMinThreads(out int currentMinWorkerThreads, out int currentMinIocpThreads);
            _log.InfoFormat(
                "[OPENSIM MAIN]: Runtime gave us {0} min worker threads and {1} min IOCP threads",
                currentMinWorkerThreads, currentMinIocpThreads);

            System.Threading.ThreadPool.GetMaxThreads(out int workerThreads, out int iocpThreads);
            _log.InfoFormat("[OPENSIM MAIN]: Runtime gave us {0} max worker threads and {1} max IOCP threads", workerThreads, iocpThreads);


            // Verify the Threadpool allocates or uses enough worker and IO completion threads
            // .NET 2.0, workerthreads default to 50 *  numcores
            // .NET 3.0, workerthreads defaults to 250 * numcores
            // .NET 4.0, workerthreads are dynamic based on bitness and OS resources
            // Max IO Completion threads are 1000 on all 3 CLRs
            //
            // Mono 2.10.9 to at least Mono 3.1, workerthreads default to 100 * numcores, iocp threads to 4 * numcores
            int workerThreadsMin = 500;
            if (workerThreads < workerThreadsMin)
            {
                workerThreads = workerThreadsMin;
                _log.InfoFormat("[OPENSIM MAIN]: Bumping up max worker threads to {0}", workerThreads);
            }

            int workerThreadsMax = 1000; // may need further adjustment to match other CLR
            if (workerThreads > workerThreadsMax)
            {
                workerThreads = workerThreadsMax;
                _log.InfoFormat("[OPENSIM MAIN]: Limiting max worker threads to {0}", workerThreads);
            }


            int iocpThreadsMin = 1000;
            // Increase the number of IOCP threads available.
            // Mono defaults to a tragically low number (24 on 6-core / 8GB Fedora 17)
            if (iocpThreads < iocpThreadsMin)
            {
                iocpThreads = iocpThreadsMin;
                _log.InfoFormat("[OPENSIM MAIN]: Bumping up max IOCP threads to {0}", iocpThreads);
            }

            int iocpThreadsMax = 2000; // may need further adjustment to match other CLR
            // Make sure we don't overallocate IOCP threads and thrash system resources
            if (iocpThreads > iocpThreadsMax)
            {
                iocpThreads = iocpThreadsMax;
                _log.InfoFormat("[OPENSIM MAIN]: Limiting max IOCP completion threads to {0}", iocpThreads);
            }
            // set the resulting worker and IO completion thread counts back to ThreadPool
            if (System.Threading.ThreadPool.SetMaxThreads(workerThreads, iocpThreads))
            {
                _log.InfoFormat(
                    "[OPENSIM MAIN]: Threadpool set to {0} max worker threads and {1} max IOCP threads",
                    workerThreads, iocpThreads);
            }
            else
            {
                _log.Warn("[OPENSIM MAIN]: Threadpool reconfiguration failed, runtime defaults still in effect.");
            }
        }

        private static bool _IsHandlingException = false; // Make sure we don't go recursive on ourself

        /// <summary>
        /// Global exception handler -- all unhandlet exceptions end up here :)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (_IsHandlingException)
            {
                return;
            }

            _IsHandlingException = true;
            // TODO: Add config option to allow users to turn off error reporting
            // TODO: Post error report (disabled for now)

            string msg = string.Empty;
            msg += Environment.NewLine;
            msg += "APPLICATION EXCEPTION DETECTED: " + e.ToString() + Environment.NewLine;
            msg += Environment.NewLine;

            msg += "Exception: " + e.ExceptionObject.ToString() + Environment.NewLine;
            Exception ex = (Exception) e.ExceptionObject;
            if (ex.InnerException != null)
            {
                msg += "InnerException: " + ex.InnerException.ToString() + Environment.NewLine;
            }

            msg += Environment.NewLine;
            msg += "Application is terminating: " + e.IsTerminating.ToString() + Environment.NewLine;

            _log.ErrorFormat("[APPLICATION]: {0}", msg);

            if (_saveCrashDumps)
            {
                // Log exception to disk
                try
                {
                    if (!Directory.Exists(_crashDir))
                    {
                        Directory.CreateDirectory(_crashDir);
                    }
                    string log = Util.GetUniqueFilename(ex.GetType() + ".txt");
                    using (StreamWriter _crashLog = new StreamWriter(Path.Combine(_crashDir, log)))
                    {
                        _crashLog.WriteLine(msg);
                    }

                    File.Copy("OpenSim.ini", Path.Combine(_crashDir, log + "_OpenSim.ini"), true);
                }
                catch (Exception e2)
                {
                    _log.ErrorFormat("[CRASH LOGGER CRASHED]: {0}", e2);
                }
            }

            _IsHandlingException = false;
        }
    }
}
