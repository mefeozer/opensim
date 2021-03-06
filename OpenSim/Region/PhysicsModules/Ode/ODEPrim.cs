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

/*
 * Revised August 26 2009 by Kitto Flora. ODEDynamics.cs replaces
 * ODEVehicleSettings.cs. It and ODEPrim.cs are re-organised:
 * ODEPrim.cs contains methods dealing with Prim editing, Prim
 * characteristics and Kinetic motion.
 * ODEDynamics.cs contains methods dealing with Prim Physical motion
 * (dynamics) and the associated settings. Old Linear and angular
 * motors for dynamic motion have been replace with  MoveLinear()
 * and MoveAngular(); 'Physical' is used only to switch ODE dynamic
 * simualtion on/off; VEHICAL_TYPE_NONE/VEHICAL_TYPE_<other> is to
 * switch between 'VEHICLE' parameter use and general dynamics
 * settings use.
 */

//#define SPAM

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.ODE
{
    /// <summary>
    /// Various properties that ODE uses for AMotors but isn't exposed in ODE.NET so we must define them ourselves.
    /// </summary>
    public class OdePrim : PhysicsActor
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _isphysical;

        public int ExpectedCollisionContacts => _expectedCollisionContacts;
        private int _expectedCollisionContacts = 0;

        /// <summary>
        /// Gets collide bits so that we can still perform land collisions if a mesh fails to load.
        /// </summary>
        private int BadMeshAssetCollideBits => _isphysical ? (int)CollisionCategories.Land : 0;

        /// <summary>
        /// Is this prim subject to physics?  Even if not, it's still solid for collision purposes.
        /// </summary>
        public override bool IsPhysical
        {
            get => _isphysical;
            set
            {
                _isphysical = value;
                if (!_isphysical)
                {
                    _zeroFlag = true; // Zero the remembered last velocity
                    _lastVelocity = Vector3.Zero;
                    _acceleration = Vector3.Zero;
                    _velocity = Vector3.Zero;
                    _taintVelocity = Vector3.Zero;
                    _rotationalVelocity = Vector3.Zero;
                }
            }
        }

        private Vector3 _position;
        private Vector3 _velocity;
        private Vector3 _torque;
        private Vector3 _lastVelocity;
        private Vector3 _lastposition;
        private Quaternion _lastorientation = new Quaternion();
        private Vector3 _rotationalVelocity;
        private Vector3 _size;
        private Vector3 _acceleration;
        // private d.Vector3 _zeroPosition = new d.Vector3(0.0f, 0.0f, 0.0f);
        private Quaternion _orientation;
        private Vector3 _taintposition;
        private Vector3 _taintsize;
        private Vector3 _taintVelocity;
        private Vector3 _taintTorque;
        private Quaternion _taintrot;

        private IntPtr Amotor = IntPtr.Zero;

        private byte _taintAngularLock = 0;
        private byte _angularlock = 0;

        private bool _assetFailed = false;

        private Vector3 _PIDTarget;
        private float _PIDTau;
        private readonly float PID_D = 35f;
        private float PID_G = 25f;

        // KF: These next 7 params apply to llSetHoverHeight(float height, integer water, float tau),
        // and are for non-VEHICLES only.

        private float _PIDHoverHeight;
        private float _PIDHoverTau;
        private bool _useHoverPID;
        private PIDHoverType _PIDHoverType = PIDHoverType.Ground;
        private float _targetHoverHeight;
        private float _groundHeight;
        private float _waterHeight;
        private float _buoyancy;                //KF: _buoyancy should be set by llSetBuoyancy() for non-vehicle.

        // private float _tensor = 5f;
        private readonly int body_autodisable_frames = 20;


        private const CollisionCategories _default_collisionFlags = CollisionCategories.Geom
                                                                     | CollisionCategories.Space
                                                                     | CollisionCategories.Body
                                                                     | CollisionCategories.Character;
        private bool _taintshape;
        private bool _taintPhysics;
        private readonly bool _collidesLand = true;
        private bool _collidesWater;

        // Default we're a Geometry
        private CollisionCategories _collisionCategories = CollisionCategories.Geom;

        // Default, Collide with Other Geometries, spaces and Bodies
        private CollisionCategories _collisionFlags = _default_collisionFlags;

        public bool _taintremove { get; private set; }
        public bool _taintdisable { get; private set; }
        internal bool _disabled;
        public bool _taintadd { get; private set; }
        public bool _taintselected { get; private set; }
        public bool _taintCollidesWater { get; private set; }

        private bool _taintforce = false;
        private bool _taintaddangularforce = false;
        private Vector3 _force;
        private List<Vector3> _forcelist = new List<Vector3>();
        private readonly List<Vector3> _angularforcelist = new List<Vector3>();

        private PrimitiveBaseShape _pbs;
        private readonly OdeScene _parent_scene;

        /// <summary>
        /// The physics space which contains prim geometries
        /// </summary>
        public IntPtr _targetSpace = IntPtr.Zero;

        /// <summary>
        /// The prim geometry, used for collision detection.
        /// </summary>
        /// <remarks>
        /// This is never null except for a brief period when the geometry needs to be replaced (due to resizing or
        /// mesh change) or when the physical prim is being removed from the scene.
        /// </remarks>
        public IntPtr pri_geom { get; private set; }

        public IntPtr _triMeshData { get; private set; }

        private readonly IntPtr _linkJointGroup = IntPtr.Zero;
        private PhysicsActor _parent;
        private PhysicsActor _taintparent;

        private readonly List<OdePrim> childrenPrim = new List<OdePrim>();

        private bool iscolliding;
        private bool _isSelected;

        internal bool _isVolumeDetect; // If true, this prim only detects collisions but doesn't collide actively

        private bool _throttleUpdates;
        private int throttleCounter;
        public int _interpenetrationcount { get; private set; }
        internal float _collisionscore;
        public int _roundsUnderMotionThreshold { get; private set; }

        public bool outofBounds { get; private set; }
        private readonly float _density = 10.000006836f; // Aluminum g/cm3;

        public bool _zeroFlag { get; private set; }
        private bool _lastUpdateSent;

        public IntPtr Body = IntPtr.Zero;
        private Vector3 _target_velocity;
        private SafeNativeMethods.Mass pMass;

        private int _eventsubscription;
        private readonly CollisionEventUpdate CollisionEventsThisFrame = new CollisionEventUpdate();

        /// <summary>
        /// Signal whether there were collisions on the previous frame, so we know if we need to send the
        /// empty CollisionEventsThisFrame to the prim so that it can detect the end of a collision.
        /// </summary>
        /// <remarks>
        /// This is probably a temporary measure, pending storing this information consistently in CollisionEventUpdate itself.
        /// </remarks>
        private bool _collisionsOnPreviousFrame;

        private IntPtr _linkJoint = IntPtr.Zero;

        internal volatile bool childPrim;

        private readonly ODEDynamics _vehicle;

        internal int _material = (int)Material.Wood;

        public OdePrim(
            string primName, OdeScene parent_scene, Vector3 pos, Vector3 size,
            Quaternion rotation, PrimitiveBaseShape pbs, bool pisPhysical)
        {
            Name = primName;
            _vehicle = new ODEDynamics();
            //gc = GCHandle.Alloc(pri_geom, GCHandleType.Pinned);

            if (!pos.IsFinite())
            {
                pos = new Vector3(Constants.RegionSize * 0.5f, Constants.RegionSize * 0.5f,
                    parent_scene.GetTerrainHeightAtXY(Constants.RegionSize * 0.5f, Constants.RegionSize * 0.5f) + 0.5f);
                _log.WarnFormat("[PHYSICS]: Got nonFinite Object create Position for {0}", Name);
            }
            _position = pos;
            _taintposition = pos;
            PID_D = parent_scene.bodyPIDD;
            PID_G = parent_scene.bodyPIDG;
            _density = parent_scene.geomDefaultDensity;
            // _tensor = parent_scene.bodyMotorJointMaxforceTensor;
            body_autodisable_frames = parent_scene.bodyFramesAutoDisable;

            pri_geom = IntPtr.Zero;

            if (!pos.IsFinite())
            {
                size = new Vector3(0.5f, 0.5f, 0.5f);
                _log.WarnFormat("[PHYSICS]: Got nonFinite Object create Size for {0}", Name);
            }

            if (size.X <= 0) size.X = 0.01f;
            if (size.Y <= 0) size.Y = 0.01f;
            if (size.Z <= 0) size.Z = 0.01f;

            _size = size;
            _taintsize = _size;

            if (!QuaternionIsFinite(rotation))
            {
                rotation = Quaternion.Identity;
                _log.WarnFormat("[PHYSICS]: Got nonFinite Object create Rotation for {0}", Name);
            }

            _orientation = rotation;
            _taintrot = _orientation;
            _pbs = pbs;

            _parent_scene = parent_scene;
            _targetSpace = (IntPtr)0;

            if (pos.Z < 0)
            {
                IsPhysical = false;
            }
            else
            {
                IsPhysical = pisPhysical;
                // If we're physical, we need to be in the master space for now.
                // linksets *should* be in a space together..  but are not currently
                if (IsPhysical)
                    _targetSpace = _parent_scene.space;
            }

            _taintadd = true;
            _assetFailed = false;
            _parent_scene.AddPhysicsActorTaint(this);
        }

        public override int PhysicsActorType
        {
            get => (int) ActorTypes.Prim;
            set { return; }
        }

        public override bool SetAlwaysRun
        {
            get => false;
            set { return; }
        }

        public override bool Grabbed
        {
            set { return; }
        }

        public override bool Selected
        {
            set
            {
                // This only makes the object not collidable if the object
                // is physical or the object is modified somehow *IN THE FUTURE*
                // without this, if an avatar selects prim, they can walk right
                // through it while it's selected
                _collisionscore = 0;

                if (IsPhysical && !_zeroFlag || !value)
                {
                    _taintselected = value;
                    _parent_scene.AddPhysicsActorTaint(this);
                }
                else
                {
                    _taintselected = value;
                    _isSelected = value;
                }

                if (_isSelected)
                    disableBodySoft();
            }
        }

        /// <summary>
        /// Set a new geometry for this prim.
        /// </summary>
        /// <param name="geom"></param>
        private void SetGeom(IntPtr geom)
        {
            pri_geom = geom;
//Console.WriteLine("SetGeom to " + pri_geom + " for " + Name);

            if (_assetFailed)
            {
                SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)BadMeshAssetCollideBits);
            }
            else
            {
                SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
            }

            _parent_scene.geo_name_map[pri_geom] = Name;
            _parent_scene.actor_name_map[pri_geom] = this;

            if (childPrim)
            {
                if (_parent != null && _parent is OdePrim)
                {
                    OdePrim parent = (OdePrim)_parent;
//Console.WriteLine("SetGeom calls ChildSetGeom");
                    parent.ChildSetGeom(this);
                }
            }
            //_log.Warn("Setting Geom to: " + pri_geom);
        }

        private void enableBodySoft()
        {
            if (!childPrim)
            {
                if (IsPhysical && Body != IntPtr.Zero)
                {
                    SafeNativeMethods.BodyEnable(Body);
                    if (_vehicle.Type != Vehicle.TYPE_NONE)
                        _vehicle.Enable(Body, _parent_scene);
                }

                _disabled = false;
            }
        }

        private void disableBodySoft()
        {
            _disabled = true;

            if (IsPhysical && Body != IntPtr.Zero)
            {
                SafeNativeMethods.BodyDisable(Body);
            }
        }

        /// <summary>
        /// Make a prim subject to physics.
        /// </summary>
        private void enableBody()
        {
            // Don't enable this body if we're a child prim
            // this should be taken care of in the parent function not here
            if (!childPrim)
            {
                // Sets the geom to a body
                Body = SafeNativeMethods.BodyCreate(_parent_scene.world);

                setMass();
                SafeNativeMethods.BodySetPosition(Body, _position.X, _position.Y, _position.Z);
                SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                {
                    X = _orientation.X,
                    Y = _orientation.Y,
                    Z = _orientation.Z,
                    W = _orientation.W
                };
                SafeNativeMethods.BodySetQuaternion(Body, ref myrot);
                SafeNativeMethods.GeomSetBody(pri_geom, Body);

                if (_assetFailed)
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)BadMeshAssetCollideBits);
                }
                else
                {
                    _collisionCategories |= CollisionCategories.Body;
                    _collisionFlags |= CollisionCategories.Land | CollisionCategories.Wind;
                }

                SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);

                SafeNativeMethods.BodySetAutoDisableFlag(Body, true);
                SafeNativeMethods.BodySetAutoDisableSteps(Body, body_autodisable_frames);

                // disconnect from world gravity so we can apply buoyancy
                SafeNativeMethods.BodySetGravityMode (Body, false);

                _interpenetrationcount = 0;
                _collisionscore = 0;
                _disabled = false;

                // The body doesn't already have a finite rotation mode set here
                if (_angularlock != 0 && _parent == null)
                {
                    createAMotor(_angularlock);
                }
                if (_vehicle.Type != Vehicle.TYPE_NONE)
                {
                    _vehicle.Enable(Body, _parent_scene);
                }

                _parent_scene.ActivatePrim(this);
            }
        }

        #region Mass Calculation

        private float CalculateMass()
        {
            float volume = _size.X * _size.Y * _size.Z; // default
            float tmp;

            float returnMass = 0;
            float hollowAmount = _pbs.ProfileHollow * 2.0e-5f;
            float hollowVolume = hollowAmount * hollowAmount;

            switch (_pbs.ProfileShape)
            {
                case ProfileShape.Square:
                    // default box

                    if (_pbs.PathCurve == (byte)Extrusion.Straight)
                        {
                        if (hollowAmount > 0.0)
                            {
                            switch (_pbs.HollowShape)
                                {
                                case HollowShape.Square:
                                case HollowShape.Same:
                                    break;

                                case HollowShape.Circle:

                                    hollowVolume *= 0.78539816339f;
                                    break;

                                case HollowShape.Triangle:

                                    hollowVolume *= 0.5f * .5f;
                                    break;

                                default:
                                    hollowVolume = 0;
                                    break;
                                }
                            volume *= 1.0f - hollowVolume;
                            }
                        }

                    else if (_pbs.PathCurve == (byte)Extrusion.Curve1)
                        {
                        //a tube

                        volume *= 0.78539816339e-2f * (200 - _pbs.PathScaleX);
                        tmp= 1.0f -2.0e-2f * (200 - _pbs.PathScaleY);
                        volume -= volume*tmp*tmp;

                        if (hollowAmount > 0.0)
                            {
                            hollowVolume *= hollowAmount;

                            switch (_pbs.HollowShape)
                                {
                                case HollowShape.Square:
                                case HollowShape.Same:
                                    break;

                                case HollowShape.Circle:
                                    hollowVolume *= 0.78539816339f;;
                                    break;

                                case HollowShape.Triangle:
                                    hollowVolume *= 0.5f * 0.5f;
                                    break;
                                default:
                                    hollowVolume = 0;
                                    break;
                                }
                            volume *= 1.0f - hollowVolume;
                            }
                        }

                    break;

                case ProfileShape.Circle:

                    if (_pbs.PathCurve == (byte)Extrusion.Straight)
                        {
                        volume *= 0.78539816339f; // elipse base

                        if (hollowAmount > 0.0)
                            {
                            switch (_pbs.HollowShape)
                                {
                                case HollowShape.Same:
                                case HollowShape.Circle:
                                    break;

                                case HollowShape.Square:
                                    hollowVolume *= 0.5f * 2.5984480504799f;
                                    break;

                                case HollowShape.Triangle:
                                    hollowVolume *= .5f * 1.27323954473516f;
                                    break;

                                default:
                                    hollowVolume = 0;
                                    break;
                                }
                            volume *= 1.0f - hollowVolume;
                            }
                        }

                    else if (_pbs.PathCurve == (byte)Extrusion.Curve1)
                        {
                        volume *= 0.61685027506808491367715568749226e-2f * (200 - _pbs.PathScaleX);
                        tmp = 1.0f - .02f * (200 - _pbs.PathScaleY);
                        volume *= 1.0f - tmp * tmp;

                        if (hollowAmount > 0.0)
                            {

                            // calculate the hollow volume by it's shape compared to the prim shape
                            hollowVolume *= hollowAmount;

                            switch (_pbs.HollowShape)
                                {
                                case HollowShape.Same:
                                case HollowShape.Circle:
                                    break;

                                case HollowShape.Square:
                                    hollowVolume *= 0.5f * 2.5984480504799f;
                                    break;

                                case HollowShape.Triangle:
                                    hollowVolume *= .5f * 1.27323954473516f;
                                    break;

                                default:
                                    hollowVolume = 0;
                                    break;
                                }
                            volume *= 1.0f - hollowVolume;
                            }
                        }
                    break;

                case ProfileShape.HalfCircle:
                    if (_pbs.PathCurve == (byte)Extrusion.Curve1)
                    {
                    volume *= 0.52359877559829887307710723054658f;
                    }
                    break;

                case ProfileShape.EquilateralTriangle:

                    if (_pbs.PathCurve == (byte)Extrusion.Straight)
                        {
                        volume *= 0.32475953f;

                        if (hollowAmount > 0.0)
                            {

                            // calculate the hollow volume by it's shape compared to the prim shape
                            switch (_pbs.HollowShape)
                                {
                                case HollowShape.Same:
                                case HollowShape.Triangle:
                                    hollowVolume *= .25f;
                                    break;

                                case HollowShape.Square:
                                    hollowVolume *= 0.499849f * 3.07920140172638f;
                                    break;

                                case HollowShape.Circle:
                                    // Hollow shape is a perfect cyllinder in respect to the cube's scale
                                    // Cyllinder hollow volume calculation

                                    hollowVolume *= 0.1963495f * 3.07920140172638f;
                                    break;

                                default:
                                    hollowVolume = 0;
                                    break;
                                }
                            volume *= 1.0f - hollowVolume;
                            }
                        }
                    else if (_pbs.PathCurve == (byte)Extrusion.Curve1)
                        {
                        volume *= 0.32475953f;
                        volume *= 0.01f * (200 - _pbs.PathScaleX);
                        tmp = 1.0f - .02f * (200 - _pbs.PathScaleY);
                        volume *= 1.0f - tmp * tmp;

                        if (hollowAmount > 0.0)
                            {

                            hollowVolume *= hollowAmount;

                            switch (_pbs.HollowShape)
                                {
                                case HollowShape.Same:
                                case HollowShape.Triangle:
                                    hollowVolume *= .25f;
                                    break;

                                case HollowShape.Square:
                                    hollowVolume *= 0.499849f * 3.07920140172638f;
                                    break;

                                case HollowShape.Circle:

                                    hollowVolume *= 0.1963495f * 3.07920140172638f;
                                    break;

                                default:
                                    hollowVolume = 0;
                                    break;
                                }
                            volume *= 1.0f - hollowVolume;
                            }
                        }
                        break;

                default:
                    break;
                }

            float taperX1;
            float taperY1;
            float taperX;
            float taperY;
            float pathBegin;
            float pathEnd;
            float profileBegin;
            float profileEnd;

            if (_pbs.PathCurve == (byte)Extrusion.Straight || _pbs.PathCurve == (byte)Extrusion.Flexible)
            {
                taperX1 = _pbs.PathScaleX * 0.01f;
                if (taperX1 > 1.0f)
                    taperX1 = 2.0f - taperX1;
                taperX = 1.0f - taperX1;

                taperY1 = _pbs.PathScaleY * 0.01f;
                if (taperY1 > 1.0f)
                    taperY1 = 2.0f - taperY1;
                taperY = 1.0f - taperY1;
            }
            else
            {
                taperX = _pbs.PathTaperX * 0.01f;
                if (taperX < 0.0f)
                    taperX = -taperX;
                taperX1 = 1.0f - taperX;

                taperY = _pbs.PathTaperY * 0.01f;
                if (taperY < 0.0f)
                    taperY = -taperY;
                taperY1 = 1.0f - taperY;
            }

            volume *= taperX1 * taperY1 + 0.5f * (taperX1 * taperY + taperX * taperY1) + 0.3333333333f * taperX * taperY;

            pathBegin = _pbs.PathBegin * 2.0e-5f;
            pathEnd = 1.0f - _pbs.PathEnd * 2.0e-5f;
            volume *= pathEnd - pathBegin;

// this is crude aproximation
            profileBegin = _pbs.ProfileBegin * 2.0e-5f;
            profileEnd = 1.0f - _pbs.ProfileEnd * 2.0e-5f;
            volume *= profileEnd - profileBegin;

            returnMass = _density * volume;

            if (returnMass <= 0)
                returnMass = 0.0001f;//ckrinke: Mass must be greater then zero.
//            else if (returnMass > _parent_scene.maximumMassObject)
//                returnMass = _parent_scene.maximumMassObject;

            // Recursively calculate mass
            bool HasChildPrim = false;
            lock (childrenPrim)
            {
                if (childrenPrim.Count > 0)
                {
                    HasChildPrim = true;
                }
            }

            if (HasChildPrim)
            {
                OdePrim[] childPrimArr = new OdePrim[0];

                lock (childrenPrim)
                    childPrimArr = childrenPrim.ToArray();

                for (int i = 0; i < childPrimArr.Length; i++)
                {
                    if (childPrimArr[i] != null && !childPrimArr[i]._taintremove)
                        returnMass += childPrimArr[i].CalculateMass();
                    // failsafe, this shouldn't happen but with OpenSim, you never know :)
                    if (i > 256)
                        break;
                }
            }

            if (returnMass > _parent_scene.maximumMassObject)
                returnMass = _parent_scene.maximumMassObject;

            return returnMass;
        }

        #endregion

        private void setMass()
        {
            if (Body != (IntPtr) 0)
            {
                float newmass = CalculateMass();

                //_log.Info("[PHYSICS]: New Mass: " + newmass.ToString());

                SafeNativeMethods.MassSetBoxTotal(out pMass, newmass, _size.X, _size.Y, _size.Z);
                SafeNativeMethods.BodySetMass(Body, ref pMass);
            }
        }

        private void setAngularVelocity(float x, float y, float z)
        {
            if (Body != (IntPtr)0)
            {
                SafeNativeMethods.BodySetAngularVel(Body, x, y, z);
            }
        }

        /// <summary>
        /// Stop a prim from being subject to physics.
        /// </summary>
        internal void disableBody()
        {
            //this kills the body so things like 'mesh' can re-create it.
            lock (this)
            {
                if (!childPrim)
                {
                    if (Body != IntPtr.Zero)
                    {
                        _parent_scene.DeactivatePrim(this);
                        _collisionCategories &= ~CollisionCategories.Body;
                        _collisionFlags &= ~(CollisionCategories.Wind | CollisionCategories.Land);

                        if (_assetFailed)
                        {
                            SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                            SafeNativeMethods.GeomSetCollideBits(pri_geom, 0);
                        }
                        else
                        {
                            SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                            SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                        }

                        SafeNativeMethods.BodyDestroy(Body);
                        lock (childrenPrim)
                        {
                            if (childrenPrim.Count > 0)
                            {
                                foreach (OdePrim prm in childrenPrim)
                                {
                                    _parent_scene.DeactivatePrim(prm);
                                    prm.Body = IntPtr.Zero;
                                }
                            }
                        }
                        Body = IntPtr.Zero;
                    }
                }
                else
                {
                    _parent_scene.DeactivatePrim(this);

                    _collisionCategories &= ~CollisionCategories.Body;
                    _collisionFlags &= ~(CollisionCategories.Wind | CollisionCategories.Land);

                    if (_assetFailed)
                    {
                        SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                        SafeNativeMethods.GeomSetCollideBits(pri_geom, 0);
                    }
                    else
                    {

                        SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                        SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                    }

                    Body = IntPtr.Zero;
                }
            }

            _disabled = true;
            _collisionscore = 0;
        }

        private static readonly Dictionary<IMesh, IntPtr> _MeshToTriMeshMap = new Dictionary<IMesh, IntPtr>();

        private void setMesh(OdeScene parent_scene, IMesh mesh)
        {
//            _log.DebugFormat("[ODE PRIM]: Setting mesh on {0} to {1}", Name, mesh);

            // This sleeper is there to moderate how long it takes between
            // setting up the mesh and pre-processing it when we get rapid fire mesh requests on a single object

            //Thread.Sleep(10);

            //Kill Body so that mesh can re-make the geom
            if (IsPhysical && Body != IntPtr.Zero)
            {
                if (childPrim)
                {
                    if (_parent != null)
                    {
                        OdePrim parent = (OdePrim)_parent;
                        parent.ChildDelink(this);
                    }
                }
                else
                {
                    disableBody();
                }
            }

            IntPtr vertices, indices;
            int vertexCount, indexCount;
            int vertexStride, triStride;
            mesh.getVertexListAsPtrToFloatArray(out vertices, out vertexStride, out vertexCount); // Note, that vertices are fixed in unmanaged heap
            mesh.getIndexListAsPtrToIntArray(out indices, out triStride, out indexCount); // Also fixed, needs release after usage
            _expectedCollisionContacts = indexCount;
            mesh.releaseSourceMeshData(); // free up the original mesh data to save memory

            // We must lock here since _MeshToTriMeshMap is static and multiple scene threads may call this method at
            // the same time.
            lock (_MeshToTriMeshMap)
            {
                if (_MeshToTriMeshMap.ContainsKey(mesh))
                {
                    _triMeshData = _MeshToTriMeshMap[mesh];
                }
                else
                {
                    _triMeshData = SafeNativeMethods.GeomTriMeshDataCreate();

                    SafeNativeMethods.GeomTriMeshDataBuildSimple(_triMeshData, vertices, vertexStride, vertexCount, indices, indexCount, triStride);
                    SafeNativeMethods.GeomTriMeshDataPreprocess(_triMeshData);
                    _MeshToTriMeshMap[mesh] = _triMeshData;
                }
            }

//            _parent_scene.waitForSpaceUnlock(_targetSpace);
            try
            {
                SetGeom(SafeNativeMethods.CreateTriMesh(_targetSpace, _triMeshData, null, null, null));
            }
            catch (AccessViolationException)
            {
                _log.ErrorFormat("[PHYSICS]: MESH LOCKED FOR {0}", Name);
                return;
            }

           // if (IsPhysical && Body == (IntPtr) 0)
           // {
                // Recreate the body
          //     _interpenetrationcount = 0;
           //     _collisionscore = 0;

           //     enableBody();
           // }
        }

        internal void ProcessTaints()
        {
#if SPAM
Console.WriteLine("ZProcessTaints for " + Name);
#endif

            // This must be processed as the very first taint so that later operations have a pri_geom to work with
            // if this is a new prim.
            if (_taintadd)
                changeadd();

            if (!_position.ApproxEquals(_taintposition, 0f))
                 changemove();

            if (_taintrot != _orientation)
            {
                if (childPrim && IsPhysical)    // For physical child prim...
                {
                    rotate();
                    // KF: ODE will also rotate the parent prim!
                    // so rotate the root back to where it was
                    OdePrim parent = (OdePrim)_parent;
                    parent.rotate();
                }
                else
                {
                    //Just rotate the prim
                    rotate();
                }
            }

            if (_taintPhysics != IsPhysical && !(_taintparent != _parent))
                changePhysicsStatus();

            if (!_size.ApproxEquals(_taintsize, 0f))
                changesize();

            if (_taintshape)
                changeshape();

            if (_taintforce)
                changeAddForce();

            if (_taintaddangularforce)
                changeAddAngularForce();

            if (!_taintTorque.ApproxEquals(Vector3.Zero, 0.001f))
                changeSetTorque();

            if (_taintdisable)
                changedisable();

            if (_taintselected != _isSelected)
                changeSelectedStatus();

            if (!_taintVelocity.ApproxEquals(Vector3.Zero, 0.001f))
                changevelocity();

            if (_taintparent != _parent)
                changelink();

            if (_taintCollidesWater != _collidesWater)
                changefloatonwater();

            if (_taintAngularLock != _angularlock)
                changeAngularLock();
        }

        /// <summary>
        /// Change prim in response to an angular lock taint.
        /// </summary>
        private void changeAngularLock()
        {
            // do we have a Physical object?
            if (Body != IntPtr.Zero)
            {
                //Check that we have a Parent
                //If we have a parent then we're not authorative here
                if (_parent == null)
                {
                    if (_taintAngularLock != 0)
                    {
                        createAMotor(_taintAngularLock);
                    }
                    else
                    {
                        if (Amotor != IntPtr.Zero)
                        {
                            SafeNativeMethods.JointDestroy(Amotor);
                            Amotor = IntPtr.Zero;
                        }
                    }
                }
            }

            _angularlock = _taintAngularLock;
        }

        /// <summary>
        /// Change prim in response to a link taint.
        /// </summary>
        private void changelink()
        {
            // If the newly set parent is not null
            // create link
            if (_parent == null && _taintparent != null)
            {
                if (_taintparent.PhysicsActorType == (int)ActorTypes.Prim)
                {
                    OdePrim obj = (OdePrim)_taintparent;
                    //obj.disableBody();
//Console.WriteLine("changelink calls ParentPrim");
                    obj.AddChildPrim(this);

                    /*
                    if (obj.Body != (IntPtr)0 && Body != (IntPtr)0 && obj.Body != Body)
                    {
                        _linkJointGroup = d.JointGroupCreate(0);
                        _linkJoint = d.JointCreateFixed(_parent_scene.world, _linkJointGroup);
                        d.JointAttach(_linkJoint, obj.Body, Body);
                        d.JointSetFixed(_linkJoint);
                    }
                     */
                }
            }
            // If the newly set parent is null
            // destroy link
            else if (_parent != null && _taintparent == null)
            {
//Console.WriteLine("  changelink B");

                if (_parent is OdePrim)
                {
                    OdePrim obj = (OdePrim)_parent;
                    obj.ChildDelink(this);
                    childPrim = false;
                    //_parent = null;
                }

                /*
                    if (Body != (IntPtr)0 && _linkJointGroup != (IntPtr)0)
                    d.JointGroupDestroy(_linkJointGroup);

                    _linkJointGroup = (IntPtr)0;
                    _linkJoint = (IntPtr)0;
                */
            }

            _parent = _taintparent;
            _taintPhysics = IsPhysical;
        }

        /// <summary>
        /// Add a child prim to this parent prim.
        /// </summary>
        /// <param name="prim">Child prim</param>
        private void AddChildPrim(OdePrim prim)
        {
            if (LocalID == prim.LocalID)
                return;

            if (Body == IntPtr.Zero)
            {
                Body = SafeNativeMethods.BodyCreate(_parent_scene.world);
                setMass();
            }

            lock (childrenPrim)
            {
                if (childrenPrim.Contains(prim))
                    return;

//                _log.DebugFormat(
//                    "[ODE PRIM]: Linking prim {0} {1} to {2} {3}", prim.Name, prim.LocalID, Name, LocalID);

                childrenPrim.Add(prim);

                foreach (OdePrim prm in childrenPrim)
                {
                    SafeNativeMethods.Mass m2;
                    SafeNativeMethods.MassSetZero(out m2);
                    SafeNativeMethods.MassSetBoxTotal(out m2, prm.CalculateMass(), prm._size.X, prm._size.Y, prm._size.Z);

                    SafeNativeMethods.Quaternion quat = new SafeNativeMethods.Quaternion
                    {
                        W = prm._orientation.W,
                        X = prm._orientation.X,
                        Y = prm._orientation.Y,
                        Z = prm._orientation.Z
                    };

                    SafeNativeMethods.Matrix3 mat = new SafeNativeMethods.Matrix3();
                    SafeNativeMethods.RfromQ(out mat, ref quat);
                    SafeNativeMethods.MassRotate(ref m2, ref mat);
                    SafeNativeMethods.MassTranslate(ref m2, Position.X - prm.Position.X, Position.Y - prm.Position.Y, Position.Z - prm.Position.Z);
                    SafeNativeMethods.MassAdd(ref pMass, ref m2);
                }

                foreach (OdePrim prm in childrenPrim)
                {
                    prm._collisionCategories |= CollisionCategories.Body;
                    prm._collisionFlags |= CollisionCategories.Land | CollisionCategories.Wind;

//Console.WriteLine(" GeomSetCategoryBits 1: " + prm.pri_geom + " - " + (int)prm._collisionCategories + " for " + Name);
                    if (prm._assetFailed)
                    {
                        SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, 0);
                        SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, (uint)prm.BadMeshAssetCollideBits);
                    }
                    else
                    {
                        SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, (uint)prm._collisionCategories);
                        SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, (uint)prm._collisionFlags);
                    }

                    SafeNativeMethods.Quaternion quat = new SafeNativeMethods.Quaternion
                    {
                        W = prm._orientation.W,
                        X = prm._orientation.X,
                        Y = prm._orientation.Y,
                        Z = prm._orientation.Z
                    };

                    SafeNativeMethods.Matrix3 mat = new SafeNativeMethods.Matrix3();
                    SafeNativeMethods.RfromQ(out mat, ref quat);
                    if (Body != IntPtr.Zero)
                    {
                        SafeNativeMethods.GeomSetBody(prm.pri_geom, Body);
                        prm.childPrim = true;
                        SafeNativeMethods.GeomSetOffsetWorldPosition(prm.pri_geom, prm.Position.X , prm.Position.Y, prm.Position.Z);
                        //d.GeomSetOffsetPosition(prim.pri_geom,
                        //    (Position.X - prm.Position.X) - pMass.c.X,
                        //    (Position.Y - prm.Position.Y) - pMass.c.Y,
                        //    (Position.Z - prm.Position.Z) - pMass.c.Z);
                        SafeNativeMethods.GeomSetOffsetWorldRotation(prm.pri_geom, ref mat);
                        //d.GeomSetOffsetRotation(prm.pri_geom, ref mat);
                        SafeNativeMethods.MassTranslate(ref pMass, -pMass.c.X, -pMass.c.Y, -pMass.c.Z);
                        SafeNativeMethods.BodySetMass(Body, ref pMass);
                    }
                    else
                    {
                        _log.DebugFormat("[PHYSICS]: {0} ain't got no boooooooooddy, no body", Name);
                    }

                    prm._interpenetrationcount = 0;
                    prm._collisionscore = 0;
                    prm._disabled = false;

                    prm.Body = Body;
                    _parent_scene.ActivatePrim(prm);
                }

                _collisionCategories |= CollisionCategories.Body;
                _collisionFlags |= CollisionCategories.Land | CollisionCategories.Wind;

                if (_assetFailed)
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)BadMeshAssetCollideBits);
                }
                else
                {
                    //Console.WriteLine("GeomSetCategoryBits 2: " + pri_geom + " - " + (int)_collisionCategories + " for " + Name);
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                    //Console.WriteLine(" Post GeomSetCategoryBits 2");
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                }

                SafeNativeMethods.Quaternion quat2 = new SafeNativeMethods.Quaternion
                {
                    W = _orientation.W,
                    X = _orientation.X,
                    Y = _orientation.Y,
                    Z = _orientation.Z
                };

                SafeNativeMethods.Matrix3 mat2 = new SafeNativeMethods.Matrix3();
                SafeNativeMethods.RfromQ(out mat2, ref quat2);
                SafeNativeMethods.GeomSetBody(pri_geom, Body);
                SafeNativeMethods.GeomSetOffsetWorldPosition(pri_geom, Position.X - pMass.c.X, Position.Y - pMass.c.Y, Position.Z - pMass.c.Z);
                //d.GeomSetOffsetPosition(prim.pri_geom,
                //    (Position.X - prm.Position.X) - pMass.c.X,
                //    (Position.Y - prm.Position.Y) - pMass.c.Y,
                //    (Position.Z - prm.Position.Z) - pMass.c.Z);
                //d.GeomSetOffsetRotation(pri_geom, ref mat2);
                SafeNativeMethods.MassTranslate(ref pMass, -pMass.c.X, -pMass.c.Y, -pMass.c.Z);
                SafeNativeMethods.BodySetMass(Body, ref pMass);

                SafeNativeMethods.BodySetAutoDisableFlag(Body, true);
                SafeNativeMethods.BodySetAutoDisableSteps(Body, body_autodisable_frames);

                _interpenetrationcount = 0;
                _collisionscore = 0;
                _disabled = false;

                // The body doesn't already have a finite rotation mode set here
                // or remove
                if (_parent == null)
                {
                    createAMotor(_angularlock);
                }

                SafeNativeMethods.BodySetPosition(Body, Position.X, Position.Y, Position.Z);

                if (_vehicle.Type != Vehicle.TYPE_NONE)
                    _vehicle.Enable(Body, _parent_scene);

                _parent_scene.ActivatePrim(this);
            }
        }

        private void ChildSetGeom(OdePrim odePrim)
        {
//            _log.DebugFormat(
//                "[ODE PRIM]: ChildSetGeom {0} {1} for {2} {3}", odePrim.Name, odePrim.LocalID, Name, LocalID);

            //if (IsPhysical && Body != IntPtr.Zero)
            lock (childrenPrim)
            {
                foreach (OdePrim prm in childrenPrim)
                {
                    //prm.childPrim = true;
                    prm.disableBody();
                    //prm._taintparent = null;
                    //prm._parent = null;
                    //prm._taintPhysics = false;
                    //prm._disabled = true;
                    //prm.childPrim = false;
                }
            }

            disableBody();

            // Spurious - Body == IntPtr.Zero after disableBody()
//            if (Body != IntPtr.Zero)
//            {
//                _parent_scene.DeactivatePrim(this);
//            }

            lock (childrenPrim)
            {
                foreach (OdePrim prm in childrenPrim)
                {
//Console.WriteLine("ChildSetGeom calls ParentPrim");
                    AddChildPrim(prm);
                }
            }
        }

        private void ChildDelink(OdePrim odePrim)
        {
//            _log.DebugFormat(
//                "[ODE PRIM]: Delinking prim {0} {1} from {2} {3}", odePrim.Name, odePrim.LocalID, Name, LocalID);

            // Okay, we have a delinked child..   need to rebuild the body.
            lock (childrenPrim)
            {
                foreach (OdePrim prm in childrenPrim)
                {
                    prm.childPrim = true;
                    prm.disableBody();
                    //prm._taintparent = null;
                    //prm._parent = null;
                    //prm._taintPhysics = false;
                    //prm._disabled = true;
                    //prm.childPrim = false;
                }
            }

            disableBody();

            lock (childrenPrim)
            {
 //Console.WriteLine("childrenPrim.Remove " + odePrim);
                childrenPrim.Remove(odePrim);
            }

            // Spurious - Body == IntPtr.Zero after disableBody()
//            if (Body != IntPtr.Zero)
//            {
//                _parent_scene.DeactivatePrim(this);
//            }

            lock (childrenPrim)
            {
                foreach (OdePrim prm in childrenPrim)
                {
//Console.WriteLine("ChildDelink calls ParentPrim");
                    AddChildPrim(prm);
                }
            }
        }

        /// <summary>
        /// Change prim in response to a selection taint.
        /// </summary>
        private void changeSelectedStatus()
        {
            if (_taintselected)
            {
                _collisionCategories = CollisionCategories.Selected;
                _collisionFlags = CollisionCategories.Sensor | CollisionCategories.Space;

                // We do the body disable soft twice because 'in theory' a collision could have happened
                // in between the disabling and the collision properties setting
                // which would wake the physical body up from a soft disabling and potentially cause it to fall
                // through the ground.

                // NOTE FOR JOINTS: this doesn't always work for jointed assemblies because if you select
                // just one part of the assembly, the rest of the assembly is non-selected and still simulating,
                // so that causes the selected part to wake up and continue moving.

                // even if you select all parts of a jointed assembly, it is not guaranteed that the entire
                // assembly will stop simulating during the selection, because of the lack of atomicity
                // of select operations (their processing could be interrupted by a thread switch, causing
                // simulation to continue before all of the selected object notifications trickle down to
                // the physics engine).

                // e.g. we select 100 prims that are connected by joints. non-atomically, the first 50 are
                // selected and disabled. then, due to a thread switch, the selection processing is
                // interrupted and the physics engine continues to simulate, so the last 50 items, whose
                // selection was not yet processed, continues to simulate. this wakes up ALL of the
                // first 50 again. then the last 50 are disabled. then the first 50, which were just woken
                // up, start simulating again, which in turn wakes up the last 50.

                if (IsPhysical)
                {
                    disableBodySoft();
                }

                if (_assetFailed)
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, 0);
                }
                else
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                }

                if (IsPhysical)
                {
                    disableBodySoft();
                }
            }
            else
            {
                _collisionCategories = CollisionCategories.Geom;

                if (IsPhysical)
                    _collisionCategories |= CollisionCategories.Body;

                _collisionFlags = _default_collisionFlags;

                if (_collidesLand)
                    _collisionFlags |= CollisionCategories.Land;
                if (_collidesWater)
                    _collisionFlags |= CollisionCategories.Water;

                if (_assetFailed)
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)BadMeshAssetCollideBits);
                }
                else
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                }

                if (IsPhysical)
                {
                    if (Body != IntPtr.Zero)
                    {
                        SafeNativeMethods.BodySetLinearVel(Body, 0f, 0f, 0f);
                        SafeNativeMethods.BodySetForce(Body, 0, 0, 0);
                        enableBodySoft();
                    }
                }
            }

            resetCollisionAccounting();
            _isSelected = _taintselected;
        }//end changeSelectedStatus

        internal void ResetTaints()
        {
            _taintposition = _position;
            _taintrot = _orientation;
            _taintPhysics = IsPhysical;
            _taintselected = _isSelected;
            _taintsize = _size;
            _taintshape = false;
            _taintforce = false;
            _taintdisable = false;
            _taintVelocity = Vector3.Zero;
        }

        /// <summary>
        /// Create a geometry for the given mesh in the given target space.
        /// </summary>
        /// <param name="_targetSpace"></param>
        /// <param name="mesh">If null, then a mesh is used that is based on the profile shape data.</param>
        private void CreateGeom(IntPtr _targetSpace, IMesh mesh)
        {
#if SPAM
Console.WriteLine("CreateGeom:");
#endif
            if (mesh != null)
            {
                setMesh(_parent_scene, mesh);
            }
            else
            {
                if (_pbs.ProfileShape == ProfileShape.HalfCircle && _pbs.PathCurve == (byte)Extrusion.Curve1)
                {
                    if (_size.X == _size.Y && _size.Y == _size.Z && _size.X == _size.Z)
                    {
                        if (_size.X / 2f > 0f)
                        {
//                            _parent_scene.waitForSpaceUnlock(_targetSpace);
                            try
                            {
//Console.WriteLine(" CreateGeom 1");
                                SetGeom(SafeNativeMethods.CreateSphere(_targetSpace, _size.X / 2));
                                _expectedCollisionContacts = 3;
                            }
                            catch (AccessViolationException)
                            {
                                _log.WarnFormat("[PHYSICS]: Unable to create physics proxy for object {0}", Name);
                                return;
                            }
                        }
                        else
                        {
//                            _parent_scene.waitForSpaceUnlock(_targetSpace);
                            try
                            {
//Console.WriteLine(" CreateGeom 2");
                                SetGeom(SafeNativeMethods.CreateBox(_targetSpace, _size.X, _size.Y, _size.Z));
                                _expectedCollisionContacts = 4;
                            }
                            catch (AccessViolationException)
                            {
                                _log.WarnFormat("[PHYSICS]: Unable to create physics proxy for object {0}", Name);
                                return;
                            }
                        }
                    }
                    else
                    {
//                        _parent_scene.waitForSpaceUnlock(_targetSpace);
                        try
                        {
//Console.WriteLine("  CreateGeom 3");
                            SetGeom(SafeNativeMethods.CreateBox(_targetSpace, _size.X, _size.Y, _size.Z));
                            _expectedCollisionContacts = 4;
                        }
                        catch (AccessViolationException)
                        {
                            _log.WarnFormat("[PHYSICS]: Unable to create physics proxy for object {0}", Name);
                            return;
                        }
                    }
                }
                else
                {
//                    _parent_scene.waitForSpaceUnlock(_targetSpace);
                    try
                    {
//Console.WriteLine("  CreateGeom 4");
                        SetGeom(SafeNativeMethods.CreateBox(_targetSpace, _size.X, _size.Y, _size.Z));
                        _expectedCollisionContacts = 4;
                    }
                    catch (AccessViolationException)
                    {
                        _log.WarnFormat("[PHYSICS]: Unable to create physics proxy for object {0}", Name);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Remove the existing geom from this prim.
        /// </summary>
        /// <param name="_targetSpace"></param>
        /// <param name="mesh">If null, then a mesh is used that is based on the profile shape data.</param>
        /// <returns>true if the geom was successfully removed, false if it was already gone or the remove failed.</returns>
        internal bool RemoveGeom()
        {
            if (pri_geom != IntPtr.Zero)
            {
                try
                {
                    _parent_scene.geo_name_map.Remove(pri_geom);
                    _parent_scene.actor_name_map.Remove(pri_geom);
                    SafeNativeMethods.GeomDestroy(pri_geom);
                    _expectedCollisionContacts = 0;
                    pri_geom = IntPtr.Zero;
                }
                catch (System.AccessViolationException)
                {
                    pri_geom = IntPtr.Zero;
                    _expectedCollisionContacts = 0;
                    _log.ErrorFormat("[PHYSICS]: PrimGeom dead for {0}", Name);

                    return false;
                }

                return true;
            }
            else
            {
                _log.WarnFormat(
                    "[ODE PRIM]: Called RemoveGeom() on {0} {1} where geometry was already null.", Name, LocalID);

                return false;
            }
        }
        /// <summary>
        /// Add prim in response to an add taint.
        /// </summary>
        private void changeadd()
        {
//            _log.DebugFormat("[ODE PRIM]: Adding prim {0}", Name);

            int[] iprimspaceArrItem = _parent_scene.calculateSpaceArrayItemFromPos(_position);
            IntPtr targetspace = _parent_scene.calculateSpaceForGeom(_position);

            if (targetspace == IntPtr.Zero)
                targetspace = _parent_scene.createprimspace(iprimspaceArrItem[0], iprimspaceArrItem[1]);

            _targetSpace = targetspace;

            IMesh mesh = null;

            if (_parent_scene.needsMeshing(_pbs))
            {
                // Don't need to re-enable body..   it's done in SetMesh
                mesh = _parent_scene.mesher.CreateMesh(Name, _pbs, _size, _parent_scene.meshSculptLOD, IsPhysical);
                // createmesh returns null when it's a shape that isn't a cube.
               // _log.Debug(_localID);
                if (mesh == null)
                    CheckMeshAsset();
                else
                    _assetFailed = false;
            }

#if SPAM
Console.WriteLine("changeadd 1");
#endif
            CreateGeom(_targetSpace, mesh);

            SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);
            SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
            {
                X = _orientation.X,
                Y = _orientation.Y,
                Z = _orientation.Z,
                W = _orientation.W
            };
            SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);

            if (IsPhysical && Body == IntPtr.Zero)
                enableBody();

            changeSelectedStatus();

            _taintadd = false;
        }

        /// <summary>
        /// Move prim in response to a move taint.
        /// </summary>
        private void changemove()
        {
            if (IsPhysical)
            {
                if (!_disabled && !_taintremove && !childPrim)
                {
                    if (Body == IntPtr.Zero)
                        enableBody();

                    //Prim auto disable after 20 frames,
                    //if you move it, re-enable the prim manually.
                    if (_parent != null)
                    {
                        if (_linkJoint != IntPtr.Zero)
                        {
                            SafeNativeMethods.JointDestroy(_linkJoint);
                            _linkJoint = IntPtr.Zero;
                        }
                    }

                    if (Body != IntPtr.Zero)
                    {
                        SafeNativeMethods.BodySetPosition(Body, _position.X, _position.Y, _position.Z);

                        if (_parent != null)
                        {
                            OdePrim odParent = (OdePrim)_parent;
                            if (Body != (IntPtr)0 && odParent.Body != (IntPtr)0 && Body != odParent.Body)
                            {
// KF: Fixed Joints were removed? Anyway - this Console.WriteLine does not show up, so routine is not used??
Console.WriteLine(" JointCreateFixed");
                                _linkJoint = SafeNativeMethods.JointCreateFixed(_parent_scene.world, _linkJointGroup);
                                SafeNativeMethods.JointAttach(_linkJoint, Body, odParent.Body);
                                SafeNativeMethods.JointSetFixed(_linkJoint);
                            }
                        }
                        SafeNativeMethods.BodyEnable(Body);
                        if (_vehicle.Type != Vehicle.TYPE_NONE)
                        {
                            _vehicle.Enable(Body, _parent_scene);
                        }
                    }
                    else
                    {
                        _log.WarnFormat("[PHYSICS]: Body for {0} still null after enableBody().  This is a crash scenario.", Name);
                    }
                }
                //else
               // {
                    //_log.Debug("[BUG]: race!");
                //}
            }

            // string primScenAvatarIn = _parent_scene.whichspaceamIin(_position);
            // int[] arrayitem = _parent_scene.calculateSpaceArrayItemFromPos(_position);
//          _parent_scene.waitForSpaceUnlock(_targetSpace);

            IntPtr tempspace = _parent_scene.recalculateSpaceForGeom(pri_geom, _position, _targetSpace);
            _targetSpace = tempspace;

//                _parent_scene.waitForSpaceUnlock(_targetSpace);

            SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);

//                    _parent_scene.waitForSpaceUnlock(_targetSpace);
            SafeNativeMethods.SpaceAdd(_targetSpace, pri_geom);

            changeSelectedStatus();

            resetCollisionAccounting();
            _taintposition = _position;
        }

        internal void Move(float timestep)
        {
            float fx = 0;
            float fy = 0;
            float fz = 0;

            if (outofBounds)
                return;

            if (IsPhysical && Body != IntPtr.Zero && !_isSelected && !childPrim)        // KF: Only move root prims.
            {
                if (_vehicle.Type != Vehicle.TYPE_NONE)
                {
                    // 'VEHICLES' are dealt with in ODEDynamics.cs
                    _vehicle.Step(timestep, _parent_scene);
                }
                else
                {
//Console.WriteLine("Move " +  Name);
                    if (!SafeNativeMethods.BodyIsEnabled (Body))  SafeNativeMethods.BodyEnable (Body); // KF add 161009

                    float _mass = CalculateMass();

//                    fz = 0f;
                    //_log.Info(_collisionFlags.ToString());


                    //KF: _buoyancy should be set by llSetBuoyancy() for non-vehicle.
                    // would come from SceneObjectPart.cs, public void SetBuoyancy(float fvalue) , PhysActor.Buoyancy = fvalue; ??
                    // _buoyancy: (unlimited value) <0=Falls fast; 0=1g; 1=0g; >1 = floats up
                    // gravityz multiplier = 1 - _buoyancy
                    fz = _parent_scene.gravityz * (1.0f - _buoyancy) * _mass;

                    if (PIDActive)
                    {
//Console.WriteLine("PID " +  Name);
                        // KF - this is for object move? eg. llSetPos() ?
                        //if (!d.BodyIsEnabled(Body))
                        //d.BodySetForce(Body, 0f, 0f, 0f);
                        // If we're using the PID controller, then we have no gravity
                        //fz = (-1 * _parent_scene.gravityz) * _mass;     //KF: ?? Prims have no global gravity,so simply...
                        fz = 0f;

                        //  no lock; for now it's only called from within Simulate()

                        // If the PID Controller isn't active then we set our force
                        // calculating base velocity to the current position

                        if (_PIDTau < 1 && _PIDTau != 0)
                        {
                            //PID_G = PID_G / _PIDTau;
                            _PIDTau = 1;
                        }

                        if (PID_G - _PIDTau <= 0)
                        {
                            PID_G = _PIDTau + 1;
                        }
                        //PidStatus = true;

                        // PhysicsVector vec = new PhysicsVector();
                        SafeNativeMethods.Vector3 vel = SafeNativeMethods.BodyGetLinearVel(Body);

                        SafeNativeMethods.Vector3 pos = SafeNativeMethods.BodyGetPosition(Body);
                        _target_velocity =
                            new Vector3(
                                (_PIDTarget.X - pos.X) * ((PID_G - _PIDTau) * timestep),
                                (_PIDTarget.Y - pos.Y) * ((PID_G - _PIDTau) * timestep),
                                (_PIDTarget.Z - pos.Z) * ((PID_G - _PIDTau) * timestep)
                                );

                        //  if velocity is zero, use position control; otherwise, velocity control

                        if (_target_velocity.ApproxEquals(Vector3.Zero,0.1f))
                        {
                            //  keep track of where we stopped.  No more slippin' & slidin'

                            // We only want to deactivate the PID Controller if we think we want to have our surrogate
                            // react to the physics scene by moving it's position.
                            // Avatar to Avatar collisions
                            // Prim to avatar collisions

                            //fx = (_target_velocity.X - vel.X) * (PID_D) + (_zeroPosition.X - pos.X) * (PID_P * 2);
                            //fy = (_target_velocity.Y - vel.Y) * (PID_D) + (_zeroPosition.Y - pos.Y) * (PID_P * 2);
                            //fz = fz + (_target_velocity.Z - vel.Z) * (PID_D) + (_zeroPosition.Z - pos.Z) * PID_P;
                            SafeNativeMethods.BodySetPosition(Body, _PIDTarget.X, _PIDTarget.Y, _PIDTarget.Z);
                            SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0);
                            SafeNativeMethods.BodyAddForce(Body, 0, 0, fz);
                            return;
                        }
                        else
                        {
                            _zeroFlag = false;

                            // We're flying and colliding with something
                            fx = (_target_velocity.X - vel.X) * PID_D;
                            fy = (_target_velocity.Y - vel.Y) * PID_D;

                            // vec.Z = (_target_velocity.Z - vel.Z) * PID_D + (_zeroPosition.Z - pos.Z) * PID_P;

                            fz = fz + (_target_velocity.Z - vel.Z) * PID_D * _mass;
                        }
                    }        // end if (PIDActive)

                    // Hover PID Controller needs to be mutually exlusive to MoveTo PID controller
                    if (_useHoverPID && !PIDActive)
                    {
//Console.WriteLine("Hover " +  Name);

                        // If we're using the PID controller, then we have no gravity
                        fz = -1 * _parent_scene.gravityz * _mass;

                        //  no lock; for now it's only called from within Simulate()

                        // If the PID Controller isn't active then we set our force
                        // calculating base velocity to the current position

                        if (_PIDTau < 1)
                        {
                            PID_G = PID_G / _PIDTau;
                        }

                        if (PID_G - _PIDTau <= 0)
                        {
                            PID_G = _PIDTau + 1;
                        }

                        // Where are we, and where are we headed?
                        SafeNativeMethods.Vector3 pos = SafeNativeMethods.BodyGetPosition(Body);
                        SafeNativeMethods.Vector3 vel = SafeNativeMethods.BodyGetLinearVel(Body);

                        //    Non-Vehicles have a limited set of Hover options.
                        // determine what our target height really is based on HoverType
                        switch (_PIDHoverType)
                        {
                            case PIDHoverType.Ground:
                                _groundHeight = _parent_scene.GetTerrainHeightAtXY(pos.X, pos.Y);
                                _targetHoverHeight = _groundHeight + _PIDHoverHeight;
                                break;
                            case PIDHoverType.GroundAndWater:
                                _groundHeight = _parent_scene.GetTerrainHeightAtXY(pos.X, pos.Y);
                                _waterHeight  = _parent_scene.GetWaterLevel();
                                if (_groundHeight > _waterHeight)
                                {
                                    _targetHoverHeight = _groundHeight + _PIDHoverHeight;
                                }
                                else
                                {
                                    _targetHoverHeight = _waterHeight + _PIDHoverHeight;
                                }
                                break;

                        }     // end switch (_PIDHoverType)


                        _target_velocity =
                            new Vector3(0.0f, 0.0f,
                                (_targetHoverHeight - pos.Z) * ((PID_G - _PIDHoverTau) * timestep)
                                );

                        //  if velocity is zero, use position control; otherwise, velocity control

                        if (_target_velocity.ApproxEquals(Vector3.Zero, 0.1f))
                        {
                            //  keep track of where we stopped.  No more slippin' & slidin'

                            // We only want to deactivate the PID Controller if we think we want to have our surrogate
                            // react to the physics scene by moving it's position.
                            // Avatar to Avatar collisions
                            // Prim to avatar collisions

                            SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, _targetHoverHeight);
                            SafeNativeMethods.BodySetLinearVel(Body, vel.X, vel.Y, 0);
                            SafeNativeMethods.BodyAddForce(Body, 0, 0, fz);
                            return;
                        }
                        else
                        {
                            _zeroFlag = false;

                            // We're flying and colliding with something
                            fz = fz + (_target_velocity.Z - vel.Z) * PID_D * _mass;
                        }
                    }

                    fx *= _mass;
                    fy *= _mass;
                    //fz *= _mass;

                    fx += _force.X;
                    fy += _force.Y;
                    fz += _force.Z;

                    //_log.Info("[OBJPID]: X:" + fx.ToString() + " Y:" + fy.ToString() + " Z:" + fz.ToString());
                    if (fx != 0 || fy != 0 || fz != 0)
                    {
                        //_taintdisable = true;
                        //base.RaiseOutOfBounds(Position);
                        //d.BodySetLinearVel(Body, fx, fy, 0f);
                        if (!SafeNativeMethods.BodyIsEnabled(Body))
                        {
                            // A physical body at rest on a surface will auto-disable after a while,
                            // this appears to re-enable it incase the surface it is upon vanishes,
                            // and the body should fall again.
                            SafeNativeMethods.BodySetLinearVel(Body, 0f, 0f, 0f);
                            SafeNativeMethods.BodySetForce(Body, 0, 0, 0);
                            enableBodySoft();
                        }

                        // 35x10 = 350n times the mass per second applied maximum.
                        float nmax = 35f * _mass;
                        float nmin = -35f * _mass;

                        if (fx > nmax)
                            fx = nmax;
                        if (fx < nmin)
                            fx = nmin;
                        if (fy > nmax)
                            fy = nmax;
                        if (fy < nmin)
                            fy = nmin;
                        SafeNativeMethods.BodyAddForce(Body, fx, fy, fz);
//Console.WriteLine("AddForce " + fx + "," + fy + "," + fz);
                    }
                }
            }
            else
            {    // is not physical, or is not a body or is selected
              //  _zeroPosition = d.BodyGetPosition(Body);
                return;
//Console.WriteLine("Nothing " +  Name);

            }
        }

        private void rotate()
        {
            SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
            {
                X = _orientation.X,
                Y = _orientation.Y,
                Z = _orientation.Z,
                W = _orientation.W
            };
            if (Body != IntPtr.Zero)
            {
                // KF: If this is a root prim do BodySet
                SafeNativeMethods.BodySetQuaternion(Body, ref myrot);
                if (IsPhysical)
                {
                    // create or remove locks
                    createAMotor(_angularlock);
                }
            }
            else
            {
                // daughter prim, do Geom set
                SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);
            }

            resetCollisionAccounting();
            _taintrot = _orientation;
        }

        private void resetCollisionAccounting()
        {
            _collisionscore = 0;
            _interpenetrationcount = 0;
            _disabled = false;
        }

        /// <summary>
        /// Change prim in response to a disable taint.
        /// </summary>
        private void changedisable()
        {
            _disabled = true;
            if (Body != IntPtr.Zero)
            {
                SafeNativeMethods.BodyDisable(Body);
                Body = IntPtr.Zero;
            }

            _taintdisable = false;
        }

        /// <summary>
        /// Change prim in response to a physics status taint
        /// </summary>
        private void changePhysicsStatus()
        {
            if (IsPhysical)
            {
                if (Body == IntPtr.Zero)
                {
                    if (_pbs.SculptEntry && _parent_scene.meshSculptedPrim)
                    {
                        changeshape();
                    }
                    else
                    {
                        enableBody();
                    }
                }
            }
            else
            {
                if (Body != IntPtr.Zero)
                {
                    if (_pbs.SculptEntry && _parent_scene.meshSculptedPrim)
                    {
                        RemoveGeom();

//Console.WriteLine("changePhysicsStatus for " + Name);
                        changeadd();
                    }

                    if (childPrim)
                    {
                        if (_parent != null)
                        {
                            OdePrim parent = (OdePrim)_parent;
                            parent.ChildDelink(this);
                        }
                    }
                    else
                    {
                        disableBody();
                    }
                }
            }

            changeSelectedStatus();

            resetCollisionAccounting();
            _taintPhysics = IsPhysical;
        }

        /// <summary>
        /// Change prim in response to a size taint.
        /// </summary>
        private void changesize()
        {
#if SPAM
            _log.DebugFormat("[ODE PRIM]: Called changesize");
#endif

            if (_size.X <= 0) _size.X = 0.01f;
            if (_size.Y <= 0) _size.Y = 0.01f;
            if (_size.Z <= 0) _size.Z = 0.01f;

            //kill body to rebuild
            if (IsPhysical && Body != IntPtr.Zero)
            {
                if (childPrim)
                {
                    if (_parent != null)
                    {
                        OdePrim parent = (OdePrim)_parent;
                        parent.ChildDelink(this);
                    }
                }
                else
                {
                    disableBody();
                }
            }

            if (SafeNativeMethods.SpaceQuery(_targetSpace, pri_geom))
            {
//                _parent_scene.waitForSpaceUnlock(_targetSpace);
                SafeNativeMethods.SpaceRemove(_targetSpace, pri_geom);
            }

            RemoveGeom();

            // we don't need to do space calculation because the client sends a position update also.

            IMesh mesh = null;

            // Construction of new prim
            if (_parent_scene.needsMeshing(_pbs))
            {
                float meshlod = _parent_scene.meshSculptLOD;

                if (IsPhysical)
                    meshlod = _parent_scene.MeshSculptphysicalLOD;
                // Don't need to re-enable body..   it's done in SetMesh

                if (_parent_scene.needsMeshing(_pbs))
                {
                    mesh = _parent_scene.mesher.CreateMesh(Name, _pbs, _size, meshlod, IsPhysical);
                    if (mesh == null)
                        CheckMeshAsset();
                    else
                        _assetFailed = false;
                }

            }

            CreateGeom(_targetSpace, mesh);
            SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);
            SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
            {
                X = _orientation.X,
                Y = _orientation.Y,
                Z = _orientation.Z,
                W = _orientation.W
            };
            SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);

            //d.GeomBoxSetLengths(pri_geom, _size.X, _size.Y, _size.Z);
            if (IsPhysical && Body == IntPtr.Zero && !childPrim)
            {
                // Re creates body on size.
                // EnableBody also does setMass()
                enableBody();
                SafeNativeMethods.BodyEnable(Body);
            }

            changeSelectedStatus();

            if (childPrim)
            {
                if (_parent is OdePrim)
                {
                    OdePrim parent = (OdePrim)_parent;
                    parent.ChildSetGeom(this);
                }
            }
            resetCollisionAccounting();
            _taintsize = _size;
        }

        /// <summary>
        /// Change prim in response to a float on water taint.
        /// </summary>
        /// <param name="timestep"></param>
        private void changefloatonwater()
        {
            _collidesWater = _taintCollidesWater;

            if (_collidesWater)
            {
                _collisionFlags |= CollisionCategories.Water;
            }
            else
            {
                _collisionFlags &= ~CollisionCategories.Water;
            }

            if (_assetFailed)
                SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)BadMeshAssetCollideBits);
            else

                SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
        }
        /// <summary>
        /// Change prim in response to a shape taint.
        /// </summary>
        private void changeshape()
        {
            _taintshape = false;

            // Cleanup of old prim geometry and Bodies
            if (IsPhysical && Body != IntPtr.Zero)
            {
                if (childPrim)
                {
                    if (_parent != null)
                    {
                        OdePrim parent = (OdePrim)_parent;
                        parent.ChildDelink(this);
                    }
                }
                else
                {
                    disableBody();
                }
            }

            RemoveGeom();

            // we don't need to do space calculation because the client sends a position update also.
            if (_size.X <= 0) _size.X = 0.01f;
            if (_size.Y <= 0) _size.Y = 0.01f;
            if (_size.Z <= 0) _size.Z = 0.01f;
            // Construction of new prim

            IMesh mesh = null;


            if (_parent_scene.needsMeshing(_pbs))
            {
                // Don't need to re-enable body..   it's done in CreateMesh
                float meshlod = _parent_scene.meshSculptLOD;

                if (IsPhysical)
                    meshlod = _parent_scene.MeshSculptphysicalLOD;

                // createmesh returns null when it doesn't mesh.
                mesh = _parent_scene.mesher.CreateMesh(Name, _pbs, _size, meshlod, IsPhysical);
                if (mesh == null)
                    CheckMeshAsset();
                else
                    _assetFailed = false;
            }

            CreateGeom(_targetSpace, mesh);
            SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);
            SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
            {
                //myrot.W = _orientation.w;
                W = _orientation.W,
                X = _orientation.X,
                Y = _orientation.Y,
                Z = _orientation.Z
            };
            SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);

            //d.GeomBoxSetLengths(pri_geom, _size.X, _size.Y, _size.Z);
            if (IsPhysical && Body == IntPtr.Zero)
            {
                // Re creates body on size.
                // EnableBody also does setMass()
                enableBody();
                if (Body != IntPtr.Zero)
                {
                    SafeNativeMethods.BodyEnable(Body);
                }
            }

            changeSelectedStatus();

            if (childPrim)
            {
                if (_parent is OdePrim)
                {
                    OdePrim parent = (OdePrim)_parent;
                    parent.ChildSetGeom(this);
                }
            }

            resetCollisionAccounting();
