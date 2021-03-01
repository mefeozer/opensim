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
using System.Timers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using OpenMetaverse;
using OpenSim.Framework;
using System.Runtime.Serialization.Formatters.Binary;
using Timer = System.Timers.Timer;

namespace OpenSim.Region.Framework.Scenes
{
    public class KeyframeTimer
    {
        private static readonly Dictionary<Scene, KeyframeTimer> _timers =
                new Dictionary<Scene, KeyframeTimer>();

        private readonly Timer _timer;
        private readonly Dictionary<KeyframeMotion, object> _motions = new Dictionary<KeyframeMotion, object>();
        private readonly object _lockObject = new object();
        private readonly object _timerLock = new object();
        private const double _tickDuration = 50.0;

        public double TickDuration => _tickDuration;

        public KeyframeTimer(Scene scene)
        {
            _timer = new Timer
            {
                Interval = TickDuration,
                AutoReset = true
            };
            _timer.Elapsed += OnTimer;
        }

        public void Start()
        {
            lock (_timer)
            {
                if (!_timer.Enabled)
                    _timer.Start();
            }
        }

        private void OnTimer(object sender, ElapsedEventArgs ea)
        {
            if (!Monitor.TryEnter(_timerLock))
                return;

            try
            {
                List<KeyframeMotion> motions;

                lock (_lockObject)
                {
                    motions = new List<KeyframeMotion>(_motions.Keys);
                }

                foreach (KeyframeMotion m in motions)
                {
                    try
                    {
                        m.OnTimer(TickDuration);
                    }
                    catch (Exception)
                    {
                        // Don't stop processing
                    }
                }
            }
            catch (Exception)
            {
                // Keep running no matter what
            }
            finally
            {
                Monitor.Exit(_timerLock);
            }
        }

        public static void Add(KeyframeMotion motion)
        {
            KeyframeTimer timer;

            if (motion.Scene == null)
                return;

            lock (_timers)
            {
                if (!_timers.TryGetValue(motion.Scene, out timer))
                {
                    timer = new KeyframeTimer(motion.Scene);
                    _timers[motion.Scene] = timer;

                    if (!SceneManager.Instance.AllRegionsReady)
                    {
                        // Start the timers only once all the regions are ready. This is required
                        // when using megaregions, because the megaregion is correctly configured
                        // only after all the regions have been loaded. (If we don't do this then
                        // when the prim moves it might think that it crossed into a region.)
                        SceneManager.Instance.OnRegionsReadyStatusChange += delegate(SceneManager sm)
                        {
                            if (sm.AllRegionsReady)
                                timer.Start();
                        };
                    }

                    // Check again, in case the regions were started while we were adding the event handler
                    if (SceneManager.Instance.AllRegionsReady)
                    {
                        timer.Start();
                    }
                }
            }

            lock (timer._lockObject)
            {
                timer._motions[motion] = null;
            }
        }

        public static void Remove(KeyframeMotion motion)
        {
            KeyframeTimer timer;

            if (motion.Scene == null)
                return;

            lock (_timers)
            {
                if (!_timers.TryGetValue(motion.Scene, out timer))
                {
                    return;
                }
            }

            lock (timer._lockObject)
            {
                timer._motions.Remove(motion);
            }
        }
    }

    [Serializable]
    public class KeyframeMotion
    {
        //private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public enum PlayMode : int
        {
            Forward = 0,
            Reverse = 1,
            Loop = 2,
            PingPong = 3
        };

        [Flags]
        public enum DataFormat : int
        {
            Translation = 2,
            Rotation = 1
        }

        [Serializable]
        public struct Keyframe
        {
            public Vector3? Position;
            public Quaternion? Rotation;
            public Quaternion StartRotation;
            public int TimeMS;
            public int TimeTotal;
            public Vector3 AngularVelocity;
            public Vector3 StartPosition;
        };

        private Vector3 _serializedPosition;
        private Vector3 _basePosition;
        private Quaternion _baseRotation;

        private Keyframe _currentFrame;

        private List<Keyframe> _frames = new List<Keyframe>();

        private Keyframe[] _keyframes;

        // skip timer events.
        //timer.stop doesn't assure there aren't event threads still being fired
        [NonSerialized()]
        private bool _timerStopped;

        [NonSerialized()]
        private bool _isCrossing;

        [NonSerialized()]
        private bool _waitingCrossing;

