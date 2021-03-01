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
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.ubOde
{
    public class OdePrim : PhysicsActor
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private bool _isphysical;
        private bool _fakeisphysical;
        private bool _isphantom;
        private bool _fakeisphantom;
        internal bool _isVolumeDetect; // If true, this prim only detects collisions but doesn't collide actively
        private bool _fakeisVolumeDetect; // If true, this prim only detects collisions but doesn't collide actively

        internal bool _building;
        protected bool _forcePosOrRotation;
        private bool _iscolliding;

        internal bool _isSelected;
        private bool _delaySelect;
        private bool _lastdoneSelected;
        internal bool _outbounds;

        private byte _angularlocks = 0;

        private Quaternion _lastorientation;
        private Quaternion _orientation;

        private Vector3 _position;
        private Vector3 _velocity;
        private Vector3 _lastVelocity;
        private Vector3 _lastposition;
        private Vector3 _rotationalVelocity;
        private Vector3 _size;
        private Vector3 _acceleration;
        private IntPtr Amotor;

        internal Vector3 _force;
        internal Vector3 _forceacc;
        internal Vector3 _torque;
        internal Vector3 _angularForceacc;

        private readonly float _invTimeStep;
        private readonly float _timeStep;

        private Vector3 _PIDTarget;
        private float _PIDTau;
        private bool _usePID;

        private float _PIDHoverHeight;
        private float _PIDHoverTau;
        private bool _useHoverPID;
        private PIDHoverType _PIDHoverType;
        private float _targetHoverHeight;
        private float _groundHeight;
        private float _waterHeight;
        private float _buoyancy;                //KF: _buoyancy should be set by llSetBuoyancy() for non-vehicle.

        private readonly int _body_autodisable_frames;
        public int _bodydisablecontrol = 0;
        private float _gravmod = 1.0f;

        // Default we're a Geometry
        private CollisionCategories _collisionCategories = CollisionCategories.Geom;
        // Default colide nonphysical don't try to colide with anything
        private const CollisionCategories _default_collisionFlagsNotPhysical = 0;

        private const CollisionCategories _default_collisionFlagsPhysical = CollisionCategories.Geom |
                                                                             CollisionCategories.Character |
                                                                             CollisionCategories.Land |
                                                                             CollisionCategories.VolumeDtc;

//        private bool _collidesLand = true;
        private bool _collidesWater;
//        public bool _returnCollisions;

        private bool _NoColide;  // for now only for internal use for bad meshs


        // Default, Collide with Other Geometries, spaces and Bodies
        private CollisionCategories _collisionFlags = _default_collisionFlagsNotPhysical;

        public bool _disabled;

        private uint _localID;

        private IMesh _mesh;
        private readonly object _meshlock = new object();
        private PrimitiveBaseShape _pbs;

        private UUID? _assetID;
        private MeshState _meshState;

        public ODEScene _parent_scene;

        /// <summary>
        /// The physics space which contains prim geometry
        /// </summary>
        public IntPtr _targetSpace;

        public IntPtr pri_geom;
        public IntPtr _triMeshData;

        private PhysicsActor _parent;

        private readonly List<OdePrim> childrenPrim = new List<OdePrim>();

        public float _collisionscore;
        private int _colliderfilter = 0;

        public IntPtr collide_geom; // for objects: geom if single prim space it linkset

        private float _density;
        private byte _shapetype;
        private byte _fakeShapetype;
        public bool _zeroFlag;
        private bool _lastUpdateSent;

        public IntPtr Body;

        private Vector3 _target_velocity;

        public Vector3 _OBBOffset;
        public Vector3 _OBB;
        public float primOOBradiusSQ;

        private bool _hasOBB = true;

        private float _physCost;
        private float _streamCost;

        internal SafeNativeMethods.Mass primdMass; // prim inertia information on it's own referencial
        private PhysicsInertiaData _InertiaOverride;
        float primMass; // prim own mass
        float primVolume; // prim own volume;
        float _mass; // object mass acording to case

        public int givefakepos;
        private Vector3 fakepos;
        public int givefakeori;
        private Quaternion fakeori;
        private PhysicsInertiaData _fakeInertiaOverride;

        private int _eventsubscription;
        private int _cureventsubscription;
        private CollisionEventUpdate CollisionEvents = null;
        private CollisionEventUpdate CollisionVDTCEvents = null;
        private bool SentEmptyCollisionsEvent;

        public volatile bool childPrim;

        public ODEDynamics _vehicle;

        internal int _material = (int)Material.Wood;
        private float mu;
        private float bounce;

        /// <summary>
        /// Is this prim subject to physics?  Even if not, it's still solid for collision purposes.
        /// </summary>
        public override bool IsPhysical  // this is not reliable for internal use
        {
            get => _fakeisphysical;
            set
            {
                _fakeisphysical = value; // we show imediatly to outside that we changed physical
                // and also to stop imediatly some updates
                // but real change will only happen in taintprocessing

                if (!value) // Zero the remembered last velocity
                    _lastVelocity = Vector3.Zero;
                AddChange(changes.Physical, value);
            }
        }

        public override bool IsVolumeDtc
        {
            get => _fakeisVolumeDetect;
            set
            {
                _fakeisVolumeDetect = value;
                AddChange(changes.VolumeDtc, value);
            }
        }

        public override bool Phantom  // this is not reliable for internal use
        {
            get => _fakeisphantom;
            set
            {
                _fakeisphantom = value;
                AddChange(changes.Phantom, value);
            }
        }

        public override bool Building  // this is not reliable for internal use
        {
            get => _building;
            set =>
                //                if (value)
                //                    _building = true;
                AddChange(changes.building, value);
        }

        public override void getContactData(ref ContactData cdata)
        {
            cdata.mu = mu;
            cdata.bounce = bounce;

            //            cdata.softcolide = _softcolide;
            cdata.softcolide = false;

            if (_isphysical)
            {
                ODEDynamics veh;
                if (_parent != null)
                    veh = ((OdePrim)_parent)._vehicle;
                else
                    veh = _vehicle;

                if (veh != null && veh.Type != Vehicle.TYPE_NONE)
                    cdata.mu *= veh.FrictionFactor;
//                    cdata.mu *= 0;
            }
        }

        public override float PhysicsCost => _physCost;

        public override float StreamCost => _streamCost;

        public override int PhysicsActorType
        {
            get => (int)ActorTypes.Prim;
            set { return; }
        }

        public override bool SetAlwaysRun
        {
            get => false;
            set { return; }
        }

        public override uint LocalID
        {
            get => _localID;
            set
            {
                uint oldid = _localID;
                _localID = value;
                _parent_scene.changePrimID(this, oldid);
            }
        }

        public override PhysicsActor ParentActor
        {
            get
            {
                if (childPrim)
                    return _parent;
                else
                    return (PhysicsActor)this;
            }
        }

        public override bool Grabbed
        {
            set { return; }
        }

        public override bool Selected
        {
            set
            {
                if (value)
                    _isSelected = value; // if true set imediatly to stop moves etc
                AddChange(changes.Selected, value);
            }
        }

        public override bool Flying
        {
            // no flying prims for you
            get => false;
            set { }
        }

        public override bool IsColliding
        {
            get => _iscolliding;
            set
            {
                if (value)
                {
                    _colliderfilter += 2;
                    if (_colliderfilter > 2)
                        _colliderfilter = 2;
                }
                else
                {
                    _colliderfilter--;
                    if (_colliderfilter < 0)
                        _colliderfilter = 0;
                }

                if (_colliderfilter == 0)
                    _iscolliding = false;
                else
                    _iscolliding = true;
            }
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


        public override bool ThrottleUpdates {get;set;}

        public override bool Stopped => _zeroFlag;

        public override Vector3 Position
        {
            get
            {
                if (givefakepos > 0)
                    return fakepos;
                else
                    return _position;
            }

            set
            {
                fakepos = value;
                givefakepos++;
                AddChange(changes.Position, value);
            }
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                if (value.IsFinite())
                {
                     _parent_scene._meshWorker.ChangeActorPhysRep(this, _pbs, value, _fakeShapetype);
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: Got NaN Size on object {0}", Name);
                }
            }
        }

        public override float Mass => primMass;

        public override Vector3 Force
        {
            get => _force;
            set
            {
                if (value.IsFinite())
                {
                    AddChange(changes.Force, value);
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: NaN in Force Applied to an Object {0}", Name);
                }
            }
        }

        public override void SetVolumeDetect(int param)
        {
            _fakeisVolumeDetect = param != 0;
            AddChange(changes.VolumeDtc, _fakeisVolumeDetect);
        }

        public override Vector3 GeometricCenter =>
            // this is not real geometric center but a average of positions relative to root prim acording to
            // http://wiki.secondlife.com/wiki/llGetGeometricCenter
            // ignoring tortured prims details since sl also seems to ignore
            // so no real use in doing it on physics
            Vector3.Zero;

        public override PhysicsInertiaData GetInertiaData()
        {
            PhysicsInertiaData inertia;
            if(childPrim)
            {
                if(_parent != null)
                    return _parent.GetInertiaData();
                else
                {
                    inertia = new PhysicsInertiaData
                    {
                        TotalMass = -1
                    };
                    return inertia;
                }
            }

            inertia = new PhysicsInertiaData();

            // double buffering
            if(_fakeInertiaOverride != null)
            { 
                SafeNativeMethods.Mass objdmass = new SafeNativeMethods.Mass();
                objdmass.I.M00 = _fakeInertiaOverride.Inertia.X;
                objdmass.I.M11 = _fakeInertiaOverride.Inertia.Y;
                objdmass.I.M22 = _fakeInertiaOverride.Inertia.Z;

                objdmass.mass = _fakeInertiaOverride.TotalMass;
                    
                if(Math.Abs(_fakeInertiaOverride.InertiaRotation.W) < 0.999)
                {
                    SafeNativeMethods.Matrix3 inertiarotmat = new SafeNativeMethods.Matrix3();
                    SafeNativeMethods.Quaternion inertiarot = new SafeNativeMethods.Quaternion
                    {
                        X = _fakeInertiaOverride.InertiaRotation.X,
                        Y = _fakeInertiaOverride.InertiaRotation.Y,
                        Z = _fakeInertiaOverride.InertiaRotation.Z,
                        W = _fakeInertiaOverride.InertiaRotation.W
                    };
                    SafeNativeMethods.RfromQ(out inertiarotmat, ref inertiarot);
                    SafeNativeMethods.MassRotate(ref objdmass, ref inertiarotmat);
                }

                inertia.TotalMass = _fakeInertiaOverride.TotalMass;
                inertia.CenterOfMass = _fakeInertiaOverride.CenterOfMass;
                inertia.Inertia.X = objdmass.I.M00;
                inertia.Inertia.Y = objdmass.I.M11;
                inertia.Inertia.Z = objdmass.I.M22;
                inertia.InertiaRotation.X =  objdmass.I.M01;
                inertia.InertiaRotation.Y =  objdmass.I.M02;
                inertia.InertiaRotation.Z =  objdmass.I.M12;
                return inertia;
            }

            inertia.TotalMass = _mass;

            if(Body == IntPtr.Zero || pri_geom == IntPtr.Zero)
            {
                inertia.CenterOfMass = Vector3.Zero;
                inertia.Inertia = Vector3.Zero;
                inertia.InertiaRotation =  Vector4.Zero;
                return inertia;
            }

            SafeNativeMethods.Vector3 dtmp;
            SafeNativeMethods.Mass m = new SafeNativeMethods.Mass();
            lock(_parent_scene.OdeLock)
            {
                SafeNativeMethods.AllocateODEDataForThread(0);
                dtmp = SafeNativeMethods.GeomGetOffsetPosition(pri_geom);
                SafeNativeMethods.BodyGetMass(Body, out m);
            }

            Vector3 cm = new Vector3(-dtmp.X, -dtmp.Y, -dtmp.Z);
            inertia.CenterOfMass = cm;
            inertia.Inertia = new Vector3(m.I.M00, m.I.M11, m.I.M22);
            inertia.InertiaRotation = new Vector4(m.I.M01, m.I.M02 , m.I.M12, 0);

            return inertia;
        }

        public override void SetInertiaData(PhysicsInertiaData inertia)
        {
            if(childPrim)
            {
                if(_parent != null)
                    _parent.SetInertiaData(inertia);
                return;
            }

            if(inertia.TotalMass > 0)
                _fakeInertiaOverride = new PhysicsInertiaData(inertia);
            else
                _fakeInertiaOverride = null;

            if (inertia.TotalMass > _parent_scene.maximumMassObject)
                inertia.TotalMass = _parent_scene.maximumMassObject;
            AddChange(changes.SetInertia,(object)_fakeInertiaOverride);
        }

        public override Vector3 CenterOfMass
        {
            get
            {
                lock (_parent_scene.OdeLock)
                {
                    SafeNativeMethods.AllocateODEDataForThread(0);

                    SafeNativeMethods.Vector3 dtmp;
                    if (!childPrim && Body != IntPtr.Zero)
                    {
                        dtmp = SafeNativeMethods.BodyGetPosition(Body);
                        return new Vector3(dtmp.X, dtmp.Y, dtmp.Z);
                    }
                    else if (pri_geom != IntPtr.Zero)
                    {
                        SafeNativeMethods.Quaternion dq;
                        SafeNativeMethods.GeomCopyQuaternion(pri_geom, out dq);
                        Quaternion q;
                        q.X = dq.X;
                        q.Y = dq.Y;
                        q.Z = dq.Z;
                        q.W = dq.W;

                        Vector3 Ptot = _OBBOffset * q;
                        dtmp = SafeNativeMethods.GeomGetPosition(pri_geom);
                        Ptot.X += dtmp.X;
                        Ptot.Y += dtmp.Y;
                        Ptot.Z += dtmp.Z;

                        //                    if(childPrim)  we only know about physical linksets
                        return Ptot;
/*
                        float tmass = _mass;
                        Ptot *= tmass;

                        float m;

                        foreach (OdePrim prm in childrenPrim)
                        {
                            m = prm._mass;
                            Ptot += prm.CenterOfMass * m;
                            tmass += m;
                        }

                        if (tmass == 0)
                            tmass = 0;
                        else
                            tmass = 1.0f / tmass;

                        Ptot *= tmass;
                        return Ptot;
*/
                    }
                    else
                        return _position;
                }
            }
        }

        public override PrimitiveBaseShape Shape
        {
            set =>
                //                AddChange(changes.Shape, value);
                _parent_scene._meshWorker.ChangeActorPhysRep(this, value, _size, _fakeShapetype);
        }

        public override byte PhysicsShapeType
        {
            get => _fakeShapetype;
            set
            {
                _fakeShapetype = value;
               _parent_scene._meshWorker.ChangeActorPhysRep(this, _pbs, _size, value);
            }
        }

        public override Vector3 rootVelocity
        {
            get
            {
                if(_parent != null)
                    return ((OdePrim)_parent).Velocity;
                return Velocity;
            }
        }

        public override Vector3 Velocity
        {
            get
            {
                if (_zeroFlag)
                    return Vector3.Zero;
                return _velocity;
            }
            set
            {
                if (value.IsFinite())
                {
                    if(_outbounds)
                        _velocity = value;
                    else
                        AddChange(changes.Velocity, value);
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
                    AddChange(changes.Torque, value);
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
            get
            {
                if (givefakeori > 0)
                    return fakeori;
                else

                    return _orientation;
            }
            set
            {
                if (QuaternionIsFinite(value))
                {
                    fakeori = value;
                    givefakeori++;

                    value.Normalize();

                    AddChange(changes.Orientation, value);
                }
                else
                    _log.WarnFormat("[PHYSICS]: Got NaN quaternion Orientation from Scene in Object {0}", Name);

            }
        }

        public override Vector3 Acceleration
        {
            get => _acceleration;
            set
            {
                if(_outbounds)
                    _acceleration = value;
            }
        }

        public override Vector3 RotationalVelocity
        {
            get
            {
                Vector3 pv = Vector3.Zero;
                if (_zeroFlag)
                    return pv;

                if (_rotationalVelocity.ApproxEquals(pv, 0.0001f))
                    return pv;

                return _rotationalVelocity;
            }
            set
            {
                if (value.IsFinite())
                {
                    if(_outbounds)
                        _rotationalVelocity = value;
                    else
                        AddChange(changes.AngVelocity, value);
                }
                else
                {
                    _log.WarnFormat("[PHYSICS]: Got NaN RotationalVelocity in Object {0}", Name);
                }
            }
        }

        public override float Buoyancy
        {
            get => _buoyancy;
            set => AddChange(changes.Buoyancy,value);
        }

        public override bool FloatOnWater
        {
            set => AddChange(changes.CollidesWater, value);
        }

        public override Vector3 PIDTarget
        {
            set
            {
                if (value.IsFinite())
                {
                    AddChange(changes.PIDTarget,value);
                }
                else
                    _log.WarnFormat("[PHYSICS]: Got NaN PIDTarget from Scene on Object {0}", Name);
            }
        }

        public override bool PIDActive
        {
            get => _usePID;
            set => AddChange(changes.PIDActive,value);
        }

        public override float PIDTau
        {
            set
            {
                float tmp = 0;
                if (value > 0)
                {
                    float mint = 0.05f > _timeStep ? 0.05f : _timeStep;
                    if (value < mint)
                        tmp = mint;
                    else
                        tmp = value;
                }
                AddChange(changes.PIDTau,tmp);
            }
        }

        public override float PIDHoverHeight
        {
            set => AddChange(changes.PIDHoverHeight,value);
        }
        public override bool PIDHoverActive
        {
            get => _useHoverPID;
            set => AddChange(changes.PIDHoverActive, value);
        }

        public override PIDHoverType PIDHoverType
        {
            set => AddChange(changes.PIDHoverType,value);
        }

        public override float PIDHoverTau
        {
            set
            {
                float tmp =0;
                if (value > 0)
                {
                    float mint = 0.05f > _timeStep ? 0.05f : _timeStep;
                    if (value < mint)
                        tmp = mint;
                    else
                        tmp = value;
                }
                AddChange(changes.PIDHoverTau, tmp);
            }
        }

        public override Quaternion APIDTarget { set { return; } }

        public override bool APIDActive { set { return; } }

        public override float APIDStrength { set { return; } }

        public override float APIDDamping { set { return; } }

        public override int VehicleType
        {
            // we may need to put a fake on this
            get
            {
                if (_vehicle == null)
                    return (int)Vehicle.TYPE_NONE;
                else
                    return (int)_vehicle.Type;
            }
            set => AddChange(changes.VehicleType, value);
        }

        public override void VehicleFloatParam(int param, float value)
        {
            strVehicleFloatParam fp = new strVehicleFloatParam
            {
                param = param,
                value = value
            };
            AddChange(changes.VehicleFloatParam, fp);
        }

        public override void VehicleVectorParam(int param, Vector3 value)
        {
            strVehicleVectorParam fp = new strVehicleVectorParam
            {
                param = param,
                value = value
            };
            AddChange(changes.VehicleVectorParam, fp);
        }

        public override void VehicleRotationParam(int param, Quaternion value)
        {
            strVehicleQuatParam fp = new strVehicleQuatParam
            {
                param = param,
                value = value
            };
            AddChange(changes.VehicleRotationParam, fp);
        }

        public override void VehicleFlags(int param, bool value)
        {
            strVehicleBoolParam bp = new strVehicleBoolParam
            {
                param = param,
                value = value
            };
            AddChange(changes.VehicleFlags, bp);
        }

        public override void SetVehicle(object vdata)
        {
            AddChange(changes.SetVehicle, vdata);
        }
        public void SetAcceleration(Vector3 accel)
        {
            _acceleration = accel;
        }

        public override void AddForce(Vector3 force, bool pushforce)
        {
            if (force.IsFinite())
            {
                if(pushforce)
                    AddChange(changes.AddForce, force);
                else // a impulse
                    AddChange(changes.AddForce, force * _invTimeStep);
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
//                if(pushforce)  for now applyrotationimpulse seems more happy applied as a force
                    AddChange(changes.AddAngForce, force);
//                else // a impulse
//                    AddChange(changes.AddAngForce, force * _invTimeStep);
            }
            else
            {
                _log.WarnFormat("[PHYSICS]: Got Invalid Angular force vector from Scene in Object {0}", Name);
            }
        }

        public override void CrossingFailure()
        {
            lock(_parent_scene.OdeLock)
            {
                if (_outbounds)
                {
                    _position.X = Util.Clip(_position.X, 0.5f, _parent_scene.WorldExtents.X - 0.5f);
                    _position.Y = Util.Clip(_position.Y, 0.5f, _parent_scene.WorldExtents.Y - 0.5f);
                    _position.Z = Util.Clip(_position.Z + 0.2f, Constants.MinSimulationHeight, Constants.MaxSimulationHeight);

                    _lastposition = _position;
                    _velocity.X = 0;
                    _velocity.Y = 0;
                    _velocity.Z = 0;

                    SafeNativeMethods.AllocateODEDataForThread(0);

                    _lastVelocity = _velocity;
                    if (_vehicle != null && _vehicle.Type != Vehicle.TYPE_NONE)
                        _vehicle.Stop();

                    if(Body != IntPtr.Zero)
                        SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0); // stop it
                    if (pri_geom != IntPtr.Zero)
                        SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);

                    _outbounds = false;
                    changeDisable(false);
                    base.RequestPhysicsterseUpdate();
                }
            }
        }

        public override void CrossingStart()
        {
            lock(_parent_scene.OdeLock)
            {
                if (_outbounds || childPrim)
                    return;

                _outbounds = true;

                _lastposition = _position;
                _lastorientation = _orientation;

                SafeNativeMethods.AllocateODEDataForThread(0);
                if(Body != IntPtr.Zero)
                {
                    SafeNativeMethods.Vector3 dtmp = SafeNativeMethods.BodyGetAngularVel(Body);
                    _rotationalVelocity.X = dtmp.X;
                    _rotationalVelocity.Y = dtmp.Y;
                    _rotationalVelocity.Z = dtmp.Z;

                    dtmp = SafeNativeMethods.BodyGetLinearVel(Body);
                    _velocity.X = dtmp.X;
                    _velocity.Y = dtmp.Y;
                    _velocity.Z = dtmp.Z;

                    SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0); // stop it
                    SafeNativeMethods.BodySetAngularVel(Body, 0, 0, 0);
                }
                if(pri_geom != IntPtr.Zero)
                    SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);
                disableBodySoft(); // stop collisions
                UnSubscribeEvents();
            }
        }

        public override void SetMomentum(Vector3 momentum)
        {
        }

        public override void SetMaterial(int pMaterial)
        {
            _material = pMaterial;
            mu = _parent_scene._materialContactsData[pMaterial].mu;
            bounce = _parent_scene._materialContactsData[pMaterial].bounce;
        }

        public override float Density
        {
            get => _density * 100f;
            set
            {
                float old = _density;
                _density = value / 100f;
 //               if(_density != old)
 //                   UpdatePrimBodyData();
            }
        }
        public override float GravModifier
        {
            get => _gravmod;
            set
            {
                _gravmod = value;
                if (_vehicle != null)
                    _vehicle.GravMod = _gravmod;
            }
        }
        public override float Friction
        {
            get => mu;
            set => mu = value;
        }

        public override float Restitution
        {
            get => bounce;
            set => bounce = value;
        }

        public void setPrimForRemoval()
        {
            AddChange(changes.Remove, null);
        }

        public override void link(PhysicsActor obj)
        {
            AddChange(changes.Link, obj);
        }

        public override void delink()
        {
            AddChange(changes.DeLink, null);
        }

        public override void LockAngularMotion(byte axislock)
        {
//                _log.DebugFormat("[axislock]: <{0},{1},{2}>", axis.X, axis.Y, axis.Z);
            AddChange(changes.AngLock, axislock);

        }

        public override void SubscribeEvents(int ms)
        {
            _eventsubscription = ms;
            _cureventsubscription = 0;
            if (CollisionEvents == null)
                CollisionEvents = new CollisionEventUpdate();
            if (CollisionVDTCEvents == null)
                CollisionVDTCEvents = new CollisionEventUpdate();
            SentEmptyCollisionsEvent = false;
        }

        public override void UnSubscribeEvents()
        {
            if (CollisionVDTCEvents != null)
            {
                CollisionVDTCEvents.Clear();
                CollisionVDTCEvents = null;
            }
            if (CollisionEvents != null)
            {
                CollisionEvents.Clear();
                CollisionEvents = null;
            }
            _eventsubscription = 0;
           _parent_scene.RemoveCollisionEventReporting(this);
        }

        public override void AddCollisionEvent(uint CollidedWith, ContactPoint contact)
        {
            if (CollisionEvents == null)
                CollisionEvents = new CollisionEventUpdate();

            CollisionEvents.AddCollider(CollidedWith, contact);
            _parent_scene.AddCollisionEventReporting(this);
        }

        public override void AddVDTCCollisionEvent(uint CollidedWith, ContactPoint contact)
        {
            if (CollisionVDTCEvents == null)
                CollisionVDTCEvents = new CollisionEventUpdate();

            CollisionVDTCEvents.AddCollider(CollidedWith, contact);
            _parent_scene.AddCollisionEventReporting(this);
        }

        internal void SleeperAddCollisionEvents()
        {
            if(CollisionEvents != null && CollisionEvents._objCollisionList.Count != 0)
            {
                foreach(KeyValuePair<uint,ContactPoint> kvp in CollisionEvents._objCollisionList)
                {
                    if(kvp.Key == 0)
                        continue;
                    OdePrim other = _parent_scene.getPrim(kvp.Key);
                    if(other == null)
                        continue;
                    ContactPoint cp = kvp.Value;
                    cp.SurfaceNormal = - cp.SurfaceNormal;
                    cp.RelativeSpeed = -cp.RelativeSpeed;
                    other.AddCollisionEvent(ParentActor.LocalID,cp);
                }
            }
            if(CollisionVDTCEvents != null && CollisionVDTCEvents._objCollisionList.Count != 0)
            {
                foreach(KeyValuePair<uint,ContactPoint> kvp in CollisionVDTCEvents._objCollisionList)
                {
                    OdePrim other = _parent_scene.getPrim(kvp.Key);
                    if(other == null)
                        continue;
                    ContactPoint cp = kvp.Value;
                    cp.SurfaceNormal = - cp.SurfaceNormal;
                    cp.RelativeSpeed = -cp.RelativeSpeed;
                    other.AddCollisionEvent(ParentActor.LocalID,cp);
                }
            }
        }

        internal void clearSleeperCollisions()
        {
            if(CollisionVDTCEvents != null && CollisionVDTCEvents.Count >0 )
                CollisionVDTCEvents.Clear();
        }

        public void SendCollisions(int timestep)
        {
            if (_cureventsubscription < 50000)
                _cureventsubscription += timestep;


            if (_cureventsubscription < _eventsubscription)
                return;

            if (CollisionEvents == null)
                return;

            int ncolisions = CollisionEvents._objCollisionList.Count;

            if (!SentEmptyCollisionsEvent || ncolisions > 0)
            {
                base.SendCollisionUpdate(CollisionEvents);
                _cureventsubscription = 0;

                if (ncolisions == 0)
                {
                    SentEmptyCollisionsEvent = true;
//                    _parent_scene.RemoveCollisionEventReporting(this);
                }
                else if(Body == IntPtr.Zero || SafeNativeMethods.BodyIsEnabled(Body) && _bodydisablecontrol >= 0)
                {
                    SentEmptyCollisionsEvent = false;
                    CollisionEvents.Clear();
                }
            }
        }

        public override bool SubscribedEvents()
        {
            if (_eventsubscription > 0)
                return true;
            return false;
        }

        public OdePrim(string primName, ODEScene parent_scene, Vector3 pos, Vector3 size,
                       Quaternion rotation, PrimitiveBaseShape pbs, bool pisPhysical,bool pisPhantom,byte _shapeType,uint plocalID)
        {
            _parent_scene = parent_scene;

            Name = primName;
            _localID = plocalID;

            _vehicle = null;

            if (!pos.IsFinite())
            {
                pos = new Vector3((float)Constants.RegionSize * 0.5f, (float)Constants.RegionSize * 0.5f,
                    parent_scene.GetTerrainHeightAtXY((float)Constants.RegionSize * 0.5f, (float)Constants.RegionSize * 0.5f) + 0.5f);
                _log.WarnFormat("[PHYSICS]: Got nonFinite Object create Position for {0}", Name);
            }
            _position = pos;
            givefakepos = 0;

            _timeStep = parent_scene.ODE_STEPSIZE;
            _invTimeStep = 1f / _timeStep;

            _density = parent_scene.geomDefaultDensity;
            _body_autodisable_frames = parent_scene.bodyFramesAutoDisable;

            pri_geom = IntPtr.Zero;
            collide_geom = IntPtr.Zero;
            Body = IntPtr.Zero;

            if (!size.IsFinite())
            {
                size = new Vector3(0.5f, 0.5f, 0.5f);
                _log.WarnFormat("[PHYSICS]: Got nonFinite Object create Size for {0}", Name);
            }

            if (size.X <= 0) size.X = 0.01f;
            if (size.Y <= 0) size.Y = 0.01f;
            if (size.Z <= 0) size.Z = 0.01f;

            _size = size;

            if (!QuaternionIsFinite(rotation))
            {
                rotation = Quaternion.Identity;
                _log.WarnFormat("[PHYSICS]: Got nonFinite Object create Rotation for {0}", Name);
            }

            _orientation = rotation;
            givefakeori = 0;

            _pbs = pbs;

            _targetSpace = IntPtr.Zero;

            if (pos.Z < 0)
            {
                _isphysical = false;
            }
            else
            {
                _isphysical = pisPhysical;
            }
            _fakeisphysical = _isphysical;

            _isVolumeDetect = false;
            _fakeisVolumeDetect = false;

            _force = Vector3.Zero;

            _iscolliding = false;
            _colliderfilter = 0;
            _NoColide = false;

            _triMeshData = IntPtr.Zero;

            _fakeShapetype = _shapeType;

            _lastdoneSelected = false;
            _isSelected = false;
            _delaySelect = false;

            _isphantom = pisPhantom;
            _fakeisphantom = pisPhantom;

            mu = parent_scene._materialContactsData[(int)Material.Wood].mu;
            bounce = parent_scene._materialContactsData[(int)Material.Wood].bounce;

            _building = true; // control must set this to false when done

            AddChange(changes.Add, null);

            // get basic mass parameters
            ODEPhysRepData repData = _parent_scene._meshWorker.NewActorPhysRep(this, _pbs, _size, _shapeType);

            primVolume = repData.volume;
            _OBB = repData.OBB;
            _OBBOffset = repData.OBBOffset;

            UpdatePrimBodyData();
        }

        private void resetCollisionAccounting()
        {
            _collisionscore = 0;
        }

        private void UpdateCollisionCatFlags()
        {
            if(_isphysical && _disabled)
            {
                _collisionCategories = 0;
                _collisionFlags = 0;
            }

            else if (_isSelected)
            {
                _collisionCategories = CollisionCategories.Selected;
                _collisionFlags = 0;
            }

            else if (_isVolumeDetect)
            {
                _collisionCategories = CollisionCategories.VolumeDtc;
                if (_isphysical)
                    _collisionFlags = CollisionCategories.Geom | CollisionCategories.Character;
                else
                    _collisionFlags = 0;
            }
            else if (_isphantom)
            {
                _collisionCategories = CollisionCategories.Phantom;
                if (_isphysical)
                    _collisionFlags = CollisionCategories.Land;
                else
                    _collisionFlags = 0;
            }
            else
            {
                _collisionCategories = CollisionCategories.Geom;
                if (_isphysical)
                    _collisionFlags = _default_collisionFlagsPhysical;
                else
                    _collisionFlags = _default_collisionFlagsNotPhysical;
            }
        }

        private void ApplyCollisionCatFlags()
        {
            if (pri_geom != IntPtr.Zero)
            {
                if (!childPrim && childrenPrim.Count > 0)
                {
                    foreach (OdePrim prm in childrenPrim)
                    {
                        if (_isphysical && _disabled)
                        {
                            prm._collisionCategories = 0;
                            prm._collisionFlags = 0;
                        }
                        else
                        {
                            // preserve some
                            if (prm._isSelected)
                            {
                                prm._collisionCategories = CollisionCategories.Selected;
                                prm._collisionFlags = 0;
                            }
                            else if (prm._isVolumeDetect)
                            {
                                prm._collisionCategories = CollisionCategories.VolumeDtc;
                                if (_isphysical)
                                    prm._collisionFlags = CollisionCategories.Geom | CollisionCategories.Character;
                                else
                                    prm._collisionFlags = 0;
                            }
                            else if (prm._isphantom)
                            {
                                prm._collisionCategories = CollisionCategories.Phantom;
                                if (_isphysical)
                                    prm._collisionFlags = CollisionCategories.Land;
                                else
                                    prm._collisionFlags = 0;
                            }
                            else
                            {
                                prm._collisionCategories = _collisionCategories;
                                prm._collisionFlags = _collisionFlags;
                            }
                        }

                        if (prm.pri_geom != IntPtr.Zero)
                        {
                            if (prm._NoColide)
                            {
                                SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, 0);
                                if (_isphysical)
                                    SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, (int)CollisionCategories.Land);
                                else
                                    SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, 0);
                            }
                            else
                            {
                                SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, (uint)prm._collisionCategories);
                                SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, (uint)prm._collisionFlags);
                            }
                        }
                    }
                }

                if (_NoColide)
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)CollisionCategories.Land);
                    if (collide_geom != pri_geom && collide_geom != IntPtr.Zero)
                    {
                        SafeNativeMethods.GeomSetCategoryBits(collide_geom, 0);
                        SafeNativeMethods.GeomSetCollideBits(collide_geom, (uint)CollisionCategories.Land);
                    }
                }
                else
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                    if (collide_geom != pri_geom && collide_geom != IntPtr.Zero)
                    {
                        SafeNativeMethods.GeomSetCategoryBits(collide_geom, (uint)_collisionCategories);
                        SafeNativeMethods.GeomSetCollideBits(collide_geom, (uint)_collisionFlags);
                    }
                }
            }
        }

        private void createAMotor(byte axislock)
        {
            if (Body == IntPtr.Zero)
                return;

            if (Amotor != IntPtr.Zero)
            {
                SafeNativeMethods.JointDestroy(Amotor);
                Amotor = IntPtr.Zero;
            }

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


        private void SetGeom(IntPtr geom)
        {
            pri_geom = geom;
            //Console.WriteLine("SetGeom to " + pri_geom + " for " + Name);
            if (pri_geom != IntPtr.Zero)
            {

                if (_NoColide)
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                    if (_isphysical)
                    {
                        SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)CollisionCategories.Land);
                    }
                    else
                    {
                        SafeNativeMethods.GeomSetCollideBits(pri_geom, 0);
                        SafeNativeMethods.GeomDisable(pri_geom);
                    }
                }
                else
                {
                    SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                    SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                }

                UpdatePrimBodyData();
                _parent_scene.actor_name_map[pri_geom] = this;

