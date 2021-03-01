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

// A pool of jobs or workitems with same method (callback) but diferent argument (as object) to run in main threadpool
// can have up to _concurrency number of execution threads
// it will hold each thread up to _threadsHoldtime ms waiting for more work, before releasing it back to the pool.

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using log4net;

namespace OpenSim.Framework
{
    public class ObjectJobEngine : IDisposable
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object _mainLock = new object();
        private readonly string _name;
        private readonly int _threadsHoldtime;
        private readonly int _concurrency = 1;

        private BlockingCollection<object> _jobQueue;
        private CancellationTokenSource _cancelSource;
        private WaitCallback _callback;
        private int _numberThreads = 0;
        private bool _isRunning;

        public ObjectJobEngine(WaitCallback callback, string name, int threadsHoldtime = 1000, int concurrency = 1)
        {
            _name = name;
            _threadsHoldtime = threadsHoldtime;

            if (concurrency < 1)
                _concurrency = 1;
            else
                _concurrency = concurrency;

            if (callback !=  null)
            {
                _callback = callback;
                _jobQueue = new BlockingCollection<object>();
                _cancelSource = new CancellationTokenSource();
                _isRunning = true;
            }
        }

        ~ObjectJobEngine()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            lock(_mainLock)
            {
                if (!_isRunning)
                    return;
                _isRunning = false;

                _cancelSource.Cancel();
            }

            if (_numberThreads > 0)
            {
                int cntr = 100;
                while (_numberThreads > 0 && --cntr > 0)
                    Thread.Yield();
            }

            if (_jobQueue != null)
            {
                _jobQueue.Dispose();
                _jobQueue = null;
            }
            if (_cancelSource != null)
            {
                _cancelSource.Dispose();
                _cancelSource = null;
            }
            _callback = null;
        }

        /// <summary>
        /// Number of jobs waiting to be processed.
        /// </summary>
        public int Count => _jobQueue == null ? 0 : _jobQueue.Count;

        public void Cancel()
        {
            if (!_isRunning || _jobQueue == null || _jobQueue.Count == 0)
                return;
            try
            {
                while(_jobQueue.TryTake(out object dummy));
                _cancelSource.Cancel();
            }
            catch { }
        }

        /// <summary>
        /// Queue the job for processing.
        /// </summary>
        /// <returns><c>true</c>, if job was queued, <c>false</c> otherwise.</returns>
        /// <param name="job">The job</param>
        /// </param>
        public bool Enqueue(object o)
        {
            if (!_isRunning)
                return false;

            _jobQueue?.Add(o);

            lock (_mainLock)
            {
                if (_numberThreads < _concurrency && _numberThreads < _jobQueue.Count)
                {
                    Util.FireAndForget(ProcessRequests, null, _name, false);
                    ++_numberThreads;
                }
            }
            return true;
        }

        private void ProcessRequests(object o)
        {
            object obj;
            while (_isRunning)
            {
                try
                {
                    if(!_jobQueue.TryTake(out obj, _threadsHoldtime, _cancelSource.Token))
                        break;
                }
                catch
                {
                    break;
                }

                if(!_isRunning || _callback == null)
                    break;
                try
                {
                    _callback.Invoke(obj);
                    obj = null;
                }
                catch (Exception e)
                {
                    _log.ErrorFormat(
                        "[ObjectJob {0}]: Job failed, continuing.  Exception {1}",_name, e);
                }
            }
            lock (_mainLock)
                --_numberThreads;
        }
    }
}