        // retry position for cross fail
        [NonSerialized()]
        private Vector3 _nextPosition;

        [NonSerialized()]
        private SceneObjectGroup _group;

        private PlayMode _mode = PlayMode.Forward;
        private DataFormat _data = DataFormat.Translation | DataFormat.Rotation;

        private bool _running = false;

        [NonSerialized()]
        private bool _selected = false;

        private int _iterations = 0;

        private int _skipLoops = 0;

        [NonSerialized()]
        private Scene _scene;

        public Scene Scene => _scene;

        public DataFormat Data => _data;

        public bool Selected
        {
            set
            {
                if (_group != null)
                {
                    if (!value)
                    {
                        // Once we're let go, recompute positions
                        if (_selected)
                            UpdateSceneObject(_group);
                    }
                    else
                    {
                        // Save selection position in case we get moved
                        if (!_selected)
                        {
                            StopTimer();
                            _serializedPosition = _group.AbsolutePosition;
                        }
                    }
                }
                _isCrossing = false;
                _waitingCrossing = false;
                _selected = value;
            }
        }

        private void StartTimer()
        {
            lock (_frames)
            {
                KeyframeTimer.Add(this);
                _lasttickMS = Util.GetTimeStampMS();
                _timerStopped = false;
            }
        }

        private void StopTimer()
        {
            lock (_frames)
                _timerStopped = true;
        }

        public static KeyframeMotion FromData(SceneObjectGroup grp, byte[] data)
        {
            KeyframeMotion newMotion = null;

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                {
                    BinaryFormatter fmt = new BinaryFormatter();
                    newMotion = (KeyframeMotion)fmt.Deserialize(ms);
                }

                newMotion._group = grp;

                if (grp != null)
                {
                    newMotion._scene = grp.Scene;
                    if (grp.IsSelected)
                        newMotion._selected = true;
                }

//                newMotion._timerStopped = false;
//                newMotion._running = true;
                newMotion._isCrossing = false;
                newMotion._waitingCrossing = false;
            }
            catch
            {
                newMotion = null;
            }

            return newMotion;
        }

        public void UpdateSceneObject(SceneObjectGroup grp)
        {
            _isCrossing = false;
            _waitingCrossing = false;
            StopTimer();

            if (grp == null)
                return;

            _group = grp;
            _scene = grp.Scene;


            lock (_frames)
            {
                Vector3 grppos = grp.AbsolutePosition;
                Vector3 offset = grppos - _serializedPosition;
                // avoid doing it more than once
                // current this will happen draging a prim to other region
                _serializedPosition = grppos;

                _basePosition += offset;
                _currentFrame.Position += offset;

                _nextPosition += offset;

                for (int i = 0; i < _frames.Count; i++)
                {
                    Keyframe k = _frames[i];
                    k.Position += offset;
                    _frames[i] = k;
                }
            }

            if (_running)
                Start();
        }

        public KeyframeMotion(SceneObjectGroup grp, PlayMode mode, DataFormat data)
        {
            _mode = mode;
            _data = data;

            _group = grp;
            if (grp != null)
            {
                _basePosition = grp.AbsolutePosition;
                _baseRotation = grp.GroupRotation;
                _scene = grp.Scene;
            }

            _timerStopped = true;
            _isCrossing = false;
            _waitingCrossing = false;
        }

        public void SetKeyframes(Keyframe[] frames)
        {
            _keyframes = frames;
        }

        public KeyframeMotion Copy(SceneObjectGroup newgrp)
        {
            StopTimer();

            KeyframeMotion newmotion = new KeyframeMotion(null, _mode, _data)
            {
                _group = newgrp,
                _scene = newgrp.Scene
            };

            if (_keyframes != null)
            {
                newmotion._keyframes = new Keyframe[_keyframes.Length];
                _keyframes.CopyTo(newmotion._keyframes, 0);
            }

            lock (_frames)
            {
                newmotion._frames = new List<Keyframe>(_frames);

                newmotion._basePosition = _basePosition;
                newmotion._baseRotation = _baseRotation;

                if (_selected)
                    newmotion._serializedPosition = _serializedPosition;
                else
                {
                    if (_group != null)
                        newmotion._serializedPosition = _group.AbsolutePosition;
                    else
                        newmotion._serializedPosition = _serializedPosition;
                }

                newmotion._currentFrame = _currentFrame;

                newmotion._iterations = _iterations;
                newmotion._running = _running;
            }

            if (_running && !_waitingCrossing)
                StartTimer();

            return newmotion;
        }

