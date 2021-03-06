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

// Extensive change Ubit 2012

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.ubOde
{
    public class ODEDynamics
    {
        public Vehicle Type => _type;

        private readonly OdePrim rootPrim;
        private readonly ODEScene _pParentScene;

        // Vehicle properties
        // WARNING this are working copies for internel use
        // their values may not be the corresponding parameter

        private Quaternion _referenceFrame = Quaternion.Identity;      // Axis modifier
        private Quaternion _RollreferenceFrame = Quaternion.Identity;  // what hell is this ?

        private Vehicle _type = Vehicle.TYPE_NONE;                     // If a 'VEHICLE', and what kind

        private VehicleFlag _flags = 0;                  // Boolean settings:
                                                                        // HOVER_TERRAIN_ONLY
                                                                        // HOVER_GLOBAL_HEIGHT
                                                                        // NO_DEFLECTION_UP
                                                                        // HOVER_WATER_ONLY
                                                                        // HOVER_UP_ONLY
                                                                        // LIMIT_MOTOR_UP
                                                                        // LIMIT_ROLL_ONLY
        private Vector3 _BlockingEndPoint = Vector3.Zero;              // not sl

        // Linear properties
        private Vector3 _linearMotorDirection = Vector3.Zero;          // velocity requested by LSL, decayed by time
        private Vector3 _linearFrictionTimescale = new Vector3(1000, 1000, 1000);
        private float _linearMotorDecayTimescale = 120;
        private float _linearMotorTimescale = 1000;
        private Vector3 _linearMotorOffset = Vector3.Zero;

        //Angular properties
        private Vector3 _angularMotorDirection = Vector3.Zero;         // angular velocity requested by LSL motor
        private float _angularMotorTimescale = 1000;                      // motor angular velocity ramp up rate
        private float _angularMotorDecayTimescale = 120;                 // motor angular velocity decay rate
        private Vector3 _angularFrictionTimescale = new Vector3(1000, 1000, 1000);      // body angular velocity  decay rate

        //Deflection properties
        private float _angularDeflectionEfficiency = 0;
        private float _angularDeflectionTimescale = 1000;
        private float _linearDeflectionEfficiency = 0;
        private float _linearDeflectionTimescale = 1000;

        //Banking properties
        private float _bankingEfficiency = 0;
        private float _bankingMix = 0;
        private float _bankingTimescale = 1000;

        //Hover and Buoyancy properties
        private float _VhoverHeight = 0f;
        private float _VhoverEfficiency = 0f;
        private float _VhoverTimescale = 1000f;
        private float _VehicleBuoyancy = 0f;           //KF: _VehicleBuoyancy is set by VEHICLE_BUOYANCY for a vehicle.
                    // Modifies gravity. Slider between -1 (double-gravity) and 1 (full anti-gravity)
                    // KF: So far I have found no good method to combine a script-requested .Z velocity and gravity.
                    // Therefore only _VehicleBuoyancy=1 (0g) will use the script-requested .Z velocity.

        //Attractor properties
        private float _verticalAttractionEfficiency = 1.0f;        // damped
        private float _verticalAttractionTimescale = 1000f;        // Timescale > 300  means no vert attractor.


        // auxiliar
        private float _lmEfect = 0f;                                            // current linear motor eficiency
        private float _lmDecay = 0f;                                            // current linear decay

        private float _amEfect = 0;                                            // current angular motor eficiency
        private float _amDecay = 0f;                                            // current linear decay

        private float _ffactor = 1.0f;

        private readonly float _timestep = 0.02f;
        private readonly float _invtimestep = 50;


        float _ampwr;
        float _amdampX;
        float _amdampY;
        float _amdampZ;

        float _gravmod;

        public float FrictionFactor => _ffactor;

        public float GravMod
        {
            set => _gravmod = value;
        }


        public ODEDynamics(OdePrim rootp)
        {
            rootPrim = rootp;
            _pParentScene = rootPrim._parent_scene;
            _timestep = _pParentScene.ODE_STEPSIZE;
            _invtimestep = 1.0f / _timestep;
            _gravmod = rootPrim.GravModifier;
        }

        public void DoSetVehicle(VehicleData vd)
        {
            _type = vd._type;
            _flags = vd._flags;


            // Linear properties
            _linearMotorDirection = vd._linearMotorDirection;

            _linearFrictionTimescale = vd._linearFrictionTimescale;
            if (_linearFrictionTimescale.X < _timestep) _linearFrictionTimescale.X = _timestep;
            if (_linearFrictionTimescale.Y < _timestep) _linearFrictionTimescale.Y = _timestep;
            if (_linearFrictionTimescale.Z < _timestep) _linearFrictionTimescale.Z = _timestep;

            _linearMotorDecayTimescale = vd._linearMotorDecayTimescale;
            if (_linearMotorDecayTimescale < _timestep) _linearMotorDecayTimescale = _timestep;
            _linearMotorDecayTimescale += 0.2f;
            _linearMotorDecayTimescale *= _invtimestep;

            _linearMotorTimescale = vd._linearMotorTimescale;
            if (_linearMotorTimescale < _timestep) _linearMotorTimescale = _timestep;

            _linearMotorOffset = vd._linearMotorOffset;

            //Angular properties
            _angularMotorDirection = vd._angularMotorDirection;
            _angularMotorTimescale = vd._angularMotorTimescale;
            if (_angularMotorTimescale < _timestep) _angularMotorTimescale = _timestep;

            _angularMotorDecayTimescale = vd._angularMotorDecayTimescale;
            if (_angularMotorDecayTimescale < _timestep) _angularMotorDecayTimescale = _timestep;
            _angularMotorDecayTimescale *= _invtimestep;

            _angularFrictionTimescale = vd._angularFrictionTimescale;
            if (_angularFrictionTimescale.X < _timestep) _angularFrictionTimescale.X = _timestep;
            if (_angularFrictionTimescale.Y < _timestep) _angularFrictionTimescale.Y = _timestep;
            if (_angularFrictionTimescale.Z < _timestep) _angularFrictionTimescale.Z = _timestep;

            //Deflection properties
            _angularDeflectionEfficiency = vd._angularDeflectionEfficiency;
            _angularDeflectionTimescale = vd._angularDeflectionTimescale;
            if (_angularDeflectionTimescale < _timestep) _angularDeflectionTimescale = _timestep;

            _linearDeflectionEfficiency = vd._linearDeflectionEfficiency;
            _linearDeflectionTimescale = vd._linearDeflectionTimescale;
            if (_linearDeflectionTimescale < _timestep) _linearDeflectionTimescale = _timestep;

            //Banking properties
            _bankingEfficiency = vd._bankingEfficiency;
            _bankingMix = vd._bankingMix;
            _bankingTimescale = vd._bankingTimescale;
            if (_bankingTimescale < _timestep) _bankingTimescale = _timestep;

            //Hover and Buoyancy properties
            _VhoverHeight = vd._VhoverHeight;
            _VhoverEfficiency = vd._VhoverEfficiency;
            _VhoverTimescale = vd._VhoverTimescale;
            if (_VhoverTimescale < _timestep) _VhoverTimescale = _timestep;

            _VehicleBuoyancy = vd._VehicleBuoyancy;

            //Attractor properties
            _verticalAttractionEfficiency = vd._verticalAttractionEfficiency;
            _verticalAttractionTimescale = vd._verticalAttractionTimescale;
            if (_verticalAttractionTimescale < _timestep) _verticalAttractionTimescale = _timestep;

            // Axis
            _referenceFrame = vd._referenceFrame;

            _lmEfect = 0;
            _lmDecay = 1.0f - 1.0f / _linearMotorDecayTimescale;
            _amEfect = 0;
            _ffactor = 1.0f;
        }

        internal void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
        {
            float len;
            if(float.IsNaN(pValue) || float.IsInfinity(pValue))
                return;

            switch (pParam)
            {
                case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    _angularDeflectionEfficiency = pValue;
                    break;
                case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _angularDeflectionTimescale = pValue;
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    else if (pValue > 120) pValue = 120;
                    _angularMotorDecayTimescale = pValue * _invtimestep;
                    _amDecay = 1.0f - 1.0f / _angularMotorDecayTimescale;
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _angularMotorTimescale = pValue;
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    if (pValue < -1f) pValue = -1f;
                    if (pValue > 1f) pValue = 1f;
                    _bankingEfficiency = pValue;
                    break;
                case Vehicle.BANKING_MIX:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    _bankingMix = pValue;
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _bankingTimescale = pValue;
                    break;
                case Vehicle.BUOYANCY:
                    if (pValue < -1f) pValue = -1f;
                    if (pValue > 1f) pValue = 1f;
                    _VehicleBuoyancy = pValue;
                    break;
                case Vehicle.HOVER_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    _VhoverEfficiency = pValue;
                    break;
                case Vehicle.HOVER_HEIGHT:
                    _VhoverHeight = pValue;
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _VhoverTimescale = pValue;
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    _linearDeflectionEfficiency = pValue;
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _linearDeflectionTimescale = pValue;
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    else if (pValue > 120) pValue = 120;
                    _linearMotorDecayTimescale = (0.2f +pValue) * _invtimestep;
                    _lmDecay = 1.0f - 1.0f / _linearMotorDecayTimescale;
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _linearMotorTimescale = pValue;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    if (pValue < 0f) pValue = 0f;
                    if (pValue > 1f) pValue = 1f;
                    _verticalAttractionEfficiency = pValue;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _verticalAttractionTimescale = pValue;
                    break;

                // These are vector properties but the engine lets you use a single float value to
                // set all of the components to the same value
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _angularFrictionTimescale = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    _angularMotorDirection = new Vector3(pValue, pValue, pValue);
                    len = _angularMotorDirection.Length();
                    if (len > 12.566f)
                        _angularMotorDirection *= 12.566f / len;

                    _amEfect = 1.0f ; // turn it on
                    _amDecay = 1.0f - 1.0f / _angularMotorDecayTimescale;

                    if (rootPrim.Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(rootPrim.Body)
                            && !rootPrim._isSelected && !rootPrim._disabled)
                        SafeNativeMethods.BodyEnable(rootPrim.Body);

                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    if (pValue < _timestep) pValue = _timestep;
                    _linearFrictionTimescale = new Vector3(pValue, pValue, pValue);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    _linearMotorDirection = new Vector3(pValue, pValue, pValue);
                    len = _linearMotorDirection.Length();
                    if (len > 100.0f)
                        _linearMotorDirection *= 100.0f / len;

                    _lmDecay = 1.0f - 1.0f / _linearMotorDecayTimescale;
                    _lmEfect = 1.0f; // turn it on

                    _ffactor = 0.0f;
                    if (rootPrim.Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(rootPrim.Body)
                            && !rootPrim._isSelected && !rootPrim._disabled)
                        SafeNativeMethods.BodyEnable(rootPrim.Body);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    _linearMotorOffset = new Vector3(pValue, pValue, pValue);
                    len = _linearMotorOffset.Length();
                    if (len > 100.0f)
                        _linearMotorOffset *= 100.0f / len;
                    break;
            }
        }//end ProcessFloatVehicleParam

        internal void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
        {
            float len;
            if(!pValue.IsFinite())
                return;

            switch (pParam)
            {
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    if (pValue.X < _timestep) pValue.X = _timestep;
                    if (pValue.Y < _timestep) pValue.Y = _timestep;
                    if (pValue.Z < _timestep) pValue.Z = _timestep;

                    _angularFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    _angularMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    // Limit requested angular speed to 2 rps= 4 pi rads/sec
                    len = _angularMotorDirection.Length();
                    if (len > 12.566f)
                        _angularMotorDirection *= 12.566f / len;

                    _amEfect = 1.0f; // turn it on
                    _amDecay = 1.0f - 1.0f / _angularMotorDecayTimescale;

                    if (rootPrim.Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(rootPrim.Body)
                            && !rootPrim._isSelected && !rootPrim._disabled)
                        SafeNativeMethods.BodyEnable(rootPrim.Body);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    if (pValue.X < _timestep) pValue.X = _timestep;
                    if (pValue.Y < _timestep) pValue.Y = _timestep;
                    if (pValue.Z < _timestep) pValue.Z = _timestep;
                    _linearFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    _linearMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    len = _linearMotorDirection.Length();
                    if (len > 100.0f)
                        _linearMotorDirection *= 100.0f / len;

                    _lmEfect = 1.0f; // turn it on
                    _lmDecay = 1.0f - 1.0f / _linearMotorDecayTimescale;

                    _ffactor = 0.0f;
                    if (rootPrim.Body != IntPtr.Zero && !SafeNativeMethods.BodyIsEnabled(rootPrim.Body)
                            && !rootPrim._isSelected && !rootPrim._disabled)
                        SafeNativeMethods.BodyEnable(rootPrim.Body);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    _linearMotorOffset = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    len = _linearMotorOffset.Length();
                    if (len > 100.0f)
                        _linearMotorOffset *= 100.0f / len;
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
                    //                    _referenceFrame = Quaternion.Inverse(pValue);
                    _referenceFrame = pValue;
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
                _flags &= ~(VehicleFlag)pParam;
            }
            else
            {
                _flags |= (VehicleFlag)pParam;
            }
        }//end ProcessVehicleFlags

        internal void ProcessTypeChange(Vehicle pType)
        {
            _lmEfect = 0;

            _amEfect = 0;
            _ffactor = 1f;

            _linearMotorDirection = Vector3.Zero;
            _angularMotorDirection = Vector3.Zero;

            _BlockingEndPoint = Vector3.Zero;
            _RollreferenceFrame = Quaternion.Identity;
            _linearMotorOffset = Vector3.Zero;

            _referenceFrame = Quaternion.Identity;

            // Set Defaults For Type
            _type = pType;
            switch (pType)
            {
                case Vehicle.TYPE_NONE:
                    _linearFrictionTimescale = new Vector3(1000, 1000, 1000);
                    _angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    _linearMotorTimescale = 1000;
                    _linearMotorDecayTimescale = 120 * _invtimestep;
                    _angularMotorTimescale = 1000;
                    _angularMotorDecayTimescale = 1000  * _invtimestep;
                    _VhoverHeight = 0;
                    _VhoverEfficiency = 1;
                    _VhoverTimescale = 1000;
                    _VehicleBuoyancy = 0;
                    _linearDeflectionEfficiency = 0;
                    _linearDeflectionTimescale = 1000;
                    _angularDeflectionEfficiency = 0;
                    _angularDeflectionTimescale = 1000;
                    _bankingEfficiency = 0;
                    _bankingMix = 1;
                    _bankingTimescale = 1000;
                    _verticalAttractionEfficiency = 0;
                    _verticalAttractionTimescale = 1000;

                    _flags = 0;
                    break;

                case Vehicle.TYPE_SLED:
                    _linearFrictionTimescale = new Vector3(30, 1, 1000);
                    _angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    _linearMotorTimescale = 1000;
                    _linearMotorDecayTimescale = 120 * _invtimestep;
                    _angularMotorTimescale = 1000;
                    _angularMotorDecayTimescale = 120 * _invtimestep;
                    _VhoverHeight = 0;
                    _VhoverEfficiency = 1;
                    _VhoverTimescale = 10;
                    _VehicleBuoyancy = 0;
                    _linearDeflectionEfficiency = 1;
                    _linearDeflectionTimescale = 1;
                    _angularDeflectionEfficiency = 0;
                    _angularDeflectionTimescale = 10;
                    _verticalAttractionEfficiency = 1;
                    _verticalAttractionTimescale = 1000;
                    _bankingEfficiency = 0;
                    _bankingMix = 1;
                    _bankingTimescale = 10;
                    _flags &=
                         ~(VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY |
                           VehicleFlag.HOVER_GLOBAL_HEIGHT | VehicleFlag.HOVER_UP_ONLY);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP |
                               VehicleFlag.LIMIT_ROLL_ONLY |
                               VehicleFlag.LIMIT_MOTOR_UP;
                    break;

                case Vehicle.TYPE_CAR:
                    _linearFrictionTimescale = new Vector3(100, 2, 1000);
                    _angularFrictionTimescale = new Vector3(1000, 1000, 1000);
                    _linearMotorTimescale = 1;
                    _linearMotorDecayTimescale = 60 * _invtimestep;
                    _angularMotorTimescale = 1;
                    _angularMotorDecayTimescale = 0.8f * _invtimestep;
                    _VhoverHeight = 0;
                    _VhoverEfficiency = 0;
                    _VhoverTimescale = 1000;
                    _VehicleBuoyancy = 0;
                    _linearDeflectionEfficiency = 1;
                    _linearDeflectionTimescale = 2;
                    _angularDeflectionEfficiency = 0;
                    _angularDeflectionTimescale = 10;
                    _verticalAttractionEfficiency = 1f;
                    _verticalAttractionTimescale = 10f;
                    _bankingEfficiency = -0.2f;
                    _bankingMix = 1;
                    _bankingTimescale = 1;
                    _flags &= ~(VehicleFlag.HOVER_WATER_ONLY |
                                VehicleFlag.HOVER_TERRAIN_ONLY |
                                VehicleFlag.HOVER_GLOBAL_HEIGHT);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP |
                               VehicleFlag.LIMIT_ROLL_ONLY |
                               VehicleFlag.LIMIT_MOTOR_UP |
                               VehicleFlag.HOVER_UP_ONLY;
                    break;
                case Vehicle.TYPE_BOAT:
                    _linearFrictionTimescale = new Vector3(10, 3, 2);
                    _angularFrictionTimescale = new Vector3(10, 10, 10);
                    _linearMotorTimescale = 5;
                    _linearMotorDecayTimescale = 60 * _invtimestep;
                    _angularMotorTimescale = 4;
                    _angularMotorDecayTimescale = 4 * _invtimestep;
                    _VhoverHeight = 0;
                    _VhoverEfficiency = 0.5f;
                    _VhoverTimescale = 2;
                    _VehicleBuoyancy = 1;
                    _linearDeflectionEfficiency = 0.5f;
                    _linearDeflectionTimescale = 3;
                    _angularDeflectionEfficiency = 0.5f;
                    _angularDeflectionTimescale = 5;
                    _verticalAttractionEfficiency = 0.5f;
                    _verticalAttractionTimescale = 5f;
                    _bankingEfficiency = -0.3f;
                    _bankingMix = 0.8f;
                    _bankingTimescale = 1;
                    _flags &= ~(VehicleFlag.HOVER_TERRAIN_ONLY |
                            VehicleFlag.HOVER_GLOBAL_HEIGHT |
                            VehicleFlag.HOVER_UP_ONLY); // |
//                            VehicleFlag.LIMIT_ROLL_ONLY);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP |
                               VehicleFlag.LIMIT_MOTOR_UP |
                               VehicleFlag.HOVER_UP_ONLY |  // new sl
                               VehicleFlag.HOVER_WATER_ONLY;
                    break;

                case Vehicle.TYPE_AIRPLANE:
                    _linearFrictionTimescale = new Vector3(200, 10, 5);
                    _angularFrictionTimescale = new Vector3(20, 20, 20);
                    _linearMotorTimescale = 2;
                    _linearMotorDecayTimescale = 60 * _invtimestep;
                    _angularMotorTimescale = 4;
                    _angularMotorDecayTimescale = 8 * _invtimestep;
                    _VhoverHeight = 0;
                    _VhoverEfficiency = 0.5f;
                    _VhoverTimescale = 1000;
                    _VehicleBuoyancy = 0;
                    _linearDeflectionEfficiency = 0.5f;
                    _linearDeflectionTimescale = 0.5f;
                    _angularDeflectionEfficiency = 1;
                    _angularDeflectionTimescale = 2;
                    _verticalAttractionEfficiency = 0.9f;
                    _verticalAttractionTimescale = 2f;
                    _bankingEfficiency = 1;
                    _bankingMix = 0.7f;
                    _bankingTimescale = 2;
                    _flags &= ~(VehicleFlag.HOVER_WATER_ONLY |
                        VehicleFlag.HOVER_TERRAIN_ONLY |
                        VehicleFlag.HOVER_GLOBAL_HEIGHT |
                        VehicleFlag.HOVER_UP_ONLY |
                        VehicleFlag.NO_DEFLECTION_UP |
                        VehicleFlag.LIMIT_MOTOR_UP);
                    _flags |= VehicleFlag.LIMIT_ROLL_ONLY;
                    break;

                case Vehicle.TYPE_BALLOON:
                    _linearFrictionTimescale = new Vector3(5, 5, 5);
                    _angularFrictionTimescale = new Vector3(10, 10, 10);
                    _linearMotorTimescale = 5;
                    _linearMotorDecayTimescale = 60 * _invtimestep;
                    _angularMotorTimescale = 6;
                    _angularMotorDecayTimescale = 10 * _invtimestep;
                    _VhoverHeight = 5;
                    _VhoverEfficiency = 0.8f;
                    _VhoverTimescale = 10;
                    _VehicleBuoyancy = 1;
                    _linearDeflectionEfficiency = 0;
                    _linearDeflectionTimescale = 5 * _invtimestep;
                    _angularDeflectionEfficiency = 0;
                    _angularDeflectionTimescale = 5;
                    _verticalAttractionEfficiency = 1f;
                    _verticalAttractionTimescale = 1000f;
                    _bankingEfficiency = 0;
                    _bankingMix = 0.7f;
                    _bankingTimescale = 5;
                    _flags &= ~(VehicleFlag.HOVER_WATER_ONLY |
                        VehicleFlag.HOVER_TERRAIN_ONLY |
                        VehicleFlag.HOVER_UP_ONLY |
                        VehicleFlag.NO_DEFLECTION_UP |
                        VehicleFlag.LIMIT_MOTOR_UP  | //);
                        VehicleFlag.LIMIT_ROLL_ONLY | // new sl
                        VehicleFlag.HOVER_GLOBAL_HEIGHT); // new sl

//                    _flags |= (VehicleFlag.LIMIT_ROLL_ONLY |
//                        VehicleFlag.HOVER_GLOBAL_HEIGHT);
                    break;

            }
            // disable mouse steering
            _flags &= ~(VehicleFlag.MOUSELOOK_STEER |
                         VehicleFlag.MOUSELOOK_BANK  |
                         VehicleFlag.CAMERA_DECOUPLED);

            _lmDecay = 1.0f - 1.0f / _linearMotorDecayTimescale;
            _amDecay = 1.0f - 1.0f / _angularMotorDecayTimescale;

        }//end SetDefaultsForType

        internal void Stop()
        {
            _lmEfect = 0;
            _lmDecay = 0f;
            _amEfect = 0;
            _amDecay = 0;
            _ffactor = 1f;
        }

        public static Vector3 Xrot(Quaternion rot)
        {
            Vector3 vec;
            rot.Normalize(); // just in case
            vec.X = 2 * (rot.X * rot.X + rot.W * rot.W) - 1;
            vec.Y = 2 * (rot.X * rot.Y + rot.Z * rot.W);
            vec.Z = 2 * (rot.X * rot.Z - rot.Y * rot.W);
            return vec;
        }

        public static Vector3 Zrot(Quaternion rot)
        {
            Vector3 vec;
            rot.Normalize(); // just in case
            vec.X = 2 * (rot.X * rot.Z + rot.Y * rot.W);
            vec.Y = 2 * (rot.Y * rot.Z - rot.X * rot.W);
            vec.Z = 2 * (rot.Z * rot.Z + rot.W * rot.W) - 1;

            return vec;
        }

        private const float pi = (float)Math.PI;
        private const float halfpi = 0.5f * (float)Math.PI;
        private const float twopi = 2.0f * pi;

        public static Vector3 ubRot2Euler(Quaternion rot)
        {
            // returns roll in X
            //         pitch in Y
            //         yaw in Z
            Vector3 vec;

            // assuming rot is normalised
            // rot.Normalize();

            float zX = rot.X * rot.Z + rot.Y * rot.W;

            if (zX < -0.49999f)
            {
                vec.X = 0;
                vec.Y = -halfpi;
                vec.Z = (float)(-2d * Math.Atan(rot.X / rot.W));
            }
            else if (zX > 0.49999f)
            {
                vec.X = 0;
                vec.Y = halfpi;
                vec.Z = (float)(2d * Math.Atan(rot.X / rot.W));
            }
            else
            {
                vec.Y = (float)Math.Asin(2 * zX);

                float sqw = rot.W * rot.W;

                float minuszY = rot.X * rot.W - rot.Y * rot.Z;
                float zZ = rot.Z * rot.Z + sqw - 0.5f;

                vec.X = (float)Math.Atan2(minuszY, zZ);

                float yX = rot.Z * rot.W - rot.X * rot.Y; //( have negative ?)
                float yY = rot.X * rot.X + sqw - 0.5f;
                vec.Z = (float)Math.Atan2(yX, yY);
            }
            return vec;
        }

        public static void GetRollPitch(Quaternion rot, out float roll, out float pitch)
        {
            // assuming rot is normalised
            // rot.Normalize();

            float zX = rot.X * rot.Z + rot.Y * rot.W;

            if (zX < -0.49999f)
            {
                roll = 0;
                pitch = -halfpi;
            }
            else if (zX > 0.49999f)
            {
                roll = 0;
                pitch = halfpi;
            }
            else
            {
                pitch = (float)Math.Asin(2 * zX);

                float minuszY = rot.X * rot.W - rot.Y * rot.Z;
                float zZ = rot.Z * rot.Z + rot.W * rot.W - 0.5f;

                roll = (float)Math.Atan2(minuszY, zZ);
            }
            return ;
        }

        internal void Step()
        {
            IntPtr Body = rootPrim.Body;

            SafeNativeMethods.Mass dmass;
            SafeNativeMethods.BodyGetMass(Body, out dmass);

            SafeNativeMethods.Quaternion rot = SafeNativeMethods.BodyGetQuaternion(Body);
            Quaternion objrotq = new Quaternion(rot.X, rot.Y, rot.Z, rot.W);    // rotq = rotation of object
            Quaternion rotq = objrotq;    // rotq = rotation of object
            rotq *= _referenceFrame; // rotq is now rotation in vehicle reference frame
            Quaternion irotq = Quaternion.Inverse(rotq);

            SafeNativeMethods.Vector3 dvtmp;
            Vector3 tmpV;
            Vector3 curVel; // velocity in world
            Vector3 curAngVel; // angular velocity in world
            Vector3 force = Vector3.Zero; // actually linear aceleration until mult by mass in world frame
            Vector3 torque = Vector3.Zero;// actually angular aceleration until mult by Inertia in vehicle frame
            SafeNativeMethods.Vector3 dtorque = new SafeNativeMethods.Vector3();

            dvtmp = SafeNativeMethods.BodyGetLinearVel(Body);
            curVel.X = dvtmp.X;
            curVel.Y = dvtmp.Y;
            curVel.Z = dvtmp.Z;
            Vector3 curLocalVel = curVel * irotq; // current velocity in  local

            dvtmp = SafeNativeMethods.BodyGetAngularVel(Body);
            curAngVel.X = dvtmp.X;
            curAngVel.Y = dvtmp.Y;
            curAngVel.Z = dvtmp.Z;
            Vector3 curLocalAngVel = curAngVel * irotq; // current angular velocity in  local

            float ldampZ = 0;

            bool mousemode = false;
            bool mousemodebank = false;

            float bankingEfficiency;
            float verticalAttractionTimescale = _verticalAttractionTimescale;

            if((_flags & (VehicleFlag.MOUSELOOK_STEER | VehicleFlag.MOUSELOOK_BANK)) != 0 )
            {
                mousemode = true;
                mousemodebank = (_flags & VehicleFlag.MOUSELOOK_BANK) != 0;
                if(mousemodebank)
                {
                    bankingEfficiency = _bankingEfficiency;
                    if(verticalAttractionTimescale < 149.9)
                        verticalAttractionTimescale *= 2.0f; // reduce current instability
                }
                else
                    bankingEfficiency = 0;
            }
            else
                bankingEfficiency = _bankingEfficiency;

            // linear motor
            if (_lmEfect > 0.01 && _linearMotorTimescale < 1000)
            {
                tmpV = _linearMotorDirection - curLocalVel; // velocity error
                tmpV *= _lmEfect / _linearMotorTimescale; // error to correct in this timestep
                tmpV *= rotq; // to world

                if ((_flags & VehicleFlag.LIMIT_MOTOR_UP) != 0)
                    tmpV.Z = 0;

                if (_linearMotorOffset.X != 0 || _linearMotorOffset.Y != 0 || _linearMotorOffset.Z != 0)
                {
                    // have offset, do it now
                    tmpV *= dmass.mass;
                    SafeNativeMethods.BodyAddForceAtRelPos(Body, tmpV.X, tmpV.Y, tmpV.Z, _linearMotorOffset.X, _linearMotorOffset.Y, _linearMotorOffset.Z);
                }
                else
                {
                    force.X += tmpV.X;
                    force.Y += tmpV.Y;
                    force.Z += tmpV.Z;
                }

                _lmEfect *= _lmDecay;
//                _ffactor = 0.01f + 1e-4f * curVel.LengthSquared();
                _ffactor = 0.0f;
            }
            else
            {
                _lmEfect = 0;
                _ffactor = 1f;
            }

            // hover
            if (_VhoverTimescale < 300 && rootPrim.pri_geom != IntPtr.Zero)
            {
                //                d.Vector3 pos = d.BodyGetPosition(Body);
                SafeNativeMethods.Vector3 pos = SafeNativeMethods.GeomGetPosition(rootPrim.pri_geom);
                pos.Z -= 0.21f; // minor offset that seems to be always there in sl

                float t = _pParentScene.GetTerrainHeightAtXY(pos.X, pos.Y);
                float perr;

                // default to global but don't go underground
                perr = _VhoverHeight - pos.Z;

                if ((_flags & VehicleFlag.HOVER_GLOBAL_HEIGHT) == 0)
                {
                    if ((_flags & VehicleFlag.HOVER_WATER_ONLY) != 0)
                    {
                        perr += _pParentScene.GetWaterLevel();
                    }
                    else if ((_flags & VehicleFlag.HOVER_TERRAIN_ONLY) != 0)
                    {
                        perr += t;
                    }
                    else
                    {
                        float w = _pParentScene.GetWaterLevel();
                        if (t > w)
                            perr += t;
                        else
                            perr += w;
                    }
                }
                else if (t > _VhoverHeight)
                        perr = t - pos.Z; ;

                if ((_flags & VehicleFlag.HOVER_UP_ONLY) == 0 || perr > -0.1)
                {
                    ldampZ = _VhoverEfficiency * _invtimestep;

                    perr *= (1.0f + ldampZ) / _VhoverTimescale;

                    //                    force.Z += perr - curVel.Z * tmp;
                    force.Z += perr;
                    ldampZ *= -curVel.Z;

                    force.Z += _pParentScene.gravityz * _gravmod * (1f - _VehicleBuoyancy);
                }
                else // no buoyancy
                    force.Z += _pParentScene.gravityz;
            }
            else
            {
                // default gravity and Buoyancy
                force.Z += _pParentScene.gravityz * _gravmod * (1f - _VehicleBuoyancy);
            }

            // linear deflection
            if (_linearDeflectionEfficiency > 0)
            {
                float len = curVel.Length();
                if (len > 0.01) // if moving
                {
                    Vector3 atAxis;
                    atAxis = Xrot(rotq); // where are we pointing to
                    atAxis *= len; // make it same size as world velocity vector

                    tmpV = -atAxis; // oposite direction
                    atAxis -= curVel; // error to one direction
                    len = atAxis.LengthSquared();

                    tmpV -= curVel; // error to oposite
                    float lens = tmpV.LengthSquared();

                    if (len > 0.01 || lens > 0.01) // do nothing if close enougth
                    {
                        if (len < lens)
                            tmpV = atAxis;

                        tmpV *= _linearDeflectionEfficiency / _linearDeflectionTimescale; // error to correct in this timestep
                        force.X += tmpV.X;
                        force.Y += tmpV.Y;
                        if ((_flags & VehicleFlag.NO_DEFLECTION_UP) == 0)
                            force.Z += tmpV.Z;
                    }
                }
            }

            // linear friction/damping
            if (curLocalVel.X != 0 || curLocalVel.Y != 0 || curLocalVel.Z != 0)
            {
                tmpV.X = -curLocalVel.X / _linearFrictionTimescale.X;
                tmpV.Y = -curLocalVel.Y / _linearFrictionTimescale.Y;
                tmpV.Z = -curLocalVel.Z / _linearFrictionTimescale.Z;
                tmpV *= rotq; // to world

                if(ldampZ != 0 && Math.Abs(ldampZ) > Math.Abs(tmpV.Z))
                    tmpV.Z = ldampZ;
                force.X += tmpV.X;
                force.Y += tmpV.Y;
                force.Z += tmpV.Z;
            }

            // vertical atractor
            if (verticalAttractionTimescale < 300)
            {
                float roll;
                float pitch;

                float ftmp = _invtimestep / verticalAttractionTimescale / verticalAttractionTimescale;

                float ftmp2;
                ftmp2 = 0.5f * _verticalAttractionEfficiency * _invtimestep;
                _amdampX = ftmp2;

                _ampwr = 1.0f - 0.8f * _verticalAttractionEfficiency;

                GetRollPitch(irotq, out roll, out pitch);

                if (roll > halfpi)
                    roll = pi - roll;
                else if (roll < -halfpi)
                    roll = -pi - roll;

                float effroll = pitch / halfpi;
                effroll *= effroll;
                effroll = 1 - effroll;
                effroll *= roll;

                torque.X += effroll * ftmp;

                if ((_flags & VehicleFlag.LIMIT_ROLL_ONLY) == 0)
                {
                    float effpitch = roll / halfpi;
                    effpitch *= effpitch;
                    effpitch = 1 - effpitch;
                    effpitch *= pitch;

                    torque.Y += effpitch * ftmp;
                }

                if (bankingEfficiency != 0 && Math.Abs(effroll) > 0.01)
                {

                    float broll = effroll;
                    /*
                                        if (broll > halfpi)
                                            broll = pi - broll;
                                        else if (broll < -halfpi)
                                            broll = -pi - broll;
                    */
                    broll *= _bankingEfficiency;
                    if (_bankingMix != 0)
                    {
                        float vfact = Math.Abs(curLocalVel.X) / 10.0f;
                        if (vfact > 1.0f) vfact = 1.0f;

                        if (curLocalVel.X >= 0)
                            broll *= 1 + (vfact - 1) * _bankingMix;
                        else
                            broll *= -(1 + (vfact - 1) * _bankingMix);
                    }
                    // make z rot be in world Z not local as seems to be in sl

                    broll = broll / _bankingTimescale;


                    tmpV = Zrot(irotq);
                    tmpV *= broll;

                    torque.X += tmpV.X;
                    torque.Y += tmpV.Y;
                    torque.Z += tmpV.Z;

                    _amdampZ = Math.Abs(_bankingEfficiency) / _bankingTimescale;
                    _amdampY = _amdampZ;

                }
                else
                {
                    _amdampZ = 1 / _angularFrictionTimescale.Z;
                    _amdampY = _amdampX;
                }
            }
            else
            {
                _ampwr = 1.0f;
                _amdampX = 1 / _angularFrictionTimescale.X;
                _amdampY = 1 / _angularFrictionTimescale.Y;
                _amdampZ = 1 / _angularFrictionTimescale.Z;
            }

            if(mousemode)
            {
                CameraData cam = rootPrim.TryGetCameraData();
                if(cam.Valid && cam.MouseLook)
                {
                    Vector3 dirv = cam.CameraAtAxis * irotq;

                    float invamts = 1.0f/_angularMotorTimescale;
                    float tmp;

                    // get out of x == 0 plane
                    if(Math.Abs(dirv.X) < 0.001f)
                        dirv.X = 0.001f;

                    if (Math.Abs(dirv.Z) > 0.01)
                    {
                        tmp = -(float)Math.Atan2(dirv.Z, dirv.X) * _angularMotorDirection.Y;
                        if(tmp < -4f)
                            tmp = -4f;
                        else if(tmp > 4f)
                            tmp = 4f;
                        torque.Y += (tmp - curLocalAngVel.Y) * invamts;
                        torque.Y -= curLocalAngVel.Y * _amdampY;
                    }
                    else
                        torque.Y -= curLocalAngVel.Y * _invtimestep;

                    if (Math.Abs(dirv.Y) > 0.01)
                    {
                        if(mousemodebank)
                        {
                            tmp = -(float)Math.Atan2(dirv.Y, dirv.X) * _angularMotorDirection.X;
                            if(tmp < -4f)
                                tmp = -4f;
                            else if(tmp > 4f)
                                tmp = 4f;
                            torque.X += (tmp - curLocalAngVel.X) * invamts;
                        }
                        else
                        {
                            tmp = (float)Math.Atan2(dirv.Y, dirv.X) * _angularMotorDirection.Z;
                            tmp *= invamts;
                            if(tmp < -4f)
                                tmp = -4f;
                            else if(tmp > 4f)
                                tmp = 4f;
                            torque.Z += (tmp - curLocalAngVel.Z) * invamts;
                        }
                        torque.X -= curLocalAngVel.X * _amdampX;
                        torque.Z -= curLocalAngVel.Z * _amdampZ;
                    }
                    else
                    {
                        if(mousemodebank)
                            torque.X -= curLocalAngVel.X * _invtimestep;
                        else
                            torque.Z -= curLocalAngVel.Z * _invtimestep;
                    }
                }
                else
                {
                    if (curLocalAngVel.X != 0 || curLocalAngVel.Y != 0 || curLocalAngVel.Z != 0)
                    {
                        torque.X -= curLocalAngVel.X * 10f;
                        torque.Y -= curLocalAngVel.Y * 10f;
                        torque.Z -= curLocalAngVel.Z * 10f;
                    }
                }
            }
            else
            {
                // angular motor
                if (_amEfect > 0.01 && _angularMotorTimescale < 1000)
                {
                    tmpV = _angularMotorDirection - curLocalAngVel; // velocity error
                    tmpV *= _amEfect / _angularMotorTimescale; // error to correct in this timestep
                    torque.X += tmpV.X * _ampwr;
                    torque.Y += tmpV.Y * _ampwr;
                    torque.Z += tmpV.Z;

                    _amEfect *= _amDecay;
                }
                else
                    _amEfect = 0;

                // angular deflection
                if (_angularDeflectionEfficiency > 0)
                {
                    Vector3 dirv;

                    if (curLocalVel.X > 0.01f)
                        dirv = curLocalVel;
                    else if (curLocalVel.X < -0.01f)
                        // use oposite
                        dirv = -curLocalVel;
                    else
                    {
                        // make it fall into small positive x case
                        dirv.X = 0.01f;
                        dirv.Y = curLocalVel.Y;
                        dirv.Z = curLocalVel.Z;
                    }

                    float ftmp = _angularDeflectionEfficiency / _angularDeflectionTimescale;

                    if (Math.Abs(dirv.Z) > 0.01)
                    {
                        torque.Y += - (float)Math.Atan2(dirv.Z, dirv.X) * ftmp;
                    }

                    if (Math.Abs(dirv.Y) > 0.01)
                    {
                        torque.Z += (float)Math.Atan2(dirv.Y, dirv.X) * ftmp;
                    }
                }

                if (curLocalAngVel.X != 0 || curLocalAngVel.Y != 0 || curLocalAngVel.Z != 0)
                {
                    torque.X -= curLocalAngVel.X * _amdampX;
                    torque.Y -= curLocalAngVel.Y * _amdampY;
                    torque.Z -= curLocalAngVel.Z * _amdampZ;
                }
            }

            force *= dmass.mass;

            force += rootPrim._force;
            force += rootPrim._forceacc;
            rootPrim._forceacc = Vector3.Zero;

            if (force.X != 0 || force.Y != 0 || force.Z != 0)
            {
                SafeNativeMethods.BodyAddForce(Body, force.X, force.Y, force.Z);
            }

            if (torque.X != 0 || torque.Y != 0 || torque.Z != 0)
            {
                torque *= _referenceFrame; // to object frame
                dtorque.X = torque.X ;
                dtorque.Y = torque.Y;
                dtorque.Z = torque.Z;

                SafeNativeMethods.MultiplyM3V3(out dvtmp, ref dmass.I, ref dtorque);
                SafeNativeMethods.BodyAddRelTorque(Body, dvtmp.X, dvtmp.Y, dvtmp.Z); // add torque in object frame
            }

            torque = rootPrim._torque;
            torque += rootPrim._angularForceacc;
            rootPrim._angularForceacc = Vector3.Zero;
            if (torque.X != 0 || torque.Y != 0 || torque.Z != 0)
                SafeNativeMethods.BodyAddTorque(Body,torque.X, torque.Y, torque.Z);
        }
    }
}
