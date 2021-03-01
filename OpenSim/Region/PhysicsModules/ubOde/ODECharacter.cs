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


// Revision by Ubit 2011/12

using System;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;
using log4net;

namespace OpenSim.Region.PhysicsModule.ubOde
{
    /// <summary>
    /// Various properties that ODE uses for AMotors but isn't exposed in ODE.NET so we must define them ourselves.
    /// </summary>

    public enum dParam:int
    {
        LowStop = 0,
        HiStop = 1,
        Vel = 2,
        FMax = 3,
        FudgeFactor = 4,
        Bounce = 5,
        CFM = 6,
        StopERP = 7,
        StopCFM = 8,
        LoStop2 = 256,
        HiStop2 = 257,
        Vel2 = 258,
        FMax2 = 259,
        StopERP2 = 7 + 256,
        StopCFM2 = 8 + 256,
        LoStop3 = 512,
        HiStop3 = 513,
        Vel3 = 514,
        FMax3 = 515,
        StopERP3 = 7 + 512,
        StopCFM3 = 8 + 512
    }

    public class OdeCharacter:PhysicsActor
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Vector3 _position;
        private Vector3 _zeroPosition;
        private Vector3 _velocity;
        private Vector3 _target_velocity;
        private Vector3 _acceleration;
        private Vector3 _rotationalVelocity;
        private Vector3 _size;
        private Vector3 _collideNormal;
        private Vector3 _lastFallVel;
        private Quaternion _orientation;
        private Quaternion _orientation2D;
        private float _mass = 80f;
        public float _density = 60f;
        private bool _pidControllerActive = true;

        public int _bodydisablecontrol = 0;

        const float basePID_D = 0.55f; // scaled for unit mass unit time (2200 /(50*80))
        const float basePID_P = 0.225f; // scaled for unit mass unit time (900 /(50*80))
        public float PID_D;
        public float PID_P;

        private readonly float timeStep;
        private readonly float invtimeStep;

        private float _feetOffset = 0;
        private float feetOff = 0;
        private float boneOff = 0;
        private float AvaAvaSizeXsq = 0.3f;
        private float AvaAvaSizeYsq = 0.2f;

        public float walkDivisor = 1.3f;
        public float runDivisor = 0.8f;
        private bool _flying = false;
        private bool _iscolliding = false;
        private bool _iscollidingGround = false;
        private bool _iscollidingObj = false;
        private bool _alwaysRun = false;

        private bool _zeroFlag = false;
        private bool _haveLastFallVel = false;

        private uint _localID = 0;
        public bool _returnCollisions = false;
        // taints and their non-tainted counterparts
        public bool _isPhysical = false; // the current physical status
        public float MinimumGroundFlightOffset = 3f;

        private float _buoyancy = 0f;

        private bool _freemove = false;

        //        private string _name = String.Empty;
        // other filter control
        int _colliderfilter = 0;
        int _colliderGroundfilter = 0;
        int _colliderObjectfilter = 0;

        // Default we're a Character
        private readonly CollisionCategories _collisionCategories = CollisionCategories.Character;

        // Default, Collide with Other Geometries, spaces, bodies and characters.
        private readonly CollisionCategories _collisionFlags = CollisionCategories.Character
                                                                | CollisionCategories.Geom
                                                                | CollisionCategories.VolumeDtc;
        // we do land collisions not ode                | CollisionCategories.Land);
        public IntPtr Body = IntPtr.Zero;
        private readonly ODEScene _parent_scene;
        private IntPtr capsule = IntPtr.Zero;
        public IntPtr collider = IntPtr.Zero;

        public IntPtr Amotor = IntPtr.Zero;

        internal SafeNativeMethods.Mass ShellMass;

        public int _eventsubscription = 0;
        private int _cureventsubscription = 0;
        private readonly CollisionEventUpdate CollisionEventsThisFrame = new CollisionEventUpdate();
        private bool SentEmptyCollisionsEvent;

        // unique UUID of this character object
        public UUID _uuid;
        public bool bad = false;

        readonly float mu;

        // HoverHeight control
        private float _PIDHoverHeight;
        private float _PIDHoverTau;
        private bool _useHoverPID;
        private PIDHoverType _PIDHoverType;
        private float _targetHoverHeight;


        public OdeCharacter(uint localID, string avName,ODEScene parent_scene,Vector3 pos,Vector3 pSize,float pfeetOffset,float density,float walk_divisor,float rundivisor)
        {
            _uuid = UUID.Random();
            _localID = localID;
            _parent_scene = parent_scene;

            timeStep = parent_scene.ODE_STEPSIZE;
            invtimeStep = 1 / timeStep;

            if(pos.IsFinite())
            {
                if(pos.Z > Constants.MaxSimulationHeight)
                {
                    pos.Z = parent_scene.GetTerrainHeightAtXY(127,127) + 5;
                }
                if(pos.Z < Constants.MinSimulationHeight) // shouldn't this be 0 ?
                {
                    pos.Z = parent_scene.GetTerrainHeightAtXY(127,127) + 5;
                }
                _position = pos;
            }
            else
            {
                _position = new Vector3((float)_parent_scene.WorldExtents.X * 0.5f,(float)_parent_scene.WorldExtents.Y * 0.5f,parent_scene.GetTerrainHeightAtXY(128f,128f) + 10f);
                _log.Warn("[PHYSICS]: Got NaN Position on Character Create");
            }

            _size.X = pSize.X;
            _size.Y = pSize.Y;
            _size.Z = pSize.Z;

            if(_size.X <0.01f)
                _size.X = 0.01f;
            if(_size.Y <0.01f)
                _size.Y = 0.01f;
            if(_size.Z <0.01f)
                _size.Z = 0.01f;

            _feetOffset = pfeetOffset;
            _orientation = Quaternion.Identity;
            _orientation2D = Quaternion.Identity;
            _density = density;

            // force lower density for testing
            _density = 3.0f;

            mu = _parent_scene.AvatarFriction;

            walkDivisor = walk_divisor;
            runDivisor = rundivisor;

            _mass = _density * _size.X * _size.Y * _size.Z;
            ; // sure we have a default

            PID_D = basePID_D * _mass * invtimeStep;
            PID_P = basePID_P * _mass * invtimeStep;

            _isPhysical = false; // current status: no ODE information exists

            Name = avName;

            AddChange(changes.Add,null);
        }

        public override int PhysicsActorType
        {
            get => (int)ActorTypes.Agent;
            set
            {
                return;
            }
        }

        public override void getContactData(ref ContactData cdata)
        {
            cdata.mu = mu;
            cdata.bounce = 0;
            cdata.softcolide = false;
        }

        public override bool Building
        {
            get; set;
        }

        /// <summary>
        /// If this is set, the avatar will move faster
        /// </summary>
        public override bool SetAlwaysRun
        {
            get => _alwaysRun;
            set => _alwaysRun = value;
        }

        public override uint LocalID
        {
            get => _localID;
            set => _localID = value;
        }

        public override PhysicsActor ParentActor => (PhysicsActor)this;