/*
// debug
                d.AABB aabb;
                d.GeomGetAABB(pri_geom, out aabb);
                float x = aabb.MaxX - aabb.MinX;
                float y = aabb.MaxY - aabb.MinY;
                float z = aabb.MaxZ - aabb.MinZ;
                if( x > 60.0f || y > 60.0f || z > 60.0f)
                    _log.WarnFormat("[PHYSICS]: large prim geo {0},size {1}, AABBsize <{2},{3},{4}, mesh {5} at {6}",
                        Name, _size.ToString(), x, y, z, _pbs.SculptEntry ? _pbs.SculptTexture.ToString() : "primMesh", _position.ToString());
                else if (x < 0.001f || y < 0.001f || z < 0.001f)
                    _log.WarnFormat("[PHYSICS]: small prim geo {0},size {1}, AABBsize <{2},{3},{4}, mesh {5} at {6}",
                        Name, _size.ToString(), x, y, z, _pbs.SculptEntry ? _pbs.SculptTexture.ToString() : "primMesh", _position.ToString());
*/

            }
            else
                _log.Warn("Setting bad Geom");
        }

        private bool GetMeshGeom()
        {
            IntPtr vertices, indices;
            int vertexCount, indexCount;
            int vertexStride, triStride;

            IMesh mesh = _mesh;

            if (mesh == null)
                return false;

            mesh.getVertexListAsPtrToFloatArray(out vertices, out vertexStride, out vertexCount);
            mesh.getIndexListAsPtrToIntArray(out indices, out triStride, out indexCount);

            if (vertexCount == 0 || indexCount == 0)
            {
                _log.WarnFormat("[PHYSICS]: Invalid mesh data on OdePrim {0}, mesh {1} at {2}",
                    Name, _pbs.SculptEntry ? _pbs.SculptTexture.ToString() : "primMesh",_position.ToString());

                _hasOBB = false;
                _OBBOffset = Vector3.Zero;
                _OBB = _size * 0.5f;

                _physCost = 0.1f;
                _streamCost = 1.0f;

                _parent_scene.mesher.ReleaseMesh(mesh);
                _meshState = MeshState.MeshFailed;
                _mesh = null;
                return false;
            }

            if (vertexCount > 64000 || indexCount > 64000)
            {
                _log.WarnFormat("[PHYSICS]: large mesh data on OdePrim {0}, mesh {1} at {2}, {3} vertices, {4} indexes",
                    Name, _pbs.SculptEntry ? _pbs.SculptTexture.ToString() : "primMesh",
                    _position.ToString() ,vertexCount , indexCount );
            }
            IntPtr geo = IntPtr.Zero;

            try
            {
                _triMeshData = SafeNativeMethods.GeomTriMeshDataCreate();

                SafeNativeMethods.GeomTriMeshDataBuildSimple(_triMeshData, vertices, vertexStride, vertexCount, indices, indexCount, triStride);
                SafeNativeMethods.GeomTriMeshDataPreprocess(_triMeshData);

                geo = SafeNativeMethods.CreateTriMesh(_targetSpace, _triMeshData, null, null, null);
            }

            catch (Exception e)
            {
                _log.ErrorFormat("[PHYSICS]: SetGeom Mesh failed for {0} exception: {1}", Name, e);
                if (_triMeshData != IntPtr.Zero)
                {
                    try
                    {
                        SafeNativeMethods.GeomTriMeshDataDestroy(_triMeshData);
                    }
                    catch
                    {
                    }
                }
                _triMeshData = IntPtr.Zero;

                _hasOBB = false;
                _OBBOffset = Vector3.Zero;
                _OBB = _size * 0.5f;
                _physCost = 0.1f;
                _streamCost = 1.0f;

                _parent_scene.mesher.ReleaseMesh(mesh);
                _meshState = MeshState.MeshFailed;
                _mesh = null;
                return false;
            }

            _physCost = 0.0013f * (float)indexCount;
            // todo
            _streamCost = 1.0f;

            SetGeom(geo);

            return true;
        }

        private void CreateGeom(bool OverrideToBox)
        {
            bool hasMesh = false;

            _NoColide = false;

            if ((_meshState & MeshState.MeshNoColide) != 0)
                _NoColide = true;

            else if(!OverrideToBox && _mesh != null)
            {
                if (GetMeshGeom())
                    hasMesh = true;
                else
                    _NoColide = true;
            }


            if (!hasMesh)
            {
                IntPtr geo = IntPtr.Zero;

                if (_pbs.ProfileShape == ProfileShape.HalfCircle && _pbs.PathCurve == (byte)Extrusion.Curve1
                    && _size.X == _size.Y && _size.Y == _size.Z)
                { // it's a sphere
                    try
                    {
                        geo = SafeNativeMethods.CreateSphere(_targetSpace, _size.X * 0.5f);
                    }
                    catch (Exception e)
                    {
                        _log.WarnFormat("[PHYSICS]: Create sphere failed: {0}", e);
                        return;
                    }
                }
                else
                {// do it as a box
                    try
                    {
                        geo = SafeNativeMethods.CreateBox(_targetSpace, _size.X, _size.Y, _size.Z);
                    }
                    catch (Exception e)
                    {
                        _log.Warn("[PHYSICS]: Create box failed: {0}", e);
                        return;
                    }
                }
                _physCost = 0.1f;
                _streamCost = 1.0f;
                SetGeom(geo);
            }
        }

        private void RemoveGeom()
        {
            if (pri_geom != IntPtr.Zero)
            {
                _parent_scene.actor_name_map.Remove(pri_geom);

                try
                {
                    SafeNativeMethods.GeomDestroy(pri_geom);
                    if (_triMeshData != IntPtr.Zero)
                    {
                        SafeNativeMethods.GeomTriMeshDataDestroy(_triMeshData);
                        _triMeshData = IntPtr.Zero;
                    }
                }
                catch (Exception e)
                {
                    _log.ErrorFormat("[PHYSICS]: PrimGeom destruction failed for {0} exception {1}", Name, e);
                }

                pri_geom = IntPtr.Zero;
                collide_geom = IntPtr.Zero;
                _targetSpace = IntPtr.Zero;
            }
            else
            {
                _log.ErrorFormat("[PHYSICS]: PrimGeom destruction BAD {0}", Name);
            }

            lock (_meshlock)
            {
                if (_mesh != null)
                {
                    _parent_scene.mesher.ReleaseMesh(_mesh);
                    _mesh = null;
                }
            }

            Body = IntPtr.Zero;
            _hasOBB = false;
        }

        //sets non physical prim _targetSpace to right space in spaces grid for static prims
        // should only be called for non physical prims unless they are becoming non physical
        private void SetInStaticSpace(OdePrim prim)
        {
            IntPtr targetSpace = _parent_scene.MoveGeomToStaticSpace(prim.pri_geom, prim._targetSpace);
            prim._targetSpace = targetSpace;
            collide_geom = IntPtr.Zero;
        }

        public void enableBodySoft()
        {
            _disabled = false;
            if (!childPrim && !_isSelected)
            {
                if (_isphysical && Body != IntPtr.Zero)
                {
                    UpdateCollisionCatFlags();
                    ApplyCollisionCatFlags();

                    _zeroFlag = true;
                    SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                    SafeNativeMethods.BodyEnable(Body);
                }
            }
            resetCollisionAccounting();
        }

        private void disableBodySoft()
        {
            _disabled = true;
            if (!childPrim)
            {
                if (_isphysical && Body != IntPtr.Zero)
                {
                    if (_isSelected)
                        _collisionFlags = CollisionCategories.Selected;
                    else
                        _collisionCategories = 0;
                    _collisionFlags = 0;
                    ApplyCollisionCatFlags();
                    SafeNativeMethods.BodyDisable(Body);
                }
            }
        }

        private void MakeBody()
        {
            if (!_isphysical) // only physical get bodies
                return;

            if (childPrim)  // child prims don't get bodies;
                return;

            if (_building)
                return;

            if (pri_geom == IntPtr.Zero)
            {
                _log.Warn("[PHYSICS]: Unable to link the linkset.  Root has no geom yet");
                return;
            }

            if (Body != IntPtr.Zero)
            {
                DestroyBody();
                _log.Warn("[PHYSICS]: MakeBody called having a body");
            }

            if (SafeNativeMethods.GeomGetBody(pri_geom) != IntPtr.Zero)
            {
                SafeNativeMethods.GeomSetBody(pri_geom, IntPtr.Zero);
                _log.Warn("[PHYSICS]: MakeBody root geom already had a body");
            }

            bool noInertiaOverride = _InertiaOverride == null;

            Body = SafeNativeMethods.BodyCreate(_parent_scene.world);

            SafeNativeMethods.Matrix3 mymat = new SafeNativeMethods.Matrix3();
            SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion();
            SafeNativeMethods.Mass objdmass = new SafeNativeMethods.Mass { };

            myrot.X = _orientation.X;
            myrot.Y = _orientation.Y;
            myrot.Z = _orientation.Z;
            myrot.W = _orientation.W;
            SafeNativeMethods.RfromQ(out mymat, ref myrot);

            // set the body rotation
            SafeNativeMethods.BodySetRotation(Body, ref mymat);

            if(noInertiaOverride)
            {
                objdmass = primdMass;
                SafeNativeMethods.MassRotate(ref objdmass, ref mymat);
            }
    
            // recompute full object inertia if needed
            if (childrenPrim.Count > 0)
            {
                SafeNativeMethods.Matrix3 mat = new SafeNativeMethods.Matrix3();
                SafeNativeMethods.Quaternion quat = new SafeNativeMethods.Quaternion();
                SafeNativeMethods.Mass tmpdmass = new SafeNativeMethods.Mass { };
                Vector3 rcm;

                rcm.X = _position.X;
                rcm.Y = _position.Y;
                rcm.Z = _position.Z;

                lock (childrenPrim)
                {
                    foreach (OdePrim prm in childrenPrim)
                    {
                        if (prm.pri_geom == IntPtr.Zero)
                        {
                            _log.Warn("[PHYSICS]: Unable to link one of the linkset elements, skipping it.  No geom yet");
                            continue;
                        }

                        quat.X = prm._orientation.X;
                        quat.Y = prm._orientation.Y;
                        quat.Z = prm._orientation.Z;
                        quat.W = prm._orientation.W;
                        SafeNativeMethods.RfromQ(out mat, ref quat);

                        // fix prim colision cats

                        if (SafeNativeMethods.GeomGetBody(prm.pri_geom) != IntPtr.Zero)
                        {
                            SafeNativeMethods.GeomSetBody(prm.pri_geom, IntPtr.Zero);
                            _log.Warn("[PHYSICS]: MakeBody child geom already had a body");
                        }

                        SafeNativeMethods.GeomClearOffset(prm.pri_geom);
                        SafeNativeMethods.GeomSetBody(prm.pri_geom, Body);
                        prm.Body = Body;
                        SafeNativeMethods.GeomSetOffsetWorldRotation(prm.pri_geom, ref mat); // set relative rotation

                        if(noInertiaOverride)
                        {
                            tmpdmass = prm.primdMass;

                            SafeNativeMethods.MassRotate(ref tmpdmass, ref mat);
                            Vector3 ppos = prm._position;
                            ppos.X -= rcm.X;
                            ppos.Y -= rcm.Y;
                            ppos.Z -= rcm.Z;
                            // refer inertia to root prim center of mass position
                            SafeNativeMethods.MassTranslate(ref tmpdmass,
                                ppos.X,
                                ppos.Y,
                                ppos.Z);

                            SafeNativeMethods.MassAdd(ref objdmass, ref tmpdmass); // add to total object inertia
                        }
                    }
                }
            }

            SafeNativeMethods.GeomClearOffset(pri_geom); // make sure we don't have a hidden offset
            // associate root geom with body
            SafeNativeMethods.GeomSetBody(pri_geom, Body);

            if(noInertiaOverride)
                SafeNativeMethods.BodySetPosition(Body, _position.X + objdmass.c.X, _position.Y + objdmass.c.Y, _position.Z + objdmass.c.Z);
            else
            {
                Vector3 ncm =  _InertiaOverride.CenterOfMass * _orientation;
                SafeNativeMethods.BodySetPosition(Body,
                    _position.X + ncm.X,
                    _position.Y + ncm.Y,
                    _position.Z + ncm.Z);
            }

            SafeNativeMethods.GeomSetOffsetWorldPosition(pri_geom, _position.X, _position.Y, _position.Z);

            if(noInertiaOverride)
            {
                SafeNativeMethods.MassTranslate(ref objdmass, -objdmass.c.X, -objdmass.c.Y, -objdmass.c.Z); // ode wants inertia at center of body
                myrot.X = -myrot.X;
                myrot.Y = -myrot.Y;
                myrot.Z = -myrot.Z;

                SafeNativeMethods.RfromQ(out mymat, ref myrot);
                SafeNativeMethods.MassRotate(ref objdmass, ref mymat);

                SafeNativeMethods.BodySetMass(Body, ref objdmass);
                _mass = objdmass.mass;
            }
            else
            {
                objdmass.c.X = 0;
                objdmass.c.Y = 0;
                objdmass.c.Z = 0;

                objdmass.I.M00 = _InertiaOverride.Inertia.X;
                objdmass.I.M11 = _InertiaOverride.Inertia.Y;
                objdmass.I.M22 = _InertiaOverride.Inertia.Z;

                objdmass.mass = _InertiaOverride.TotalMass;

                if(Math.Abs(_InertiaOverride.InertiaRotation.W) < 0.999)
                {
                    SafeNativeMethods.Matrix3 inertiarotmat = new SafeNativeMethods.Matrix3();
                    SafeNativeMethods.Quaternion inertiarot = new SafeNativeMethods.Quaternion
                    {
                        X = _InertiaOverride.InertiaRotation.X,
                        Y = _InertiaOverride.InertiaRotation.Y,
                        Z = _InertiaOverride.InertiaRotation.Z,
                        W = _InertiaOverride.InertiaRotation.W
                    };
                    SafeNativeMethods.RfromQ(out inertiarotmat, ref inertiarot);
                    SafeNativeMethods.MassRotate(ref objdmass, ref inertiarotmat);
                }
                SafeNativeMethods.BodySetMass(Body, ref objdmass);

                _mass = objdmass.mass;
            }

            // disconnect from world gravity so we can apply buoyancy
            SafeNativeMethods.BodySetGravityMode(Body, false);

            SafeNativeMethods.BodySetAutoDisableFlag(Body, true);
            SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
            SafeNativeMethods.BodySetAutoDisableAngularThreshold(Body, 0.05f);
            SafeNativeMethods.BodySetAutoDisableLinearThreshold(Body, 0.05f);
            SafeNativeMethods.BodySetDamping(Body, .004f, .001f);

            if (_targetSpace != IntPtr.Zero)
            {
                _parent_scene.waitForSpaceUnlock(_targetSpace);
                if (SafeNativeMethods.SpaceQuery(_targetSpace, pri_geom))
                    SafeNativeMethods.SpaceRemove(_targetSpace, pri_geom);
            }

            if (childrenPrim.Count == 0)
            {
                collide_geom = pri_geom;
                _targetSpace = _parent_scene.ActiveSpace;
            }
            else
            {
                _targetSpace = SafeNativeMethods.SimpleSpaceCreate(_parent_scene.ActiveSpace);
                SafeNativeMethods.SpaceSetSublevel(_targetSpace, 3);
                SafeNativeMethods.SpaceSetCleanup(_targetSpace, false);

                SafeNativeMethods.GeomSetCategoryBits(_targetSpace, (uint)(CollisionCategories.Space |
                                                            CollisionCategories.Geom |
                                                            CollisionCategories.Phantom |
                                                            CollisionCategories.VolumeDtc
                                                            ));
                SafeNativeMethods.GeomSetCollideBits(_targetSpace, 0);
                collide_geom = _targetSpace;
            }

            if (SafeNativeMethods.SpaceQuery(_targetSpace, pri_geom))
                _log.Debug("[PRIM]: parent already in target space");
            else
                SafeNativeMethods.SpaceAdd(_targetSpace, pri_geom);

            if (_delaySelect)
            {
                _isSelected = true;
                _delaySelect = false;
            }

            _collisionscore = 0;

            UpdateCollisionCatFlags();
            ApplyCollisionCatFlags();

            _parent_scene.addActivePrim(this);

            lock (childrenPrim)
            {
                foreach (OdePrim prm in childrenPrim)
                {
                    IntPtr prmgeom = prm.pri_geom;
                    if (prmgeom == IntPtr.Zero)
                        continue;

                    Vector3 ppos = prm._position;
                    SafeNativeMethods.GeomSetOffsetWorldPosition(prm.pri_geom, ppos.X, ppos.Y, ppos.Z); // set relative position

                    IntPtr prmspace = prm._targetSpace;
                    if (prmspace != _targetSpace)
                    {
                        if (prmspace != IntPtr.Zero)
                        {
                            _parent_scene.waitForSpaceUnlock(prmspace);
                            if (SafeNativeMethods.SpaceQuery(prmspace, prmgeom))
                                SafeNativeMethods.SpaceRemove(prmspace, prmgeom);
                        }
                        prm._targetSpace = _targetSpace;
                        if (SafeNativeMethods.SpaceQuery(_targetSpace, prmgeom))
                            _log.Debug("[PRIM]: child already in target space");
                        else
                            SafeNativeMethods.SpaceAdd(_targetSpace, prmgeom);
                    }

                    prm._collisionscore = 0;

                    if(!_disabled)
                        prm._disabled = false;

                    _parent_scene.addActivePrim(prm);
                }
            }

            // The body doesn't already have a finite rotation mode set here
            if (_angularlocks != 0 && _parent == null)
            {
                createAMotor(_angularlocks);
            }

            if (_isSelected || _disabled)
            {
                SafeNativeMethods.BodyDisable(Body);
                _zeroFlag = true;
            }
            else
            {
                SafeNativeMethods.BodySetAngularVel(Body, _rotationalVelocity.X, _rotationalVelocity.Y, _rotationalVelocity.Z);
                SafeNativeMethods.BodySetLinearVel(Body, _velocity.X, _velocity.Y, _velocity.Z);

                _zeroFlag = false;
                _bodydisablecontrol = 0;
            }
            _parent_scene.addActiveGroups(this);
        }

        private void DestroyBody()
        {
            if (Body != IntPtr.Zero)
            {
                _parent_scene.remActivePrim(this);

                collide_geom = IntPtr.Zero;

                if (_disabled)
                    _collisionCategories = 0;
                else if (_isSelected)
                    _collisionCategories = CollisionCategories.Selected;
                else if (_isVolumeDetect)
                    _collisionCategories = CollisionCategories.VolumeDtc;
                else if (_isphantom)
                    _collisionCategories = CollisionCategories.Phantom;
                else
                    _collisionCategories = CollisionCategories.Geom;

                _collisionFlags = 0;

                if (pri_geom != IntPtr.Zero)
                {
                    if (_NoColide)
                    {
                        SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                        SafeNativeMethods.GeomSetCollideBits(pri_geom, 0);
                    }
                    else
                    {
                        SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                        SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                    }
                    UpdateDataFromGeom();
                    SafeNativeMethods.GeomSetBody(pri_geom, IntPtr.Zero);
                    SetInStaticSpace(this);
                }

                if (!childPrim)
                {
                    lock (childrenPrim)
                    {
                        foreach (OdePrim prm in childrenPrim)
                        {
                            _parent_scene.remActivePrim(prm);

                            if (prm._isSelected)
                                prm._collisionCategories = CollisionCategories.Selected;
                            else if (prm._isVolumeDetect)
                                prm._collisionCategories = CollisionCategories.VolumeDtc;
                            else if (prm._isphantom)
                                prm._collisionCategories = CollisionCategories.Phantom;
                            else
                                prm._collisionCategories = CollisionCategories.Geom;

                            prm._collisionFlags = 0;

                            if (prm.pri_geom != IntPtr.Zero)
                            {
                                if (prm._NoColide)
                                {
                                    SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, 0);
                                    SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, 0);
                                }
                                else
                                {
                                    SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, (uint)prm._collisionCategories);
                                    SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, (uint)prm._collisionFlags);
                                }
                                prm.UpdateDataFromGeom();
                                SetInStaticSpace(prm);
                            }
                            prm.Body = IntPtr.Zero;
                            prm._mass = prm.primMass;
                            prm._collisionscore = 0;
                        }
                    }
                    if (Amotor != IntPtr.Zero)
                    {
                        SafeNativeMethods.JointDestroy(Amotor);
                        Amotor = IntPtr.Zero;
                    }
                    _parent_scene.remActiveGroup(this);
                    SafeNativeMethods.BodyDestroy(Body);
                }
                Body = IntPtr.Zero;
            }
            _mass = primMass;
            _collisionscore = 0;
        }

        private void FixInertia(Vector3 NewPos,Quaternion newrot)
        {
            SafeNativeMethods.Matrix3 mat = new SafeNativeMethods.Matrix3();
            SafeNativeMethods.Quaternion quat = new SafeNativeMethods.Quaternion();

            SafeNativeMethods.Mass tmpdmass = new SafeNativeMethods.Mass { };
            SafeNativeMethods.Mass objdmass = new SafeNativeMethods.Mass { };

            SafeNativeMethods.BodyGetMass(Body, out tmpdmass);
            objdmass = tmpdmass;

            SafeNativeMethods.Vector3 dobjpos;
            SafeNativeMethods.Vector3 thispos;

            // get current object position and rotation
            dobjpos = SafeNativeMethods.BodyGetPosition(Body);

            // get prim own inertia in its local frame
            tmpdmass = primdMass;

            // transform to object frame
            mat = SafeNativeMethods.GeomGetOffsetRotation(pri_geom);
            SafeNativeMethods.MassRotate(ref tmpdmass, ref mat);

            thispos = SafeNativeMethods.GeomGetOffsetPosition(pri_geom);
            SafeNativeMethods.MassTranslate(ref tmpdmass,
                            thispos.X,
                            thispos.Y,
                            thispos.Z);

            // subtract current prim inertia from object
            DMassSubPartFromObj(ref tmpdmass, ref objdmass);

            // back prim own inertia
            tmpdmass = primdMass;

            // update to new position and orientation
            _position = NewPos;
            SafeNativeMethods.GeomSetOffsetWorldPosition(pri_geom, NewPos.X, NewPos.Y, NewPos.Z);
            _orientation = newrot;
            quat.X = newrot.X;
            quat.Y = newrot.Y;
            quat.Z = newrot.Z;
            quat.W = newrot.W;
            SafeNativeMethods.GeomSetOffsetWorldQuaternion(pri_geom, ref quat);

            mat = SafeNativeMethods.GeomGetOffsetRotation(pri_geom);
            SafeNativeMethods.MassRotate(ref tmpdmass, ref mat);

            thispos = SafeNativeMethods.GeomGetOffsetPosition(pri_geom);
            SafeNativeMethods.MassTranslate(ref tmpdmass,
                            thispos.X,
                            thispos.Y,
                            thispos.Z);

            SafeNativeMethods.MassAdd(ref objdmass, ref tmpdmass);

            // fix all positions
            IntPtr g = SafeNativeMethods.BodyGetFirstGeom(Body);
            while (g != IntPtr.Zero)
            {
                thispos = SafeNativeMethods.GeomGetOffsetPosition(g);
                thispos.X -= objdmass.c.X;
                thispos.Y -= objdmass.c.Y;
                thispos.Z -= objdmass.c.Z;
                SafeNativeMethods.GeomSetOffsetPosition(g, thispos.X, thispos.Y, thispos.Z);
                g = SafeNativeMethods.dBodyGetNextGeom(g);
            }
            SafeNativeMethods.BodyVectorToWorld(Body,objdmass.c.X, objdmass.c.Y, objdmass.c.Z,out thispos);

            SafeNativeMethods.BodySetPosition(Body, dobjpos.X + thispos.X, dobjpos.Y + thispos.Y, dobjpos.Z + thispos.Z);
            SafeNativeMethods.MassTranslate(ref objdmass, -objdmass.c.X, -objdmass.c.Y, -objdmass.c.Z); // ode wants inertia at center of body
            SafeNativeMethods.BodySetMass(Body, ref objdmass);
            _mass = objdmass.mass;
        }

        private void FixInertia(Vector3 NewPos)
        {
            SafeNativeMethods.Matrix3 primmat = new SafeNativeMethods.Matrix3();
            SafeNativeMethods.Mass tmpdmass = new SafeNativeMethods.Mass { };
            SafeNativeMethods.Mass objdmass = new SafeNativeMethods.Mass { };
            SafeNativeMethods.Mass primmass = new SafeNativeMethods.Mass { };

            SafeNativeMethods.Vector3 dobjpos;
            SafeNativeMethods.Vector3 thispos;

            SafeNativeMethods.BodyGetMass(Body, out objdmass);

            // get prim own inertia in its local frame
            primmass = primdMass;
            // transform to object frame
            primmat = SafeNativeMethods.GeomGetOffsetRotation(pri_geom);
            SafeNativeMethods.MassRotate(ref primmass, ref primmat);

            tmpdmass = primmass;

            thispos = SafeNativeMethods.GeomGetOffsetPosition(pri_geom);
            SafeNativeMethods.MassTranslate(ref tmpdmass,
                            thispos.X,
                            thispos.Y,
                            thispos.Z);

            // subtract current prim inertia from object
            DMassSubPartFromObj(ref tmpdmass, ref objdmass);

            // update to new position
            _position = NewPos;
            SafeNativeMethods.GeomSetOffsetWorldPosition(pri_geom, NewPos.X, NewPos.Y, NewPos.Z);

            thispos = SafeNativeMethods.GeomGetOffsetPosition(pri_geom);
            SafeNativeMethods.MassTranslate(ref primmass,
                            thispos.X,
                            thispos.Y,
                            thispos.Z);

            SafeNativeMethods.MassAdd(ref objdmass, ref primmass);

            // fix all positions
            IntPtr g = SafeNativeMethods.BodyGetFirstGeom(Body);
            while (g != IntPtr.Zero)
            {
                thispos = SafeNativeMethods.GeomGetOffsetPosition(g);
                thispos.X -= objdmass.c.X;
                thispos.Y -= objdmass.c.Y;
                thispos.Z -= objdmass.c.Z;
                SafeNativeMethods.GeomSetOffsetPosition(g, thispos.X, thispos.Y, thispos.Z);
                g = SafeNativeMethods.dBodyGetNextGeom(g);
            }

            SafeNativeMethods.BodyVectorToWorld(Body, objdmass.c.X, objdmass.c.Y, objdmass.c.Z, out thispos);

            // get current object position and rotation
            dobjpos = SafeNativeMethods.BodyGetPosition(Body);

            SafeNativeMethods.BodySetPosition(Body, dobjpos.X + thispos.X, dobjpos.Y + thispos.Y, dobjpos.Z + thispos.Z);
            SafeNativeMethods.MassTranslate(ref objdmass, -objdmass.c.X, -objdmass.c.Y, -objdmass.c.Z); // ode wants inertia at center of body
            SafeNativeMethods.BodySetMass(Body, ref objdmass);
            _mass = objdmass.mass;
        }

        private void FixInertia(Quaternion newrot)
        {
            SafeNativeMethods.Matrix3 mat = new SafeNativeMethods.Matrix3();
            SafeNativeMethods.Quaternion quat = new SafeNativeMethods.Quaternion();

            SafeNativeMethods.Mass tmpdmass = new SafeNativeMethods.Mass { };
            SafeNativeMethods.Mass objdmass = new SafeNativeMethods.Mass { };
            SafeNativeMethods.Vector3 dobjpos;
            SafeNativeMethods.Vector3 thispos;

            SafeNativeMethods.BodyGetMass(Body, out objdmass);

            // get prim own inertia in its local frame
            tmpdmass = primdMass;
            mat = SafeNativeMethods.GeomGetOffsetRotation(pri_geom);
            SafeNativeMethods.MassRotate(ref tmpdmass, ref mat);
            // transform to object frame
            thispos = SafeNativeMethods.GeomGetOffsetPosition(pri_geom);
            SafeNativeMethods.MassTranslate(ref tmpdmass,
                            thispos.X,
                            thispos.Y,
                            thispos.Z);

            // subtract current prim inertia from object
            DMassSubPartFromObj(ref tmpdmass, ref objdmass);

            // update to new orientation
            _orientation = newrot;
            quat.X = newrot.X;
            quat.Y = newrot.Y;
            quat.Z = newrot.Z;
            quat.W = newrot.W;
            SafeNativeMethods.GeomSetOffsetWorldQuaternion(pri_geom, ref quat);

            tmpdmass = primdMass;
            mat = SafeNativeMethods.GeomGetOffsetRotation(pri_geom);
            SafeNativeMethods.MassRotate(ref tmpdmass, ref mat);
            SafeNativeMethods.MassTranslate(ref tmpdmass,
                            thispos.X,
                            thispos.Y,
                            thispos.Z);

            SafeNativeMethods.MassAdd(ref objdmass, ref tmpdmass);

            // fix all positions
            IntPtr g = SafeNativeMethods.BodyGetFirstGeom(Body);
            while (g != IntPtr.Zero)
            {
                thispos = SafeNativeMethods.GeomGetOffsetPosition(g);
                thispos.X -= objdmass.c.X;
                thispos.Y -= objdmass.c.Y;
                thispos.Z -= objdmass.c.Z;
                SafeNativeMethods.GeomSetOffsetPosition(g, thispos.X, thispos.Y, thispos.Z);
                g = SafeNativeMethods.dBodyGetNextGeom(g);
            }

            SafeNativeMethods.BodyVectorToWorld(Body, objdmass.c.X, objdmass.c.Y, objdmass.c.Z, out thispos);
            // get current object position and rotation
            dobjpos = SafeNativeMethods.BodyGetPosition(Body);

            SafeNativeMethods.BodySetPosition(Body, dobjpos.X + thispos.X, dobjpos.Y + thispos.Y, dobjpos.Z + thispos.Z);
            SafeNativeMethods.MassTranslate(ref objdmass, -objdmass.c.X, -objdmass.c.Y, -objdmass.c.Z); // ode wants inertia at center of body
            SafeNativeMethods.BodySetMass(Body, ref objdmass);
            _mass = objdmass.mass;
        }


        #region Mass Calculation

        private void UpdatePrimBodyData()
        {
            primMass = _density * primVolume;

            if (primMass <= 0)
                primMass = 0.0001f;//ckrinke: Mass must be greater then zero.
            if (primMass > _parent_scene.maximumMassObject)
                primMass = _parent_scene.maximumMassObject;

            _mass = primMass; // just in case

            SafeNativeMethods.MassSetBoxTotal(out primdMass, primMass, 2.0f * _OBB.X, 2.0f * _OBB.Y, 2.0f * _OBB.Z);

            SafeNativeMethods.MassTranslate(ref primdMass,
                                _OBBOffset.X,
                                _OBBOffset.Y,
                                _OBBOffset.Z);

            primOOBradiusSQ = _OBB.LengthSquared();

            if (_triMeshData != IntPtr.Zero)
            {
                float pc = _physCost;
                float psf = primOOBradiusSQ;
                psf *= 1.33f * .2f;
                pc *= psf;
                if (pc < 0.1f)
                    pc = 0.1f;

                _physCost = pc;
            }
            else
                _physCost = 0.1f;

            _streamCost = 1.0f;
        }

        #endregion


        /// <summary>
        /// Add a child prim to this parent prim.
        /// </summary>
        /// <param name="prim">Child prim</param>
        // I'm the parent
        // prim is the child
        public void ParentPrim(OdePrim prim)
        {
            //Console.WriteLine("ParentPrim  " + _primName);
            if (this._localID != prim._localID)
            {
                DestroyBody();  // for now we need to rebuil entire object on link change

                lock (childrenPrim)
                {
                    // adopt the prim
                    if (!childrenPrim.Contains(prim))
                        childrenPrim.Add(prim);

                    // see if this prim has kids and adopt them also
                    // should not happen for now
                    foreach (OdePrim prm in prim.childrenPrim)
                    {
                        if (!childrenPrim.Contains(prm))
                        {
                            if (prm.Body != IntPtr.Zero)
                            {
                                if (prm.pri_geom != IntPtr.Zero)
                                    SafeNativeMethods.GeomSetBody(prm.pri_geom, IntPtr.Zero);
                                if (prm.Body != prim.Body)
                                    prm.DestroyBody(); // don't loose bodies around
                                prm.Body = IntPtr.Zero;
                            }

                            childrenPrim.Add(prm);
                            prm._parent = this;
                        }
                    }
                }
                //Remove old children from the prim
                prim.childrenPrim.Clear();

                if (prim.Body != IntPtr.Zero)
                {
                    if (prim.pri_geom != IntPtr.Zero)
                        SafeNativeMethods.GeomSetBody(prim.pri_geom, IntPtr.Zero);
                    prim.DestroyBody(); // don't loose bodies around
                    prim.Body = IntPtr.Zero;
                }

                prim.childPrim = true;
                prim._parent = this;

                MakeBody(); // full nasty reconstruction
            }
        }

        private void UpdateChildsfromgeom()
        {
            if (childrenPrim.Count > 0)
            {
                foreach (OdePrim prm in childrenPrim)
                    prm.UpdateDataFromGeom();
            }
        }

        private void UpdateDataFromGeom()
        {
            if (pri_geom != IntPtr.Zero)
            {
                SafeNativeMethods.Quaternion qtmp;
                SafeNativeMethods.GeomCopyQuaternion(pri_geom, out qtmp);
                _orientation.X = qtmp.X;
                _orientation.Y = qtmp.Y;
                _orientation.Z = qtmp.Z;
                _orientation.W = qtmp.W;
/*
// Debug
                float qlen = _orientation.Length();
                if (qlen > 1.01f || qlen < 0.99)
                    _log.WarnFormat("[PHYSICS]: Got nonnorm quaternion from geom in Object {0} norm {1}", Name, qlen);
//
*/
                _orientation.Normalize();

                SafeNativeMethods.Vector3 lpos = SafeNativeMethods.GeomGetPosition(pri_geom);
                _position.X = lpos.X;
                _position.Y = lpos.Y;
                _position.Z = lpos.Z;
            }
        }

        private void ChildDelink(OdePrim odePrim, bool remakebodies)
        {
            // Okay, we have a delinked child.. destroy all body and remake
            if (odePrim != this && !childrenPrim.Contains(odePrim))
                return;

            DestroyBody();

            if (odePrim == this) // delinking the root prim
            {
                OdePrim newroot = null;
                lock (childrenPrim)
                {
                    if (childrenPrim.Count > 0)
                    {
                        newroot = childrenPrim[0];
                        childrenPrim.RemoveAt(0);
                        foreach (OdePrim prm in childrenPrim)
                        {
                            newroot.childrenPrim.Add(prm);
                        }
                        childrenPrim.Clear();
                    }
                    if (newroot != null)
                    {
                        newroot.childPrim = false;
                        newroot._parent = null;
                        if (remakebodies)
                            newroot.MakeBody();
                    }
                }
            }

            else
            {
                lock (childrenPrim)
                {
                    childrenPrim.Remove(odePrim);
                    odePrim.childPrim = false;
                    odePrim._parent = null;
                    //                    odePrim.UpdateDataFromGeom();
                    if (remakebodies)
                        odePrim.MakeBody();
                }
            }
            if (remakebodies)
                MakeBody();
        }

        protected void ChildRemove(OdePrim odePrim, bool reMakeBody)
        {
            // Okay, we have a delinked child.. destroy all body and remake
            if (odePrim != this && !childrenPrim.Contains(odePrim))
                return;

            DestroyBody();

            if (odePrim == this)
            {
                OdePrim newroot = null;
                lock (childrenPrim)
                {
                    if (childrenPrim.Count > 0)
                    {
                        newroot = childrenPrim[0];
                        childrenPrim.RemoveAt(0);
                        foreach (OdePrim prm in childrenPrim)
                        {
                            newroot.childrenPrim.Add(prm);
                        }
                        childrenPrim.Clear();
                    }
                    if (newroot != null)
                    {
                        newroot.childPrim = false;
                        newroot._parent = null;
                        newroot.MakeBody();
                    }
                }
                if (reMakeBody)
                    MakeBody();
                return;
            }
            else
            {
                lock (childrenPrim)
                {
                    childrenPrim.Remove(odePrim);
                    odePrim.childPrim = false;
                    odePrim._parent = null;
                    if (reMakeBody)
                        odePrim.MakeBody();
                }
            }
            MakeBody();
        }


        #region changes

        private void changeadd()
        {
            _parent_scene.addToPrims(this);
        }

        private void changeAngularLock(byte newLocks)
        {
            // do we have a Physical object?
            if (Body != IntPtr.Zero)
            {
                //Check that we have a Parent
                //If we have a parent then we're not authorative here
                if (_parent == null)
                {
                    if (newLocks != 0)
                    {
                        createAMotor(newLocks);
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
            // Store this for later in case we get turned into a separate body
            _angularlocks = newLocks;
        }

        private void changeLink(OdePrim NewParent)
        {
            if (_parent == null && NewParent != null)
            {
                NewParent.ParentPrim(this);
            }
            else if (_parent != null)
            {
                if (_parent is OdePrim)
                {
                    if (NewParent != _parent)
                    {
                        (_parent as OdePrim).ChildDelink(this, false); // for now...
                        childPrim = false;

                        if (NewParent != null)
                        {
                            NewParent.ParentPrim(this);
                        }
                    }
                }
            }
            _parent = NewParent;
        }


        private void Stop()
        {
            if (!childPrim)
            {
//                _force = Vector3.Zero;
                _forceacc = Vector3.Zero;
                _angularForceacc = Vector3.Zero;
//                _torque = Vector3.Zero;
                _velocity = Vector3.Zero;
                _acceleration = Vector3.Zero;
                _rotationalVelocity = Vector3.Zero;
                _target_velocity = Vector3.Zero;
                if (_vehicle != null && _vehicle.Type != Vehicle.TYPE_NONE)
                    _vehicle.Stop();

                _zeroFlag = false;
                base.RequestPhysicsterseUpdate();
            }

            if (Body != IntPtr.Zero)
            {
                SafeNativeMethods.BodySetForce(Body, 0f, 0f, 0f);
                SafeNativeMethods.BodySetTorque(Body, 0f, 0f, 0f);
                SafeNativeMethods.BodySetLinearVel(Body, 0f, 0f, 0f);
                SafeNativeMethods.BodySetAngularVel(Body, 0f, 0f, 0f);
            }
        }

        private void changePhantomStatus(bool newval)
        {
            _isphantom = newval;

            UpdateCollisionCatFlags();
            ApplyCollisionCatFlags();
        }

/* not in use
        internal void ChildSelectedChange(bool childSelect)
        {
            if(childPrim)
                return;

            if (childSelect == _isSelected)
                return;

            if (childSelect)
            {
                DoSelectedStatus(true);
            }

            else
            {
                foreach (OdePrim prm in childrenPrim)
                {
                    if (prm._isSelected)
                        return;
                }
                DoSelectedStatus(false);
            }
        }
*/
        private void changeSelectedStatus(bool newval)
        {
            if (_lastdoneSelected == newval)
                return;

            _lastdoneSelected = newval;
            DoSelectedStatus(newval);
        }

        private void CheckDelaySelect()
        {
            if (_delaySelect)
            {
                DoSelectedStatus(_isSelected);
            }
        }

        private void DoSelectedStatus(bool newval)
        {
            _isSelected = newval;
            Stop();

            if (newval)
            {
                if (!childPrim && Body != IntPtr.Zero)
                    SafeNativeMethods.BodyDisable(Body);

                if (_delaySelect || _isphysical)
                {
                    _collisionCategories = CollisionCategories.Selected;
                    _collisionFlags = 0;

                    if (!childPrim)
                    {
                        foreach (OdePrim prm in childrenPrim)
                        {
                            prm._collisionCategories = _collisionCategories;
                            prm._collisionFlags = _collisionFlags;

                            if (prm.pri_geom != IntPtr.Zero)
                            {

                                if (prm._NoColide)
                                {
                                    SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, 0);
                                    SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, 0);
                                }
                                else
                                {
                                    SafeNativeMethods.GeomSetCategoryBits(prm.pri_geom, (uint)_collisionCategories);
                                    SafeNativeMethods.GeomSetCollideBits(prm.pri_geom, (uint)_collisionFlags);
                                }
                            }
                            prm._delaySelect = false;
                        }
                    }
//                    else if (_parent != null)
//                        ((OdePrim)_parent).ChildSelectedChange(true);


                    if (pri_geom != IntPtr.Zero)
                    {
                        if (_NoColide)
                        {
                            SafeNativeMethods.GeomSetCategoryBits(pri_geom, 0);
                            SafeNativeMethods.GeomSetCollideBits(pri_geom, 0);
                            if (collide_geom != pri_geom && collide_geom != IntPtr.Zero)
                            {
                                SafeNativeMethods.GeomSetCategoryBits(collide_geom, 0);
                                SafeNativeMethods.GeomSetCollideBits(collide_geom, 0);
                            }

                        }
                        else
                        {
                            SafeNativeMethods.GeomSetCategoryBits(pri_geom, (uint)_collisionCategories);
                            SafeNativeMethods.GeomSetCollideBits(pri_geom, (uint)_collisionFlags);
                            if (collide_geom != pri_geom && collide_geom != IntPtr.Zero)
                            {
                                SafeNativeMethods.GeomSetCategoryBits(collide_geom, (uint)_collisionCategories);
                                SafeNativeMethods.GeomSetCollideBits(collide_geom, (uint)_collisionFlags);
                            }
                        }
                    }

                    _delaySelect = false;
                }
                else if(!_isphysical)
                {
                    _delaySelect = true;
                }
            }
            else
            {
                if (!childPrim)
                {
                    if (Body != IntPtr.Zero && !_disabled)
                    {
                        _zeroFlag = true;
                        SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                        SafeNativeMethods.BodyEnable(Body);
                    }
                }
//                else if (_parent != null)
//                    ((OdePrim)_parent).ChildSelectedChange(false);

                UpdateCollisionCatFlags();
                ApplyCollisionCatFlags();

                _delaySelect = false;
            }

            resetCollisionAccounting();
        }

        private void changePosition(Vector3 newPos)
        {
            CheckDelaySelect();
            if (_isphysical)
            {
                if (childPrim)  // inertia is messed, must rebuild
                {
                    if (_building)
                    {
                        _position = newPos;
                    }

                    else if (_forcePosOrRotation && _position != newPos && Body != IntPtr.Zero)
                    {
                        FixInertia(newPos);
                        if (!SafeNativeMethods.BodyIsEnabled(Body))
                        {
                            _zeroFlag = true;
                            SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                            SafeNativeMethods.BodyEnable(Body);
                        }
                    }
                }
                else
                {
                    if (_position != newPos)
                    {
                        SafeNativeMethods.GeomSetPosition(pri_geom, newPos.X, newPos.Y, newPos.Z);
                        _position = newPos;
                    }
                    if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
                    {
                        _zeroFlag = true;
                        SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                        SafeNativeMethods.BodyEnable(Body);
                    }
                }
            }
            else
            {
                if (pri_geom != IntPtr.Zero)
                {
                    if (newPos != _position)
                    {
                        SafeNativeMethods.GeomSetPosition(pri_geom, newPos.X, newPos.Y, newPos.Z);
                        _position = newPos;

                        _targetSpace = _parent_scene.MoveGeomToStaticSpace(pri_geom, _targetSpace);
                    }
                }
            }
            givefakepos--;
            if (givefakepos < 0)
                givefakepos = 0;
//            changeSelectedStatus();
            resetCollisionAccounting();
        }

        private void changeOrientation(Quaternion newOri)
        {
            CheckDelaySelect();
            if (_isphysical)
            {
                if (childPrim)  // inertia is messed, must rebuild
                {
                    if (_building)
                    {
                        _orientation = newOri;
                    }
/*
                    else if (_forcePosOrRotation && _orientation != newOri && Body != IntPtr.Zero)
                    {
                        FixInertia(_position, newOri);
                        if (!d.BodyIsEnabled(Body))
                            d.BodyEnable(Body);
                    }
*/
                }
                else
                {
                    if (newOri != _orientation)
                    {
                        SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                        {
                            X = newOri.X,
                            Y = newOri.Y,
                            Z = newOri.Z,
                            W = newOri.W
                        };
                        SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);
                        _orientation = newOri;
                        
                        if (Body != IntPtr.Zero)
                        {
                            if(_angularlocks != 0)
                                createAMotor(_angularlocks);
                        }
                    }
                    if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
                    {
                        _zeroFlag = true;
                        SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                        SafeNativeMethods.BodyEnable(Body);
                    }
                }
            }
            else
            {
                if (pri_geom != IntPtr.Zero)
                {
                    if (newOri != _orientation)
                    {
                        SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                        {
                            X = newOri.X,
                            Y = newOri.Y,
                            Z = newOri.Z,
                            W = newOri.W
                        };
                        SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);
                        _orientation = newOri;
                    }
                }
            }
            givefakeori--;
            if (givefakeori < 0)
                givefakeori = 0;
            resetCollisionAccounting();
        }

        private void changePositionAndOrientation(Vector3 newPos, Quaternion newOri)
        {
            CheckDelaySelect();
            if (_isphysical)
            {
                if (childPrim && _building)  // inertia is messed, must rebuild
                {
                    _position = newPos;
                    _orientation = newOri;
                }
                else
                {
                    if (newOri != _orientation)
                    {
                        SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                        {
                            X = newOri.X,
                            Y = newOri.Y,
                            Z = newOri.Z,
                            W = newOri.W
                        };
                        SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);
                        _orientation = newOri;
                        if (Body != IntPtr.Zero && _angularlocks != 0)
                            createAMotor(_angularlocks);
                    }
                    if (_position != newPos)
                    {
                        SafeNativeMethods.GeomSetPosition(pri_geom, newPos.X, newPos.Y, newPos.Z);
                        _position = newPos;
                    }
                    if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
                    {
                        _zeroFlag = true;
                        SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                        SafeNativeMethods.BodyEnable(Body);
                    }
                }
            }
            else
            {
                // string primScenAvatarIn = _parent_scene.whichspaceamIin(_position);
                // int[] arrayitem = _parent_scene.calculateSpaceArrayItemFromPos(_position);

                if (pri_geom != IntPtr.Zero)
                {
                    if (newOri != _orientation)
                    {
                        SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                        {
                            X = newOri.X,
                            Y = newOri.Y,
                            Z = newOri.Z,
                            W = newOri.W
                        };
                        SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);
                        _orientation = newOri;
                    }

                    if (newPos != _position)
                    {
                        SafeNativeMethods.GeomSetPosition(pri_geom, newPos.X, newPos.Y, newPos.Z);
                        _position = newPos;

                        _targetSpace = _parent_scene.MoveGeomToStaticSpace(pri_geom, _targetSpace);
                    }
                }
            }
            givefakepos--;
            if (givefakepos < 0)
                givefakepos = 0;
            givefakeori--;
            if (givefakeori < 0)
                givefakeori = 0;
            resetCollisionAccounting();
        }

        private void changeDisable(bool disable)
        {
            if (disable)
            {
                if (!_disabled)
                    disableBodySoft();
            }
            else
            {
                if (_disabled)
                    enableBodySoft();
            }
        }

        private void changePhysicsStatus(bool NewStatus)
        {
            CheckDelaySelect();

            _isphysical = NewStatus;

            if (!childPrim)
            {
                if (NewStatus)
                {
                    if (Body == IntPtr.Zero)
                        MakeBody();
                }
                else
                {
                    if (Body != IntPtr.Zero)
                    {
                        DestroyBody();
                    }
                    Stop();
                }
            }

            resetCollisionAccounting();
        }

        private void changeSize(Vector3 newSize)
        {
        }

        private void changeShape(PrimitiveBaseShape newShape)
        {
        }

        private void changeAddPhysRep(ODEPhysRepData repData)
        {
            _size = repData.size; //??
            _pbs = repData.pbs;

            _mesh = repData.mesh;

            _assetID = repData.assetID;
            _meshState = repData.meshState;

            _hasOBB = repData.hasOBB;
            _OBBOffset = repData.OBBOffset;
            _OBB = repData.OBB;

            primVolume = repData.volume;

            CreateGeom(repData.isTooSmall);

            if (pri_geom != IntPtr.Zero)
            {
                SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);
                SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                {
                    X = _orientation.X,
                    Y = _orientation.Y,
                    Z = _orientation.Z,
                    W = _orientation.W
                };
                SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);
            }

            if (!_isphysical)
            {
                SetInStaticSpace(this);
                UpdateCollisionCatFlags();
                ApplyCollisionCatFlags();
            }
            else
                MakeBody();

            if ((_meshState & MeshState.NeedMask) != 0)
            {
                repData.size = _size;
                repData.pbs = _pbs;
                repData.shapetype = _fakeShapetype;
                _parent_scene._meshWorker.RequestMesh(repData);
            }
            else
                _shapetype = repData.shapetype;
        }

        private void changePhysRepData(ODEPhysRepData repData)
        {
            if(_size == repData.size &&
                    _pbs == repData.pbs &&
                    _shapetype == repData.shapetype &&
                    _mesh == repData.mesh &&
                    primVolume == repData.volume)
                return;

            CheckDelaySelect();

            OdePrim parent = (OdePrim)_parent;

            bool chp = childPrim;

            if (chp)
            {
                if (parent != null)
                {
                    parent.DestroyBody();
                }
            }
            else
            {
                DestroyBody();
            }

            RemoveGeom();

            _size = repData.size;
            _pbs = repData.pbs;

            _mesh = repData.mesh;

            _assetID = repData.assetID;
            _meshState = repData.meshState;

            _hasOBB = repData.hasOBB;
            _OBBOffset = repData.OBBOffset;
            _OBB = repData.OBB;

            primVolume = repData.volume;

            CreateGeom(repData.isTooSmall);

            if (pri_geom != IntPtr.Zero)
            {
                SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);
                SafeNativeMethods.Quaternion myrot = new SafeNativeMethods.Quaternion
                {
                    X = _orientation.X,
                    Y = _orientation.Y,
                    Z = _orientation.Z,
                    W = _orientation.W
                };
                SafeNativeMethods.GeomSetQuaternion(pri_geom, ref myrot);
            }

            if (_isphysical)
            {
                if (chp)
                {
                    if (parent != null)
                    {
                        parent.MakeBody();
                    }
                }
                else
                    MakeBody();
            }
            else
            {
                SetInStaticSpace(this);
                UpdateCollisionCatFlags();
                ApplyCollisionCatFlags();
            }

            resetCollisionAccounting();

            if ((_meshState & MeshState.NeedMask) != 0)
            {
                repData.size = _size;
                repData.pbs = _pbs;
                repData.shapetype = _fakeShapetype;
                _parent_scene._meshWorker.RequestMesh(repData);
            }
            else
                _shapetype = repData.shapetype;
        }

        private void changeFloatOnWater(bool newval)
        {
            _collidesWater = newval;

            UpdateCollisionCatFlags();
            ApplyCollisionCatFlags();
        }

        private void changeSetTorque(Vector3 newtorque)
        {
            if (!_isSelected && !_outbounds)
            {
                if (_isphysical && Body != IntPtr.Zero)
                {
                    if (_disabled)
                        enableBodySoft();
                    else if (!SafeNativeMethods.BodyIsEnabled(Body))
                    {
                        SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                        SafeNativeMethods.BodyEnable(Body);
                    }
                }
                _torque = newtorque;
            }
        }

        private void changeForce(Vector3 force)
        {
            _force = force;
            if (!_isSelected && !_outbounds && Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changeAddForce(Vector3 theforce)
        {
            _forceacc += theforce;
            if (!_isSelected && !_outbounds)
            {
                lock (this)
                {
                    //_log.Info("[PHYSICS]: dequeing forcelist");
                    if (_isphysical && Body != IntPtr.Zero)
                    {
                        if (_disabled)
                            enableBodySoft();
                        else if (!SafeNativeMethods.BodyIsEnabled(Body))
                        {
                            SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                            SafeNativeMethods.BodyEnable(Body);
                        }
                    }
                }
                _collisionscore = 0;
            }
        }

        // actually angular impulse
        private void changeAddAngularImpulse(Vector3 aimpulse)
        {
            _angularForceacc += aimpulse * _invTimeStep;
            if (!_isSelected && !_outbounds)
            {
                lock (this)
                {
                    if (_isphysical && Body != IntPtr.Zero)
                    {
                        if (_disabled)
                            enableBodySoft();
                        else if (!SafeNativeMethods.BodyIsEnabled(Body))
                        {
                            SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                            SafeNativeMethods.BodyEnable(Body);
                        }
                    }
                }
                _collisionscore = 0;
            }
        }

        private void changevelocity(Vector3 newVel)
        {
            float len = newVel.LengthSquared();
            if (len > 100000.0f) // limit to 100m/s
            {
                len = 100.0f / (float)Math.Sqrt(len);
                newVel *= len;
            }

            if (!_isSelected && !_outbounds)
            {
                if (Body != IntPtr.Zero)
                {
                    if (_disabled)
                        enableBodySoft();
                    else if (!SafeNativeMethods.BodyIsEnabled(Body))
                    {
                        SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                        SafeNativeMethods.BodyEnable(Body);
                    }
                    SafeNativeMethods.BodySetLinearVel(Body, newVel.X, newVel.Y, newVel.Z);
                }
                //resetCollisionAccounting();
            }
            _velocity = newVel;
        }

        private void changeangvelocity(Vector3 newAngVel)
        {
            float len = newAngVel.LengthSquared();
            if (len > _parent_scene.maxAngVelocitySQ)
            {
                len = _parent_scene.maximumAngularVelocity / (float)Math.Sqrt(len);
                newAngVel *= len;
            }

            if (!_isSelected && !_outbounds)
            {
                if (Body != IntPtr.Zero)
                {
                    if (_disabled)
                        enableBodySoft();
                    else if (!SafeNativeMethods.BodyIsEnabled(Body))
                    {
                        SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                        SafeNativeMethods.BodyEnable(Body);
                    }
                    SafeNativeMethods.BodySetAngularVel(Body, newAngVel.X, newAngVel.Y, newAngVel.Z);
                }
                //resetCollisionAccounting();
            }
            _rotationalVelocity = newAngVel;
        }

        private void changeVolumedetetion(bool newVolDtc)
        {
            _isVolumeDetect = newVolDtc;
            _fakeisVolumeDetect = newVolDtc;
            UpdateCollisionCatFlags();
            ApplyCollisionCatFlags();
        }

        protected void changeBuilding(bool newbuilding)
        {
            // Check if we need to do anything
            if (newbuilding == _building)
                return;

            if ((bool)newbuilding)
            {
                _building = true;
                if (!childPrim)
                    DestroyBody();
            }
            else
            {
                _building = false;
                CheckDelaySelect();
                if (!childPrim)
                    MakeBody();
            }
            if (!childPrim && childrenPrim.Count > 0)
            {
                foreach (OdePrim prm in childrenPrim)
                    prm.changeBuilding(_building); // call directly
            }
        }

       public void changeSetVehicle(VehicleData vdata)
        {
            if (_vehicle == null)
                _vehicle = new ODEDynamics(this);
            _vehicle.DoSetVehicle(vdata);
        }

        private void changeVehicleType(int value)
        {
            if (value == (int)Vehicle.TYPE_NONE)
            {
                if (_vehicle != null)
                    _vehicle = null;
            }
            else
            {
                if (_vehicle == null)
                    _vehicle = new ODEDynamics(this);

                _vehicle.ProcessTypeChange((Vehicle)value);
            }
        }

        private void changeVehicleFloatParam(strVehicleFloatParam fp)
        {
            if (_vehicle == null)
                return;

            _vehicle.ProcessFloatVehicleParam((Vehicle)fp.param, fp.value);
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changeVehicleVectorParam(strVehicleVectorParam vp)
        {
            if (_vehicle == null)
                return;
            _vehicle.ProcessVectorVehicleParam((Vehicle)vp.param, vp.value);
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changeVehicleRotationParam(strVehicleQuatParam qp)
        {
            if (_vehicle == null)
                return;
            _vehicle.ProcessRotationVehicleParam((Vehicle)qp.param, qp.value);
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changeVehicleFlags(strVehicleBoolParam bp)
        {
            if (_vehicle == null)
                return;
            _vehicle.ProcessVehicleFlags(bp.param, bp.value);
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changeBuoyancy(float b)
        {
            _buoyancy = b;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDTarget(Vector3 trg)
        {
            _PIDTarget = trg;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDTau(float tau)
        {
            _PIDTau = tau;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDActive(bool val)
        {
            _usePID = val;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDHoverHeight(float val)
        {
            _PIDHoverHeight = val;
            if (val == 0)
                _useHoverPID = false;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDHoverType(PIDHoverType type)
        {
            _PIDHoverType = type;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDHoverTau(float tau)
        {
            _PIDHoverTau = tau;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changePIDHoverActive(bool active)
        {
            _useHoverPID = active;
            if (Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(Body))
            {
                SafeNativeMethods.BodySetAutoDisableSteps(Body, _body_autodisable_frames);
                SafeNativeMethods.BodyEnable(Body);
            }
        }

        private void changeInertia(PhysicsInertiaData inertia)
        {
            _InertiaOverride = inertia;

            if (Body != IntPtr.Zero)
                DestroyBody();
            MakeBody();
        }

        #endregion

        public void Move()
        {
            if (!childPrim && _isphysical && Body != IntPtr.Zero &&
                !_disabled && !_isSelected && !_building && !_outbounds)
            {
                if (!SafeNativeMethods.BodyIsEnabled(Body))
                {
                    // let vehicles sleep
                    if (_vehicle != null && _vehicle.Type != Vehicle.TYPE_NONE)
                        return;

                    if (++_bodydisablecontrol < 50)
                        return;

                    // clear residuals
                    SafeNativeMethods.BodySetAngularVel(Body,0f,0f,0f);
                    SafeNativeMethods.BodySetLinearVel(Body,0f,0f,0f);
                    _zeroFlag = true;
                    SafeNativeMethods.BodySetAutoDisableSteps(Body, 1);
                    SafeNativeMethods.BodyEnable(Body);
                    _bodydisablecontrol = -3;
                }

                if(_bodydisablecontrol < 0)
                    _bodydisablecontrol++;

                SafeNativeMethods.Vector3 lpos = SafeNativeMethods.GeomGetPosition(pri_geom); // root position that is seem by rest of simulator

                if (_vehicle != null && _vehicle.Type != Vehicle.TYPE_NONE)
                {
                    // 'VEHICLES' are dealt with in ODEDynamics.cs
                    _vehicle.Step();
                    return;
                }

                float fx = 0;
                float fy = 0;
                float fz = 0;

                float mass = _mass;

                if (_usePID && _PIDTau > 0)
                {
                    // for now position error
                    _target_velocity =
                        new Vector3(
                            _PIDTarget.X - lpos.X,
                            _PIDTarget.Y - lpos.Y,
                            _PIDTarget.Z - lpos.Z
                            );

                    if (_target_velocity.ApproxEquals(Vector3.Zero, 0.02f))
                    {
                        SafeNativeMethods.BodySetPosition(Body, _PIDTarget.X, _PIDTarget.Y, _PIDTarget.Z);
                        SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0);
                        return;
                    }
                    else
                    {
                        _zeroFlag = false;

                        float tmp = 1 / _PIDTau;
                        _target_velocity *= tmp;

                        // apply limits
                        tmp = _target_velocity.Length();
                        if (tmp > 50.0f)
                        {
                            tmp = 50 / tmp;
                            _target_velocity *= tmp;
                        }
                        else if (tmp < 0.05f)
                        {
                            tmp = 0.05f / tmp;
                            _target_velocity *= tmp;
                        }

                        SafeNativeMethods.Vector3 vel = SafeNativeMethods.BodyGetLinearVel(Body);
                        fx = (_target_velocity.X - vel.X) * _invTimeStep;
                        fy = (_target_velocity.Y - vel.Y) * _invTimeStep;
                        fz = (_target_velocity.Z - vel.Z) * _invTimeStep;
//                        d.BodySetLinearVel(Body, _target_velocity.X, _target_velocity.Y, _target_velocity.Z);
                    }
                }        // end if (_usePID)

                // Hover PID Controller needs to be mutually exlusive to MoveTo PID controller
                else if (_useHoverPID && _PIDHoverTau != 0 && _PIDHoverHeight != 0)
                {

                    //    Non-Vehicles have a limited set of Hover options.
                    // determine what our target height really is based on HoverType

                    _groundHeight = _parent_scene.GetTerrainHeightAtXY(lpos.X, lpos.Y);

                    switch (_PIDHoverType)
                    {
                        case PIDHoverType.Ground:
                            _targetHoverHeight = _groundHeight + _PIDHoverHeight;
                            break;

                        case PIDHoverType.GroundAndWater:
                            _waterHeight = _parent_scene.GetWaterLevel();
                            if (_groundHeight > _waterHeight)
                                _targetHoverHeight = _groundHeight + _PIDHoverHeight;
                            else
                                _targetHoverHeight = _waterHeight + _PIDHoverHeight;
                            break;
                    }     // end switch (_PIDHoverType)

                    // don't go underground unless volumedetector

                    if (_targetHoverHeight > _groundHeight || _isVolumeDetect)
                    {
                        SafeNativeMethods.Vector3 vel = SafeNativeMethods.BodyGetLinearVel(Body);

                        fz = _targetHoverHeight - lpos.Z;

                        //  if error is zero, use position control; otherwise, velocity control
                        if (Math.Abs(fz) < 0.01f)
                        {
                            SafeNativeMethods.BodySetPosition(Body, lpos.X, lpos.Y, _targetHoverHeight);
                            SafeNativeMethods.BodySetLinearVel(Body, vel.X, vel.Y, 0);
                        }
                        else
                        {
                            _zeroFlag = false;
                            fz /= _PIDHoverTau;

                            float tmp = Math.Abs(fz);
                            if (tmp > 50)
                                fz = 50 * Math.Sign(fz);
                            else if (tmp < 0.1)
                                fz = 0.1f * Math.Sign(fz);

                            fz = (fz - vel.Z) * _invTimeStep;
                        }
                    }
                }
                else
                {
                    float b = (1.0f - _buoyancy) * _gravmod;
                    fx = _parent_scene.gravityx * b;
                    fy = _parent_scene.gravityy * b;
                    fz = _parent_scene.gravityz * b;
                }

                fx *= mass;
                fy *= mass;
                fz *= mass;

                // constant force
                fx += _force.X;
                fy += _force.Y;
                fz += _force.Z;

                fx += _forceacc.X;
                fy += _forceacc.Y;
                fz += _forceacc.Z;

                _forceacc = Vector3.Zero;

                //_log.Info("[OBJPID]: X:" + fx.ToString() + " Y:" + fy.ToString() + " Z:" + fz.ToString());
                if (fx != 0 || fy != 0 || fz != 0)
                {
                    SafeNativeMethods.BodyAddForce(Body, fx, fy, fz);
                    //Console.WriteLine("AddForce " + fx + "," + fy + "," + fz);
                }

                Vector3 trq;

                trq = _torque;
                trq += _angularForceacc;
                _angularForceacc = Vector3.Zero;
                if (trq.X != 0 || trq.Y != 0 || trq.Z != 0)
                {
                    SafeNativeMethods.BodyAddTorque(Body, trq.X, trq.Y, trq.Z);
                }
            }
            else
            {    // is not physical, or is not a body or is selected
                //  _zeroPosition = d.BodyGetPosition(Body);
                return;
                //Console.WriteLine("Nothing " +  Name);

            }
        }

        public void UpdatePositionAndVelocity(int frame)
        {
            if (_parent == null && !_isSelected && !_disabled && !_building && !_outbounds && Body != IntPtr.Zero)
            {
                if(_bodydisablecontrol < 0)
                    return;

                bool bodyenabled = SafeNativeMethods.BodyIsEnabled(Body);
                if (bodyenabled || !_zeroFlag)
                {
                    bool lastZeroFlag = _zeroFlag;

                    SafeNativeMethods.Vector3 lpos = SafeNativeMethods.GeomGetPosition(pri_geom);

                    // check outside region
                    if (lpos.Z < -100 || lpos.Z > 100000f)
                    {
                        _outbounds = true;

                        lpos.Z = Util.Clip(lpos.Z, -100f, 100000f);
                        _acceleration.X = 0;
                        _acceleration.Y = 0;
                        _acceleration.Z = 0;

                        _velocity.X = 0;
                        _velocity.Y = 0;
                        _velocity.Z = 0;
                        _rotationalVelocity.X = 0;
                        _rotationalVelocity.Y = 0;
                        _rotationalVelocity.Z = 0;

                        SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0); // stop it
                        SafeNativeMethods.BodySetAngularVel(Body, 0, 0, 0); // stop it
                        SafeNativeMethods.BodySetPosition(Body, lpos.X, lpos.Y, lpos.Z); // put it somewhere
                        _lastposition = _position;
                        _lastorientation = _orientation;

                        base.RequestPhysicsterseUpdate();

//                        throttleCounter = 0;
                        _zeroFlag = true;

                        disableBodySoft(); // disable it and colisions
                        base.RaiseOutOfBounds(_position);
                        return;
                    }

                    if (lpos.X < 0f)
                    {
                        _position.X = Util.Clip(lpos.X, -2f, -0.1f);
                        _outbounds = true;
                    }
                    else if (lpos.X > _parent_scene.WorldExtents.X)
                    {
                        _position.X = Util.Clip(lpos.X, _parent_scene.WorldExtents.X + 0.1f, _parent_scene.WorldExtents.X + 2f);
                        _outbounds = true;
                    }
                    if (lpos.Y < 0f)
                    {
                        _position.Y = Util.Clip(lpos.Y, -2f, -0.1f);
                        _outbounds = true;
                    }
                    else if (lpos.Y > _parent_scene.WorldExtents.Y)
                    {
                        _position.Y = Util.Clip(lpos.Y, _parent_scene.WorldExtents.Y + 0.1f, _parent_scene.WorldExtents.Y + 2f);
                        _outbounds = true;
                    }

                    if (_outbounds)
                    {
                        _lastposition = _position;
                        _lastorientation = _orientation;

                        SafeNativeMethods.Vector3 dtmp = SafeNativeMethods.BodyGetAngularVel(Body);
                        _rotationalVelocity.X = dtmp.X;
                        _rotationalVelocity.Y = dtmp.Y;
                        _rotationalVelocity.Z = dtmp.Z;

                        dtmp = SafeNativeMethods.BodyGetLinearVel(Body);
                        _velocity.X = dtmp.X;
                        _velocity.Y = dtmp.Y;
                        _velocity.Z = dtmp.Z;

                        SafeNativeMethods.BodySetLinearVel(Body, 0, 0, 0); // stop it
                        SafeNativeMethods.BodySetAngularVel(Body, 0, 0, 0);
                        SafeNativeMethods.GeomSetPosition(pri_geom, _position.X, _position.Y, _position.Z);
                        disableBodySoft(); // stop collisions
                        UnSubscribeEvents();

                        base.RequestPhysicsterseUpdate();
                        return;
                    }

                    SafeNativeMethods.Quaternion ori;
                    SafeNativeMethods.GeomCopyQuaternion(pri_geom, out ori);

                    // decide if moving
                    // use positions since this are integrated quantities
                    // tolerance values depende a lot on simulation noise...
                    // use simple math.abs since we dont need to be exact
                    if(!bodyenabled)
                    {
                        _zeroFlag = true;
                    }
                    else
                    {
                        float poserror;
                        float angerror;
                        if(_zeroFlag)
                        {
                            poserror = 0.01f;
                            angerror = 0.001f;
                        }
                        else
                        {
                            poserror = 0.005f;
                            angerror = 0.0005f;
                        }

                        if (
                            Math.Abs(_position.X - lpos.X) < poserror
                            && Math.Abs(_position.Y - lpos.Y) < poserror
                            && Math.Abs(_position.Z - lpos.Z) < poserror
                            && Math.Abs(_orientation.X - ori.X) < angerror
                            && Math.Abs(_orientation.Y - ori.Y) < angerror
                            && Math.Abs(_orientation.Z - ori.Z) < angerror  // ignore W
                            )
                            _zeroFlag = true;
                        else
                            _zeroFlag = false;
                    }

                    // update position
                    if (!(_zeroFlag && lastZeroFlag))
                    {
                        _position.X = lpos.X;
                        _position.Y = lpos.Y;
                        _position.Z = lpos.Z;

                        _orientation.X = ori.X;
                        _orientation.Y = ori.Y;
                        _orientation.Z = ori.Z;
                        _orientation.W = ori.W;
                    }

                    // update velocities and acceleration
                    if (_zeroFlag || lastZeroFlag)
                    {
                         // disable interpolators
                        _velocity = Vector3.Zero;
                        _acceleration = Vector3.Zero;
                        _rotationalVelocity = Vector3.Zero;
                    }
                    else
                    {
                        SafeNativeMethods.Vector3 vel = SafeNativeMethods.BodyGetLinearVel(Body);

                        _acceleration = _velocity;

                        if (Math.Abs(vel.X) < 0.005f &&
                            Math.Abs(vel.Y) < 0.005f &&
                            Math.Abs(vel.Z) < 0.005f)
                        {
                            _velocity = Vector3.Zero;
                            float t = -_invTimeStep;
                            _acceleration = _acceleration * t;
                        }
                        else
                        {
                            _velocity.X = vel.X;
                            _velocity.Y = vel.Y;
                            _velocity.Z = vel.Z;
                            _acceleration = (_velocity - _acceleration) * _invTimeStep;
                        }

                        if (Math.Abs(_acceleration.X) < 0.01f &&
                            Math.Abs(_acceleration.Y) < 0.01f &&
                            Math.Abs(_acceleration.Z) < 0.01f)
                        {
                            _acceleration = Vector3.Zero;
                        }

                        vel = SafeNativeMethods.BodyGetAngularVel(Body);
                        if (Math.Abs(vel.X) < 0.0001 &&
                            Math.Abs(vel.Y) < 0.0001 &&
                            Math.Abs(vel.Z) < 0.0001
                            )
                        {
                            _rotationalVelocity = Vector3.Zero;
                        }
                        else
                        {
                            _rotationalVelocity.X = vel.X;
                            _rotationalVelocity.Y = vel.Y;
                            _rotationalVelocity.Z = vel.Z;
                        }
                    }

                    if (_zeroFlag)
                    {
                        if (!_lastUpdateSent)
                        {
                            base.RequestPhysicsterseUpdate();
                            if (lastZeroFlag)
                                _lastUpdateSent = true;
                        }
                        return;
                    }

                    base.RequestPhysicsterseUpdate();
                    _lastUpdateSent = false;
                }
            }
        }

        internal static bool QuaternionIsFinite(Quaternion q)
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

        internal static void DMassSubPartFromObj(ref SafeNativeMethods.Mass part, ref SafeNativeMethods.Mass theobj)
        {
            // assumes object center of mass is zero
            float smass = part.mass;
            theobj.mass -= smass;

            smass *= 1.0f / theobj.mass; ;

            theobj.c.X -= part.c.X * smass;
            theobj.c.Y -= part.c.Y * smass;
            theobj.c.Z -= part.c.Z * smass;

            theobj.I.M00 -= part.I.M00;
            theobj.I.M01 -= part.I.M01;
            theobj.I.M02 -= part.I.M02;
            theobj.I.M10 -= part.I.M10;
            theobj.I.M11 -= part.I.M11;
            theobj.I.M12 -= part.I.M12;
            theobj.I.M20 -= part.I.M20;
            theobj.I.M21 -= part.I.M21;
            theobj.I.M22 -= part.I.M22;
        }

        private void donullchange()
        {
        }

        public bool DoAChange(changes what, object arg)
        {
            if (pri_geom == IntPtr.Zero && what != changes.Add && what != changes.AddPhysRep && what != changes.Remove)
            {
                return false;
            }

            // nasty switch
            switch (what)
            {
                case changes.Add:
                    changeadd();
                    break;

                case changes.AddPhysRep:
                    changeAddPhysRep((ODEPhysRepData)arg);
                    break;

                case changes.Remove:
                    //If its being removed, we don't want to rebuild the physical rep at all, so ignore this stuff...
                    //When we return true, it destroys all of the prims in the linkset anyway
                    if (_parent != null)
                    {
                        OdePrim parent = (OdePrim)_parent;
                        parent.ChildRemove(this, false);
                    }
                    else
                        ChildRemove(this, false);

                    _vehicle = null;
                    RemoveGeom();
                    _targetSpace = IntPtr.Zero;
                    UnSubscribeEvents();
                    return true;

                case changes.Link:
                    OdePrim tmp = (OdePrim)arg;
                    changeLink(tmp);
                    break;

                case changes.DeLink:
                    changeLink(null);
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
                    changevelocity((Vector3)arg);
                    break;

                case changes.TargetVelocity:
                    break;

//                case changes.Acceleration:
//                    changeacceleration((Vector3)arg);
//                    break;

                case changes.AngVelocity:
                    changeangvelocity((Vector3)arg);
                    break;

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
                    changeAddAngularImpulse((Vector3)arg);
                    break;

                case changes.AngLock:
                    changeAngularLock((byte)arg);
                    break;

                case changes.Size:
                    changeSize((Vector3)arg);
                    break;

                case changes.Shape:
                    changeShape((PrimitiveBaseShape)arg);
                    break;

                case changes.PhysRepData:
                    changePhysRepData((ODEPhysRepData) arg);
                    break;

                case changes.CollidesWater:
                    changeFloatOnWater((bool)arg);
                    break;

                case changes.VolumeDtc:
                    changeVolumedetetion((bool)arg);
                    break;

                case changes.Phantom:
                    changePhantomStatus((bool)arg);
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

                case changes.VehicleType:
                    changeVehicleType((int)arg);
                    break;

                case changes.VehicleFlags:
                    changeVehicleFlags((strVehicleBoolParam) arg);
                    break;

                case changes.VehicleFloatParam:
                    changeVehicleFloatParam((strVehicleFloatParam) arg);
                    break;

                case changes.VehicleVectorParam:
                    changeVehicleVectorParam((strVehicleVectorParam) arg);
                    break;

                case changes.VehicleRotationParam:
                    changeVehicleRotationParam((strVehicleQuatParam) arg);
                    break;

                case changes.SetVehicle:
                    changeSetVehicle((VehicleData) arg);
                    break;

                case changes.Buoyancy:
                    changeBuoyancy((float)arg);
                    break;

                case changes.PIDTarget:
                    changePIDTarget((Vector3)arg);
                    break;

                case changes.PIDTau:
                    changePIDTau((float)arg);
                    break;

                case changes.PIDActive:
                    changePIDActive((bool)arg);
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

                case changes.SetInertia:
                    changeInertia((PhysicsInertiaData) arg);
                    break;

                case changes.Null:
                    donullchange();
                    break;

                default:
                    donullchange();
                    break;
            }
            return false;
        }

        public void AddChange(changes what, object arg)
        {
            _parent_scene.AddChange((PhysicsActor) this, what, arg);
        }

        private struct strVehicleBoolParam
        {
            public int param;
            public bool value;
        }

        private struct strVehicleFloatParam
        {
            public int param;
            public float value;
        }

        private struct strVehicleQuatParam
        {
            public int param;
            public Quaternion value;
        }

        private struct strVehicleVectorParam
        {
            public int param;
            public Vector3 value;
        }
    }
}