        public void Delete()
        {
            _running = false;
            StopTimer();
            _isCrossing = false;
            _waitingCrossing = false;
            _frames.Clear();
            _keyframes = null;
        }

        public void Start()
        {
            _isCrossing = false;
            _waitingCrossing = false;
            if (_keyframes != null && _group != null && _keyframes.Length > 0)
            {
                StartTimer();
                _running = true;
                _group.Scene.EventManager.TriggerMovingStartEvent(_group.RootPart.LocalId);
            }
            else
            {
                StopTimer();
                _running = false;
            }
        }

        public void Stop()
        {
            StopTimer();
            _running = false;
            _isCrossing = false;
            _waitingCrossing = false;

            _basePosition = _group.AbsolutePosition;
            _baseRotation = _group.GroupRotation;

            _group.RootPart.Velocity = Vector3.Zero;
            _group.RootPart.AngularVelocity = Vector3.Zero;
//            _group.SendGroupRootTerseUpdate();
            _group.RootPart.ScheduleTerseUpdate();
            _frames.Clear();
            _group.Scene.EventManager.TriggerMovingEndEvent(_group.RootPart.LocalId);
        }

        public void Pause()
        {
            StopTimer();
            _running = false;

            _group.RootPart.Velocity = Vector3.Zero;
            _group.RootPart.AngularVelocity = Vector3.Zero;
//            _skippedUpdates = 1000;
//            _group.SendGroupRootTerseUpdate();
            _group.RootPart.ScheduleTerseUpdate();
            _group.Scene.EventManager.TriggerMovingEndEvent(_group.RootPart.LocalId);
        }

        public void Suspend()
        {
            lock (_frames)
            {
                if (_timerStopped)
                    return;
                _timerStopped = true;
            }
        }

        public void Resume()
        {
            lock (_frames)
            {
                if (!_timerStopped)
                    return;
                if (_running && !_waitingCrossing)
                    StartTimer();
//                _skippedUpdates = 1000;
            }
        }

        private void GetNextList()
        {
            _frames.Clear();
            Vector3 pos = _basePosition;
            Quaternion rot = _baseRotation;

            if (_mode == PlayMode.Loop || _mode == PlayMode.PingPong || _iterations == 0)
            {
                int direction = 1;
                if (_mode == PlayMode.Reverse || _mode == PlayMode.PingPong && (_iterations & 1) != 0)
                    direction = -1;

                int start = 0;
                int end = _keyframes.Length;

                if (direction < 0)
                {
                    start = _keyframes.Length - 1;
                    end = -1;
                }

                for (int i = start; i != end ; i += direction)
                {
                    Keyframe k = _keyframes[i];

                    k.StartPosition = pos;
                    if (k.Position.HasValue)
                    {
                        k.Position = k.Position * direction;
//                        k.Velocity = (Vector3)k.Position / (k.TimeMS / 1000.0f);
                        k.Position += pos;
                    }
                    else
                    {
                        k.Position = pos;
//                        k.Velocity = Vector3.Zero;
                    }

                    k.StartRotation = rot;
                    if (k.Rotation.HasValue)
                    {
                        if (direction == -1)
                            k.Rotation = Quaternion.Conjugate((Quaternion)k.Rotation);
                        k.Rotation = rot * k.Rotation;
                    }
                    else
                    {
                        k.Rotation = rot;
                    }

/* ang vel not in use for now

                    float angle = 0;

                    float aa = k.StartRotation.X * k.StartRotation.X + k.StartRotation.Y * k.StartRotation.Y + k.StartRotation.Z * k.StartRotation.Z + k.StartRotation.W * k.StartRotation.W;
                    float bb = ((Quaternion)k.Rotation).X * ((Quaternion)k.Rotation).X + ((Quaternion)k.Rotation).Y * ((Quaternion)k.Rotation).Y + ((Quaternion)k.Rotation).Z * ((Quaternion)k.Rotation).Z + ((Quaternion)k.Rotation).W * ((Quaternion)k.Rotation).W;
                    float aa_bb = aa * bb;

                    if (aa_bb == 0)
                    {
                        angle = 0;
                    }
                    else
                    {
                        float ab = k.StartRotation.X * ((Quaternion)k.Rotation).X +
                                   k.StartRotation.Y * ((Quaternion)k.Rotation).Y +
                                   k.StartRotation.Z * ((Quaternion)k.Rotation).Z +
                                   k.StartRotation.W * ((Quaternion)k.Rotation).W;
                        float q = (ab * ab) / aa_bb;

                        if (q > 1.0f)
                        {
                            angle = 0;
                        }
                        else
                        {
                            angle = (float)Math.Acos(2 * q - 1);
                        }
                    }

                    k.AngularVelocity = (new Vector3(0, 0, 1) * (Quaternion)k.Rotation) * (angle / (k.TimeMS / 1000));
 */
                    k.TimeTotal = k.TimeMS;

                    _frames.Add(k);

                    pos = (Vector3)k.Position;
                    rot = (Quaternion)k.Rotation;

                }

                _basePosition = pos;
                _baseRotation = rot;

                _iterations++;
            }
        }

