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
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Threading;

namespace OSHttpServer
{
    /// <summary>
    /// Timeout Manager.   Checks for dead clients.  Clients with open connections that are not doing anything.   Closes sessions opened with keepalive.
    /// </summary>
    public static class ContextTimeoutManager
    {
        /// <summary>
        /// Use a Thread or a Timer to monitor the ugly
        /// </summary>
        private static Thread _internalThread = null;
        private static readonly object _threadLock = new object();
        private static readonly ConcurrentQueue<HttpClientContext> _contexts = new ConcurrentQueue<HttpClientContext>();
        private static readonly ConcurrentQueue<HttpClientContext> _highPrio = new ConcurrentQueue<HttpClientContext>();
        private static readonly ConcurrentQueue<HttpClientContext> _midPrio = new ConcurrentQueue<HttpClientContext>();
        private static readonly ConcurrentQueue<HttpClientContext> _lowPrio = new ConcurrentQueue<HttpClientContext>();
        private static AutoResetEvent _processWaitEven = new AutoResetEvent(false);
        private static bool _shuttingDown;

        private static int _ActiveSendingCount;
        private static double _lastTimeOutCheckTime = 0;
        private static double _lastSendCheckTime = 0;

        const int _maxBandWidth = 10485760; //80Mbps
        const int _maxConcurrenSend = 32;

        static ContextTimeoutManager()
        {
            TimeStampClockPeriod = 1.0 / (double)Stopwatch.Frequency;
            TimeStampClockPeriodMS = 1e3 / (double)Stopwatch.Frequency;
        }

        public static void Start()
        {
            lock (_threadLock)
            {
                if (_internalThread != null)
                    return;

                _lastTimeOutCheckTime = GetTimeStampMS();
                using(ExecutionContext.SuppressFlow())
                    _internalThread = new Thread(ThreadRunProcess);

                _internalThread.Priority = ThreadPriority.Normal;
                _internalThread.IsBackground = true;
                _internalThread.CurrentCulture = new CultureInfo("en-US", false);
                _internalThread.Name = "HttpServerMain";
                _internalThread.Start();
            }
        }

        public static void Stop()
        {
            _shuttingDown = true;
            _processWaitEven.Set();
            //_internalThread.Join();
            //ProcessShutDown();
        }

        private static void ThreadRunProcess()
        {
            while (!_shuttingDown)
            {
                _processWaitEven.WaitOne(500);

                if(_shuttingDown)
                    return;

                double now = GetTimeStamp();
                if(_contexts.Count > 0)
                {
                    ProcessSendQueues(now);

                    if (now - _lastTimeOutCheckTime > 1.0)
                    {
                        ProcessContextTimeouts();
                        _lastTimeOutCheckTime = now;
                    }
                }
                else
                    _lastTimeOutCheckTime = now;
            }
        }

        public static void ProcessShutDown()
        {
            try
            {
                SocketError disconnectError = SocketError.HostDown;
                for (int i = 0; i < _contexts.Count; i++)
                {
                    if (_contexts.TryDequeue(out HttpClientContext context))
                    {
                        try
                        {
                            context.Disconnect(disconnectError);
                        }
                        catch { }
                    }
                }
                _processWaitEven.Dispose();
                _processWaitEven = null;
            }
            catch
            {
                // We can't let this crash.
            }
        }

        public static void ProcessSendQueues(double now)
        {
            int inqueues = _highPrio.Count + _midPrio.Count + _lowPrio.Count;
            if(inqueues == 0)
                return;

            double dt = now - _lastSendCheckTime;
            _lastSendCheckTime = now;

            int totalSending = _ActiveSendingCount;

            int curConcurrentLimit = _maxConcurrenSend - totalSending;
            if(curConcurrentLimit <= 0)
                return;

            if(curConcurrentLimit > inqueues)
                curConcurrentLimit = inqueues;

            if (dt > 0.5)
                dt = 0.5;

            dt /= curConcurrentLimit;
            int curbytesLimit = (int)(_maxBandWidth * dt);
            if(curbytesLimit < 1024)
                curbytesLimit = 1024;

            HttpClientContext ctx;
            int sent;
            while (curConcurrentLimit > 0)
            {
                sent = 0;
                while (_highPrio.TryDequeue(out ctx))
                {
                    if(TrySend(ctx, curbytesLimit))
                        _highPrio.Enqueue(ctx);

                    if (_shuttingDown)
                        return;
                    --curConcurrentLimit;
                    if (++sent == 4)
                        break;
                }

                sent = 0;
                while(_midPrio.TryDequeue(out ctx))
                {
                    if(TrySend(ctx, curbytesLimit))
                        _midPrio.Enqueue(ctx);

                    if (_shuttingDown)
                        return;
                    --curConcurrentLimit;
                    if (++sent >= 2)
                        break;
                }

                if (_lowPrio.TryDequeue(out ctx))
                {
                    --curConcurrentLimit;
                    if(TrySend(ctx, curbytesLimit))
                        _lowPrio.Enqueue(ctx);
                }

                if (_shuttingDown)
                    return;
            }
        }

        private static bool TrySend(HttpClientContext ctx, int bytesLimit)
        {
            if(!ctx.CanSend())
                return false;

            return ctx.TrySendResponse(bytesLimit);
        }