        public override bool Grabbed
        {
            set
            {
                return;
            }
        }

        public override bool Selected
        {
            set
            {
                return;
            }
        }

        public override float Buoyancy
        {
            get => _buoyancy;
            set => _buoyancy = value;
        }

        public override bool FloatOnWater
        {
            set
            {
                return;
            }
        }

        public override bool IsPhysical
        {
            get => _isPhysical;
            set
            {
                return;
            }
        }

        public override bool ThrottleUpdates
        {
            get => false;
            set
            {
                return;
            }
        }

        public override bool Flying
        {
            get => _flying;
            set => _flying = value;
            //                _log.DebugFormat("[PHYSICS]: Set OdeCharacter Flying to {0}", flying);
        }

        /// <summary>
        /// Returns if the avatar is colliding in general.
        /// This includes the ground and objects and avatar.
        /// </summary>
        public override bool IsColliding
        {
            get => _iscolliding || _iscollidingGround;
            set
            {
                if(value)
                {
                    _colliderfilter += 3;
                    if(_colliderfilter > 3)
                        _colliderfilter = 3;
                }
                else
                {
                    _colliderfilter--;
                    if(_colliderfilter < 0)
                        _colliderfilter = 0;
                }

                if(_colliderfilter == 0)
                    _iscolliding = false;
                else
                {
                    _pidControllerActive = true;
                    _iscolliding = true;
                    _freemove = false;
                }
            }
        }

        /// <summary>
        /// Returns if an avatar is colliding with the ground
        /// </summary>
        public override bool CollidingGround
        {
            get => _iscollidingGround;
            set
            {
                /*  we now control this
                                if (value)
                                    {
                                    _colliderGroundfilter += 2;
                                    if (_colliderGroundfilter > 2)
                                        _colliderGroundfilter = 2;
                                    }
                                else
                                    {
                                    _colliderGroundfilter--;
                                    if (_colliderGroundfilter < 0)
                                        _colliderGroundfilter = 0;
                                    }

                                if (_colliderGroundfilter == 0)
                                    _iscollidingGround = false;
                                else
                                    _iscollidingGround = true;
                 */
            }

        }

        /// <summary>
        /// Returns if the avatar is colliding with an object
        /// </summary>
        public override bool CollidingObj
        {
            get => _iscollidingObj;
            set
            {
                // Ubit filter this also
                if(value)
                {
                    _colliderObjectfilter += 2;
                    if(_colliderObjectfilter > 2)
                        _colliderObjectfilter = 2;
                }
                else
                {
                    _colliderObjectfilter--;
                    if(_colliderObjectfilter < 0)
                        _colliderObjectfilter = 0;
                }

                if(_colliderObjectfilter == 0)
                    _iscollidingObj = false;
                else
                    _iscollidingObj = true;

                //            _iscollidingObj = value;

                if(_iscollidingObj)
                    _pidControllerActive = false;
                else
                    _pidControllerActive = true;
            }
        }

        /// <summary>
        /// turn the PID controller on or off.
        /// The PID Controller will turn on all by itself in many situations
        /// </summary>
        /// <param name="status"></param>
        public void SetPidStatus(bool status)
        {
            _pidControllerActive = status;
        }

        public override bool Stopped => _zeroFlag;

        /// <summary>
        /// This 'puts' an avatar somewhere in the physics space.
        /// Not really a good choice unless you 'know' it's a good
        /// spot otherwise you're likely to orbit the avatar.
        /// </summary>
        public override Vector3 Position
        {
            get => _position;
            set
            {
                if(value.IsFinite())
                {
                    if(value.Z > 9999999f)
                    {
                        value.Z = _parent_scene.GetTerrainHeightAtXY(127,127) + 5;
                    }
                    if(value.Z < -100f)
                    {
                        value.Z = _parent_scene.GetTerrainHeightAtXY(127,127) + 5;
                    }
                    AddChange(changes.Position,value);
                }
                else
                {
                    _log.Warn("[PHYSICS]: Got a NaN Position from Scene on a Character");
                }
            }
        }

        public override Vector3 RotationalVelocity
        {
            get => _rotationalVelocity;
            set => _rotationalVelocity = value;
        }

        /// <summary>
        /// This property sets the height of the avatar only.  We use the height to make sure the avatar stands up straight
        /// and use it to offset landings properly
        /// </summary>
        public override Vector3 Size
        {
            get => _size;
            set
            {
                if(value.IsFinite())
                {
                    if(value.X <0.01f)
                        value.X = 0.01f;
                    if(value.Y <0.01f)
                        value.Y = 0.01f;
                    if(value.Z <0.01f)
                        value.Z = 0.01f;

                    AddChange(changes.Size,value);
                }
                else
                {
                    _log.Warn("[PHYSICS]: Got a NaN Size from Scene on a Character");
                }
            }
        }

        public override void setAvatarSize(Vector3 size,float feetOffset)
        {
            if(size.IsFinite())
            {
                if(size.X < 0.01f)
                    size.X = 0.01f;
                if(size.Y < 0.01f)
                    size.Y = 0.01f;
                if(size.Z < 0.01f)
                    size.Z = 0.01f;

                strAvatarSize st = new strAvatarSize
                {
                    size = size,
                    offset = feetOffset
                };
                AddChange(changes.AvatarSize,st);
            }
            else
            {
                _log.Warn("[PHYSICS]: Got a NaN AvatarSize from Scene on a Character");
            }

        }
        /// <summary>
        /// This creates the Avatar's physical Surrogate at the position supplied
        /// </summary>
        /// <param name="npositionX"></param>
        /// <param name="npositionY"></param>
        /// <param name="npositionZ"></param>

        //
        /// <summary>
        /// Uses the capped cyllinder volume formula to calculate the avatar's mass.
        /// This may be used in calculations in the scene/scenepresence
        /// </summary>
        public override float Mass => _mass;

        public override void link(PhysicsActor obj)
        {

        }

        public override void delink()
        {

        }

        public override void LockAngularMotion(byte axislocks)
        {

        }


        public override Vector3 Force
        {
            get => _target_velocity;
            set
            {
                return;
            }
        }

        public override int VehicleType
        {
            get => 0;
            set
            {
                return;
            }
        }

        public override void VehicleFloatParam(int param,float value)
        {

        }

        public override void VehicleVectorParam(int param,Vector3 value)
        {

        }

        public override void VehicleRotationParam(int param,Quaternion rotation)
        {

        }

        public override void VehicleFlags(int param,bool remove)
        {

        }

        public override void SetVolumeDetect(int param)
        {

        }

        public override Vector3 CenterOfMass
        {
            get
            {
                Vector3 pos = _position;
                return pos;
            }
        }

        public override Vector3 GeometricCenter
        {
            get
            {
                Vector3 pos = _position;
                return pos;
            }
        }

        public override PrimitiveBaseShape Shape
        {
            set
            {
                return;
            }
        }

        public override Vector3 rootVelocity => _velocity;

        public override Vector3 Velocity
        {
            get => _velocity;
            set
            {
                if(value.IsFinite())
                {
                    AddChange(changes.Velocity,value);
                }
                else
                {
                    _log.Warn("[PHYSICS]: Got a NaN velocity from Scene in a Character");
                }
            }
        }