        public void OnTimer(double tickDuration)
        {
            if (!Monitor.TryEnter(_frames))
                return;
            if (_timerStopped)
                KeyframeTimer.Remove(this);
            else
                DoOnTimer(tickDuration);
            Monitor.Exit(_frames);
        }

        private void Done()
        {
            KeyframeTimer.Remove(this);
            _timerStopped = true;
            _running = false;
            _isCrossing = false;
            _waitingCrossing = false;

            _basePosition = _group.AbsolutePosition;
            _baseRotation = _group.GroupRotation;

            _group.RootPart.Velocity = Vector3.Zero;
            _group.RootPart.AngularVelocity = Vector3.Zero;
//            _group.SendGroupRootTerseUpdate();
            _group.RootPart.ScheduleTerseUpdate();
            _frames.Clear();
        }

//        [NonSerialized()] Vector3 _lastPosUpdate;
//        [NonSerialized()] Quaternion _lastRotationUpdate;
        [NonSerialized()] Vector3 _currentVel;
//        [NonSerialized()] int _skippedUpdates;
        [NonSerialized()] double _lasttickMS;

        private void DoOnTimer(double tickDuration)
        {
            if (_skipLoops > 0)
            {
                _skipLoops--;
                return;
            }

            if (_group == null)
                return;

//            bool update = false;

            if (_selected)
            {
                if (_group.RootPart.Velocity != Vector3.Zero)
                {
                    _group.RootPart.Velocity = Vector3.Zero;
//                    _skippedUpdates = 1000;
//                    _group.SendGroupRootTerseUpdate();
                    _group.RootPart.ScheduleTerseUpdate();
                }
                return;
            }

            if (_isCrossing)
            {
                // if crossing and timer running then cross failed
                // wait some time then
                // retry to set the position that evtually caused the outbound
                // if still outside region this will call startCrossing below
                _isCrossing = false;
//                _skippedUpdates = 1000;
                _group.AbsolutePosition = _nextPosition;

                if (!_isCrossing)
                {
                    StopTimer();
                    StartTimer();
                }
                return;
            }

            double nowMS = Util.GetTimeStampMS();

            if (_frames.Count == 0)
            {
                lock (_frames)
                {
                    GetNextList();

                    if (_frames.Count == 0)
                    {
                        Done();
                        _group.Scene.EventManager.TriggerMovingEndEvent(_group.RootPart.LocalId);
                        return;
                    }

                    _currentFrame = _frames[0];
                }
                _nextPosition = _group.AbsolutePosition;
                _currentVel = (Vector3)_currentFrame.Position - _nextPosition;
                _currentVel /= _currentFrame.TimeMS * 0.001f;

                _currentFrame.TimeMS += (int)tickDuration;
                _lasttickMS = nowMS - 50f;
//                update = true;
            }

            int elapsed = (int)(nowMS - _lasttickMS);
            if( elapsed > 3 * tickDuration)
                elapsed = (int)tickDuration;

            _currentFrame.TimeMS -= elapsed;
            _lasttickMS = nowMS;

            // Do the frame processing
            double remainingSteps = (double)_currentFrame.TimeMS / tickDuration;

            if (remainingSteps <= 1.0)
            {
                _group.RootPart.Velocity = Vector3.Zero;
                _group.RootPart.AngularVelocity = Vector3.Zero;

                _nextPosition = (Vector3)_currentFrame.Position;
                _group.AbsolutePosition = _nextPosition;

                _group.RootPart.RotationOffset = (Quaternion)_currentFrame.Rotation;

                lock (_frames)
                {
                    _frames.RemoveAt(0);
                    if (_frames.Count > 0)
                    {
                        _currentFrame = _frames[0];
                        _currentVel = (Vector3)_currentFrame.Position - _nextPosition;
                        _currentVel /= _currentFrame.TimeMS * 0.001f;
                        _group.RootPart.Velocity = _currentVel;
                        _currentFrame.TimeMS += (int)tickDuration;
                    }
                    else
                        _group.RootPart.Velocity = Vector3.Zero;
                }
//                update = true;
            }
            else
            {
//                bool lastSteps = remainingSteps < 4;
        
                Vector3 currentPosition = _group.AbsolutePosition;
                Vector3 motionThisFrame = (Vector3)_currentFrame.Position - currentPosition;
                motionThisFrame /= (float)remainingSteps;
 
                _nextPosition = currentPosition + motionThisFrame;

                Quaternion currentRotation = _group.GroupRotation;
                if ((Quaternion)_currentFrame.Rotation != currentRotation)
                {
                    float completed = ((float)_currentFrame.TimeTotal - (float)_currentFrame.TimeMS) / (float)_currentFrame.TimeTotal;
                    Quaternion step = Quaternion.Slerp(_currentFrame.StartRotation, (Quaternion)_currentFrame.Rotation, completed);
                    step.Normalize();
                    _group.RootPart.RotationOffset = step;
/*
                    if (Math.Abs(step.X - _lastRotationUpdate.X) > 0.001f
                        || Math.Abs(step.Y - _lastRotationUpdate.Y) > 0.001f
                        || Math.Abs(step.Z - _lastRotationUpdate.Z) > 0.001f)
                        update = true;
*/
                }

                _group.AbsolutePosition = _nextPosition;
//                if(lastSteps)
//                    _group.RootPart.Velocity = Vector3.Zero;
//                else
                    _group.RootPart.Velocity = _currentVel;
/*
                if(!update && (
//                    lastSteps ||
                    _skippedUpdates * tickDuration > 0.5 ||
                    Math.Abs(_nextPosition.X - currentPosition.X) > 5f ||
                    Math.Abs(_nextPosition.Y - currentPosition.Y) > 5f ||
                    Math.Abs(_nextPosition.Z - currentPosition.Z) > 5f
                    ))
                {
                    update = true;
                }
                else
                    _skippedUpdates++;
*/
            }
//            if(update)
//            {
//                _lastPosUpdate = _nextPosition;
//                _lastRotationUpdate = _group.GroupRotation; 
//                _skippedUpdates = 0;
//                _group.SendGroupRootTerseUpdate();
                _group.RootPart.ScheduleTerseUpdate();
//            }
        }