        /// <summary>
        /// Causes the watcher to immediately check the connections. 
        /// </summary>
        public static void ProcessContextTimeouts()
        {
            try
            {
                for (int i = 0; i < _contexts.Count; i++)
                {
                    if (_shuttingDown)
                        return;
                    if (_contexts.TryDequeue(out HttpClientContext context))
                    {
                        if (!ContextTimedOut(context, out SocketError disconnectError))
                            _contexts.Enqueue(context);
                        else if(disconnectError != SocketError.InProgress)
                            context.Disconnect(disconnectError);
                    }
                }
            }
            catch
            {
                // We can't let this crash.
            }
        }

        private static bool ContextTimedOut(HttpClientContext context, out SocketError disconnectError)
        {
            disconnectError = SocketError.InProgress;

            // First our error conditions
            if (context.contextID < 0 || context.StopMonitoring || context.StreamPassedOff)
                return true;

            int nowMS = EnvironmentTickCount();

            // First we check first contact line
            if (!context.FirstRequestLineReceived)
            {
                if (EnvironmentTickCountAdd(context.TimeoutFirstLine, context.LastActivityTimeMS) < nowMS)
                {
                    disconnectError = SocketError.TimedOut;
                    return true;
                }
                return false;
            }

            // First we check first contact request
            if (!context.FullRequestReceived)
            {
                if (EnvironmentTickCountAdd(context.TimeoutRequestReceived, context.LastActivityTimeMS) < nowMS)
                {
                    disconnectError = SocketError.TimedOut;
                    return true;
                }
                return false;
            }

            if (context.TriggerKeepalive)
            {
                context.TriggerKeepalive = false;
                context.MonitorKeepaliveStartMS = nowMS + 500;
                return false;
            }

            if (context.MonitorKeepaliveStartMS != 0)
            {
                if (context.IsClosing)
                {
                    disconnectError = SocketError.Success;
                    return true;
                }

                if (EnvironmentTickCountAdd(context.TimeoutKeepAlive, context.MonitorKeepaliveStartMS) < nowMS)
                {
                    disconnectError = SocketError.TimedOut;
                    context.MonitorKeepaliveStartMS = 0;
                    return true;
                }
            }

            if (EnvironmentTickCountAdd(context.TimeoutMaxIdle, context.LastActivityTimeMS) < nowMS)
            {
                disconnectError = SocketError.TimedOut;
                context.MonitorKeepaliveStartMS = 0;
                return true;
            }
            return false;
        }

        public static void StartMonitoringContext(HttpClientContext context)
        {
            context.LastActivityTimeMS = EnvironmentTickCount();
            _contexts.Enqueue(context);
        }

        public static void EnqueueSend(HttpClientContext context, int priority, bool notThrottled = true)
        {
            switch(priority)
            {
                case 0:
                    _highPrio.Enqueue(context);
                    break;
                case 1:
                    _midPrio.Enqueue(context);
                    break;
                case 2:
                    _lowPrio.Enqueue(context);
                    break;
                default:
                    return;
            }
            if(notThrottled)
                _processWaitEven.Set();
        }

        public static void ContextEnterActiveSend()
        {
            Interlocked.Increment(ref _ActiveSendingCount);
        }

        public static void ContextLeaveActiveSend()
        {
            Interlocked.Decrement(ref _ActiveSendingCount);
        }

        /// <summary>
        /// Environment.TickCount is an int but it counts all 32 bits so it goes positive
        /// and negative every 24.9 days. This trims down TickCount so it doesn't wrap
        /// for the callers. 
        /// This trims it to a 12 day interval so don't let your frame time get too long.
        /// </summary>
        /// <returns></returns>
        public static int EnvironmentTickCount()
        {
            return Environment.TickCount & EnvironmentTickCountMask;
        }
        const int EnvironmentTickCountMask = 0x3fffffff;

        /// <summary>
        /// Environment.TickCount is an int but it counts all 32 bits so it goes positive
        /// and negative every 24.9 days. Subtracts the passed value (previously fetched by
        /// 'EnvironmentTickCount()') and accounts for any wrapping.
        /// </summary>
        /// <param name="newValue"></param>
        /// <param name="prevValue"></param>
        /// <returns>subtraction of passed prevValue from current Environment.TickCount</returns>
        public static int EnvironmentTickCountSubtract(int newValue, int prevValue)
        {
            int diff = newValue - prevValue;
            return diff >= 0 ? diff : diff + EnvironmentTickCountMask + 1;
        }

        /// <summary>
        /// Environment.TickCount is an int but it counts all 32 bits so it goes positive
        /// and negative every 24.9 days. Subtracts the passed value (previously fetched by
        /// 'EnvironmentTickCount()') and accounts for any wrapping.
        /// </summary>
        /// <param name="newValue"></param>
        /// <param name="prevValue"></param>
        /// <returns>subtraction of passed prevValue from current Environment.TickCount</returns>
        public static int EnvironmentTickCountAdd(int newValue, int prevValue)
        {
            int ret = newValue + prevValue;
            return ret >= 0 ? ret : ret + EnvironmentTickCountMask + 1;
        }

        public static double TimeStampClockPeriodMS;
        public static double TimeStampClockPeriod;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static double GetTimeStamp()
        {
            return Stopwatch.GetTimestamp() * TimeStampClockPeriod;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static double GetTimeStampMS()
        {
            return Stopwatch.GetTimestamp() * TimeStampClockPeriodMS;
        }

        // doing math in ticks is usefull to avoid loss of resolution
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static long GetTimeStampTicks()
        {
            return Stopwatch.GetTimestamp();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public static double TimeStampTicksToMS(long ticks)
        {
            return ticks * TimeStampClockPeriodMS;
        }

    }
}