        public override Vector3 TargetVelocity
        {
            get => _targetVelocity;
            set
            {
                if(value.IsFinite())
                {
                    AddChange(changes.TargetVelocity,value);
                }
                else
                {
                    _log.Warn("[PHYSICS]: Got a NaN velocity from Scene in a Character");
                }
            }
        }

        public override Vector3 Torque
        {
            get => Vector3.Zero;
            set
            {
                return;
            }
        }

        public override float CollisionScore
        {
            get => 0f;
            set
            {
            }
        }

        public override bool Kinematic
        {
            get => false;
            set
            {
            }
        }

        public override Quaternion Orientation
        {
            get => _orientation;
            set
            {
                //                fakeori = value;
                //                givefakeori++;
                value.Normalize();
                AddChange(changes.Orientation,value);
            }
        }

        public override Vector3 Acceleration
        {
            get => _acceleration;
            set
            {
            }
        }

        public void SetAcceleration(Vector3 accel)
        {
            _pidControllerActive = true;
            _acceleration = accel;
            if (Body != IntPtr.Zero)
                SafeNativeMethods.BodyEnable(Body);
        }

        /// <summary>
        /// Adds the force supplied to the Target Velocity
        /// The PID controller takes this target velocity and tries to make it a reality
        /// </summary>
        /// <param name="force"></param>
        public override void AddForce(Vector3 force,bool pushforce)
        {
            if(force.IsFinite())
            {
                if(pushforce)
                {
                    AddChange(changes.Force,force * _density / (_parent_scene.ODE_STEPSIZE * 28f));
                }
                else
                {
                    AddChange(changes.TargetVelocity,force);
                }
            }
            else
            {
                _log.Warn("[PHYSICS]: Got a NaN force applied to a Character");
            }
            //_lastUpdateSent = false;
        }

        public override void AddAngularForce(Vector3 force,bool pushforce)
        {

        }

        public override void SetMomentum(Vector3 momentum)
        {
            if(momentum.IsFinite())
                AddChange(changes.Momentum,momentum);
        }