//            _taintshape = false;
        }

        /// <summary>
        /// Change prim in response to an add force taint.
        /// </summary>
        private void changeAddForce()
        {
            if (!_isSelected)
            {
                lock (_forcelist)
                {
                    //_log.Info("[PHYSICS]: dequeing forcelist");
                    if (IsPhysical)
                    {
                        Vector3 iforce = Vector3.Zero;
                        int i = 0;
                        try
                        {
                            for (i = 0; i < _forcelist.Count; i++)
                            {

                                iforce = iforce + _forcelist[i] * 100;
                            }
                        }
                        catch (IndexOutOfRangeException)
                        {
                            _forcelist = new List<Vector3>();
                            _collisionscore = 0;
                            _interpenetrationcount = 0;
                            _taintforce = false;
                            return;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            _forcelist = new List<Vector3>();
                            _collisionscore = 0;
                            _interpenetrationcount = 0;
                            _taintforce = false;
                            return;
                        }
                        SafeNativeMethods.BodyEnable(Body);
                        SafeNativeMethods.BodyAddForce(Body, iforce.X, iforce.Y, iforce.Z);
                    }
                    _forcelist.Clear();
                }

                _collisionscore = 0;
                _interpenetrationcount = 0;
            }

            _taintforce = false;
        }

        /// <summary>
        /// Change prim in response to a torque taint.
        /// </summary>
        private void changeSetTorque()
        {
            if (!_isSelected)
            {
                if (IsPhysical && Body != IntPtr.Zero)
                {
                    SafeNativeMethods.BodySetTorque(Body, _taintTorque.X, _taintTorque.Y, _taintTorque.Z);
                }
            }

            _taintTorque = Vector3.Zero;
        }

        /// <summary>
        /// Change prim in response to an angular force taint.
        /// </summary>
        private void changeAddAngularForce()
        {
            if (!_isSelected)
            {
                lock (_angularforcelist)
                {
                    //_log.Info("[PHYSICS]: dequeing forcelist");
                    if (IsPhysical)
                    {
                        Vector3 iforce = Vector3.Zero;
                        for (int i = 0; i < _angularforcelist.Count; i++)
                        {
                            iforce = iforce + _angularforcelist[i] * 100;
                        }
                        SafeNativeMethods.BodyEnable(Body);
                        SafeNativeMethods.BodyAddTorque(Body, iforce.X, iforce.Y, iforce.Z);

                    }
                    _angularforcelist.Clear();
                }

                _collisionscore = 0;
                _interpenetrationcount = 0;
            }

            _taintaddangularforce = false;
        }

        /// <summary>
        /// Change prim in response to a velocity taint.
        /// </summary>
        private void changevelocity()
        {
            if (!_isSelected)
            {
                // Not sure exactly why this sleep is here, but from experimentation it appears to stop an avatar
                // walking through a default rez size prim if it keeps kicking it around - justincc.
                Thread.Sleep(20);

                if (IsPhysical)
                {
                    if (Body != IntPtr.Zero)
                    {
                        SafeNativeMethods.BodySetLinearVel(Body, _taintVelocity.X, _taintVelocity.Y, _taintVelocity.Z);
                    }
                }

                //resetCollisionAccounting();
            }

            _taintVelocity = Vector3.Zero;
        }

        internal void setPrimForRemoval()
        {
            _taintremove = true;
        }

        public override bool Flying
        {
            // no flying prims for you
            get => false;
            set { }
        }

        public override bool IsColliding
        {
            get => iscolliding;
            set => iscolliding = value;
        }

        public override bool CollidingGround
        {
            get => false;
            set { return; }
        }

        public override bool CollidingObj
        {
            get => false;
            set { return; }
        }

        public override bool ThrottleUpdates
        {
            get => _throttleUpdates;
            set => _throttleUpdates = value;
        }

        public override bool Stopped => _zeroFlag;

        public override Vector3 Position
        {
            get => _position;

            set => _position = value;
            //_log.Info("[PHYSICS]: " + _position.ToString());
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (value.IsFinite())
                {
                    _size = value;
//                    _log.DebugFormat("[PHYSICS]: Set size on {0} to {1}", Name, value);
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: Got NaN Size on object {0}", Name);
                }
            }
        }

        public override float Mass => CalculateMass();

        public override Vector3 Force
        {
            //get { return Vector3.Zero; }
            get => _force;
            set
            {
                if (value.IsFinite())
                {
                    _force = value;
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: NaN in Force Applied to an Object {0}", Name);
                }
            }
        }

        public override int VehicleType
        {
            get => (int)_vehicle.Type;
            set => _vehicle.ProcessTypeChange((Vehicle)value);
        }

        public override void VehicleFloatParam(int param, float value)
        {
            _vehicle.ProcessFloatVehicleParam((Vehicle) param, value);
        }

        public override void VehicleVectorParam(int param, Vector3 value)
        {
            _vehicle.ProcessVectorVehicleParam((Vehicle) param, value);
        }

        public override void VehicleRotationParam(int param, Quaternion rotation)
        {
            _vehicle.ProcessRotationVehicleParam((Vehicle) param, rotation);
        }

        public override void VehicleFlags(int param, bool remove)
        {
            _vehicle.ProcessVehicleFlags(param, remove);
        }

        public override void SetVolumeDetect(int param)
        {
            // We have to lock the scene here so that an entire simulate loop either uses volume detect for all
            // possible collisions with this prim or for none of them.
            lock (_parent_scene.OdeLock)
            {
                _isVolumeDetect = param != 0;
            }
        }

        public override Vector3 CenterOfMass => Vector3.Zero;

        public override Vector3 GeometricCenter => Vector3.Zero;

        public override PrimitiveBaseShape Shape
        {
            set
            {
                _pbs = value;
                _assetFailed = false;
                _taintshape = true;
            }
        }

        public override Vector3 Velocity
        {
            get
            {
                // Average previous velocity with the new one so
                // client object interpolation works a 'little' better
                if (_zeroFlag)
                    return Vector3.Zero;

                Vector3 returnVelocity = Vector3.Zero;
                returnVelocity.X = (_lastVelocity.X + _velocity.X) * 0.5f; // 0.5f is mathematically equiv to '/ 2'
                returnVelocity.Y = (_lastVelocity.Y + _velocity.Y) * 0.5f;
                returnVelocity.Z = (_lastVelocity.Z + _velocity.Z) * 0.5f;
                return returnVelocity;
            }
            set
            {
                if (value.IsFinite())
                {
                    _velocity = value;

                    _taintVelocity = value;
                    _parent_scene.AddPhysicsActorTaint(this);
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: Got NaN Velocity in Object {0}", Name);
                }

            }
        }

        public override Vector3 Torque
        {
            get
            {
                if (!IsPhysical || Body == IntPtr.Zero)
                    return Vector3.Zero;

                return _torque;
            }

            set
            {
                if (value.IsFinite())
                {
                    _taintTorque = value;
                    _parent_scene.AddPhysicsActorTaint(this);
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: Got NaN Torque in Object {0}", Name);
                }
            }
        }

        public override float CollisionScore
        {
            get => _collisionscore;
            set => _collisionscore = value;
        }

        public override bool Kinematic
        {
            get => false;
            set { }
        }

        public override Quaternion Orientation
        {
            get => _orientation;
            set
            {
                if (QuaternionIsFinite(value))
                    _orientation = value;
                else
                    _log.WarnFormat("[PHYSICS]: Got NaN quaternion Orientation from Scene in Object {0}", Name);
            }
        }

        private static bool QuaternionIsFinite(Quaternion q)
        {
            if (float.IsNaN(q.X) || float.IsInfinity(q.X))
                return false;
            if (float.IsNaN(q.Y) || float.IsInfinity(q.Y))
                return false;
            if (float.IsNaN(q.Z) || float.IsInfinity(q.Z))
                return false;
            if (float.IsNaN(q.W) || float.IsInfinity(q.W))
                return false;
            return true;
        }

        public override Vector3 Acceleration
        {
            get => _acceleration;
            set => _acceleration = value;
        }

        public override void AddForce(Vector3 force, bool pushforce)
        {
            if (force.IsFinite())
            {
                lock (_forcelist)
                    _forcelist.Add(force);

                _taintforce = true;
            }
            else
            {
                _log.WarnFormat("[PHYSICS]: Got Invalid linear force vector from Scene in Object {0}", Name);
            }
            //_log.Info("[PHYSICS]: Added Force:" + force.ToString() +  " to prim at " + Position.ToString());
        }

        public override void AddAngularForce(Vector3 force, bool pushforce)
        {
            if (force.IsFinite())
            {
                _angularforcelist.Add(force);
                _taintaddangularforce = true;
            }
            else
            {
                _log.WarnFormat("[PHYSICS]: Got Invalid Angular force vector from Scene in Object {0}", Name);
            }
        }

        public override Vector3 RotationalVelocity
        {
            get
            {
                Vector3 pv = Vector3.Zero;
                if (_zeroFlag)
                    return pv;
                _lastUpdateSent = false;

                if (_rotationalVelocity.ApproxEquals(pv, 0.2f))
                    return pv;

                return _rotationalVelocity;
            }
            set
            {
                if (value.IsFinite())
                {
                    _rotationalVelocity = value;
                    setAngularVelocity(value.X, value.Y, value.Z);
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: Got NaN RotationalVelocity in Object {0}", Name);
                }
            }
        }

        public override void CrossingFailure()
        {
            /*
                        _crossingfailures++;
                        if (_crossingfailures > _parent_scene.geomCrossingFailuresBeforeOutofbounds)
                        {
                            base.RaiseOutOfBounds(_position);
                            return;
                        }
                        else if (_crossingfailures == _parent_scene.geomCrossingFailuresBeforeOutofbounds)
                        {
                            _log.Warn("[PHYSICS]: Too many crossing failures for: " + Name);
                        }
            */

            SafeNativeMethods.AllocateODEDataForThread(0);

            _position.X = Util.Clip(_position.X, 0.5f, _parent_scene.WorldExtents.X - 0.5f);
            _position.Y = Util.Clip(_position.Y, 0.5f, _parent_scene.WorldExtents.Y - 0.5f);
            _position.Z = Util.Clip(_position.Z + 0.2f, -100f, 50000f);

            _lastposition = _position;
            _velocity.X = 0;
            _velocity.Y = 0;
            _velocity.Z = 0;

            _lastVelocity = _velocity;

            if (Body != IntPtr.Zero)
            {
                SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0); // stop it
                SafeNativeMethods.BodySetPosition(Body, _position.X, _position.Y, _position.Z);
            }

            if(_vehicle != null && _vehicle.Type != Vehicle.TYPE_NONE)
                _vehicle.Stop(); // this also updates vehicle last position from the body position

            enableBodySoft();

            outofBounds = false;
            base.RequestPhysicsterseUpdate();

        }

        public override float Buoyancy
        {
            get => _buoyancy;
            set => _buoyancy = value;
        }

        public override void link(PhysicsActor obj)
        {
            _taintparent = obj;
        }

        public override void delink()
        {
            _taintparent = null;
        }

        public override void LockAngularMotion(byte axislocks)
        {
            // _log.DebugFormat("[axislocks]: {0}", axislocks);
            _taintAngularLock = axislocks;
        }

        internal void UpdatePositionAndVelocity()
        {
            //  no lock; called from Simulate() -- if you call this from elsewhere, gotta lock or do Monitor.Enter/Exit!
            if (outofBounds)
                return;
            if (_parent == null)
            {
                Vector3 pv = Vector3.Zero;
                bool lastZeroFlag = _zeroFlag;
                float _minvelocity = 0;
                if (Body != IntPtr.Zero) // FIXME -> or if it is a joint
                {
                    SafeNativeMethods.Vector3 vec = SafeNativeMethods.BodyGetPosition(Body);
                    SafeNativeMethods.Quaternion ori = SafeNativeMethods.BodyGetQuaternion(Body);
                    SafeNativeMethods.Vector3 vel = SafeNativeMethods.BodyGetLinearVel(Body);
                    SafeNativeMethods.Vector3 rotvel = SafeNativeMethods.BodyGetAngularVel(Body);
                    SafeNativeMethods.Vector3 torque = SafeNativeMethods.BodyGetTorque(Body);
                    _torque = new Vector3(torque.X, torque.Y, torque.Z);
                    Vector3 l_position = Vector3.Zero;
                    Quaternion l_orientation = Quaternion.Identity;

                    _lastposition = _position;
                    _lastorientation = _orientation;

                    l_position.X = vec.X;
                    l_position.Y = vec.Y;
                    l_position.Z = vec.Z;
                    l_orientation.X = ori.X;
                    l_orientation.Y = ori.Y;
                    l_orientation.Z = ori.Z;
                    l_orientation.W = ori.W;

                    if (l_position.Z < 0)
                    {
                        // This is so prim that get lost underground don't fall forever and suck up
                        //
                        // Sim resources and memory.
                        // Disables the prim's movement physics....
                        // It's a hack and will generate a console message if it fails.

                        //IsPhysical = false;

                        _acceleration.X = 0;
                        _acceleration.Y = 0;
                        _acceleration.Z = 0;

                        _velocity.X = 0;
                        _velocity.Y = 0;
                        _velocity.Z = 0;
                        _rotationalVelocity.X = 0;
                        _rotationalVelocity.Y = 0;
                        _rotationalVelocity.Z = 0;

                        if (_parent == null)
                            base.RaiseOutOfBounds(_position);

                        if (_parent == null)
                            base.RequestPhysicsterseUpdate();

                        _throttleUpdates = false;
                        throttleCounter = 0;
                        _zeroFlag = true;
                        //outofBounds = true;
                        return;
                    }

                    if (l_position.X > (int)_parent_scene.WorldExtents.X - 0.05f || l_position.X < 0f || l_position.Y > (int)_parent_scene.WorldExtents.Y - 0.05f || l_position.Y < 0f)
                    {
                        //base.RaiseOutOfBounds(l_position);
                        /*
                                                if (_crossingfailures < _parent_scene.geomCrossingFailuresBeforeOutofbounds)
                                                {
                                                    _position = l_position;
                                                    //_parent_scene.remActivePrim(this);
                                                    if (_parent == null)
                                                        base.RequestPhysicsterseUpdate();
                                                    return;
                                                }
                                                else
                                                {
                                                    if (_parent == null)
                                                        base.RaiseOutOfBounds(l_position);
                                                    return;
                                                }
                        */
                        outofBounds = true;
                        // part near the border on outside
                        if (l_position.X < 0)
                            Util.Clamp(l_position.X, -0.1f, -2f);
                        else
                            Util.Clamp(l_position.X, _parent_scene.WorldExtents.X + 0.1f, _parent_scene.WorldExtents.X + 2f);
                        if (l_position.Y < 0)
                            Util.Clamp(l_position.Y, -0.1f, -2f);
                        else
                            Util.Clamp(l_position.Y, _parent_scene.WorldExtents.Y + 0.1f, _parent_scene.WorldExtents.Y + 2f);

                        SafeNativeMethods.BodySetPosition(Body, l_position.X, l_position.Y, l_position.Z);

                        // stop it
                        SafeNativeMethods.BodySetAngularVel(Body, 0, 0, 0);
                        SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0);
                        disableBodySoft();

                        _position = l_position;
                        // tell framework to fix it
                        if (_parent == null)
                            base.RequestPhysicsterseUpdate();
                        return;
                    }


                    //float Adiff = 1.0f - Math.Abs(Quaternion.Dot(_lastorientation, l_orientation));
                    //Console.WriteLine("Adiff " + Name + " = " + Adiff);
                    if (Math.Abs(_lastposition.X - l_position.X) < 0.02
                        && Math.Abs(_lastposition.Y - l_position.Y) < 0.02
                        && Math.Abs(_lastposition.Z - l_position.Z) < 0.02
//                        && (1.0 - Math.Abs(Quaternion.Dot(_lastorientation, l_orientation)) < 0.01))
                        && 1.0 - Math.Abs(Quaternion.Dot(_lastorientation, l_orientation)) < 0.0001)  // KF 0.01 is far to large
                    {
                        _zeroFlag = true;
//Console.WriteLine("ZFT 2");
                        _throttleUpdates = false;
                    }
                    else
                    {
                        //_log.Debug(Math.Abs(_lastposition.X - l_position.X).ToString());
                        _zeroFlag = false;
                        _lastUpdateSent = false;
                        //_throttleUpdates = false;
                    }

                    if (_zeroFlag)
                    {
                        _velocity.X = 0.0f;
                        _velocity.Y = 0.0f;
                        _velocity.Z = 0.0f;

                        _acceleration.X = 0;
                        _acceleration.Y = 0;
                        _acceleration.Z = 0;

                        //_orientation.w = 0f;
                        //_orientation.X = 0f;
                        //_orientation.Y = 0f;
                        //_orientation.Z = 0f;
                        _rotationalVelocity.X = 0;
                        _rotationalVelocity.Y = 0;
                        _rotationalVelocity.Z = 0;
                        if (!_lastUpdateSent)
                        {
                            _throttleUpdates = false;
                            throttleCounter = 0;
                            _rotationalVelocity = pv;

                            if (_parent == null)
                            {
                                base.RequestPhysicsterseUpdate();
                            }

                            _lastUpdateSent = true;
                        }
                    }
                    else
                    {
                        if (lastZeroFlag != _zeroFlag)
                        {
                            if (_parent == null)
                            {
                                base.RequestPhysicsterseUpdate();
                            }
                        }

                        _lastVelocity = _velocity;

                        _position = l_position;

                        _velocity.X = vel.X;
                        _velocity.Y = vel.Y;
                        _velocity.Z = vel.Z;

                        _acceleration = (_velocity - _lastVelocity) / 0.1f;
                        _acceleration = new Vector3(_velocity.X - _lastVelocity.X / 0.1f, _velocity.Y - _lastVelocity.Y / 0.1f, _velocity.Z - _lastVelocity.Z / 0.1f);
                        //_log.Info("[PHYSICS]: V1: " + _velocity + " V2: " + _lastVelocity + " Acceleration: " + _acceleration.ToString());

                        // Note here that linearvelocity is affecting angular velocity...  so I'm guessing this is a vehicle specific thing...
                        // it does make sense to do this for tiny little instabilities with physical prim, however 0.5m/frame is fairly large.
                        // reducing this to 0.02m/frame seems to help the angular rubberbanding quite a bit, however, to make sure it doesn't affect elevators and vehicles
                        // adding these logical exclusion situations to maintain this where I think it was intended to be.
                        if (_throttleUpdates || PIDActive || _vehicle != null && _vehicle.Type != Vehicle.TYPE_NONE || Amotor != IntPtr.Zero)
                        {
                            _minvelocity = 0.5f;
                        }
                        else
                        {
                            _minvelocity = 0.02f;
                        }

                        if (_velocity.ApproxEquals(pv, _minvelocity))
                        {
                            _rotationalVelocity = pv;
                        }
                        else
                        {
                            _rotationalVelocity = new Vector3(rotvel.X, rotvel.Y, rotvel.Z);
                        }

                        //_log.Debug("ODE: " + _rotationalVelocity.ToString());
                        _orientation.X = ori.X;
                        _orientation.Y = ori.Y;
                        _orientation.Z = ori.Z;
                        _orientation.W = ori.W;
                        _lastUpdateSent = false;
                        if (!_throttleUpdates || throttleCounter > _parent_scene.geomUpdatesPerThrottledUpdate)
                        {
                            if (_parent == null)
                            {
                                base.RequestPhysicsterseUpdate();
                            }
                        }
                        else
                        {
                            throttleCounter++;
                        }
                    }
                    _lastposition = l_position;
                }
                else
                {
                    // Not a body..   so Make sure the client isn't interpolating
                    _velocity.X = 0;
                    _velocity.Y = 0;
                    _velocity.Z = 0;

                    _acceleration.X = 0;
                    _acceleration.Y = 0;
                    _acceleration.Z = 0;

                    _rotationalVelocity.X = 0;
                    _rotationalVelocity.Y = 0;
                    _rotationalVelocity.Z = 0;
                    _zeroFlag = true;
                }
            }
        }

        public override bool FloatOnWater
        {
            set {
                _taintCollidesWater = value;
                _parent_scene.AddPhysicsActorTaint(this);
            }
        }

        public override void SetMomentum(Vector3 momentum)
        {
        }

        public override Vector3 PIDTarget
        {
            set
            {
                if (value.IsFinite())
                {
                    _PIDTarget = value;
                }
                else
                    _log.WarnFormat("[PHYSICS]: Got NaN PIDTarget from Scene on Object {0}", Name);
            }
        }

        public override bool PIDActive { get; set; }
        public override float PIDTau { set => _PIDTau = value; }

        public override float PIDHoverHeight { set => _PIDHoverHeight = value; }
        public override bool PIDHoverActive { get => _useHoverPID;
            set => _useHoverPID = value;
        }
        public override PIDHoverType PIDHoverType { set => _PIDHoverType = value; }
        public override float PIDHoverTau { set => _PIDHoverTau = value; }

        public override Quaternion APIDTarget{ set { return; } }

        public override bool APIDActive{ set { return; } }

        public override float APIDStrength{ set { return; } }

        public override float APIDDamping{ set { return; } }

        private void createAMotor(byte axislock)
        {
            if (Body == IntPtr.Zero)
                return;

            if (Amotor != IntPtr.Zero)
            {
                SafeNativeMethods.JointDestroy(Amotor);
                Amotor = IntPtr.Zero;
            }

            if(axislock == 0)
                return;

            int axisnum = 0;
            bool axisX = false;
            bool axisY = false;
            bool axisZ = false;
            if((axislock & 0x02) != 0)
                {
                axisnum++;
                axisX = true;
                }
            if((axislock & 0x04) != 0)
                {
                axisnum++;
                axisY = true;
                }
            if((axislock & 0x08) != 0)
                {
                axisnum++;
                axisZ = true;
                }

            if(axisnum == 0)
                return;
            // stop it
            SafeNativeMethods.BodySetTorque(Body, 0, 0, 0);
            SafeNativeMethods.BodySetAngularVel(Body, 0, 0, 0);

            Amotor = SafeNativeMethods.JointCreateAMotor(_parent_scene.world, IntPtr.Zero);
            SafeNativeMethods.JointAttach(Amotor, Body, IntPtr.Zero);

            SafeNativeMethods.JointSetAMotorMode(Amotor, 0);

            SafeNativeMethods.JointSetAMotorNumAxes(Amotor, axisnum);

            // get current orientation to lock

            SafeNativeMethods.Quaternion dcur = SafeNativeMethods.BodyGetQuaternion(Body);
            Quaternion curr; // crap convertion between identical things
            curr.X = dcur.X;
            curr.Y = dcur.Y;
            curr.Z = dcur.Z;
            curr.W = dcur.W;
            Vector3 ax;

            int i = 0;
            int j = 0;
            if (axisX)
            {
                ax = new Vector3(1, 0, 0) * curr; // rotate world X to current local X
                SafeNativeMethods.JointSetAMotorAxis(Amotor, 0, 0, ax.X, ax.Y, ax.Z);
                SafeNativeMethods.JointSetAMotorAngle(Amotor, 0, 0);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.LoStop, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.HiStop, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.Vel, 0);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.FudgeFactor, 0.0001f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.Bounce, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.CFM, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.FMax, 5e8f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.StopCFM, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, (int)SafeNativeMethods.JointParam.StopERP, 0.8f);
                i++;
                j = 256; // move to next axis set
            }

            if (axisY)
            {
                ax = new Vector3(0, 1, 0) * curr;
                SafeNativeMethods.JointSetAMotorAxis(Amotor, i, 0, ax.X, ax.Y, ax.Z);
                SafeNativeMethods.JointSetAMotorAngle(Amotor, i, 0);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.LoStop, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.HiStop, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.Vel, 0);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.FudgeFactor, 0.0001f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.Bounce, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.CFM, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.FMax, 5e8f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.StopCFM, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.StopERP, 0.8f);
                i++;
                j += 256;
            }

            if (axisZ)
            {
                ax = new Vector3(0, 0, 1) * curr;
                SafeNativeMethods.JointSetAMotorAxis(Amotor, i, 0, ax.X, ax.Y, ax.Z);
                SafeNativeMethods.JointSetAMotorAngle(Amotor, i, 0);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.LoStop, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.HiStop, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.Vel, 0);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.FudgeFactor, 0.0001f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.Bounce, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.CFM, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.FMax, 5e8f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.StopCFM, 0f);
                SafeNativeMethods.JointSetAMotorParam(Amotor, j + (int)SafeNativeMethods.JointParam.StopERP, 0.8f);
            }
        }

        public override void SubscribeEvents(int ms)
        {
            _eventsubscription = ms;
            _parent_scene.AddCollisionEventReporting(this);
        }

        public override void UnSubscribeEvents()
        {
            _parent_scene.RemoveCollisionEventReporting(this);
            _eventsubscription = 0;
        }

        public override void AddCollisionEvent(uint CollidedWith, ContactPoint contact)
        {
            CollisionEventsThisFrame.AddCollider(CollidedWith, contact);
        }

        public void SendCollisions()
        {
            if (_collisionsOnPreviousFrame || CollisionEventsThisFrame.Count > 0)
            {
                base.SendCollisionUpdate(CollisionEventsThisFrame);

                if (CollisionEventsThisFrame.Count > 0)
                {
                    _collisionsOnPreviousFrame = true;
                    CollisionEventsThisFrame.Clear();
                }
                else
                {
                    _collisionsOnPreviousFrame = false;
                }
            }
        }

        public override bool SubscribedEvents()
        {
            if (_eventsubscription > 0)
                return true;
            return false;
        }

        public override void SetMaterial(int pMaterial)
        {
            _material = pMaterial;
        }

        private void CheckMeshAsset()
        {
            if (_pbs.SculptEntry && !_assetFailed && _pbs.SculptTexture != UUID.Zero)
            {
                _assetFailed = true;
                Util.FireAndForget(delegate
                    {
                        RequestAssetDelegate assetProvider = _parent_scene.RequestAssetMethod;
                        if (assetProvider != null)
                            assetProvider(_pbs.SculptTexture, MeshAssetReceived);
                    }, null, "ODEPrim.CheckMeshAsset");
            }
        }

        private void MeshAssetReceived(AssetBase asset)
        {
            if (asset != null && asset.Data != null && asset.Data.Length > 0)
            {
                if (!_pbs.SculptEntry)
                    return;
                if (_pbs.SculptTexture.ToString() != asset.ID)
                    return;

                _pbs.SculptData = new byte[asset.Data.Length];
                asset.Data.CopyTo(_pbs.SculptData, 0);
//                _assetFailed = false;

//                _log.DebugFormat(
//                    "[ODE PRIM]: Received mesh/sculpt data asset {0} with {1} bytes for {2} at {3} in {4}",
//                    _pbs.SculptTexture, _pbs.SculptData.Length, Name, _position, _parent_scene.Name);

                _taintshape = true;
               _parent_scene.AddPhysicsActorTaint(this);
            }
            else
            {
                _log.WarnFormat(
                    "[ODE PRIM]: Could not get mesh/sculpt asset {0} for {1} at {2} in {3}",
                    _pbs.SculptTexture, Name, _position, _parent_scene.PhysicsSceneName);
            }
        }
    }
}