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
using System.Linq;

namespace OpenSim.Framework.Monitoring
{
    /// <summary>
    /// Experimental watchdog for memory usage.
    /// </summary>
    public static class MemoryWatchdog
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Is this watchdog active?
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set
            {
//                _log.DebugFormat("[MEMORY WATCHDOG]: Setting MemoryWatchdog.Enabled to {0}", value);

                if (value && !_enabled)
                    UpdateLastRecord(GC.GetTotalMemory(false), Util.EnvironmentTickCount());

                _enabled = value;
            }
        }
        private static bool _enabled;

        /// <summary>
        /// Average heap allocation rate in bytes per millisecond.
        /// </summary>
        public static double AverageHeapAllocationRate
        {
            get { if (_samples.Count > 0) return _samples.Average(); else return 0; }
        }

        /// <summary>
        /// Last heap allocation in bytes
        /// </summary>
        public static double LastHeapAllocationRate
        {
            get { if (_samples.Count > 0) return _samples.Last(); else return 0; }
        }

        /// <summary>
        /// Maximum number of statistical samples.
        /// </summary>
        /// <remarks>
        /// At the moment this corresponds to 1 minute since the sampling rate is every 2.5 seconds as triggered from
        /// the main Watchdog.
        /// </remarks>
        private static readonly int _maxSamples = 24;

        /// <summary>
        /// Time when the watchdog was last updated.
        /// </summary>
        private static int _lastUpdateTick;

        /// <summary>
        /// Memory used at time of last watchdog update.
        /// </summary>
        private static long _lastUpdateMemory;

        /// <summary>
        /// Memory churn rate per millisecond.
        /// </summary>
//        private static double _churnRatePerMillisecond;

        /// <summary>
        /// Historical samples for calculating moving average.
        /// </summary>
        private static readonly Queue<double> _samples = new Queue<double>(_maxSamples);

        public static void Update()
        {
            int now = Util.EnvironmentTickCount();
            long memoryNow = GC.GetTotalMemory(false);
            long memoryDiff = memoryNow - _lastUpdateMemory;

            if (_samples.Count >= _maxSamples)
                    _samples.Dequeue();

            double elapsed = Util.EnvironmentTickCountSubtract(now, _lastUpdateTick);

            // This should never happen since it's not useful for updates to occur with no time elapsed, but
            // protect ourselves from a divide-by-zero just in case.
            if (elapsed == 0)
                return;

            _samples.Enqueue(memoryDiff / (double)elapsed);

            UpdateLastRecord(memoryNow, now);
        }

        private static void UpdateLastRecord(long memoryNow, int timeNow)
        {
            _lastUpdateMemory = memoryNow;
            _lastUpdateTick = timeNow;
        }
    }
}