        public byte[] Serialize()
        {
            bool timerWasStopped;
            lock (_frames)
            {
                timerWasStopped = _timerStopped;
            }
            StopTimer();

            SceneObjectGroup tmp = _group;
            _group = null;

            using (MemoryStream ms = new MemoryStream())
            {
                BinaryFormatter fmt = new BinaryFormatter();
                if (!_selected && tmp != null)
                    _serializedPosition = tmp.AbsolutePosition;
                fmt.Serialize(ms, this);
                _group = tmp;
                if (!timerWasStopped && _running && !_waitingCrossing)
                    StartTimer();

                return ms.ToArray();
            }
        }

        public void StartCrossingCheck()
        {
            // timer will be restart by crossingFailure
            // or never since crossing worked and this
            // should be deleted
            StopTimer();

            _isCrossing = true;
            _waitingCrossing = true;

            // to remove / retune to smoth crossings
            if (_group.RootPart.Velocity != Vector3.Zero)
            {
                _group.RootPart.Velocity = Vector3.Zero;
//                _skippedUpdates = 1000;
//                _group.SendGroupRootTerseUpdate();
                _group.RootPart.ScheduleTerseUpdate();
            }
        }

        public void CrossingFailure()
        {
            _waitingCrossing = false;

            if (_group != null)
            {
                _group.RootPart.Velocity = Vector3.Zero;
//                _skippedUpdates = 1000;
//                _group.SendGroupRootTerseUpdate();
                _group.RootPart.ScheduleTerseUpdate();

                if (_running)
                {
                    StopTimer();
                    _skipLoops = 1200; // 60 seconds
                    StartTimer();
                }
            }
        }
    }
}