        private void AvatarGeomAndBodyCreation(float npositionX,float npositionY,float npositionZ)
        {
            float sx = _size.X;
            float sy = _size.Y;
            float sz = _size.Z;

            float bot = -sz * 0.5f + _feetOffset;
            boneOff = bot + 0.3f;

            float feetsz = sz * 0.45f;
            if(feetsz > 0.6f)
                feetsz = 0.6f;
            feetOff = bot + feetsz;

            AvaAvaSizeXsq = 0.4f * sx;
            AvaAvaSizeXsq *= AvaAvaSizeXsq;
            AvaAvaSizeYsq = 0.5f * sy;
            AvaAvaSizeYsq *= AvaAvaSizeYsq;

            _parent_scene.waitForSpaceUnlock(_parent_scene.CharsSpace);

            collider = SafeNativeMethods.SimpleSpaceCreate(_parent_scene.CharsSpace);
            SafeNativeMethods.SpaceSetSublevel(collider,3);
            SafeNativeMethods.SpaceSetCleanup(collider,false);
            SafeNativeMethods.GeomSetCategoryBits(collider,(uint)_collisionCategories);
            SafeNativeMethods.GeomSetCollideBits(collider,(uint)_collisionFlags);

            float r = sx;
            if(sy > r)
                r = sy;
            float l = sz - r;
            r *= 0.5f;

            capsule = SafeNativeMethods.CreateCapsule(collider, r, l);

            _mass = _density * sx * sy * sz;  // update mass
            SafeNativeMethods.MassSetBoxTotal(out ShellMass, _mass, sx, sy, sz);

            PID_D = basePID_D * _mass / _parent_scene.ODE_STEPSIZE;
            PID_P = basePID_P * _mass / _parent_scene.ODE_STEPSIZE;

            Body = SafeNativeMethods.BodyCreate(_parent_scene.world);

            _zeroFlag = false;
            _pidControllerActive = true;
            _freemove = false;

            _velocity = Vector3.Zero;

            // SafeNativeMethods.BodySetAutoDisableFlag(Body,false);
            SafeNativeMethods.BodySetAutoDisableFlag(Body, true);
            _bodydisablecontrol = 0;

            SafeNativeMethods.BodySetPosition(Body, npositionX, npositionY, npositionZ);

            _position.X = npositionX;
            _position.Y = npositionY;
            _position.Z = npositionZ;

            SafeNativeMethods.BodySetMass(Body,ref ShellMass);
            SafeNativeMethods.GeomSetBody(capsule,Body);

            // The purpose of the AMotor here is to keep the avatar's physical
            // surrogate from rotating while moving
            Amotor = SafeNativeMethods.JointCreateAMotor(_parent_scene.world,IntPtr.Zero);
            SafeNativeMethods.JointAttach(Amotor,Body,IntPtr.Zero);

            SafeNativeMethods.JointSetAMotorMode(Amotor,0);
            SafeNativeMethods.JointSetAMotorNumAxes(Amotor,3);
            SafeNativeMethods.JointSetAMotorAxis(Amotor,0,0,1,0,0);
            SafeNativeMethods.JointSetAMotorAxis(Amotor,1,0,0,1,0);
            SafeNativeMethods.JointSetAMotorAxis(Amotor,2,0,0,0,1);

            SafeNativeMethods.JointSetAMotorAngle(Amotor,0,0);
            SafeNativeMethods.JointSetAMotorAngle(Amotor,1,0);
            SafeNativeMethods.JointSetAMotorAngle(Amotor,2,0);

            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.StopCFM,0f); // make it HARD
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.StopCFM2,0f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.StopCFM3,0f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.StopERP,0.8f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.StopERP2,0.8f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.StopERP3,0.8f);

            // These lowstops and high stops are effectively (no wiggle room)
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.LowStop,-1e-5f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.HiStop,1e-5f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.LoStop2,-1e-5f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.HiStop2,1e-5f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.LoStop3,-1e-5f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.HiStop3,1e-5f);

            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)SafeNativeMethods.JointParam.Vel,0);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)SafeNativeMethods.JointParam.Vel2,0);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)SafeNativeMethods.JointParam.Vel3,0);

            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.FMax,5e8f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.FMax2,5e8f);
            SafeNativeMethods.JointSetAMotorParam(Amotor,(int)dParam.FMax3,5e8f);
        }

        /// <summary>
        /// Destroys the avatar body and geom

        private void AvatarGeomAndBodyDestroy()
        {
            // Kill the Amotor
            if(Amotor != IntPtr.Zero)
            {
                SafeNativeMethods.JointDestroy(Amotor);
                Amotor = IntPtr.Zero;
            }

            if(Body != IntPtr.Zero)
            {
                //kill the body
                SafeNativeMethods.BodyDestroy(Body);
                Body = IntPtr.Zero;
            }

            //kill the Geoms
            if(capsule != IntPtr.Zero)
            {
                _parent_scene.actor_name_map.Remove(capsule);
                _parent_scene.waitForSpaceUnlock(collider);
                SafeNativeMethods.GeomDestroy(capsule);
                capsule = IntPtr.Zero;
            }

            if(collider != IntPtr.Zero)
            {
                SafeNativeMethods.SpaceDestroy(collider);
                collider = IntPtr.Zero;
            }

        }

        //in place 2D rotation around Z assuming rot is normalised and is a rotation around Z
        public void RotateXYonZ(ref float x,ref float y,ref Quaternion rot)
        {
            float sin = 2.0f * rot.Z * rot.W;
            float cos = rot.W * rot.W - rot.Z * rot.Z;
            float tx = x;

            x = tx * cos - y * sin;
            y = tx * sin + y * cos;
        }
        public void RotateXYonZ(ref float x,ref float y,ref float sin,ref float cos)
        {
            float tx = x;
            x = tx * cos - y * sin;
            y = tx * sin + y * cos;
        }
        public void invRotateXYonZ(ref float x,ref float y,ref float sin,ref float cos)
        {
            float tx = x;
            x = tx * cos + y * sin;
            y = -tx * sin + y * cos;
        }

        public void invRotateXYonZ(ref float x,ref float y,ref Quaternion rot)
        {
            float sin = -2.0f * rot.Z * rot.W;
            float cos = rot.W * rot.W - rot.Z * rot.Z;
            float tx = x;

            x = tx * cos - y * sin;
            y = tx * sin + y * cos;
        }

        internal bool Collide(IntPtr me,IntPtr other,bool reverse,ref SafeNativeMethods.ContactGeom contact,
            ref SafeNativeMethods.ContactGeom altContact,ref bool useAltcontact,ref bool feetcollision)
        {
            feetcollision = false;
            useAltcontact = false;

            if(me == capsule)
            {
                Vector3 offset;

                float h = contact.pos.Z - _position.Z;
                offset.Z = h - feetOff;

                offset.X = contact.pos.X - _position.X;
                offset.Y = contact.pos.Y - _position.Y;

                SafeNativeMethods.GeomClassID gtype = SafeNativeMethods.GeomGetClass(other);
                if(gtype == SafeNativeMethods.GeomClassID.CapsuleClass)
                {
                    Vector3 roff = offset * Quaternion.Inverse(_orientation2D);
                    float r = roff.X *roff.X / AvaAvaSizeXsq;
                    r += roff.Y * roff.Y / AvaAvaSizeYsq;
                    if(r > 1.0f)
                        return false;

                    float dp = 1.0f -(float)Math.Sqrt((double)r);
                    if(dp > 0.05f)
                        dp = 0.05f;

                    contact.depth = dp;

                    if(offset.Z < 0)
                    {
                        feetcollision = true;
                        if(h < boneOff)
                        {
                            _collideNormal.X = contact.normal.X;
                            _collideNormal.Y = contact.normal.Y;
                            _collideNormal.Z = contact.normal.Z;
                            IsColliding = true;
                        }
                    }
                    return true;
                }

                if(gtype == SafeNativeMethods.GeomClassID.SphereClass && SafeNativeMethods.GeomGetBody(other) != IntPtr.Zero)
                {
                    if(SafeNativeMethods.GeomSphereGetRadius(other) < 0.5)
                        return true;
                }

                if(offset.Z > 0 || contact.normal.Z > 0.35f)
                {
                    if(offset.Z <= 0)
                    {
                        feetcollision = true;
                        if(h < boneOff)
                        {
                            _collideNormal.X = contact.normal.X;
                            _collideNormal.Y = contact.normal.Y;
                            _collideNormal.Z = contact.normal.Z;
                            IsColliding = true;
                        }
                    }
                    return true;
                }

                if(_flying)
                    return true;

                feetcollision = true;
                if(h < boneOff)
                {
                    _collideNormal.X = contact.normal.X;
                    _collideNormal.Y = contact.normal.Y;
                    _collideNormal.Z = contact.normal.Z;
                    IsColliding = true;
                }

                altContact = contact;
                useAltcontact = true;

                offset.Z -= 0.2f;

                offset.Normalize();

                float tdp = contact.depth;
                float t = offset.X;
                t = Math.Abs(t);
                if(t > 1e-6)
                {
                    tdp /= t;
                    tdp *= contact.normal.X;
                }
                else
                    tdp *= 10;

                if(tdp > 0.25f)
                    tdp = 0.25f;

                altContact.depth = tdp;

                if(reverse)
                {
                    altContact.normal.X = offset.X;
                    altContact.normal.Y = offset.Y;
                    altContact.normal.Z = offset.Z;
                }
                else
                {
                    altContact.normal.X = -offset.X;
                    altContact.normal.Y = -offset.Y;
                    altContact.normal.Z = -offset.Z;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Called from Simulate
        /// This is the avatar's movement control + PID Controller
        /// </summary>
        /// <param name="timeStep"></param>
        public void Move()
        {
            if(Body == IntPtr.Zero)
                return;

            if (!SafeNativeMethods.BodyIsEnabled(Body))
            {
                if (++_bodydisablecontrol < 50)
                    return;

                // clear residuals
                SafeNativeMethods.BodySetAngularVel(Body, 0f, 0f, 0f);
                SafeNativeMethods.BodySetLinearVel(Body, 0f, 0f, 0f);
                _zeroFlag = true;
                SafeNativeMethods.BodyEnable(Body);
            }

            _bodydisablecontrol = 0;

            SafeNativeMethods.Vector3 dtmp = SafeNativeMethods.BodyGetPosition(Body);
            Vector3 localpos = new Vector3(dtmp.X,dtmp.Y,dtmp.Z);

            // the Amotor still lets avatar rotation to drift during colisions
            // so force it back to identity

            SafeNativeMethods.Quaternion qtmp;
            qtmp.W = _orientation2D.W;
            qtmp.X = _orientation2D.X;
            qtmp.Y = _orientation2D.Y;
            qtmp.Z = _orientation2D.Z;
            SafeNativeMethods.BodySetQuaternion(Body,ref qtmp);

            if(_pidControllerActive == false)
            {
                _zeroPosition = localpos;
            }

            // check outbounds forcing to be in world
            bool fixbody = false;
            float tmp = localpos.X;
            if (float.IsNaN(tmp) || float.IsInfinity(tmp))
            {
                fixbody = true;
                localpos.X = 128f;
            }
            else if (tmp < 0.0f)
            {
                fixbody = true;
                localpos.X = 0.1f;
            }
            else if (tmp > _parent_scene.WorldExtents.X - 0.1f)
            {
                fixbody = true;
                localpos.X = _parent_scene.WorldExtents.X - 0.1f;
            }

            tmp = localpos.Y;
            if (float.IsNaN(tmp) || float.IsInfinity(tmp))
            {
                fixbody = true;
                localpos.X = 128f;
            }
            else if (tmp < 0.0f)
            {
                fixbody = true;
                localpos.Y = 0.1f;
            }
            else if(tmp > _parent_scene.WorldExtents.Y - 0.1)
            {
                fixbody = true;
                localpos.Y = _parent_scene.WorldExtents.Y - 0.1f;
            }

            tmp = localpos.Z;
            if (float.IsNaN(tmp) || float.IsInfinity(tmp))
            {
                fixbody = true;
                localpos.Z = 128f;
            }

            if (fixbody)
            {
                _freemove = false;
                SafeNativeMethods.BodySetPosition(Body,localpos.X,localpos.Y,localpos.Z);
            }

            float breakfactor;

            Vector3 vec = Vector3.Zero;
            dtmp = SafeNativeMethods.BodyGetLinearVel(Body);
            Vector3 vel = new Vector3(dtmp.X,dtmp.Y,dtmp.Z);
            float velLengthSquared = vel.LengthSquared();

            Vector3 ctz = _target_velocity;

            float movementdivisor = 1f;
            //Ubit change divisions into multiplications below
            if(!_alwaysRun)
                movementdivisor = 1 / walkDivisor;
            else
                movementdivisor = 1 / runDivisor;

            ctz.X *= movementdivisor;
            ctz.Y *= movementdivisor;

            //******************************************
            // colide with land

            SafeNativeMethods.AABB aabb;
            //            d.GeomGetAABB(feetbox, out aabb);
            SafeNativeMethods.GeomGetAABB(capsule,out aabb);
            float chrminZ = aabb.MinZ; // move up a bit
            Vector3 posch = localpos;

            float ftmp;

            if(_flying)
            {
                ftmp = timeStep;
                posch.X += vel.X * ftmp;
                posch.Y += vel.Y * ftmp;
            }

            float terrainheight = _parent_scene.GetTerrainHeightAtXY(posch.X,posch.Y);
            if(chrminZ < terrainheight)
            {
                if(ctz.Z < 0)
                    ctz.Z = 0;

                if(!_haveLastFallVel)
                {
                    _lastFallVel = vel;
                    _haveLastFallVel = true;
                }

                Vector3 n = _parent_scene.GetTerrainNormalAtXY(posch.X,posch.Y);
                float depth = terrainheight - chrminZ;

                vec.Z = depth * PID_P * 50;

                if(!_flying)
                {
                    vec.Z += -vel.Z * PID_D;
                    if(n.Z < 0.4f)
                    {
                        vec.X = depth * PID_P * 50 - vel.X * PID_D;
                        vec.X *= n.X;
                        vec.Y = depth * PID_P * 50 - vel.Y * PID_D;
                        vec.Y *= n.Y;
                        vec.Z *= n.Z;
                        if(n.Z < 0.1f)
                        {
                            // cancel the slope pose
                            n.X = 0f;
                            n.Y = 0f;
                            n.Z = 1.0f;
                        }
                    }
                }

                if(depth < 0.2f)
                {
                    _colliderGroundfilter++;
                    if(_colliderGroundfilter > 2)
                    {
                        _iscolliding = true;
                        _colliderfilter = 2;

                        if(_colliderGroundfilter > 10)
                        {
                            _colliderGroundfilter = 10;
                            _freemove = false;
                        }

                        _collideNormal.X = n.X;
                        _collideNormal.Y = n.Y;
                        _collideNormal.Z = n.Z;

                        _iscollidingGround = true;

                        ContactPoint contact = new ContactPoint
                        {
                            PenetrationDepth = depth
                        };
                        contact.Position.X = localpos.X;
                        contact.Position.Y = localpos.Y;
                        contact.Position.Z = terrainheight;
                        contact.SurfaceNormal.X = -n.X;
                        contact.SurfaceNormal.Y = -n.Y;
                        contact.SurfaceNormal.Z = -n.Z;
                        contact.RelativeSpeed = Vector3.Dot(_lastFallVel,n);
                        contact.CharacterFeet = true;
                        AddCollisionEvent(0,contact);
                        _lastFallVel = vel;

                        //                        vec.Z *= 0.5f;
                    }
                }

                else
                {
                    _colliderGroundfilter -= 5;
                    if(_colliderGroundfilter <= 0)
                    {
                        _colliderGroundfilter = 0;
                        _iscollidingGround = false;
                    }
                }
            }
            else
            {
                _haveLastFallVel = false;
                _colliderGroundfilter -= 5;
                if(_colliderGroundfilter <= 0)
                {
                    _colliderGroundfilter = 0;
                    _iscollidingGround = false;
                }
            }

            bool hoverPIDActive = false;

            if(_useHoverPID && _PIDHoverTau != 0 && _PIDHoverHeight != 0)
            {
                hoverPIDActive = true;

                switch(_PIDHoverType)
                {
                    case PIDHoverType.Ground:
                        _targetHoverHeight = terrainheight + _PIDHoverHeight;
                        break;

                    case PIDHoverType.GroundAndWater:
                        float waterHeight = _parent_scene.GetWaterLevel();
                        if(terrainheight > waterHeight)
                            _targetHoverHeight = terrainheight + _PIDHoverHeight;
                        else
                            _targetHoverHeight = waterHeight + _PIDHoverHeight;
                        break;
                }     // end switch (_PIDHoverType)

                // don't go underground
                if(_targetHoverHeight > terrainheight + 0.5f * (aabb.MaxZ - aabb.MinZ))
                {
                    float fz = _targetHoverHeight - localpos.Z;

                    //  if error is zero, use position control; otherwise, velocity control
                    if(Math.Abs(fz) < 0.01f)
                    {
                        ctz.Z = 0;
                    }
                    else
                    {
                        _zeroFlag = false;
                        fz /= _PIDHoverTau;

                        tmp = Math.Abs(fz);
                        if(tmp > 50)
                            fz = 50 * Math.Sign(fz);
                        else if(tmp < 0.1)
                            fz = 0.1f * Math.Sign(fz);

                        ctz.Z = fz;
                    }
                }
            }

            //******************************************
            if(!_iscolliding)
                _collideNormal.Z = 0;

            bool tviszero = ctz.X == 0.0f && ctz.Y == 0.0f && ctz.Z == 0.0f;

            if(!tviszero)
            {
                _freemove = false;

                // movement relative to surface if moving on it
                // dont disturbe vertical movement, ie jumps
                if(_iscolliding && !_flying && ctz.Z == 0 && _collideNormal.Z > 0.2f && _collideNormal.Z < 0.94f)
                {
                    float p = ctz.X * _collideNormal.X + ctz.Y * _collideNormal.Y;
                    ctz.X *= (float)Math.Sqrt(1 - _collideNormal.X * _collideNormal.X);
                    ctz.Y *= (float)Math.Sqrt(1 - _collideNormal.Y * _collideNormal.Y);
                    ctz.Z -= p;
                    if(ctz.Z < 0)
                        ctz.Z *= 2;

                }

            }

            if(!_freemove)
            {
                //  if velocity is zero, use position control; otherwise, velocity control
                if(tviszero) 
                {
                    if(_iscolliding || _flying)
                    {
                        //  keep track of where we stopped.  No more slippin' & slidin'
                        if (!_zeroFlag)
                        {
                            _zeroFlag = true;
                            _zeroPosition = localpos;
                        }
                        if(_pidControllerActive)
                        {
                            // We only want to deactivate the PID Controller if we think we want to have our surrogate
                            // react to the physics scene by moving it's position.
                            // Avatar to Avatar collisions
                            // Prim to avatar collisions

                            vec.X = -vel.X * PID_D * 2f + (_zeroPosition.X - localpos.X) * (PID_P * 5);
                            vec.Y = -vel.Y * PID_D * 2f + (_zeroPosition.Y - localpos.Y) * (PID_P * 5);
                            if(vel.Z > 0)
                                vec.Z += -vel.Z * PID_D + (_zeroPosition.Z - localpos.Z) * PID_P;
                            else
                                vec.Z += (-vel.Z * PID_D + (_zeroPosition.Z - localpos.Z) * PID_P) * 0.2f;
                        }
                    }
                    else
                    {
                        _zeroFlag = false;
                        vec.X += (ctz.X - vel.X) * PID_D * 0.833f;
                        vec.Y += (ctz.Y - vel.Y) * PID_D * 0.833f;
                        // hack for  breaking on fall
                        if (ctz.Z == -9999f)
                            vec.Z += -vel.Z * PID_D - _parent_scene.gravityz * _mass;
                    }
                }
                else
                {
                    _pidControllerActive = true;
                    _zeroFlag = false;

                    if(_iscolliding)
                    {
                        if(!_flying)
                        {
                            // we are on a surface
                            if(ctz.Z > 0f)
                            {
                                // moving up or JUMPING
                                vec.Z += (ctz.Z - vel.Z) * PID_D * 2f;
                                vec.X += (ctz.X - vel.X) * PID_D;
                                vec.Y += (ctz.Y - vel.Y) * PID_D;
                            }
                            else
                            {
                                // we are moving down on a surface
                                if(ctz.Z == 0)
                                {
                                    if(vel.Z > 0)
                                        vec.Z -= vel.Z * PID_D * 2f;
                                    vec.X += (ctz.X - vel.X) * PID_D;
                                    vec.Y += (ctz.Y - vel.Y) * PID_D;
                                }
                                // intencionally going down
                                else
                                {
                                    if(ctz.Z < vel.Z)
                                        vec.Z += (ctz.Z - vel.Z) * PID_D;
                                    else
                                    {
                                    }

                                    if(Math.Abs(ctz.X) > Math.Abs(vel.X))
                                        vec.X += (ctz.X - vel.X) * PID_D;
                                    if(Math.Abs(ctz.Y) > Math.Abs(vel.Y))
                                        vec.Y += (ctz.Y - vel.Y) * PID_D;
                                }
                            }

                            // We're standing on something
                        }
                        else
                        {
                            // We're flying and colliding with something
                            vec.X += (ctz.X - vel.X) * (PID_D * 0.0625f);
                            vec.Y += (ctz.Y - vel.Y) * (PID_D * 0.0625f);
                            vec.Z += (ctz.Z - vel.Z) * (PID_D * 0.0625f);
                        }
                    }
                    else // ie not colliding
                    {
                        if(_flying || hoverPIDActive) //(!_iscolliding && flying)
                        {
                            // we're in mid air suspended
                            vec.X += (ctz.X - vel.X) * PID_D;
                            vec.Y += (ctz.Y - vel.Y) * PID_D;
                            vec.Z += (ctz.Z - vel.Z) * PID_D;
                        }

                        else
                        {
                            // we're not colliding and we're not flying so that means we're falling!
                            // _iscolliding includes collisions with the ground.

                            // d.Vector3 pos = d.BodyGetPosition(Body);
                            vec.X += (ctz.X - vel.X) * PID_D * 0.833f;
                            vec.Y += (ctz.Y - vel.Y) * PID_D * 0.833f;
                            // hack for  breaking on fall
                            if (ctz.Z == -9999f)
                                vec.Z += -vel.Z * PID_D - _parent_scene.gravityz * _mass;
                        }
                    }
                }

                if(velLengthSquared > 2500.0f) // 50m/s apply breaks
                {
                    breakfactor = 0.16f * _mass;
                    vec.X -= breakfactor * vel.X;
                    vec.Y -= breakfactor * vel.Y;
                    vec.Z -= breakfactor * vel.Z;
                }
            }
            else
            {
                breakfactor = _mass;
                vec.X -= breakfactor * vel.X;
                vec.Y -= breakfactor * vel.Y;
                if(_flying)
                    vec.Z -= 0.5f * breakfactor * vel.Z;
                else
                    vec.Z -= .16f* _mass * vel.Z;
            }

            if(_flying || hoverPIDActive)
            {
                vec.Z -= _parent_scene.gravityz * _mass;

                if(!hoverPIDActive)
                {
                    //Added for auto fly height. Kitto Flora
                    float target_altitude = terrainheight + MinimumGroundFlightOffset;

                    if(localpos.Z < target_altitude)
                    {
                        vec.Z += (target_altitude - localpos.Z) * PID_P * 5.0f;
                    }
                    // end add Kitto Flora
                }
            }

            if(vec.IsFinite())
            {
                if(vec.X != 0 || vec.Y !=0 || vec.Z !=0)
                    SafeNativeMethods.BodyAddForce(Body,vec.X,vec.Y,vec.Z);
            }
 
            // update our local ideia of position velocity and aceleration
            //            _position = localpos;
            _position = localpos;

            if(_zeroFlag)
            {
                _velocity = Vector3.Zero;
                _acceleration = Vector3.Zero;
                _rotationalVelocity = Vector3.Zero;
            }
            else
            {
                Vector3 a = _velocity; // previous velocity
                SetSmooth(ref _velocity,ref vel,2);
                a = (_velocity - a) * invtimeStep;
                SetSmooth(ref _acceleration,ref a,2);

                dtmp = SafeNativeMethods.BodyGetAngularVel(Body);
                _rotationalVelocity.X = 0f;
                _rotationalVelocity.Y = 0f;
                _rotationalVelocity.Z = dtmp.Z;
                Math.Round(_rotationalVelocity.Z,3);
            }
        }

        public void round(ref Vector3 v,int digits)
        {
            v.X = (float)Math.Round(v.X,digits);
            v.Y = (float)Math.Round(v.Y,digits);
            v.Z = (float)Math.Round(v.Z,digits);
        }

        public void SetSmooth(ref Vector3 dst,ref Vector3 value)
        {
            dst.X = 0.1f * dst.X + 0.9f * value.X;
            dst.Y = 0.1f * dst.Y + 0.9f * value.Y;
            dst.Z = 0.1f * dst.Z + 0.9f * value.Z;
        }

        public void SetSmooth(ref Vector3 dst,ref Vector3 value,int rounddigits)
        {
            dst.X = 0.4f * dst.X + 0.6f * value.X;
            dst.X = (float)Math.Round(dst.X,rounddigits);

            dst.Y = 0.4f * dst.Y + 0.6f * value.Y;
            dst.Y = (float)Math.Round(dst.Y,rounddigits);

            dst.Z = 0.4f * dst.Z + 0.6f * value.Z;
            dst.Z = (float)Math.Round(dst.Z,rounddigits);
        }


        /// <summary>
        /// Updates the reported position and velocity.
        /// Used to copy variables from unmanaged space at heartbeat rate and also trigger scene updates acording
        /// also outbounds checking
        /// copy and outbounds now done in move(..) at ode rate
        ///
        /// </summary>
        public void UpdatePositionAndVelocity()
        {
            return;

            //            if (Body == IntPtr.Zero)
            //                return;

        }

        /// <summary>
        /// Cleanup the things we use in the scene.
        /// </summary>
        public void Destroy()
        {
            AddChange(changes.Remove,null);
        }

        public override void CrossingFailure()
        {
        }

        public override Vector3 PIDTarget
        {
            set
            {
                return;
            }
        }
        public override bool PIDActive
        {
            get => _pidControllerActive;
            set
            {
                return;
            }
        }
        public override float PIDTau
        {
            set
            {
                return;
            }
        }

        public override float PIDHoverHeight
        {
            set => AddChange(changes.PIDHoverHeight,value);
        }
        public override bool PIDHoverActive
        {
            get => _useHoverPID;
            set => AddChange(changes.PIDHoverActive,value);
        }

        public override PIDHoverType PIDHoverType
        {
            set => AddChange(changes.PIDHoverType,value);
        }

        public override float PIDHoverTau
        {
            set
            {
                float tmp = 0;
                if(value > 0)
                {
                    float mint = 0.05f > timeStep ? 0.05f : timeStep;
                    if(value < mint)
                        tmp = mint;
                    else
                        tmp = value;
                }
                AddChange(changes.PIDHoverTau,tmp);
            }
        }

        public override Quaternion APIDTarget
        {
            set
            {
                return;
            }
        }

        public override bool APIDActive
        {
            set
            {
                return;
            }
        }

        public override float APIDStrength
        {
            set
            {
                return;
            }
        }

        public override float APIDDamping
        {
            set
            {
                return;
            }
        }

        public override void SubscribeEvents(int ms)
        {
            _eventsubscription = ms;
            _cureventsubscription = 0;
            CollisionEventsThisFrame.Clear();
            SentEmptyCollisionsEvent = false;
        }

        public override void UnSubscribeEvents()
        {
            _eventsubscription = 0;
            _parent_scene.RemoveCollisionEventReporting(this);
            lock(CollisionEventsThisFrame)
                CollisionEventsThisFrame.Clear();
        }

        public override void AddCollisionEvent(uint CollidedWith,ContactPoint contact)
        {
            lock(CollisionEventsThisFrame)
                CollisionEventsThisFrame.AddCollider(CollidedWith,contact);
            _parent_scene.AddCollisionEventReporting(this);
        }

        public void SendCollisions(int timestep)
        {
            if(_cureventsubscription < 50000)
                _cureventsubscription += timestep;

            if(_cureventsubscription < _eventsubscription)
                return;

            if(Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
                return;

            lock(CollisionEventsThisFrame)
            {
                int ncolisions = CollisionEventsThisFrame._objCollisionList.Count;

                if(!SentEmptyCollisionsEvent || ncolisions > 0)
                {
                    base.SendCollisionUpdate(CollisionEventsThisFrame);
                    _cureventsubscription = 0;

                    if(ncolisions == 0)
                    {
                        SentEmptyCollisionsEvent = true;
                        //                  _parent_scene.RemoveCollisionEventReporting(this);
                    }
                    else
                    {
                        SentEmptyCollisionsEvent = false;
                        CollisionEventsThisFrame.Clear();
                    }
                }
            }
        }

        public override bool SubscribedEvents()
        {
            if(_eventsubscription > 0)
                return true;
            return false;
        }

        private void changePhysicsStatus(bool NewStatus)
        {
            if(NewStatus != _isPhysical)
            {
                if(NewStatus)
                {
                    AvatarGeomAndBodyDestroy();

                    AvatarGeomAndBodyCreation(_position.X,_position.Y,_position.Z);

                    _parent_scene.actor_name_map[capsule] = (PhysicsActor)this;
                    _parent_scene.AddCharacter(this);
                }
                else
                {
                    _parent_scene.RemoveCollisionEventReporting(this);
                    _parent_scene.RemoveCharacter(this);
                    // destroy avatar capsule and related ODE data
                    AvatarGeomAndBodyDestroy();
                }
                _freemove = false;
                _isPhysical = NewStatus;
            }
        }

        private void changeAdd()
        {
            changePhysicsStatus(true);
        }

        private void changeRemove()
        {
            changePhysicsStatus(false);
        }

        private void changeShape(PrimitiveBaseShape arg)
        {
        }

        private void changeAvatarSize(strAvatarSize st)
        {
            _feetOffset = st.offset;
            changeSize(st.size);
        }

        private void changeSize(Vector3 pSize)
        {
            if(pSize.IsFinite())
            {
                // for now only look to Z changes since viewers also don't change X and Y
                if(pSize.Z != _size.Z)
                {
                    float oldsz = _size.Z;
                    _size = pSize;

                    float sz = _size.Z;

                    float bot = -sz * 0.5f + _feetOffset;
                    boneOff = bot + 0.3f;

                    float feetsz = sz * 0.45f;
                    if (feetsz > 0.6f)
                        feetsz = 0.6f;
                    feetOff = bot + feetsz;

                    float sx = _size.X;
                    AvaAvaSizeXsq = 0.4f * sx;
                    AvaAvaSizeXsq *= AvaAvaSizeXsq;

                    float sy = _size.Y;
                    AvaAvaSizeYsq = 0.5f * sy;
                    AvaAvaSizeYsq *= AvaAvaSizeYsq;

                    float r = sx;
                    if (sy > r)
                        r = sy;
                    float l = sz - r;
                    r *= 0.5f;

                    SafeNativeMethods.GeomCapsuleSetParams(capsule, r, l);

                    _mass = _density * sx * sy * sz;  // update mass
                    PID_D = basePID_D * _mass / _parent_scene.ODE_STEPSIZE;
                    PID_P = basePID_P * _mass / _parent_scene.ODE_STEPSIZE;
                    SafeNativeMethods.MassSetBoxTotal(out ShellMass, _mass, sx, sy, sz);
                    SafeNativeMethods.BodySetMass(Body, ref ShellMass);

                    _position.Z += (sz - oldsz) * 0.5f;
                    SafeNativeMethods.BodySetPosition(Body, _position.X, _position.Y, _position.Z);

                    _bodydisablecontrol = 0;
                    _zeroFlag = false;
                    _velocity = Vector3.Zero;
                    _targetVelocity = Vector3.Zero;
                }
                _freemove = false;
                _pidControllerActive = true;
            }
            else
            {
                _log.Warn("[PHYSICS]: Got a NaN Size from Scene on a Character");
            }
        }

        private void changePosition(Vector3 newPos)
        {
            if(Body != IntPtr.Zero)
            {
                SafeNativeMethods.BodySetPosition(Body, newPos.X, newPos.Y, newPos.Z);
                SafeNativeMethods.BodyEnable(Body);
            }

            _position = newPos;
            _freemove = false;
            _zeroFlag = false;
            _pidControllerActive = true;
        }

        private void changeOrientation(Quaternion newOri)
        {
            if(_orientation != newOri)
            {
                _orientation = newOri; // keep a copy for core use
                // but only use rotations around Z

                _orientation2D.W = newOri.W;
                _orientation2D.Z = newOri.Z;

                float t = _orientation2D.W * _orientation2D.W + _orientation2D.Z * _orientation2D.Z;
                if(t > 0)
                {
                    t = 1.0f / (float)Math.Sqrt(t);
                    _orientation2D.W *= t;
                    _orientation2D.Z *= t;
                }
                else
                {
                    _orientation2D.W = 1.0f;
                    _orientation2D.Z = 0f;
                }
                _orientation2D.Y = 0f;
                _orientation2D.X = 0f;

                if (Body != IntPtr.Zero)
                {
                    SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                    {
                        X = _orientation2D.X,
                        Y = _orientation2D.Y,
                        Z = _orientation2D.Z,
                        W = _orientation2D.W
                    };
                    SafeNativeMethods.BodySetQuaternion(Body,ref myrot);
                    SafeNativeMethods.BodyEnable(Body);
                }
            }
        }

        private void changeVelocity(Vector3 newVel)
        {
            _velocity = newVel;
            setFreeMove();

            if (Body != IntPtr.Zero)
            {
                SafeNativeMethods.BodySetLinearVel(Body, newVel.X, newVel.Y, newVel.Z);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changeTargetVelocity(Vector3 newVel)
        {
            //_pidControllerActive = true;
            //_freemove = false;
            _target_velocity = newVel;
            if (Body != IntPtr.Zero)
                SafeNativeMethods.BodyEnable(Body);
        }

        private void changeSetTorque(Vector3 newTorque)
        {
        }

        private void changeAddForce(Vector3 newForce)
        {
        }

        private void changeAddAngularForce(Vector3 arg)
        {
        }

        private void changeAngularLock(byte arg)
        {
        }

        private void changeFloatOnWater(bool arg)
        {
        }

        private void changeVolumedetetion(bool arg)
        {
        }

        private void changeSelectedStatus(bool arg)
        {
        }

        private void changeDisable(bool arg)
        {
        }

        private void changeBuilding(bool arg)
        {
        }

        private void setFreeMove()
        {
            _pidControllerActive = true;
            _zeroFlag = false;
            _target_velocity = Vector3.Zero;
            _freemove = true;
            _colliderfilter = -1;
            _colliderObjectfilter = -1;
            _colliderGroundfilter = -1;

            _iscolliding = false;
            _iscollidingGround = false;
            _iscollidingObj = false;

            CollisionEventsThisFrame.Clear();
        }

        private void changeForce(Vector3 newForce)
        {
            setFreeMove();

            if(Body != IntPtr.Zero)
            {
                if(newForce.X != 0f || newForce.Y != 0f || newForce.Z != 0)
                    SafeNativeMethods.BodyAddForce(Body,newForce.X,newForce.Y,newForce.Z);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        // for now momentum is actually velocity
        private void changeMomentum(Vector3 newmomentum)
        {
            _velocity = newmomentum;
            setFreeMove();

            if(Body != IntPtr.Zero)
            {
                SafeNativeMethods.BodySetLinearVel(Body,newmomentum.X,newmomentum.Y,newmomentum.Z);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDHoverHeight(float val)
        {
            _PIDHoverHeight = val;
            if(val == 0)
                _useHoverPID = false;
        }

        private void changePIDHoverType(PIDHoverType type)
        {
            _PIDHoverType = type;
        }

        private void changePIDHoverTau(float tau)
        {
            _PIDHoverTau = tau;
        }

        private void changePIDHoverActive(bool active)
        {
            _useHoverPID = active;
        }

        private void donullchange()
        {
        }

        public bool DoAChange(changes what,object arg)
        {
            if(collider == IntPtr.Zero && what != changes.Add && what != changes.Remove)
            {
                return false;
            }

            // nasty switch
            switch(what)
            {
                case changes.Add:
                    changeAdd();
                    break;
                case changes.Remove:
                    changeRemove();
                    break;

                case changes.Position:
                    changePosition((Vector3)arg);
                    break;

                case changes.Orientation:
                    changeOrientation((Quaternion)arg);
                    break;

                case changes.PosOffset:
                    donullchange();
                    break;

                case changes.OriOffset:
                    donullchange();
                    break;

                case changes.Velocity:
                    changeVelocity((Vector3)arg);
                    break;

                case changes.TargetVelocity:
                    changeTargetVelocity((Vector3)arg);
                    break;

                //                case changes.Acceleration:
                //                    changeacceleration((Vector3)arg);
                //                    break;
                //                case changes.AngVelocity:
                //                    changeangvelocity((Vector3)arg);
                //                    break;

                case changes.Force:
                    changeForce((Vector3)arg);
                    break;

                case changes.Torque:
                    changeSetTorque((Vector3)arg);
                    break;

                case changes.AddForce:
                    changeAddForce((Vector3)arg);
                    break;

                case changes.AddAngForce:
                    changeAddAngularForce((Vector3)arg);
                    break;

                case changes.AngLock:
                    changeAngularLock((byte)arg);
                    break;

                case changes.Size:
                    changeSize((Vector3)arg);
                    break;

                case changes.AvatarSize:
                    changeAvatarSize((strAvatarSize)arg);
                    break;

                case changes.Momentum:
                    changeMomentum((Vector3)arg);
                    break;

                case changes.PIDHoverHeight:
                    changePIDHoverHeight((float)arg);
                    break;

                case changes.PIDHoverType:
                    changePIDHoverType((PIDHoverType)arg);
                    break;

                case changes.PIDHoverTau:
                    changePIDHoverTau((float)arg);
                    break;

                case changes.PIDHoverActive:
                    changePIDHoverActive((bool)arg);
                    break;

                /* not in use for now
                                case changes.Shape:
                                    changeShape((PrimitiveBaseShape)arg);
                                    break;

                                case changes.CollidesWater:
                                    changeFloatOnWater((bool)arg);
                                    break;

                                case changes.VolumeDtc:
                                    changeVolumedetetion((bool)arg);
                                    break;

                                case changes.Physical:
                                    changePhysicsStatus((bool)arg);
                                    break;

                                case changes.Selected:
                                    changeSelectedStatus((bool)arg);
                                    break;

                                case changes.disabled:
                                    changeDisable((bool)arg);
                                    break;

                                case changes.building:
                                    changeBuilding((bool)arg);
                                    break;
                */
                case changes.Null:
                    donullchange();
                    break;

                default:
                    donullchange();
                    break;
            }
            return false;
        }

        public void AddChange(changes what,object arg)
        {
            _parent_scene.AddChange((PhysicsActor)this,what,arg);
        }

        private struct strAvatarSize
        {
            public Vector3 size;
            public float offset;
        }

    }
}
