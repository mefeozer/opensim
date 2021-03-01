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

/* Revised Aug, Sept 2009 by Kitto Flora. ODEDynamics.cs replaces
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

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;


namespace OpenSim.Region.PhysicsModule.ODE
{
    public class ODEDynamics
    {
        public Vehicle Type => _type;

        public IntPtr Body => _body;

        private int frcount = 0;                                        // Used to limit dynamics debug output to
                                                                        // every 100th frame

        // private OdeScene _parentScene = null;
        private IntPtr _body = IntPtr.Zero;
//        private IntPtr _jointGroup = IntPtr.Zero;
//        private IntPtr _aMotor = IntPtr.Zero;


        // Vehicle properties
        private Vehicle _type = Vehicle.TYPE_NONE;                     // If a 'VEHICLE', and what kind
        // private Quaternion _referenceFrame = Quaternion.Identity;   // Axis modifier
        private VehicleFlag _flags = (VehicleFlag) 0;                  // Boolean settings:
                                                                        // HOVER_TERRAIN_ONLY
                                                                        // HOVER_GLOBAL_HEIGHT
                                                                        // NO_DEFLECTION_UP
                                                                        // HOVER_WATER_ONLY
                                                                        // HOVER_UP_ONLY
                                                                        // LIMIT_MOTOR_UP
                                                                        // LIMIT_ROLL_ONLY
        private VehicleFlag _Hoverflags = (VehicleFlag)0;
        private Vector3 _BlockingEndPoint = Vector3.Zero;
        private Quaternion _RollreferenceFrame = Quaternion.Identity;
        // Linear properties
        private Vector3 _linearMotorDirection = Vector3.Zero;          // velocity requested by LSL, decayed by time
        private Vector3 _linearMotorDirectionLASTSET = Vector3.Zero;   // velocity requested by LSL
        private Vector3 _dir = Vector3.Zero;                           // velocity applied to body
        private Vector3 _linearFrictionTimescale = Vector3.Zero;
        private float _linearMotorDecayTimescale = 0;
        private float _linearMotorTimescale = 0;
        private Vector3 _lastLinearVelocityVector = Vector3.Zero;
        private SafeNativeMethods.Vector3 _lastPositionVector = new SafeNativeMethods.Vector3();
        // private bool _LinearMotorSetLastFrame = false;
        // private Vector3 _linearMotorOffset = Vector3.Zero;

        //Angular properties
        private Vector3 _angularMotorDirection = Vector3.Zero;         // angular velocity requested by LSL motor
        private int _angularMotorApply = 0;                            // application frame counter
        private Vector3 _angularMotorVelocity = Vector3.Zero;          // current angular motor velocity
        private float _angularMotorTimescale = 0;                      // motor angular velocity ramp up rate
        private float _angularMotorDecayTimescale = 0;                 // motor angular velocity decay rate
        private Vector3 _angularFrictionTimescale = Vector3.Zero;      // body angular velocity  decay rate
        private Vector3 _lastAngularVelocity = Vector3.Zero;           // what was last applied to body
 //       private Vector3 _lastVertAttractor = Vector3.Zero;             // what VA was last applied to body

        //Deflection properties
        // private float _angularDeflectionEfficiency = 0;
        // private float _angularDeflectionTimescale = 0;
        // private float _linearDeflectionEfficiency = 0;
        // private float _linearDeflectionTimescale = 0;

        //Banking properties
        // private float _bankingEfficiency = 0;
        // private float _bankingMix = 0;
        // private float _bankingTimescale = 0;

        //Hover and Buoyancy properties
        private float _VhoverHeight = 0f;
//        private float _VhoverEfficiency = 0f;
        private float _VhoverTimescale = 0f;
        private float _VhoverTargetHeight = -1.0f;     // if <0 then no hover, else its the current target height
        private float _VehicleBuoyancy = 0f;           //KF: _VehicleBuoyancy is set by VEHICLE_BUOYANCY for a vehicle.
                    // Modifies gravity. Slider between -1 (double-gravity) and 1 (full anti-gravity)
                    // KF: So far I have found no good method to combine a script-requested .Z velocity and gravity.
                    // Therefore only _VehicleBuoyancy=1 (0g) will use the script-requested .Z velocity.

        //Attractor properties
        private float _verticalAttractionEfficiency = 1.0f;        // damped
        private float _verticalAttractionTimescale = 500f;         // Timescale > 300  means no vert attractor.

        internal void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
        {
            switch (pParam)
            {
                case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                    if (pValue < 0.01f) pValue = 0.01f;
                    // _angularDeflectionEfficiency = pValue;
                    break;
                case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    // _angularDeflectionTimescale = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    _angularMotorDecayTimescale = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    _angularMotorTimescale = pValue;
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    if (pValue < 0.01f) pValue = 0.01f;
                    // _bankingEfficiency = pValue;
                    break;
                case Vehicle.BANKING_MIX:
                    if (pValue < 0.01f) pValue = 0.01f;
                    // _bankingMix = pValue;
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    // _bankingTimescale = pValue;
                    break;
                case Vehicle.BUOYANCY:
                    if (pValue < -1f) pValue = -1f;
                    if (pValue > 1f) pValue = 1f;
                    _VehicleBuoyancy = pValue;
                    break;
//                case Vehicle.HOVER_EFFICIENCY:
//                    if (pValue < 0f) pValue = 0f;
//                    if (pValue > 1f) pValue = 1f;
//                    _VhoverEfficiency = pValue;
//                    break;
                case Vehicle.HOVER_HEIGHT:
                    _VhoverHeight = pValue;
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    _VhoverTimescale = pValue;
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    if (pValue < 0.01f) pValue = 0.01f;
                    // _linearDeflectionEfficiency = pValue;
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    // _linearDeflectionTimescale = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    _linearMotorDecayTimescale = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    _linearMotorTimescale = pValue;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    if (pValue < 0.1f) pValue = 0.1f;    // Less goes unstable
                    if (pValue > 1.0f) pValue = 1.0f;
                    _verticalAttractionEfficiency = pValue;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    if (pValue < 0.01f) pValue = 0.01f;
                    _verticalAttractionTimescale = pValue;
                    break;

                // These are vector properties but the engine lets you use a single float value to
                // set all of the components to the same value
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    _angularFrictionTimescale = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    _angularMotorDirection = new Vector3(pValue, pValue, pValue);
                    _angularMotorApply = 10;
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    _linearFrictionTimescale = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    _linearMotorDirection = new Vector3(pValue, pValue, pValue);
                    _linearMotorDirectionLASTSET = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    // _linearMotorOffset = new Vector3(pValue, pValue, pValue);
                    break;

            }
        }//end ProcessFloatVehicleParam

        internal void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
        {
            switch (pParam)
            {
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    _angularFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    _angularMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    // Limit requested angular speed to 2 rps= 4 pi rads/sec
                    if (_angularMotorDirection.X > 12.56f) _angularMotorDirection.X = 12.56f;
                    if (_angularMotorDirection.X < - 12.56f) _angularMotorDirection.X = - 12.56f;
                    if (_angularMotorDirection.Y > 12.56f) _angularMotorDirection.Y = 12.56f;
                    if (_angularMotorDirection.Y < - 12.56f) _angularMotorDirection.Y = - 12.56f;
                    if (_angularMotorDirection.Z > 12.56f) _angularMotorDirection.Z = 12.56f;
                    if (_angularMotorDirection.Z < - 12.56f) _angularMotorDirection.Z = - 12.56f;
                    _angularMotorApply = 10;
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    _linearFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    _linearMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    _linearMotorDirectionLASTSET = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    // _linearMotorOffset = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.BLOCK_EXIT:
                    _BlockingEndPoint = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
            }
        }//end ProcessVectorVehicleParam

        internal void ProcessRotationVehicleParam(Vehicle pParam, Quaternion pValue)
        {
            switch (pParam)
            {
                case Vehicle.REFERENCE_FRAME:
                    // _referenceFrame = pValue;
                    break;
                case Vehicle.ROLL_FRAME:
                    _RollreferenceFrame = pValue;
                    break;
            }
        }//end ProcessRotationVehicleParam

        internal void ProcessVehicleFlags(int pParam, bool remove)
        {
            if (remove)
            {
                if (pParam == -1)
                {
                    _flags = (VehicleFlag)0;
                    _Hoverflags = (VehicleFlag)0;
                    return;
                }
                if ((pParam & (int)VehicleFlag.HOVER_GLOBAL_HEIGHT) == (int)VehicleFlag.HOVER_GLOBAL_HEIGHT)
                {
                    if ((_Hoverflags & VehicleFlag.HOVER_GLOBAL_HEIGHT) != (VehicleFlag)0)
                        _Hoverflags &= ~VehicleFlag.HOVER_GLOBAL_HEIGHT;
                }
                if ((pParam & (int)VehicleFlag.HOVER_TERRAIN_ONLY) == (int)VehicleFlag.HOVER_TERRAIN_ONLY)
                {
                    if ((_Hoverflags & VehicleFlag.HOVER_TERRAIN_ONLY) != (VehicleFlag)0)
                        _Hoverflags &= ~VehicleFlag.HOVER_TERRAIN_ONLY;
                }
                if ((pParam & (int)VehicleFlag.HOVER_UP_ONLY) == (int)VehicleFlag.HOVER_UP_ONLY)
                {
                    if ((_Hoverflags & VehicleFlag.HOVER_UP_ONLY) != (VehicleFlag)0)
                        _Hoverflags &= ~VehicleFlag.HOVER_UP_ONLY;
                }
                if ((pParam & (int)VehicleFlag.HOVER_WATER_ONLY) == (int)VehicleFlag.HOVER_WATER_ONLY)
                {
                    if ((_Hoverflags & VehicleFlag.HOVER_WATER_ONLY) != (VehicleFlag)0)
                        _Hoverflags &= ~VehicleFlag.HOVER_WATER_ONLY;
                }
                if ((pParam & (int)VehicleFlag.LIMIT_MOTOR_UP) == (int)VehicleFlag.LIMIT_MOTOR_UP)
                {
                    if ((_flags & VehicleFlag.LIMIT_MOTOR_UP) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.LIMIT_MOTOR_UP;
                }
                if ((pParam & (int)VehicleFlag.LIMIT_ROLL_ONLY) == (int)VehicleFlag.LIMIT_ROLL_ONLY)
                {
                    if ((_flags & VehicleFlag.LIMIT_ROLL_ONLY) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.LIMIT_ROLL_ONLY;
                }
                if ((pParam & (int)VehicleFlag.MOUSELOOK_BANK) == (int)VehicleFlag.MOUSELOOK_BANK)
                {
                    if ((_flags & VehicleFlag.MOUSELOOK_BANK) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.MOUSELOOK_BANK;
                }
                if ((pParam & (int)VehicleFlag.MOUSELOOK_STEER) == (int)VehicleFlag.MOUSELOOK_STEER)
                {
                    if ((_flags & VehicleFlag.MOUSELOOK_STEER) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.MOUSELOOK_STEER;
                }
                if ((pParam & (int)VehicleFlag.NO_DEFLECTION_UP) == (int)VehicleFlag.NO_DEFLECTION_UP)
                {
                    if ((_flags & VehicleFlag.NO_DEFLECTION_UP) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.NO_DEFLECTION_UP;
                }
                if ((pParam & (int)VehicleFlag.CAMERA_DECOUPLED) == (int)VehicleFlag.CAMERA_DECOUPLED)
                {
                    if ((_flags & VehicleFlag.CAMERA_DECOUPLED) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.CAMERA_DECOUPLED;
                }
                if ((pParam & (int)VehicleFlag.NO_X) == (int)VehicleFlag.NO_X)
                {
                    if ((_flags & VehicleFlag.NO_X) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.NO_X;
                }
                if ((pParam & (int)VehicleFlag.NO_Y) == (int)VehicleFlag.NO_Y)
                {
                    if ((_flags & VehicleFlag.NO_Y) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.NO_Y;
                }
                if ((pParam & (int)VehicleFlag.NO_Z) == (int)VehicleFlag.NO_Z)
                {
                    if ((_flags & VehicleFlag.NO_Z) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.NO_Z;
                }
                if ((pParam & (int)VehicleFlag.LOCK_HOVER_HEIGHT) == (int)VehicleFlag.LOCK_HOVER_HEIGHT)
                {
                    if ((_Hoverflags & VehicleFlag.LOCK_HOVER_HEIGHT) != (VehicleFlag)0)
                        _Hoverflags &= ~VehicleFlag.LOCK_HOVER_HEIGHT;
                }
                if ((pParam & (int)VehicleFlag.NO_DEFLECTION) == (int)VehicleFlag.NO_DEFLECTION)
                {
                    if ((_flags & VehicleFlag.NO_DEFLECTION) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.NO_DEFLECTION;
                }
                if ((pParam & (int)VehicleFlag.LOCK_ROTATION) == (int)VehicleFlag.LOCK_ROTATION)
                {
                    if ((_flags & VehicleFlag.LOCK_ROTATION) != (VehicleFlag)0)
                        _flags &= ~VehicleFlag.LOCK_ROTATION;
                }
            }
            else
            {
                if ((pParam & (int)VehicleFlag.HOVER_GLOBAL_HEIGHT) == (int)VehicleFlag.HOVER_GLOBAL_HEIGHT)
                {
                    _Hoverflags |= VehicleFlag.HOVER_GLOBAL_HEIGHT | _flags;
                }
                if ((pParam & (int)VehicleFlag.HOVER_TERRAIN_ONLY) == (int)VehicleFlag.HOVER_TERRAIN_ONLY)
                {
                    _Hoverflags |= VehicleFlag.HOVER_TERRAIN_ONLY | _flags;
                }
                if ((pParam & (int)VehicleFlag.HOVER_UP_ONLY) == (int)VehicleFlag.HOVER_UP_ONLY)
                {
                    _Hoverflags |= VehicleFlag.HOVER_UP_ONLY | _flags;
                }
                if ((pParam & (int)VehicleFlag.HOVER_WATER_ONLY) == (int)VehicleFlag.HOVER_WATER_ONLY)
                {
                    _Hoverflags |= VehicleFlag.HOVER_WATER_ONLY | _flags;
                }
                if ((pParam & (int)VehicleFlag.LIMIT_MOTOR_UP) == (int)VehicleFlag.LIMIT_MOTOR_UP)
                {
                    _flags |= VehicleFlag.LIMIT_MOTOR_UP | _flags;
                }
                if ((pParam & (int)VehicleFlag.MOUSELOOK_BANK) == (int)VehicleFlag.MOUSELOOK_BANK)
                {
                    _flags |= VehicleFlag.MOUSELOOK_BANK | _flags;
                }
                if ((pParam & (int)VehicleFlag.MOUSELOOK_STEER) == (int)VehicleFlag.MOUSELOOK_STEER)
                {
                    _flags |= VehicleFlag.MOUSELOOK_STEER | _flags;
                }
                if ((pParam & (int)VehicleFlag.NO_DEFLECTION_UP) == (int)VehicleFlag.NO_DEFLECTION_UP)
                {
                    _flags |= VehicleFlag.NO_DEFLECTION_UP | _flags;
                }
                if ((pParam & (int)VehicleFlag.CAMERA_DECOUPLED) == (int)VehicleFlag.CAMERA_DECOUPLED)
                {
                    _flags |= VehicleFlag.CAMERA_DECOUPLED | _flags;
                }
                if ((pParam & (int)VehicleFlag.NO_X) == (int)VehicleFlag.NO_X)
                {
                    _flags |= VehicleFlag.NO_X;
                }
                if ((pParam & (int)VehicleFlag.NO_Y) == (int)VehicleFlag.NO_Y)
                {
                    _flags |= VehicleFlag.NO_Y;
                }
                if ((pParam & (int)VehicleFlag.NO_Z) == (int)VehicleFlag.NO_Z)
                {
                    _flags |= VehicleFlag.NO_Z;
                }
                if ((pParam & (int)VehicleFlag.LOCK_HOVER_HEIGHT) == (int)VehicleFlag.LOCK_HOVER_HEIGHT)
                {
                    _Hoverflags |= VehicleFlag.LOCK_HOVER_HEIGHT;
                }
                if ((pParam & (int)VehicleFlag.NO_DEFLECTION) == (int)VehicleFlag.NO_DEFLECTION)
                {
                    _flags |= VehicleFlag.NO_DEFLECTION;
                }
                if ((pParam & (int)VehicleFlag.LOCK_ROTATION) == (int)VehicleFlag.LOCK_ROTATION)
                {
                    _flags |= VehicleFlag.LOCK_ROTATION;
                }
            }
        }//end ProcessVehicleFlags

        internal void ProcessTypeChange(Vehicle pType)
        {
            // Set Defaults For Type
            _type = pType;
            switch (pType)
            {
                    case Vehicle.TYPE_NONE:
                    _linearFrictionTimescale = new Vector3(0, 0, 0);
                    _angularFrictionTimescale = new Vector3(0, 0, 0);
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 0;
                    _linearMotorDecayTimescale = 0;
                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 0;
                    _angularMotorDecayTimescale = 0;
                    _VhoverHeight = 0;
                    _VhoverTimescale = 0;
                    _VehicleBuoyancy = 0;
                    _flags = (VehicleFlag)0;
                    break;

                case Vehicle.TYPE_SLED:
                    _linearFrictionTimescale = new Vector3(30, 1, 1000);
                    _angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 1000;
                    _linearMotorDecayTimescale = 120;
                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 1000;
                    _angularMotorDecayTimescale = 120;
                    _VhoverHeight = 0;
//                    _VhoverEfficiency = 1;
                    _VhoverTimescale = 10;
                    _VehicleBuoyancy = 0;
                    // _linearDeflectionEfficiency = 1;
                    // _linearDeflectionTimescale = 1;
                    // _angularDeflectionEfficiency = 1;
                    // _angularDeflectionTimescale = 1000;
                    // _bankingEfficiency = 0;
                    // _bankingMix = 1;
                    // _bankingTimescale = 10;
                    // _referenceFrame = Quaternion.Identity;
                    _Hoverflags &=
                         ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY |
                           VehicleFlag.HOVER_GLOBAL_HEIGHT | VehicleFlag.HOVER_UP_ONLY);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_ROLL_ONLY | VehicleFlag.LIMIT_MOTOR_UP;
                    break;
                case Vehicle.TYPE_CAR:
                    _linearFrictionTimescale = new Vector3(100, 2, 1000);
                    _angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 1;
                    _linearMotorDecayTimescale = 60;
                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 1;
                    _angularMotorDecayTimescale = 0.8f;
                    _VhoverHeight = 0;
//                    _VhoverEfficiency = 0;
                    _VhoverTimescale = 1000;
                    _VehicleBuoyancy = 0;
                    // // _linearDeflectionEfficiency = 1;
                    // // _linearDeflectionTimescale = 2;
                    // // _angularDeflectionEfficiency = 0;
                    // _angularDeflectionTimescale = 10;
                    _verticalAttractionEfficiency = 1f;
                    _verticalAttractionTimescale = 10f;
                    // _bankingEfficiency = -0.2f;
                    // _bankingMix = 1;
                    // _bankingTimescale = 1;
                    // _referenceFrame = Quaternion.Identity;
                    _Hoverflags &= ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY | VehicleFlag.HOVER_GLOBAL_HEIGHT);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_ROLL_ONLY |
                               VehicleFlag.LIMIT_MOTOR_UP;
                    _Hoverflags |= VehicleFlag.HOVER_UP_ONLY;
                    break;
                case Vehicle.TYPE_BOAT:
                    _linearFrictionTimescale = new Vector3(10, 3, 2);
                    _angularFrictionTimescale = new Vector3(10,10,10);
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 5;
                    _linearMotorDecayTimescale = 60;
                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 4;
                    _angularMotorDecayTimescale = 4;
                    _VhoverHeight = 0;
//                    _VhoverEfficiency = 0.5f;
                    _VhoverTimescale = 2;
                    _VehicleBuoyancy = 1;
                    // _linearDeflectionEfficiency = 0.5f;
                    // _linearDeflectionTimescale = 3;
                    // _angularDeflectionEfficiency = 0.5f;
                    // _angularDeflectionTimescale = 5;
                    _verticalAttractionEfficiency = 0.5f;
                    _verticalAttractionTimescale = 5f;
                    // _bankingEfficiency = -0.3f;
                    // _bankingMix = 0.8f;
                    // _bankingTimescale = 1;
                    // _referenceFrame = Quaternion.Identity;
                    _Hoverflags &= ~(VehicleFlag.HOVER_TERRAIN_ONLY |
                            VehicleFlag.HOVER_GLOBAL_HEIGHT | VehicleFlag.HOVER_UP_ONLY);
                    _flags &= ~VehicleFlag.LIMIT_ROLL_ONLY;
                    _flags |= VehicleFlag.NO_DEFLECTION_UP |
                               VehicleFlag.LIMIT_MOTOR_UP;
                    _Hoverflags |= VehicleFlag.HOVER_WATER_ONLY;
                    break;
                case Vehicle.TYPE_AIRPLANE:
                    _linearFrictionTimescale = new Vector3(200, 10, 5);
                    _angularFrictionTimescale = new Vector3(20, 20, 20);
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 2;
                    _linearMotorDecayTimescale = 60;
                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 4;
                    _angularMotorDecayTimescale = 4;
                    _VhoverHeight = 0;
//                    _VhoverEfficiency = 0.5f;
                    _VhoverTimescale = 1000;
                    _VehicleBuoyancy = 0;
                    // _linearDeflectionEfficiency = 0.5f;
                    // _linearDeflectionTimescale = 3;
                    // _angularDeflectionEfficiency = 1;
                    // _angularDeflectionTimescale = 2;
                    _verticalAttractionEfficiency = 0.9f;
                    _verticalAttractionTimescale = 2f;
                    // _bankingEfficiency = 1;
                    // _bankingMix = 0.7f;
                    // _bankingTimescale = 2;
                    // _referenceFrame = Quaternion.Identity;
                    _Hoverflags &= ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY |
                        VehicleFlag.HOVER_GLOBAL_HEIGHT | VehicleFlag.HOVER_UP_ONLY);
                    _flags &= ~(VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_MOTOR_UP);
                    _flags |= VehicleFlag.LIMIT_ROLL_ONLY;
                    break;
                case Vehicle.TYPE_BALLOON:
                    _linearFrictionTimescale = new Vector3(5, 5, 5);
                    _angularFrictionTimescale = new Vector3(10, 10, 10);
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 5;
                    _linearMotorDecayTimescale = 60;
                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 6;
                    _angularMotorDecayTimescale = 10;
                    _VhoverHeight = 5;
//                    _VhoverEfficiency = 0.8f;
                    _VhoverTimescale = 10;
                    _VehicleBuoyancy = 1;
                    // _linearDeflectionEfficiency = 0;
                    // _linearDeflectionTimescale = 5;
                    // _angularDeflectionEfficiency = 0;
                    // _angularDeflectionTimescale = 5;
                    _verticalAttractionEfficiency = 1f;
                    _verticalAttractionTimescale = 100f;
                    // _bankingEfficiency = 0;
                    // _bankingMix = 0.7f;
                    // _bankingTimescale = 5;
                    // _referenceFrame = Quaternion.Identity;
                    _Hoverflags &= ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY |
                        VehicleFlag.HOVER_UP_ONLY);
                    _flags &= ~(VehicleFlag.NO_DEFLECTION_UP | VehicleFlag.LIMIT_MOTOR_UP);
                    _flags |= VehicleFlag.LIMIT_ROLL_ONLY;
                    _Hoverflags |= VehicleFlag.HOVER_GLOBAL_HEIGHT;
                    break;

            }
        }//end SetDefaultsForType

        internal void Enable(IntPtr pBody, OdeScene pParentScene)
        {
            if (_type == Vehicle.TYPE_NONE)
                return;

            _body = pBody;
        }

        internal void Stop()
        {
            _lastLinearVelocityVector = Vector3.Zero;
            _lastAngularVelocity = Vector3.Zero;
            _lastPositionVector = SafeNativeMethods.BodyGetPosition(Body);
        }

        internal void Step(float pTimestep,  OdeScene pParentScene)
        {
            if (_body == IntPtr.Zero || _type == Vehicle.TYPE_NONE)
                return;
            frcount++;  // used to limit debug comment output
            if (frcount > 100)
                frcount = 0;

            MoveLinear(pTimestep, pParentScene);
            MoveAngular(pTimestep);
            LimitRotation(pTimestep);
        }// end Step

        private void MoveLinear(float pTimestep, OdeScene _pParentScene)
        {
            if (!_linearMotorDirection.ApproxEquals(Vector3.Zero, 0.01f))  // requested _linearMotorDirection is significant
            {
                 if (!SafeNativeMethods.BodyIsEnabled(Body))
                     SafeNativeMethods.BodyEnable(Body);

                // add drive to body
                Vector3 addAmount = _linearMotorDirection/(_linearMotorTimescale/pTimestep);
                _lastLinearVelocityVector += addAmount*10;  // lastLinearVelocityVector is the current body velocity vector?

                // This will work temporarily, but we really need to compare speed on an axis
                // KF: Limit body velocity to applied velocity?
                if (Math.Abs(_lastLinearVelocityVector.X) > Math.Abs(_linearMotorDirectionLASTSET.X))
                    _lastLinearVelocityVector.X = _linearMotorDirectionLASTSET.X;
                if (Math.Abs(_lastLinearVelocityVector.Y) > Math.Abs(_linearMotorDirectionLASTSET.Y))
                    _lastLinearVelocityVector.Y = _linearMotorDirectionLASTSET.Y;
                if (Math.Abs(_lastLinearVelocityVector.Z) > Math.Abs(_linearMotorDirectionLASTSET.Z))
                    _lastLinearVelocityVector.Z = _linearMotorDirectionLASTSET.Z;

                // decay applied velocity
                Vector3 decayfraction = Vector3.One/(_linearMotorDecayTimescale/pTimestep);
                //Console.WriteLine("decay: " + decayfraction);
                _linearMotorDirection -= _linearMotorDirection * decayfraction * 0.5f;
                //Console.WriteLine("actual: " + _linearMotorDirection);
            }
            else
            {        // requested is not significant
                    // if what remains of applied is small, zero it.
                if (_lastLinearVelocityVector.ApproxEquals(Vector3.Zero, 0.01f))
                    _lastLinearVelocityVector = Vector3.Zero;
            }

            // convert requested object velocity to world-referenced vector
            _dir = _lastLinearVelocityVector;
            SafeNativeMethods.Quaternion rot = SafeNativeMethods.BodyGetQuaternion(Body);
            Quaternion rotq = new Quaternion(rot.X, rot.Y, rot.Z, rot.W);    // rotq = rotation of object
            _dir *= rotq;                            // apply obj rotation to velocity vector

            // add Gravity andBuoyancy
            // KF: So far I have found no good method to combine a script-requested
            // .Z velocity and gravity. Therefore only 0g will used script-requested
            // .Z velocity. >0g (_VehicleBuoyancy < 1) will used modified gravity only.
            Vector3 grav = Vector3.Zero;
            // There is some gravity, make a gravity force vector
            // that is applied after object velocity.
            SafeNativeMethods.Mass objMass;
            SafeNativeMethods.BodyGetMass(Body, out objMass);
            // _VehicleBuoyancy: -1=2g; 0=1g; 1=0g;
            grav.Z = _pParentScene.gravityz * objMass.mass * (1f - _VehicleBuoyancy);
            // Preserve the current Z velocity
            SafeNativeMethods.Vector3 vel_now = SafeNativeMethods.BodyGetLinearVel(Body);
            _dir.Z = vel_now.Z;        // Preserve the accumulated falling velocity

            SafeNativeMethods.Vector3 pos = SafeNativeMethods.BodyGetPosition(Body);
            //            Vector3 accel = new Vector3(-(_dir.X - _lastLinearVelocityVector.X / 0.1f), -(_dir.Y - _lastLinearVelocityVector.Y / 0.1f), _dir.Z - _lastLinearVelocityVector.Z / 0.1f);
            Vector3 posChange = new Vector3
            {
                X = pos.X - _lastPositionVector.X,
                Y = pos.Y - _lastPositionVector.Y,
                Z = pos.Z - _lastPositionVector.Z
            };
            double Zchange = Math.Abs(posChange.Z);
            if (_BlockingEndPoint != Vector3.Zero)
            {
                if (pos.X >= _BlockingEndPoint.X - (float)1)
                {
                    pos.X -= posChange.X + 1;
                    SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, pos.Z);
                }
                if (pos.Y >= _BlockingEndPoint.Y - (float)1)
                {
                    pos.Y -= posChange.Y + 1;
                    SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, pos.Z);
                }
                if (pos.Z >= _BlockingEndPoint.Z - (float)1)
                {
                    pos.Z -= posChange.Z + 1;
                    SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, pos.Z);
                }
                if (pos.X <= 0)
                {
                    pos.X += posChange.X + 1;
                    SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, pos.Z);
                }
                if (pos.Y <= 0)
                {
                    pos.Y += posChange.Y + 1;
                    SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, pos.Z);
                }
            }
            if (pos.Z < _pParentScene.GetTerrainHeightAtXY(pos.X, pos.Y))
            {
                pos.Z = _pParentScene.GetTerrainHeightAtXY(pos.X, pos.Y) + 2;
                SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, pos.Z);
            }

            // Check if hovering
            if ((_Hoverflags & (VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY | VehicleFlag.HOVER_GLOBAL_HEIGHT)) != 0)
            {
                // We should hover, get the target height
                if ((_Hoverflags & VehicleFlag.HOVER_WATER_ONLY) != 0)
                {
                    _VhoverTargetHeight = _pParentScene.GetWaterLevel() + _VhoverHeight;
                }
                if ((_Hoverflags & VehicleFlag.HOVER_TERRAIN_ONLY) != 0)
                {
                    _VhoverTargetHeight = _pParentScene.GetTerrainHeightAtXY(pos.X, pos.Y) + _VhoverHeight;
                }
                if ((_Hoverflags & VehicleFlag.HOVER_GLOBAL_HEIGHT) != 0)
                {
                    _VhoverTargetHeight = _VhoverHeight;
                }

                if ((_Hoverflags & VehicleFlag.HOVER_UP_ONLY) != 0)
                {
                    // If body is aready heigher, use its height as target height
                    if (pos.Z > _VhoverTargetHeight) _VhoverTargetHeight = pos.Z;
                }
                if ((_Hoverflags & VehicleFlag.LOCK_HOVER_HEIGHT) != 0)
                {
                    if (pos.Z - _VhoverTargetHeight > .2 || pos.Z - _VhoverTargetHeight < -.2)
                    {
                        SafeNativeMethods.BodySetPosition(Body, pos.X, pos.Y, _VhoverTargetHeight);
                    }
                }
                else
                {
                    float herr0 = pos.Z - _VhoverTargetHeight;
                    // Replace Vertical speed with correction figure if significant
                    if (Math.Abs(herr0) > 0.01f)
                    {
                        _dir.Z = -(herr0 * pTimestep * 50.0f / _VhoverTimescale);
                        //KF: _VhoverEfficiency is not yet implemented
                    }
                    else
                    {
                        _dir.Z = 0f;
                    }
                }

//                _VhoverEfficiency = 0f;    // 0=boucy, 1=Crit.damped
//                _VhoverTimescale = 0f;        // time to acheive height
//                pTimestep  is time since last frame,in secs
            }

            if ((_flags & VehicleFlag.LIMIT_MOTOR_UP) != 0)
            {
                //Start Experimental Values
                if (Zchange > .3)
                {
                    grav.Z = (float)(grav.Z * 3);
                }
                if (Zchange > .15)
                {
                    grav.Z = (float)(grav.Z * 2);
                }
                if (Zchange > .75)
                {
                    grav.Z = (float)(grav.Z * 1.5);
                }
                if (Zchange > .05)
                {
                    grav.Z = (float)(grav.Z * 1.25);
                }
                if (Zchange > .025)
                {
                    grav.Z = (float)(grav.Z * 1.125);
                }
                float terraintemp = _pParentScene.GetTerrainHeightAtXY(pos.X, pos.Y);
                float postemp = pos.Z - terraintemp;
                if (postemp > 2.5f)
                {
                    grav.Z = (float)(grav.Z * 1.037125);
                }
                //End Experimental Values
            }
            if ((_flags & VehicleFlag.NO_X) != 0)
            {
                _dir.X = 0;
            }
            if ((_flags & VehicleFlag.NO_Y) != 0)
            {
                _dir.Y = 0;
            }
            if ((_flags & VehicleFlag.NO_Z) != 0)
            {
                _dir.Z = 0;
            }

            _lastPositionVector = SafeNativeMethods.BodyGetPosition(Body);

            // Apply velocity
            SafeNativeMethods.BodySetLinearVel(Body, _dir.X, _dir.Y, _dir.Z);
            // apply gravity force
            SafeNativeMethods.BodyAddForce(Body, grav.X, grav.Y, grav.Z);


            // apply friction
            Vector3 decayamount = Vector3.One / (_linearFrictionTimescale / pTimestep);
            _lastLinearVelocityVector -= _lastLinearVelocityVector * decayamount;
        } // end MoveLinear()

        private void MoveAngular(float pTimestep)
        {
            /*
            private Vector3 _angularMotorDirection = Vector3.Zero;            // angular velocity requested by LSL motor
            private int _angularMotorApply = 0;                            // application frame counter
             private float _angularMotorVelocity = 0;                        // current angular motor velocity (ramps up and down)
            private float _angularMotorTimescale = 0;                        // motor angular velocity ramp up rate
            private float _angularMotorDecayTimescale = 0;                    // motor angular velocity decay rate
            private Vector3 _angularFrictionTimescale = Vector3.Zero;        // body angular velocity  decay rate
            private Vector3 _lastAngularVelocity = Vector3.Zero;            // what was last applied to body
            */

            // Get what the body is doing, this includes 'external' influences
            SafeNativeMethods.Vector3 angularVelocity = SafeNativeMethods.BodyGetAngularVel(Body);
   //         Vector3 angularVelocity = Vector3.Zero;

            if (_angularMotorApply > 0)
            {
                // ramp up to new value
                //   current velocity  +=                         error                       /    (time to get there / step interval)
                //                               requested speed            -  last motor speed
                _angularMotorVelocity.X += (_angularMotorDirection.X - _angularMotorVelocity.X) /  (_angularMotorTimescale / pTimestep);
                _angularMotorVelocity.Y += (_angularMotorDirection.Y - _angularMotorVelocity.Y) /  (_angularMotorTimescale / pTimestep);
                _angularMotorVelocity.Z += (_angularMotorDirection.Z - _angularMotorVelocity.Z) /  (_angularMotorTimescale / pTimestep);

                _angularMotorApply--;        // This is done so that if script request rate is less than phys frame rate the expected
                                            // velocity may still be acheived.
            }
            else
            {
                // no motor recently applied, keep the body velocity
        /*        _angularMotorVelocity.X = angularVelocity.X;
                _angularMotorVelocity.Y = angularVelocity.Y;
                _angularMotorVelocity.Z = angularVelocity.Z; */

                // and decay the velocity
                _angularMotorVelocity -= _angularMotorVelocity /  (_angularMotorDecayTimescale / pTimestep);
            } // end motor section

            // Vertical attractor section
            Vector3 vertattr = Vector3.Zero;

            if (_verticalAttractionTimescale < 300)
            {
                float VAservo = 0.2f / (_verticalAttractionTimescale * pTimestep);
                // get present body rotation
                SafeNativeMethods.Quaternion rot = SafeNativeMethods.BodyGetQuaternion(Body);
                Quaternion rotq = new Quaternion(rot.X, rot.Y, rot.Z, rot.W);
                // make a vector pointing up
                Vector3 verterr = Vector3.Zero;
                verterr.Z = 1.0f;
                // rotate it to Body Angle
                verterr = verterr * rotq;
                // verterr.X and .Y are the World error ammounts. They are 0 when there is no error (Vehicle Body is 'vertical'), and .Z will be 1.
                // As the body leans to its side |.X| will increase to 1 and .Z fall to 0. As body inverts |.X| will fall and .Z will go
                // negative. Similar for tilt and |.Y|. .X and .Y must be modulated to prevent a stable inverted body.
                if (verterr.Z < 0.0f)
                {
                    verterr.X = 2.0f - verterr.X;
                    verterr.Y = 2.0f - verterr.Y;
                }
                // Error is 0 (no error) to +/- 2 (max error)
                // scale it by VAservo
                verterr = verterr * VAservo;
//if (frcount == 0) Console.WriteLine("VAerr=" + verterr);

                // As the body rotates around the X axis, then verterr.Y increases; Rotated around Y then .X increases, so
                // Change  Body angular velocity  X based on Y, and Y based on X. Z is not changed.
                vertattr.X =    verterr.Y;
                vertattr.Y =  - verterr.X;
                vertattr.Z = 0f;

                // scaling appears better usingsquare-law
                float bounce = 1.0f - _verticalAttractionEfficiency * _verticalAttractionEfficiency;
                vertattr.X += bounce * angularVelocity.X;
                vertattr.Y += bounce * angularVelocity.Y;

            } // else vertical attractor is off

    //        _lastVertAttractor = vertattr;

            // Bank section tba
            // Deflection section tba

            // Sum velocities
            _lastAngularVelocity = _angularMotorVelocity + vertattr; // + bank + deflection

            if ((_flags & VehicleFlag.NO_DEFLECTION_UP) != 0)
            {
                _lastAngularVelocity.X = 0;
                _lastAngularVelocity.Y = 0;
            }

            if (!_lastAngularVelocity.ApproxEquals(Vector3.Zero, 0.01f))
            {
                if (!SafeNativeMethods.BodyIsEnabled (Body))  SafeNativeMethods.BodyEnable (Body);
            }
            else
            {
                _lastAngularVelocity = Vector3.Zero; // Reduce small value to zero.
            }

             // apply friction
            Vector3 decayamount = Vector3.One / (_angularFrictionTimescale / pTimestep);
            _lastAngularVelocity -= _lastAngularVelocity * decayamount;

            // Apply to the body
            SafeNativeMethods.BodySetAngularVel (Body, _lastAngularVelocity.X, _lastAngularVelocity.Y, _lastAngularVelocity.Z);

        } //end MoveAngular
        internal void LimitRotation(float timestep)
        {
            SafeNativeMethods.Quaternion rot = SafeNativeMethods.BodyGetQuaternion(Body);
            Quaternion rotq = new Quaternion(rot.X, rot.Y, rot.Z, rot.W);    // rotq = rotation of object
            SafeNativeMethods.Quaternion _rot = new SafeNativeMethods.Quaternion();
            bool changed = false;
            _rot.X = rotq.X;
            _rot.Y = rotq.Y;
            _rot.Z = rotq.Z;
            _rot.W = rotq.W;
            if (_RollreferenceFrame != Quaternion.Identity)
            {
                if (rotq.X >= _RollreferenceFrame.X)
                {
                    _rot.X = rotq.X - _RollreferenceFrame.X / 2;
                }
                if (rotq.Y >= _RollreferenceFrame.Y)
                {
                    _rot.Y = rotq.Y - _RollreferenceFrame.Y / 2;
                }
                if (rotq.X <= -_RollreferenceFrame.X)
                {
                    _rot.X = rotq.X + _RollreferenceFrame.X / 2;
                }
                if (rotq.Y <= -_RollreferenceFrame.Y)
                {
                    _rot.Y = rotq.Y + _RollreferenceFrame.Y / 2;
                }
                changed = true;
            }
            if ((_flags & VehicleFlag.LOCK_ROTATION) != 0)
            {
                _rot.X = 0;
                _rot.Y = 0;
                changed = true;
            }
            if (changed)
                SafeNativeMethods.BodySetQuaternion(Body, ref _rot);
        }
    }
}
