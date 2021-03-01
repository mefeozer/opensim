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
 *
 * The quotations from http://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial
 * are Copyright (c) 2009 Linden Research, Inc and are used under their license
 * of Creative Commons Attribution-Share Alike 3.0
 * (http://creativecommons.org/licenses/by-sa/3.0/).
 */

using System;
using OpenMetaverse;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    public sealed class BSDynamics : BSActor
    {
#pragma warning disable 414
        private static string LogHeader = "[BULLETSIM VEHICLE]";
#pragma warning restore 414

        // the prim this dynamic controller belongs to
        private BSPrimLinkable ControllingPrim { get; }

        private bool _haveRegisteredForSceneEvents;

        // mass of the vehicle fetched each time we're calles
        private float _vehicleMass;

        // Vehicle properties
        public Vehicle Type { get; set; }

        // private Quaternion _referenceFrame = Quaternion.Identity;   // Axis modifier
        private VehicleFlag _flags = (VehicleFlag) 0;                  // Boolean settings:
                                                                        // HOVER_TERRAIN_ONLY
                                                                        // HOVER_GLOBAL_HEIGHT
                                                                        // NO_DEFLECTION_UP
                                                                        // HOVER_WATER_ONLY
                                                                        // HOVER_UP_ONLY
                                                                        // LIMIT_MOTOR_UP
                                                                        // LIMIT_ROLL_ONLY
        private Vector3 _BlockingEndPoint = Vector3.Zero;
        private Quaternion _RollreferenceFrame = Quaternion.Identity;
        private Quaternion _referenceFrame = Quaternion.Identity;

        // Linear properties
        private BSVMotor _linearMotor = new BSVMotor("LinearMotor");
        private Vector3 _linearMotorDirection = Vector3.Zero;          // velocity requested by LSL, decayed by time
        private Vector3 _linearMotorOffset = Vector3.Zero;             // the point of force can be offset from the center
        private Vector3 _linearMotorDirectionLASTSET = Vector3.Zero;   // velocity requested by LSL
        private Vector3 _linearFrictionTimescale = Vector3.Zero;
        private float _linearMotorDecayTimescale = 1;
        private float _linearMotorTimescale = 1;
        private Vector3 _lastLinearVelocityVector = Vector3.Zero;
        private Vector3 _lastPositionVector = Vector3.Zero;
        // private bool _LinearMotorSetLastFrame = false;
        // private Vector3 _linearMotorOffset = Vector3.Zero;

        //Angular properties
        private BSVMotor _angularMotor = new BSVMotor("AngularMotor");
        private Vector3 _angularMotorDirection = Vector3.Zero;         // angular velocity requested by LSL motor
        // private int _angularMotorApply = 0;                            // application frame counter
        private Vector3 _angularMotorVelocity = Vector3.Zero;          // current angular motor velocity
        private float _angularMotorTimescale = 1;                      // motor angular velocity ramp up rate
        private float _angularMotorDecayTimescale = 1;                 // motor angular velocity decay rate
        private Vector3 _angularFrictionTimescale = Vector3.Zero;      // body angular velocity  decay rate
        private Vector3 _lastAngularVelocity = Vector3.Zero;
        private Vector3 _lastVertAttractor = Vector3.Zero;             // what VA was last applied to body

        //Deflection properties
        private BSVMotor _angularDeflectionMotor = new BSVMotor("AngularDeflection");
        private float _angularDeflectionEfficiency = 0;
        private float _angularDeflectionTimescale = 0;
        private float _linearDeflectionEfficiency = 0;
        private float _linearDeflectionTimescale = 0;

        //Banking properties
        private float _bankingEfficiency = 0;
        private float _bankingMix = 1;
        private float _bankingTimescale = 0;

        //Hover and Buoyancy properties
        private BSVMotor _hoverMotor = new BSVMotor("Hover");
        private float _VhoverHeight = 0f;
        private float _VhoverEfficiency = 0f;
        private float _VhoverTimescale = 0f;
        private float _VhoverTargetHeight = -1.0f;     // if <0 then no hover, else its the current target height
        // Modifies gravity. Slider between -1 (double-gravity) and 1 (full anti-gravity)
        private float _VehicleBuoyancy = 0f;
        private Vector3 _VehicleGravity = Vector3.Zero;    // Gravity computed when buoyancy set

        //Attractor properties
        private readonly BSVMotor _verticalAttractionMotor = new BSVMotor("VerticalAttraction");
        private float _verticalAttractionEfficiency = 1.0f; // damped
        private readonly float _verticalAttractionCutoff = 500f;     // per the documentation
        // Timescale > cutoff  means no vert attractor.
        private float _verticalAttractionTimescale = 510f;

        // Just some recomputed constants:
#pragma warning disable 414
        static readonly float TwoPI = (float)Math.PI * 2f;
        static readonly float FourPI = (float)Math.PI * 4f;
        static readonly float PIOverFour = (float)Math.PI / 4f;
        static readonly float PIOverTwo = (float)Math.PI / 2f;
#pragma warning restore 414

        public BSDynamics(BSScene myScene, BSPrim myPrim, string actorName)
            : base(myScene, myPrim, actorName)
        {
            Type = Vehicle.TYPE_NONE;
            _haveRegisteredForSceneEvents = false;

            ControllingPrim = myPrim as BSPrimLinkable;
            if (ControllingPrim == null)
            {
                // THIS CANNOT HAPPEN!!
            }
            VDetailLog("{0},Creation", ControllingPrim.LocalID);
        }

        // Return 'true' if this vehicle is doing vehicle things
        public bool IsActive => Type != Vehicle.TYPE_NONE && ControllingPrim.IsPhysicallyActive;

        // Return 'true' if this a vehicle that should be sitting on the ground
        public bool IsGroundVehicle => Type == Vehicle.TYPE_CAR || Type == Vehicle.TYPE_SLED;

        #region Vehicle parameter setting
        public void ProcessFloatVehicleParam(Vehicle pParam, float pValue)
        {
            VDetailLog("{0},ProcessFloatVehicleParam,param={1},val={2}", ControllingPrim.LocalID, pParam, pValue);
            float clampTemp;

            switch (pParam)
            {
                case Vehicle.ANGULAR_DEFLECTION_EFFICIENCY:
                    _angularDeflectionEfficiency = ClampInRange(0f, pValue, 1f);
                    break;
                case Vehicle.ANGULAR_DEFLECTION_TIMESCALE:
                    _angularDeflectionTimescale = ClampInRange(0.25f, pValue, 120);
                    break;
                case Vehicle.ANGULAR_MOTOR_DECAY_TIMESCALE:
                    _angularMotorDecayTimescale = ClampInRange(0.25f, pValue, 120);
                    _angularMotor.TargetValueDecayTimeScale = _angularMotorDecayTimescale;
                    break;
                case Vehicle.ANGULAR_MOTOR_TIMESCALE:
                    _angularMotorTimescale = ClampInRange(0.25f, pValue, 120);
                    _angularMotor.TimeScale = _angularMotorTimescale;
                    break;
                case Vehicle.BANKING_EFFICIENCY:
                    _bankingEfficiency = ClampInRange(-1f, pValue, 1f);
                    break;
                case Vehicle.BANKING_MIX:
                    _bankingMix = ClampInRange(0.01f, pValue, 1);
                    break;
                case Vehicle.BANKING_TIMESCALE:
                    _bankingTimescale = ClampInRange(0.25f, pValue, 120);
                    break;
                case Vehicle.BUOYANCY:
                    _VehicleBuoyancy = ClampInRange(-1f, pValue, 1f);
                    _VehicleGravity = ControllingPrim.ComputeGravity(_VehicleBuoyancy);
                    break;
                case Vehicle.HOVER_EFFICIENCY:
                    _VhoverEfficiency = ClampInRange(0.01f, pValue, 1f);
                    break;
                case Vehicle.HOVER_HEIGHT:
                    _VhoverHeight = ClampInRange(0f, pValue, 1000000f);
                    break;
                case Vehicle.HOVER_TIMESCALE:
                    _VhoverTimescale = ClampInRange(0.01f, pValue, 120);
                    break;
                case Vehicle.LINEAR_DEFLECTION_EFFICIENCY:
                    _linearDeflectionEfficiency = ClampInRange(0f, pValue, 1f);
                    break;
                case Vehicle.LINEAR_DEFLECTION_TIMESCALE:
                    _linearDeflectionTimescale = ClampInRange(0.01f, pValue, 120);
                    break;
                case Vehicle.LINEAR_MOTOR_DECAY_TIMESCALE:
                    _linearMotorDecayTimescale = ClampInRange(0.01f, pValue, 120);
                    _linearMotor.TargetValueDecayTimeScale = _linearMotorDecayTimescale;
                    break;
                case Vehicle.LINEAR_MOTOR_TIMESCALE:
                    _linearMotorTimescale = ClampInRange(0.01f, pValue, 120);
                    _linearMotor.TimeScale = _linearMotorTimescale;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_EFFICIENCY:
                    _verticalAttractionEfficiency = ClampInRange(0.1f, pValue, 1f);
                    _verticalAttractionMotor.Efficiency = _verticalAttractionEfficiency;
                    break;
                case Vehicle.VERTICAL_ATTRACTION_TIMESCALE:
                    _verticalAttractionTimescale = ClampInRange(0.01f, pValue, 120);
                    _verticalAttractionMotor.TimeScale = _verticalAttractionTimescale;
                    break;

                // These are vector properties but the engine lets you use a single float value to
                // set all of the components to the same value
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    clampTemp = ClampInRange(0.01f, pValue, 120);
                    _angularFrictionTimescale = new Vector3(clampTemp, clampTemp, clampTemp);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    clampTemp = ClampInRange(-TwoPI, pValue, TwoPI);
                    _angularMotorDirection = new Vector3(clampTemp, clampTemp, clampTemp);
                    _angularMotor.Zero();
                    _angularMotor.SetTarget(_angularMotorDirection);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    clampTemp = ClampInRange(0.01f, pValue, 120);
                    _linearFrictionTimescale = new Vector3(clampTemp, clampTemp, clampTemp);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    clampTemp = ClampInRange(-BSParam.MaxLinearVelocity, pValue, BSParam.MaxLinearVelocity);
                    _linearMotorDirection = new Vector3(clampTemp, clampTemp, clampTemp);
                    _linearMotorDirectionLASTSET = new Vector3(clampTemp, clampTemp, clampTemp);
                    _linearMotor.SetTarget(_linearMotorDirection);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    clampTemp = ClampInRange(-1000, pValue, 1000);
                    _linearMotorOffset = new Vector3(clampTemp, clampTemp, clampTemp);
                    break;

            }
        }//end ProcessFloatVehicleParam

        internal void ProcessVectorVehicleParam(Vehicle pParam, Vector3 pValue)
        {
            VDetailLog("{0},ProcessVectorVehicleParam,param={1},val={2}", ControllingPrim.LocalID, pParam, pValue);
            switch (pParam)
            {
                case Vehicle.ANGULAR_FRICTION_TIMESCALE:
                    pValue.X = ClampInRange(0.25f, pValue.X, 120);
                    pValue.Y = ClampInRange(0.25f, pValue.Y, 120);
                    pValue.Z = ClampInRange(0.25f, pValue.Z, 120);
                    _angularFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.ANGULAR_MOTOR_DIRECTION:
                    // Limit requested angular speed to 2 rps= 4 pi rads/sec
                    pValue.X = ClampInRange(-FourPI, pValue.X, FourPI);
                    pValue.Y = ClampInRange(-FourPI, pValue.Y, FourPI);
                    pValue.Z = ClampInRange(-FourPI, pValue.Z, FourPI);
                    _angularMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    _angularMotor.Zero();
                    _angularMotor.SetTarget(_angularMotorDirection);
                    break;
                case Vehicle.LINEAR_FRICTION_TIMESCALE:
                    pValue.X = ClampInRange(0.25f, pValue.X, 120);
                    pValue.Y = ClampInRange(0.25f, pValue.Y, 120);
                    pValue.Z = ClampInRange(0.25f, pValue.Z, 120);
                    _linearFrictionTimescale = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.LINEAR_MOTOR_DIRECTION:
                    pValue.X = ClampInRange(-BSParam.MaxLinearVelocity, pValue.X, BSParam.MaxLinearVelocity);
                    pValue.Y = ClampInRange(-BSParam.MaxLinearVelocity, pValue.Y, BSParam.MaxLinearVelocity);
                    pValue.Z = ClampInRange(-BSParam.MaxLinearVelocity, pValue.Z, BSParam.MaxLinearVelocity);
                    _linearMotorDirection = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    _linearMotorDirectionLASTSET = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    _linearMotor.SetTarget(_linearMotorDirection);
                    break;
                case Vehicle.LINEAR_MOTOR_OFFSET:
                    // Not sure the correct range to limit this variable
                    pValue.X = ClampInRange(-1000, pValue.X, 1000);
                    pValue.Y = ClampInRange(-1000, pValue.Y, 1000);
                    pValue.Z = ClampInRange(-1000, pValue.Z, 1000);
                    _linearMotorOffset = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
                case Vehicle.BLOCK_EXIT:
                    // Not sure the correct range to limit this variable
                    pValue.X = ClampInRange(-10000, pValue.X, 10000);
                    pValue.Y = ClampInRange(-10000, pValue.Y, 10000);
                    pValue.Z = ClampInRange(-10000, pValue.Z, 10000);
                    _BlockingEndPoint = new Vector3(pValue.X, pValue.Y, pValue.Z);
                    break;
            }
        }//end ProcessVectorVehicleParam

        internal void ProcessRotationVehicleParam(Vehicle pParam, Quaternion pValue)
        {
            VDetailLog("{0},ProcessRotationalVehicleParam,param={1},val={2}", ControllingPrim.LocalID, pParam, pValue);
            switch (pParam)
            {
                case Vehicle.REFERENCE_FRAME:
                    _referenceFrame = pValue;
                    break;
                case Vehicle.ROLL_FRAME:
                    _RollreferenceFrame = pValue;
                    break;
            }
        }//end ProcessRotationVehicleParam

        internal void ProcessVehicleFlags(int pParam, bool remove)
        {
            VDetailLog("{0},ProcessVehicleFlags,param={1},remove={2}", ControllingPrim.LocalID, pParam, remove);
            VehicleFlag parm = (VehicleFlag)pParam;
            if (pParam == -1)
                _flags = (VehicleFlag)0;
            else
            {
                if (remove)
                    _flags &= ~parm;
                else
                    _flags |= parm;
            }
        }

        public void ProcessTypeChange(Vehicle pType)
        {
            VDetailLog("{0},ProcessTypeChange,type={1}", ControllingPrim.LocalID, pType);
            // Set Defaults For Type
            Type = pType;
            switch (pType)
            {
                case Vehicle.TYPE_NONE:
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 0;
                    _linearMotorDecayTimescale = 0;
                    _linearFrictionTimescale = new Vector3(0, 0, 0);

                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorDecayTimescale = 0;
                    _angularMotorTimescale = 0;
                    _angularFrictionTimescale = new Vector3(0, 0, 0);

                    _VhoverHeight = 0;
                    _VhoverEfficiency = 0;
                    _VhoverTimescale = 0;
                    _VehicleBuoyancy = 0;

                    _linearDeflectionEfficiency = 1;
                    _linearDeflectionTimescale = 1;

                    _angularDeflectionEfficiency = 0;
                    _angularDeflectionTimescale = 1000;

                    _verticalAttractionEfficiency = 0;
                    _verticalAttractionTimescale = 0;

                    _bankingEfficiency = 0;
                    _bankingTimescale = 1000;
                    _bankingMix = 1;

                    _referenceFrame = Quaternion.Identity;
                    _flags = (VehicleFlag)0;

                    break;

                case Vehicle.TYPE_SLED:
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 1000;
                    _linearMotorDecayTimescale = 120;
                    _linearFrictionTimescale = new Vector3(30, 1, 1000);

                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 1000;
                    _angularMotorDecayTimescale = 120;
                    _angularFrictionTimescale = new Vector3(1000, 1000, 1000);

                    _VhoverHeight = 0;
                    _VhoverEfficiency = 10;    // TODO: this looks wrong!!
                    _VhoverTimescale = 10;
                    _VehicleBuoyancy = 0;

                    _linearDeflectionEfficiency = 1;
                    _linearDeflectionTimescale = 1;

                    _angularDeflectionEfficiency = 1;
                    _angularDeflectionTimescale = 1000;

                    _verticalAttractionEfficiency = 0;
                    _verticalAttractionTimescale = 0;

                    _bankingEfficiency = 0;
                    _bankingTimescale = 10;
                    _bankingMix = 1;

                    _referenceFrame = Quaternion.Identity;
                    _flags &= ~(VehicleFlag.HOVER_WATER_ONLY
                                | VehicleFlag.HOVER_TERRAIN_ONLY
                                | VehicleFlag.HOVER_GLOBAL_HEIGHT
                                | VehicleFlag.HOVER_UP_ONLY);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP
                               | VehicleFlag.LIMIT_ROLL_ONLY
                               | VehicleFlag.LIMIT_MOTOR_UP;

                    break;
                case Vehicle.TYPE_CAR:
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 1;
                    _linearMotorDecayTimescale = 60;
                    _linearFrictionTimescale = new Vector3(100, 2, 1000);

                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 1;
                    _angularMotorDecayTimescale = 0.8f;
                    _angularFrictionTimescale = new Vector3(1000, 1000, 1000);

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

                    _referenceFrame = Quaternion.Identity;
                    _flags &= ~(VehicleFlag.HOVER_WATER_ONLY
                                | VehicleFlag.HOVER_TERRAIN_ONLY
                                | VehicleFlag.HOVER_GLOBAL_HEIGHT);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP
                               | VehicleFlag.LIMIT_ROLL_ONLY
                               | VehicleFlag.LIMIT_MOTOR_UP
                               | VehicleFlag.HOVER_UP_ONLY;
                    break;
                case Vehicle.TYPE_BOAT:
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 5;
                    _linearMotorDecayTimescale = 60;
                    _linearFrictionTimescale = new Vector3(10, 3, 2);

                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 4;
                    _angularMotorDecayTimescale = 4;
                    _angularFrictionTimescale = new Vector3(10,10,10);

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

                    _referenceFrame = Quaternion.Identity;
                    _flags &= ~(VehicleFlag.HOVER_TERRAIN_ONLY
                                    | VehicleFlag.HOVER_GLOBAL_HEIGHT
                                    | VehicleFlag.LIMIT_ROLL_ONLY
                                    | VehicleFlag.HOVER_UP_ONLY);
                    _flags |= VehicleFlag.NO_DEFLECTION_UP
                               | VehicleFlag.LIMIT_MOTOR_UP
                               | VehicleFlag.HOVER_WATER_ONLY;
                    break;
                case Vehicle.TYPE_AIRPLANE:
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 2;
                    _linearMotorDecayTimescale = 60;
                    _linearFrictionTimescale = new Vector3(200, 10, 5);

                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 4;
                    _angularMotorDecayTimescale = 4;
                    _angularFrictionTimescale = new Vector3(20, 20, 20);

                    _VhoverHeight = 0;
                    _VhoverEfficiency = 0.5f;
                    _VhoverTimescale = 1000;
                    _VehicleBuoyancy = 0;

                    _linearDeflectionEfficiency = 0.5f;
                    _linearDeflectionTimescale = 3;

                    _angularDeflectionEfficiency = 1;
                    _angularDeflectionTimescale = 2;

                    _verticalAttractionEfficiency = 0.9f;
                    _verticalAttractionTimescale = 2f;

                    _bankingEfficiency = 1;
                    _bankingMix = 0.7f;
                    _bankingTimescale = 2;

                    _referenceFrame = Quaternion.Identity;
                    _flags &= ~(VehicleFlag.HOVER_WATER_ONLY
                                    | VehicleFlag.HOVER_TERRAIN_ONLY
                                    | VehicleFlag.HOVER_GLOBAL_HEIGHT
                                    | VehicleFlag.HOVER_UP_ONLY
                                    | VehicleFlag.NO_DEFLECTION_UP
                                    | VehicleFlag.LIMIT_MOTOR_UP);
                    _flags |= VehicleFlag.LIMIT_ROLL_ONLY;
                    break;
                case Vehicle.TYPE_BALLOON:
                    _linearMotorDirection = Vector3.Zero;
                    _linearMotorTimescale = 5;
                    _linearFrictionTimescale = new Vector3(5, 5, 5);
                    _linearMotorDecayTimescale = 60;

                    _angularMotorDirection = Vector3.Zero;
                    _angularMotorTimescale = 6;
                    _angularFrictionTimescale = new Vector3(10, 10, 10);
                    _angularMotorDecayTimescale = 10;

                    _VhoverHeight = 5;
                    _VhoverEfficiency = 0.8f;
                    _VhoverTimescale = 10;
                    _VehicleBuoyancy = 1;

                    _linearDeflectionEfficiency = 0;
                    _linearDeflectionTimescale = 5;

                    _angularDeflectionEfficiency = 0;
                    _angularDeflectionTimescale = 5;

                    _verticalAttractionEfficiency = 1f;
                    _verticalAttractionTimescale = 100f;

                    _bankingEfficiency = 0;
                    _bankingMix = 0.7f;
                    _bankingTimescale = 5;

                    _referenceFrame = Quaternion.Identity;

                    _referenceFrame = Quaternion.Identity;
                    _flags &= ~(VehicleFlag.HOVER_WATER_ONLY
                                    | VehicleFlag.HOVER_TERRAIN_ONLY
                                    | VehicleFlag.HOVER_UP_ONLY
                                    | VehicleFlag.NO_DEFLECTION_UP
                                    | VehicleFlag.LIMIT_MOTOR_UP);
                    _flags |= VehicleFlag.LIMIT_ROLL_ONLY
                               | VehicleFlag.HOVER_GLOBAL_HEIGHT;
                    break;
            }

            _linearMotor = new BSVMotor("LinearMotor", _linearMotorTimescale, _linearMotorDecayTimescale, 1f);
            // _linearMotor.PhysicsScene = _physicsScene;  // DEBUG DEBUG DEBUG (enables detail logging)

            _angularMotor = new BSVMotor("AngularMotor", _angularMotorTimescale, _angularMotorDecayTimescale, 1f);
            // _angularMotor.PhysicsScene = _physicsScene;  // DEBUG DEBUG DEBUG (enables detail logging)

            /*  Not implemented
            _verticalAttractionMotor = new BSVMotor("VerticalAttraction", _verticalAttractionTimescale,
                                BSMotor.Infinite, BSMotor.InfiniteVector,
                                _verticalAttractionEfficiency);
            // Z goes away and we keep X and Y
            _verticalAttractionMotor.PhysicsScene = PhysicsScene;  // DEBUG DEBUG DEBUG (enables detail logging)
             */

            if (this.Type == Vehicle.TYPE_NONE)
            {
                UnregisterForSceneEvents();
            }
            else
            {
                RegisterForSceneEvents();
            }

            // Update any physical parameters based on this type.
            Refresh();
        }
        #endregion // Vehicle parameter setting

        // BSActor.Refresh()
        public override void Refresh()
        {
            // If asking for a refresh, reset the physical parameters before the next simulation step.
            // Called whether active or not since the active state may be updated before the next step.
            _physicsScene.PostTaintObject("BSDynamics.Refresh", ControllingPrim.LocalID, delegate()
            {
                SetPhysicalParameters();
            });
        }

        // Some of the properties of this prim may have changed.
        // Do any updating needed for a vehicle
        private void SetPhysicalParameters()
        {
            if (IsActive)
            {
                // Remember the mass so we don't have to fetch it every step
                _vehicleMass = ControllingPrim.TotalMass;

                // Friction affects are handled by this vehicle code
                // _physicsScene.PE.SetFriction(ControllingPrim.PhysBody, BSParam.VehicleFriction);
                // _physicsScene.PE.SetRestitution(ControllingPrim.PhysBody, BSParam.VehicleRestitution);
                ControllingPrim.Linkset.SetPhysicalFriction(BSParam.VehicleFriction);
                ControllingPrim.Linkset.SetPhysicalRestitution(BSParam.VehicleRestitution);

                // Moderate angular movement introduced by Bullet.
                // TODO: possibly set AngularFactor and LinearFactor for the type of vehicle.
                //     Maybe compute linear and angular factor and damping from params.
                _physicsScene.PE.SetAngularDamping(ControllingPrim.PhysBody, BSParam.VehicleAngularDamping);
                _physicsScene.PE.SetLinearFactor(ControllingPrim.PhysBody, BSParam.VehicleLinearFactor);
                _physicsScene.PE.SetAngularFactorV(ControllingPrim.PhysBody, BSParam.VehicleAngularFactor);

                // Vehicles report collision events so we know when it's on the ground
                // _physicsScene.PE.AddToCollisionFlags(ControllingPrim.PhysBody, CollisionFlags.BS_VEHICLE_COLLISIONS);
                ControllingPrim.Linkset.AddToPhysicalCollisionFlags(CollisionFlags.BS_VEHICLE_COLLISIONS);

                // Vector3 inertia = _physicsScene.PE.CalculateLocalInertia(ControllingPrim.PhysShape.physShapeInfo, _vehicleMass);
                // ControllingPrim.Inertia = inertia * BSParam.VehicleInertiaFactor;
                // _physicsScene.PE.SetMassProps(ControllingPrim.PhysBody, _vehicleMass, ControllingPrim.Inertia);
                // _physicsScene.PE.UpdateInertiaTensor(ControllingPrim.PhysBody);
                ControllingPrim.Linkset.ComputeAndSetLocalInertia(BSParam.VehicleInertiaFactor, _vehicleMass);

                // Set the gravity for the vehicle depending on the buoyancy
                // TODO: what should be done if prim and vehicle buoyancy differ?
                _VehicleGravity = ControllingPrim.ComputeGravity(_VehicleBuoyancy);
                // The actual vehicle gravity is set to zero in Bullet so we can do all the application of same.
                // _physicsScene.PE.SetGravity(ControllingPrim.PhysBody, Vector3.Zero);
                ControllingPrim.Linkset.SetPhysicalGravity(Vector3.Zero);

                VDetailLog("{0},BSDynamics.SetPhysicalParameters,mass={1},inert={2},vehGrav={3},aDamp={4},frict={5},rest={6},lFact={7},aFact={8}",
                        ControllingPrim.LocalID, _vehicleMass, ControllingPrim.Inertia, _VehicleGravity,
                        BSParam.VehicleAngularDamping, BSParam.VehicleFriction, BSParam.VehicleRestitution,
                        BSParam.VehicleLinearFactor, BSParam.VehicleAngularFactor
                        );
            }
            else
            {
                if (ControllingPrim.PhysBody.HasPhysicalBody)
                    _physicsScene.PE.RemoveFromCollisionFlags(ControllingPrim.PhysBody, CollisionFlags.BS_VEHICLE_COLLISIONS);
                // ControllingPrim.Linkset.RemoveFromPhysicalCollisionFlags(CollisionFlags.BS_VEHICLE_COLLISIONS);
            }
        }

        // BSActor.RemoveBodyDependencies
        public override void RemoveDependencies()
        {
            Refresh();
        }

        // BSActor.Release()
        public override void Dispose()
        {
            VDetailLog("{0},Dispose", ControllingPrim.LocalID);
            UnregisterForSceneEvents();
            Type = Vehicle.TYPE_NONE;
            Enabled = false;
            return;
        }

        private void RegisterForSceneEvents()
        {
            if (!_haveRegisteredForSceneEvents)
            {
                _physicsScene.BeforeStep += this.Step;
                _physicsScene.AfterStep += this.PostStep;
                ControllingPrim.OnPreUpdateProperty += this.PreUpdateProperty;
                _haveRegisteredForSceneEvents = true;
            }
        }

        private void UnregisterForSceneEvents()
        {
            if (_haveRegisteredForSceneEvents)
            {
                _physicsScene.BeforeStep -= this.Step;
                _physicsScene.AfterStep -= this.PostStep;
                ControllingPrim.OnPreUpdateProperty -= this.PreUpdateProperty;
                _haveRegisteredForSceneEvents = false;
            }
        }

        private void PreUpdateProperty(ref EntityProperties entprop)
        {
            // A temporary kludge to suppress the rotational effects introduced on vehicles by Bullet
            // TODO: handle physics introduced by Bullet with computed vehicle physics.
            if (IsActive)
            {
                entprop.RotationalVelocity = Vector3.Zero;
            }
        }

        #region Known vehicle value functions
        // Vehicle physical parameters that we buffer from constant getting and setting.
        // The "_known*" values are unknown until they are fetched and the _knownHas flag is set.
        //      Changing is remembered and the parameter is stored back into the physics engine only if updated.
        //      This does two things: 1) saves continuious calls into unmanaged code, and
        //      2) signals when a physics property update must happen back to the simulator
        //      to update values modified for the vehicle.
        private int _knownChanged;
        private int _knownHas;
        private float _knownTerrainHeight;
        private float _knownWaterLevel;
        private Vector3 _knownPosition;
        private Vector3 _knownVelocity;
        private Vector3 _knownForce;
        private Vector3 _knownForceImpulse;
        private Quaternion _knownOrientation;
        private Vector3 _knownRotationalVelocity;
        private Vector3 _knownRotationalForce;
        private Vector3 _knownRotationalImpulse;

        private const int _knownChangedPosition           = 1 << 0;
        private const int _knownChangedVelocity           = 1 << 1;
        private const int _knownChangedForce              = 1 << 2;
        private const int _knownChangedForceImpulse       = 1 << 3;
        private const int _knownChangedOrientation        = 1 << 4;
        private const int _knownChangedRotationalVelocity = 1 << 5;
        private const int _knownChangedRotationalForce    = 1 << 6;
        private const int _knownChangedRotationalImpulse  = 1 << 7;
        private const int _knownChangedTerrainHeight      = 1 << 8;
        private const int _knownChangedWaterLevel         = 1 << 9;

        public void ForgetKnownVehicleProperties()
        {
            _knownHas = 0;
            _knownChanged = 0;
        }
        // Push all the changed values back into the physics engine
        public void PushKnownChanged()
        {
            if (_knownChanged != 0)
            {
                if ((_knownChanged & _knownChangedPosition) != 0)
                    ControllingPrim.ForcePosition = _knownPosition;

                if ((_knownChanged & _knownChangedOrientation) != 0)
                    ControllingPrim.ForceOrientation = _knownOrientation;

                if ((_knownChanged & _knownChangedVelocity) != 0)
                {
                    ControllingPrim.ForceVelocity = _knownVelocity;
                    // Fake out Bullet by making it think the velocity is the same as last time.
                    // Bullet does a bunch of smoothing for changing parameters.
                    //    Since the vehicle is demanding this setting, we override Bullet's smoothing
                    //    by telling Bullet the value was the same last time.
                    // PhysicsScene.PE.SetInterpolationLinearVelocity(Prim.PhysBody, _knownVelocity);
                }

                if ((_knownChanged & _knownChangedForce) != 0)
                    ControllingPrim.AddForce(false /* inTaintTime */, (Vector3)_knownForce);

                if ((_knownChanged & _knownChangedForceImpulse) != 0)
                    ControllingPrim.AddForceImpulse((Vector3)_knownForceImpulse, false /*pushforce*/, true /*inTaintTime*/);

                if ((_knownChanged & _knownChangedRotationalVelocity) != 0)
                {
                    ControllingPrim.ForceRotationalVelocity = _knownRotationalVelocity;
                    // PhysicsScene.PE.SetInterpolationAngularVelocity(Prim.PhysBody, _knownRotationalVelocity);
                }

                if ((_knownChanged & _knownChangedRotationalImpulse) != 0)
                    ControllingPrim.ApplyTorqueImpulse((Vector3)_knownRotationalImpulse, true /*inTaintTime*/);

                if ((_knownChanged & _knownChangedRotationalForce) != 0)
                {
                    ControllingPrim.AddAngularForce(true /* inTaintTime */, (Vector3)_knownRotationalForce);
                }

                // If we set one of the values (ie, the physics engine didn't do it) we must force
                //      an UpdateProperties event to send the changes up to the simulator.
                _physicsScene.PE.PushUpdate(ControllingPrim.PhysBody);
            }
            _knownChanged = 0;
        }

        // Since the computation of terrain height can be a little involved, this routine
        //    is used to fetch the height only once for each vehicle simulation step.
        Vector3 lastRememberedHeightPos = new Vector3(-1, -1, -1);
        private float GetTerrainHeight(Vector3 pos)
        {
            if ((_knownHas & _knownChangedTerrainHeight) == 0 || pos != lastRememberedHeightPos)
            {
                lastRememberedHeightPos = pos;
                _knownTerrainHeight = ControllingPrim.PhysScene.TerrainManager.GetTerrainHeightAtXYZ(pos);
                _knownHas |= _knownChangedTerrainHeight;
            }
            return _knownTerrainHeight;
        }

        // Since the computation of water level can be a little involved, this routine
        //    is used ot fetch the level only once for each vehicle simulation step.
        Vector3 lastRememberedWaterHeightPos = new Vector3(-1, -1, -1);
        private float GetWaterLevel(Vector3 pos)
        {
            if ((_knownHas & _knownChangedWaterLevel) == 0 || pos != lastRememberedWaterHeightPos)
            {
                lastRememberedWaterHeightPos = pos;
                _knownWaterLevel = ControllingPrim.PhysScene.TerrainManager.GetWaterLevelAtXYZ(pos);
                _knownHas |= _knownChangedWaterLevel;
            }
            return _knownWaterLevel;
        }

        private Vector3 VehiclePosition
        {
            get
            {
                if ((_knownHas & _knownChangedPosition) == 0)
                {
                    _knownPosition = ControllingPrim.ForcePosition;
                    _knownHas |= _knownChangedPosition;
                }
                return _knownPosition;
            }
            set
            {
                _knownPosition = value;
                _knownChanged |= _knownChangedPosition;
                _knownHas |= _knownChangedPosition;
            }
        }

        private Quaternion VehicleOrientation
        {
            get
            {
                if ((_knownHas & _knownChangedOrientation) == 0)
                {
                    _knownOrientation = ControllingPrim.ForceOrientation;
                    _knownHas |= _knownChangedOrientation;
                }
                return _knownOrientation;
            }
            set
            {
                _knownOrientation = value;
                _knownChanged |= _knownChangedOrientation;
                _knownHas |= _knownChangedOrientation;
            }
        }

        private Vector3 VehicleVelocity
        {
            get
            {
                if ((_knownHas & _knownChangedVelocity) == 0)
                {
                    _knownVelocity = ControllingPrim.ForceVelocity;
                    _knownHas |= _knownChangedVelocity;
                }
                return _knownVelocity;
            }
            set
            {
                _knownVelocity = value;
                _knownChanged |= _knownChangedVelocity;
                _knownHas |= _knownChangedVelocity;
            }
        }

        private void VehicleAddForce(Vector3 pForce)
        {
            if ((_knownHas & _knownChangedForce) == 0)
            {
                _knownForce = Vector3.Zero;
                _knownHas |= _knownChangedForce;
            }
            _knownForce += pForce;
            _knownChanged |= _knownChangedForce;
        }

        private void VehicleAddForceImpulse(Vector3 pImpulse)
        {
            if ((_knownHas & _knownChangedForceImpulse) == 0)
            {
                _knownForceImpulse = Vector3.Zero;
                _knownHas |= _knownChangedForceImpulse;
            }
            _knownForceImpulse += pImpulse;
            _knownChanged |= _knownChangedForceImpulse;
        }

        private Vector3 VehicleRotationalVelocity
        {
            get
            {
                if ((_knownHas & _knownChangedRotationalVelocity) == 0)
                {
                    _knownRotationalVelocity = ControllingPrim.ForceRotationalVelocity;
                    _knownHas |= _knownChangedRotationalVelocity;
                }
                return (Vector3)_knownRotationalVelocity;
            }
            set
            {
                _knownRotationalVelocity = value;
                _knownChanged |= _knownChangedRotationalVelocity;
                _knownHas |= _knownChangedRotationalVelocity;
            }
        }
        private void VehicleAddAngularForce(Vector3 aForce)
        {
            if ((_knownHas & _knownChangedRotationalForce) == 0)
            {
                _knownRotationalForce = Vector3.Zero;
            }
            _knownRotationalForce += aForce;
            _knownChanged |= _knownChangedRotationalForce;
            _knownHas |= _knownChangedRotationalForce;
        }
        private void VehicleAddRotationalImpulse(Vector3 pImpulse)
        {
            if ((_knownHas & _knownChangedRotationalImpulse) == 0)
            {
                _knownRotationalImpulse = Vector3.Zero;
                _knownHas |= _knownChangedRotationalImpulse;
            }
            _knownRotationalImpulse += pImpulse;
            _knownChanged |= _knownChangedRotationalImpulse;
        }

        // Vehicle relative forward velocity
        private Vector3 VehicleForwardVelocity => VehicleVelocity * Quaternion.Inverse(Quaternion.Normalize(VehicleFrameOrientation));

        private float VehicleForwardSpeed => VehicleForwardVelocity.X;

        private Quaternion VehicleFrameOrientation => VehicleOrientation * _referenceFrame;

        #endregion // Known vehicle value functions

        // One step of the vehicle properties for the next 'pTimestep' seconds.
        internal void Step(float pTimestep)
        {
            if (!IsActive) return;

            ForgetKnownVehicleProperties();

            MoveLinear(pTimestep);
            MoveAngular(pTimestep);

            LimitRotation(pTimestep);

            // remember the position so next step we can limit absolute movement effects
            _lastPositionVector = VehiclePosition;

            // If we forced the changing of some vehicle parameters, update the values and
            //      for the physics engine to note the changes so an UpdateProperties event will happen.
            PushKnownChanged();

            if (_physicsScene.VehiclePhysicalLoggingEnabled)
                _physicsScene.PE.DumpRigidBody(_physicsScene.World, ControllingPrim.PhysBody);

            VDetailLog("{0},BSDynamics.Step,done,pos={1}, force={2},velocity={3},angvel={4}",
                    ControllingPrim.LocalID, VehiclePosition, _knownForce, VehicleVelocity, VehicleRotationalVelocity);
        }

        // Called after the simulation step
        internal void PostStep(float pTimestep)
        {
            if (!IsActive) return;

            if (_physicsScene.VehiclePhysicalLoggingEnabled)
                _physicsScene.PE.DumpRigidBody(_physicsScene.World, ControllingPrim.PhysBody);
        }

        // Apply the effect of the linear motor and other linear motions (like hover and float).
        private void MoveLinear(float pTimestep)
        {
            ComputeLinearVelocity(pTimestep);

            ComputeLinearDeflection(pTimestep);

            ComputeLinearTerrainHeightCorrection(pTimestep);

            ComputeLinearHover(pTimestep);

            ComputeLinearBlockingEndPoint(pTimestep);

            ComputeLinearMotorUp(pTimestep);

            ApplyGravity(pTimestep);

            // If not changing some axis, reduce out velocity
            if ((_flags & (VehicleFlag.NO_X | VehicleFlag.NO_Y | VehicleFlag.NO_Z)) != 0)
            {
                Vector3 vel = VehicleVelocity;
                if ((_flags & VehicleFlag.NO_X) != 0)
                {
                    vel.X = 0;
                }
                if ((_flags & VehicleFlag.NO_Y) != 0)
                {
                    vel.Y = 0;
                }
                if ((_flags & VehicleFlag.NO_Z) != 0)
                {
                    vel.Z = 0;
                }
                VehicleVelocity = vel;
            }

            // ==================================================================
            // Clamp high or low velocities
            float newVelocityLengthSq = VehicleVelocity.LengthSquared();
            if (newVelocityLengthSq > BSParam.VehicleMaxLinearVelocitySquared)
            {
                Vector3 origVelW = VehicleVelocity;         // DEBUG DEBUG
                VehicleVelocity /= VehicleVelocity.Length();
                VehicleVelocity *= BSParam.VehicleMaxLinearVelocity;
                VDetailLog("{0},  MoveLinear,clampMax,origVelW={1},lenSq={2},maxVelSq={3},,newVelW={4}",
                            ControllingPrim.LocalID, origVelW, newVelocityLengthSq, BSParam.VehicleMaxLinearVelocitySquared, VehicleVelocity);
            }
            else if (newVelocityLengthSq < BSParam.VehicleMinLinearVelocitySquared)
            {
                Vector3 origVelW = VehicleVelocity;         // DEBUG DEBUG
                VDetailLog("{0},  MoveLinear,clampMin,origVelW={1},lenSq={2}",
                            ControllingPrim.LocalID, origVelW, newVelocityLengthSq);
                VehicleVelocity = Vector3.Zero;
            }

            VDetailLog("{0},  MoveLinear,done,isColl={1},newVel={2}", ControllingPrim.LocalID, ControllingPrim.HasSomeCollision, VehicleVelocity );

        } // end MoveLinear()

        public void ComputeLinearVelocity(float pTimestep)
        {
            // Step the motor from the current value. Get the correction needed this step.
            Vector3 origVelW = VehicleVelocity;             // DEBUG
            Vector3 currentVelV = VehicleForwardVelocity;
            Vector3 linearMotorCorrectionV = _linearMotor.Step(pTimestep, currentVelV);

            // Friction reduces vehicle motion based on absolute speed. Slow vehicle down by friction.
            Vector3 frictionFactorV = ComputeFrictionFactor(_linearFrictionTimescale, pTimestep);
            linearMotorCorrectionV -= currentVelV * frictionFactorV;

            // Motor is vehicle coordinates. Rotate it to world coordinates
            Vector3 linearMotorVelocityW = linearMotorCorrectionV * VehicleFrameOrientation;

            // If we're a ground vehicle, don't add any upward Z movement
            if ((_flags & VehicleFlag.LIMIT_MOTOR_UP) != 0)
            {
                if (linearMotorVelocityW.Z > 0f)
                    linearMotorVelocityW.Z = 0f;
            }

            // Add this correction to the velocity to make it faster/slower.
            VehicleVelocity += linearMotorVelocityW;

            VDetailLog("{0},  MoveLinear,velocity,origVelW={1},velV={2},tgt={3},correctV={4},correctW={5},newVelW={6},fricFact={7}",
                        ControllingPrim.LocalID, origVelW, currentVelV, _linearMotor.TargetValue, linearMotorCorrectionV,
                        linearMotorVelocityW, VehicleVelocity, frictionFactorV);
        }

        //Given a Deflection Effiency and a Velocity, Returns a Velocity that is Partially Deflected onto the X Axis
        //Clamped so that a DeflectionTimescale of less then 1 does not increase force over original velocity
        private void ComputeLinearDeflection(float pTimestep)
        {
            Vector3 linearDeflectionV = Vector3.Zero;
            Vector3 velocityV = VehicleForwardVelocity;

            if (BSParam.VehicleEnableLinearDeflection)
            {
                // Velocity in Y and Z dimensions is movement to the side or turning.
                // Compute deflection factor from the to the side and rotational velocity
                linearDeflectionV.Y = SortedClampInRange(0, velocityV.Y * _linearDeflectionEfficiency / _linearDeflectionTimescale, velocityV.Y);
                linearDeflectionV.Z = SortedClampInRange(0, velocityV.Z * _linearDeflectionEfficiency / _linearDeflectionTimescale, velocityV.Z);

                // Velocity to the side and around is corrected and moved into the forward direction
                linearDeflectionV.X += Math.Abs(linearDeflectionV.Y);
                linearDeflectionV.X += Math.Abs(linearDeflectionV.Z);

                // Scale the deflection to the fractional simulation time
                linearDeflectionV *= pTimestep;

                // Subtract the sideways and rotational velocity deflection factors while adding the correction forward
                linearDeflectionV *= new Vector3(1, -1, -1);

                // Correction is vehicle relative. Convert to world coordinates.
                Vector3 linearDeflectionW = linearDeflectionV * VehicleFrameOrientation;

                // Optionally, if not colliding, don't effect world downward velocity. Let falling things fall.
                if (BSParam.VehicleLinearDeflectionNotCollidingNoZ && !_controllingPrim.HasSomeCollision)
                {
                    linearDeflectionW.Z = 0f;
                }

                VehicleVelocity += linearDeflectionW;

                VDetailLog("{0},  MoveLinear,LinearDeflection,linDefEff={1},linDefTS={2},linDeflectionV={3}",
                            ControllingPrim.LocalID, _linearDeflectionEfficiency, _linearDeflectionTimescale, linearDeflectionV);
            }
        }

        public void ComputeLinearTerrainHeightCorrection(float pTimestep)
        {
            // If below the terrain, move us above the ground a little.
            // TODO: Consider taking the rotated size of the object or possibly casting a ray.
            if (VehiclePosition.Z < GetTerrainHeight(VehiclePosition))
            {
                // Force position because applying force won't get the vehicle through the terrain
                Vector3 newPosition = VehiclePosition;
                newPosition.Z = GetTerrainHeight(VehiclePosition) + 1f;
                VehiclePosition = newPosition;
                VDetailLog("{0},  MoveLinear,terrainHeight,terrainHeight={1},pos={2}",
                        ControllingPrim.LocalID, GetTerrainHeight(VehiclePosition), VehiclePosition);
            }
        }

        public void ComputeLinearHover(float pTimestep)
        {
            // _VhoverEfficiency: 0=bouncy, 1=totally damped
            // _VhoverTimescale: time to achieve height
            if ((_flags & (VehicleFlag.HOVER_WATER_ONLY | VehicleFlag.HOVER_TERRAIN_ONLY | VehicleFlag.HOVER_GLOBAL_HEIGHT)) != 0 && _VhoverHeight > 0 && _VhoverTimescale < 300)
            {
                // We should hover, get the target height
                if ((_flags & VehicleFlag.HOVER_WATER_ONLY) != 0)
                {
                    _VhoverTargetHeight = GetWaterLevel(VehiclePosition) + _VhoverHeight;
                }
                if ((_flags & VehicleFlag.HOVER_TERRAIN_ONLY) != 0)
                {
                    _VhoverTargetHeight = GetTerrainHeight(VehiclePosition) + _VhoverHeight;
                }
                if ((_flags & VehicleFlag.HOVER_GLOBAL_HEIGHT) != 0)
                {
                    _VhoverTargetHeight = _VhoverHeight;
                }
                if ((_flags & VehicleFlag.HOVER_UP_ONLY) != 0)
                {
                    // If body is already heigher, use its height as target height
                    if (VehiclePosition.Z > _VhoverTargetHeight)
                    {
                        _VhoverTargetHeight = VehiclePosition.Z;

                        // A 'misfeature' of this flag is that if the vehicle is above it's hover height,
                        //     the vehicle's buoyancy goes away. This is an SL bug that got used by so many
                        //    scripts that it could not be changed.
                        // So, if above the height, reapply gravity if buoyancy had it turned off.
                        if (_VehicleBuoyancy != 0)
                        {
                            Vector3 appliedGravity = ControllingPrim.ComputeGravity(ControllingPrim.Buoyancy) * _vehicleMass;
                            VehicleAddForce(appliedGravity);
                        }
                    }
                }

                if ((_flags & VehicleFlag.LOCK_HOVER_HEIGHT) != 0)
                {
                    if (Math.Abs(VehiclePosition.Z - _VhoverTargetHeight) > 0.2f)
                    {
                        Vector3 pos = VehiclePosition;
                        pos.Z = _VhoverTargetHeight;
                        VehiclePosition = pos;

                        VDetailLog("{0},  MoveLinear,hover,pos={1},lockHoverHeight", ControllingPrim.LocalID, pos);
                    }
                }
                else
                {
                    // Error is positive if below the target and negative if above.
                    Vector3 hpos = VehiclePosition;
                    float verticalError = _VhoverTargetHeight - hpos.Z;
                    float verticalCorrection = verticalError / _VhoverTimescale;
                    verticalCorrection *= _VhoverEfficiency;

                    hpos.Z += verticalCorrection;
                    VehiclePosition = hpos;

                    // Since we are hovering, we need to do the opposite of falling -- get rid of world Z
                    Vector3 vel = VehicleVelocity;
                    vel.Z = 0f;
                    VehicleVelocity = vel;

                    /*
                    float verticalCorrectionVelocity = verticalError / _VhoverTimescale;
                    Vector3 verticalCorrection = new Vector3(0f, 0f, verticalCorrectionVelocity);
                    verticalCorrection *= _vehicleMass;

                    // TODO: implement _VhoverEfficiency correctly
                    VehicleAddForceImpulse(verticalCorrection);
                     */

                    VDetailLog("{0},  MoveLinear,hover,pos={1},eff={2},hoverTS={3},height={4},target={5},err={6},corr={7}",
                                    ControllingPrim.LocalID, VehiclePosition, _VhoverEfficiency,
                                    _VhoverTimescale, _VhoverHeight, _VhoverTargetHeight,
                                    verticalError, verticalCorrection);
                }
            }
        }

        public bool ComputeLinearBlockingEndPoint(float pTimestep)
        {
            bool changed = false;

            Vector3 pos = VehiclePosition;
            Vector3 posChange = pos - _lastPositionVector;
            if (_BlockingEndPoint != Vector3.Zero)
            {
                if (pos.X >= _BlockingEndPoint.X - (float)1)
                {
                    pos.X -= posChange.X + 1;
                    changed = true;
                }
                if (pos.Y >= _BlockingEndPoint.Y - (float)1)
                {
                    pos.Y -= posChange.Y + 1;
                    changed = true;
                }
                if (pos.Z >= _BlockingEndPoint.Z - (float)1)
                {
                    pos.Z -= posChange.Z + 1;
                    changed = true;
                }
                if (pos.X <= 0)
                {
                    pos.X += posChange.X + 1;
                    changed = true;
                }
                if (pos.Y <= 0)
                {
                    pos.Y += posChange.Y + 1;
                    changed = true;
                }
                if (changed)
                {
                    VehiclePosition = pos;
                    VDetailLog("{0},  MoveLinear,blockingEndPoint,block={1},origPos={2},pos={3}",
                                ControllingPrim.LocalID, _BlockingEndPoint, posChange, pos);
                }
            }
            return changed;
        }

        // From http://wiki.secondlife.com/wiki/LlSetVehicleFlags :
        //    Prevent ground vehicles from motoring into the sky. This flag has a subtle effect when
        //    used with conjunction with banking: the strength of the banking will decay when the
        //    vehicle no longer experiences collisions. The decay timescale is the same as
        //    VEHICLE_BANKING_TIMESCALE. This is to help prevent ground vehicles from steering
        //    when they are in mid jump.
        // TODO: this code is wrong. Also, what should it do for boats (height from water)?
        //    This is just using the ground and a general collision check. Should really be using
        //    a downward raycast to find what is below.
        public void ComputeLinearMotorUp(float pTimestep)
        {
            if ((_flags & VehicleFlag.LIMIT_MOTOR_UP) != 0)
            {
                // This code tries to decide if the object is not on the ground and then pushing down
                /*
                float targetHeight = Type == Vehicle.TYPE_BOAT ? GetWaterLevel(VehiclePosition) : GetTerrainHeight(VehiclePosition);
                distanceAboveGround = VehiclePosition.Z - targetHeight;
                // Not colliding if the vehicle is off the ground
                if (!Prim.HasSomeCollision)
                {
                    // downForce = new Vector3(0, 0, -distanceAboveGround / _bankingTimescale);
                    VehicleVelocity += new Vector3(0, 0, -distanceAboveGround);
                }
                // TODO: this calculation is wrong. From the description at
                //     (http://wiki.secondlife.com/wiki/Category:LSL_Vehicle), the downForce
                //     has a decay factor. This says this force should
                //     be computed with a motor.
                // TODO: add interaction with banking.
                VDetailLog("{0},  MoveLinear,limitMotorUp,distAbove={1},colliding={2},ret={3}",
                                Prim.LocalID, distanceAboveGround, Prim.HasSomeCollision, ret);
                 */

                // Another approach is to measure if we're going up. If going up and not colliding,
                //     the vehicle is in the air.  Fix that by pushing down.
                if (!ControllingPrim.HasSomeCollision && VehicleVelocity.Z > 0.1)
                {
                    // Get rid of any of the velocity vector that is pushing us up.
                    float upVelocity = VehicleVelocity.Z;
                    VehicleVelocity += new Vector3(0, 0, -upVelocity);

                    /*
                    // If we're pointed up into the air, we should nose down
                    Vector3 pointingDirection = Vector3.UnitX * VehicleOrientation;
                    // The rotation around the Y axis is pitch up or down
                    if (pointingDirection.Y > 0.01f)
                    {
                        float angularCorrectionForce = -(float)Math.Asin(pointingDirection.Y);
                        Vector3 angularCorrectionVector = new Vector3(0f, angularCorrectionForce, 0f);
                        // Rotate into world coordinates and apply to vehicle
                        angularCorrectionVector *= VehicleOrientation;
                        VehicleAddAngularForce(angularCorrectionVector);
                        VDetailLog("{0},  MoveLinear,limitMotorUp,newVel={1},pntDir={2},corrFrc={3},aCorr={4}",
                                    Prim.LocalID, VehicleVelocity, pointingDirection, angularCorrectionForce, angularCorrectionVector);
                    }
                        */
                    VDetailLog("{0},  MoveLinear,limitMotorUp,collide={1},upVel={2},newVel={3}",
                                    ControllingPrim.LocalID, ControllingPrim.HasSomeCollision, upVelocity, VehicleVelocity);
                }
            }
        }

        private void ApplyGravity(float pTimeStep)
        {
            Vector3 appliedGravity = _VehicleGravity * _vehicleMass;

            // Hack to reduce downward force if the vehicle is probably sitting on the ground
            if (ControllingPrim.HasSomeCollision && IsGroundVehicle)
                appliedGravity *= BSParam.VehicleGroundGravityFudge;

            VehicleAddForce(appliedGravity);

            VDetailLog("{0},  MoveLinear,applyGravity,vehGrav={1},collid={2},fudge={3},mass={4},appliedForce={5}",
                            ControllingPrim.LocalID, _VehicleGravity,
                            ControllingPrim.HasSomeCollision, BSParam.VehicleGroundGravityFudge, _vehicleMass, appliedGravity);
        }

        // =======================================================================
        // =======================================================================
        // Apply the effect of the angular motor.
        // The 'contribution' is how much angular correction velocity each function wants.
        //     All the contributions are added together and the resulting velocity is
        //     set directly on the vehicle.
        private void MoveAngular(float pTimestep)
        {
            ComputeAngularTurning(pTimestep);

            ComputeAngularVerticalAttraction();

            ComputeAngularDeflection();

            ComputeAngularBanking();

            // ==================================================================
            if (VehicleRotationalVelocity.ApproxEquals(Vector3.Zero, 0.0001f))
            {
                // The vehicle is not adding anything angular wise.
                VehicleRotationalVelocity = Vector3.Zero;
                VDetailLog("{0},  MoveAngular,done,zero", ControllingPrim.LocalID);
            }
            else
            {
                VDetailLog("{0},  MoveAngular,done,nonZero,angVel={1}", ControllingPrim.LocalID, VehicleRotationalVelocity);
            }

            // ==================================================================
            //Offset section
            if (_linearMotorOffset != Vector3.Zero)
            {
                //Offset of linear velocity doesn't change the linear velocity,
                //   but causes a torque to be applied, for example...
                //
                //      IIIII     >>>   IIIII
                //      IIIII     >>>    IIIII
                //      IIIII     >>>     IIIII
                //          ^
                //          |  Applying a force at the arrow will cause the object to move forward, but also rotate
                //
                //
                // The torque created is the linear velocity crossed with the offset

                // TODO: this computation should be in the linear section
                //    because that is where we know the impulse being applied.
                Vector3 torqueFromOffset = Vector3.Zero;
                // torqueFromOffset = Vector3.Cross(_linearMotorOffset, appliedImpulse);
                if (float.IsNaN(torqueFromOffset.X))
                    torqueFromOffset.X = 0;
                if (float.IsNaN(torqueFromOffset.Y))
                    torqueFromOffset.Y = 0;
                if (float.IsNaN(torqueFromOffset.Z))
                    torqueFromOffset.Z = 0;

                VehicleAddAngularForce(torqueFromOffset * _vehicleMass);
                VDetailLog("{0},  BSDynamic.MoveAngular,motorOffset,applyTorqueImpulse={1}", ControllingPrim.LocalID, torqueFromOffset);
            }

        }

        private void ComputeAngularTurning(float pTimestep)
        {
            // The user wants this many radians per second angular change?
            Vector3 origVehicleRotationalVelocity = VehicleRotationalVelocity;      // DEBUG DEBUG
            Vector3 currentAngularV = VehicleRotationalVelocity * Quaternion.Inverse(VehicleFrameOrientation);
            Vector3 angularMotorContributionV = _angularMotor.Step(pTimestep, currentAngularV);

            // ==================================================================
            // From http://wiki.secondlife.com/wiki/LlSetVehicleFlags :
            //    This flag prevents linear deflection parallel to world z-axis. This is useful
            //    for preventing ground vehicles with large linear deflection, like bumper cars,
            //    from climbing their linear deflection into the sky.
            // That is, NO_DEFLECTION_UP says angular motion should not add any pitch or roll movement
            // TODO: This is here because this is where ODE put it but documentation says it
            //    is a linear effect. Where should this check go?
            //if ((_flags & (VehicleFlag.NO_DEFLECTION_UP)) != 0)
            // {
            //    angularMotorContributionV.X = 0f;
            //    angularMotorContributionV.Y = 0f;
            //  }

            // Reduce any velocity by friction.
            Vector3 frictionFactorW = ComputeFrictionFactor(_angularFrictionTimescale, pTimestep);
            angularMotorContributionV -= currentAngularV * frictionFactorW;

            Vector3 angularMotorContributionW = angularMotorContributionV * VehicleFrameOrientation;
            VehicleRotationalVelocity += angularMotorContributionW;

            VDetailLog("{0},  MoveAngular,angularTurning,curAngVelV={1},origVehRotVel={2},vehRotVel={3},frictFact={4}, angContribV={5},angContribW={6}",
                        ControllingPrim.LocalID, currentAngularV, origVehicleRotationalVelocity, VehicleRotationalVelocity, frictionFactorW, angularMotorContributionV, angularMotorContributionW);
        }

        // From http://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial:
        //      Some vehicles, like boats, should always keep their up-side up. This can be done by
        //      enabling the "vertical attractor" behavior that springs the vehicle's local z-axis to
        //      the world z-axis (a.k.a. "up"). To take advantage of this feature you would set the
        //      VEHICLE_VERTICAL_ATTRACTION_TIMESCALE to control the period of the spring frequency,
        //      and then set the VEHICLE_VERTICAL_ATTRACTION_EFFICIENCY to control the damping. An
        //      efficiency of 0.0 will cause the spring to wobble around its equilibrium, while an
        //      efficiency of 1.0 will cause the spring to reach its equilibrium with exponential decay.
        public void ComputeAngularVerticalAttraction()
        {

            // If vertical attaction timescale is reasonable
            if (BSParam.VehicleEnableAngularVerticalAttraction && _verticalAttractionTimescale < _verticalAttractionCutoff)
            {
                Vector3 vehicleUpAxis = Vector3.UnitZ * VehicleFrameOrientation;
                switch (BSParam.VehicleAngularVerticalAttractionAlgorithm)
                {
                    case 0:
                        {
                            //Another formula to try got from :
                            //http://answers.unity3d.com/questions/10425/how-to-stabilize-angular-motion-alignment-of-hover.html

                            // Flipping what was originally a timescale into a speed variable and then multiplying it by 2
                            //    since only computing half the distance between the angles.
                            float verticalAttractionSpeed = 1 / _verticalAttractionTimescale * 2.0f;

                            // Make a prediction of where the up axis will be when this is applied rather then where it is now as
                            //     this makes for a smoother adjustment and less fighting between the various forces.
                            Vector3 predictedUp = vehicleUpAxis * Quaternion.CreateFromAxisAngle(VehicleRotationalVelocity, 0f);

                            // This is only half the distance to the target so it will take 2 seconds to complete the turn.
                            Vector3 torqueVector = Vector3.Cross(predictedUp, Vector3.UnitZ);

                            if ((_flags & VehicleFlag.LIMIT_ROLL_ONLY) != 0)
                            {
                                Vector3 vehicleForwardAxis = Vector3.UnitX * VehicleFrameOrientation;
                                torqueVector = ProjectVector(torqueVector, vehicleForwardAxis);
                            }

                            // Scale vector by our timescale since it is an acceleration it is r/s^2 or radians a timescale squared
                            Vector3 vertContributionV = torqueVector * verticalAttractionSpeed * verticalAttractionSpeed;

                            VehicleRotationalVelocity += vertContributionV;

                            VDetailLog("{0},  MoveAngular,verticalAttraction,vertAttrSpeed={1},upAxis={2},PredictedUp={3},torqueVector={4},contrib={5}",
                                            ControllingPrim.LocalID,
                                            verticalAttractionSpeed,
                                            vehicleUpAxis,
                                            predictedUp,
                                            torqueVector,
                                            vertContributionV);
                            break;
                        }
                    case 1:
                        {
                            // Possible solution derived from a discussion at:
                            // http://stackoverflow.com/questions/14939657/computing-vector-from-quaternion-works-computing-quaternion-from-vector-does-no

                            // Create a rotation that is only the vehicle's rotation around Z
                            Vector3 currentEulerW = Vector3.Zero;
                            VehicleFrameOrientation.GetEulerAngles(out currentEulerW.X, out currentEulerW.Y, out currentEulerW.Z);
                            Quaternion justZOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, currentEulerW.Z);

                            // Create the axis that is perpendicular to the up vector and the rotated up vector.
                            Vector3 differenceAxisW = Vector3.Cross(Vector3.UnitZ * justZOrientation, Vector3.UnitZ * VehicleFrameOrientation);
                            // Compute the angle between those to vectors.
                            double differenceAngle = Math.Acos((double)Vector3.Dot(Vector3.UnitZ, Vector3.Normalize(Vector3.UnitZ * VehicleFrameOrientation)));
                            // 'differenceAngle' is the angle to rotate and 'differenceAxis' is the plane to rotate in to get the vehicle vertical

                            // Reduce the change by the time period it is to change in. Timestep is handled when velocity is applied.
                            // TODO: add 'efficiency'.
                            // differenceAngle /= _verticalAttractionTimescale;

                            // Create the quaterian representing the correction angle
                            Quaternion correctionRotationW = Quaternion.CreateFromAxisAngle(differenceAxisW, (float)differenceAngle);

                            // Turn that quaternion into Euler values to make it into velocities to apply.
                            Vector3 vertContributionW = Vector3.Zero;
                            correctionRotationW.GetEulerAngles(out vertContributionW.X, out vertContributionW.Y, out vertContributionW.Z);
                            vertContributionW *= -1f;
                            vertContributionW /= _verticalAttractionTimescale;

                            VehicleRotationalVelocity += vertContributionW;

                            VDetailLog("{0},  MoveAngular,verticalAttraction,upAxis={1},diffAxis={2},diffAng={3},corrRot={4},contrib={5}",
                                            ControllingPrim.LocalID,
                                            vehicleUpAxis,
                                            differenceAxisW,
                                            differenceAngle,
                                            correctionRotationW,
                                            vertContributionW);
                            break;
                        }
                    case 2:
                        {
                            Vector3 vertContributionV = Vector3.Zero;
                            Vector3 origRotVelW = VehicleRotationalVelocity;        // DEBUG DEBUG

                            // Take a vector pointing up and convert it from world to vehicle relative coords.
                            Vector3 verticalError = Vector3.Normalize(Vector3.UnitZ * VehicleFrameOrientation);

                            // If vertical attraction correction is needed, the vector that was pointing up (UnitZ)
                            //    is now:
                            //    leaning to one side: rotated around the X axis with the Y value going
                            //        from zero (nearly straight up) to one (completely to the side)) or
                            //    leaning front-to-back: rotated around the Y axis with the value of X being between
                            //         zero and one.
                            // The value of Z is how far the rotation is off with 1 meaning none and 0 being 90 degrees.

                            // Y error means needed rotation around X axis and visa versa.
                            // Since the error goes from zero to one, the asin is the corresponding angle.
                            vertContributionV.X = (float)Math.Asin(verticalError.Y);
                            // (Tilt forward (positive X) needs to tilt back (rotate negative) around Y axis.)
                            vertContributionV.Y = -(float)Math.Asin(verticalError.X);

                            // If verticalError.Z is negative, the vehicle is upside down. Add additional push.
                            if (verticalError.Z < 0f)
                            {
                                vertContributionV.X += Math.Sign(vertContributionV.X) * PIOverFour;
                                // vertContribution.Y -= PIOverFour;
                            }

                            // 'vertContrbution' is now the necessary angular correction to correct tilt in one second.
                            //     Correction happens over a number of seconds.
                            Vector3 unscaledContribVerticalErrorV = vertContributionV;     // DEBUG DEBUG

                            // The correction happens over the user's time period
                            vertContributionV /= _verticalAttractionTimescale;

                            // Rotate the vehicle rotation to the world coordinates.
                            VehicleRotationalVelocity += vertContributionV * VehicleFrameOrientation;

                            VDetailLog("{0},  MoveAngular,verticalAttraction,,upAxis={1},origRotVW={2},vertError={3},unscaledV={4},eff={5},ts={6},vertContribV={7}",
                                            ControllingPrim.LocalID,
                                            vehicleUpAxis,
                                            origRotVelW,
                                            verticalError,
                                            unscaledContribVerticalErrorV,
                                            _verticalAttractionEfficiency,
                                            _verticalAttractionTimescale,
                                            vertContributionV);
                            break;
                        }
                    default:
                        {
                            break;
                        }
                }
            }
        }

        // Angular correction to correct the direction the vehicle is pointing to be
        //      the direction is should want to be pointing.
        // The vehicle is moving in some direction and correct its orientation to it is pointing
        //     in that direction.
        // TODO: implement reference frame.
        public void ComputeAngularDeflection()
        {

            if (BSParam.VehicleEnableAngularDeflection && _angularDeflectionEfficiency != 0 && VehicleForwardSpeed > 0.2)
            {
                Vector3 deflectContributionV = Vector3.Zero;

                // The direction the vehicle is moving
                Vector3 movingDirection = VehicleVelocity;
                movingDirection.Normalize();

                // If the vehicle is going backward, it is still pointing forward
                movingDirection *= Math.Sign(VehicleForwardSpeed);

                // The direction the vehicle is pointing
                Vector3 pointingDirection = Vector3.UnitX * VehicleFrameOrientation;
                //Predict where the Vehicle will be pointing after AngularVelocity change is applied. This will keep
                //   from overshooting and allow this correction to merge with the Vertical Attraction peacefully.
                Vector3 predictedPointingDirection = pointingDirection * Quaternion.CreateFromAxisAngle(VehicleRotationalVelocity, 0f);
                predictedPointingDirection.Normalize();

                // The difference between what is and what should be.
               // Vector3 deflectionError = movingDirection - predictedPointingDirection;
                Vector3 deflectionError = Vector3.Cross(movingDirection, predictedPointingDirection);

                // Don't try to correct very large errors (not our job)
                // if (Math.Abs(deflectionError.X) > PIOverFour) deflectionError.X = PIOverTwo * Math.Sign(deflectionError.X);
                // if (Math.Abs(deflectionError.Y) > PIOverFour) deflectionError.Y = PIOverTwo * Math.Sign(deflectionError.Y);
                // if (Math.Abs(deflectionError.Z) > PIOverFour) deflectionError.Z = PIOverTwo * Math.Sign(deflectionError.Z);
                if (Math.Abs(deflectionError.X) > PIOverFour) deflectionError.X = 0f;
                if (Math.Abs(deflectionError.Y) > PIOverFour) deflectionError.Y = 0f;
                if (Math.Abs(deflectionError.Z) > PIOverFour) deflectionError.Z = 0f;

                // ret = _angularDeflectionCorrectionMotor(1f, deflectionError);

                // Scale the correction by recovery timescale and efficiency
                //    Not modeling a spring so clamp the scale to no more then the arc
                deflectContributionV = -deflectionError * ClampInRange(0, _angularDeflectionEfficiency/_angularDeflectionTimescale,1f);
                //deflectContributionV /= _angularDeflectionTimescale;

                VehicleRotationalVelocity += deflectContributionV;
                VDetailLog("{0},  MoveAngular,Deflection,movingDir={1},pointingDir={2},deflectError={3},ret={4}",
                    ControllingPrim.LocalID, movingDirection, pointingDirection, deflectionError, deflectContributionV);
                VDetailLog("{0},  MoveAngular,Deflection,fwdSpd={1},defEff={2},defTS={3},PredictedPointingDir={4}",
                    ControllingPrim.LocalID, VehicleForwardSpeed, _angularDeflectionEfficiency, _angularDeflectionTimescale, predictedPointingDirection);
            }
        }

        // Angular change to rotate the vehicle around the Z axis when the vehicle
        //     is tipped around the X axis.
        // From http://wiki.secondlife.com/wiki/Linden_Vehicle_Tutorial:
        //      The vertical attractor feature must be enabled in order for the banking behavior to
        //      function. The way banking works is this: a rotation around the vehicle's roll-axis will
        //      produce a angular velocity around the yaw-axis, causing the vehicle to turn. The magnitude
        //      of the yaw effect will be proportional to the
        //          VEHICLE_BANKING_EFFICIENCY, the angle of the roll rotation, and sometimes the vehicle's
        //                 velocity along its preferred axis of motion.
        //          The VEHICLE_BANKING_EFFICIENCY can vary between -1 and +1. When it is positive then any
        //                  positive rotation (by the right-hand rule) about the roll-axis will effect a
        //                  (negative) torque around the yaw-axis, making it turn to the right--that is the
        //                  vehicle will lean into the turn, which is how real airplanes and motorcycle's work.
        //                  Negating the banking coefficient will make it so that the vehicle leans to the
        //                  outside of the turn (not very "physical" but might allow interesting vehicles so why not?).
        //           The VEHICLE_BANKING_MIX is a fake (i.e. non-physical) parameter that is useful for making
        //                  banking vehicles do what you want rather than what the laws of physics allow.
        //                  For example, consider a real motorcycle...it must be moving forward in order for
        //                  it to turn while banking, however video-game motorcycles are often configured
        //                  to turn in place when at a dead stop--because they are often easier to control
        //                  that way using the limited interface of the keyboard or game controller. The
        //                  VEHICLE_BANKING_MIX enables combinations of both realistic and non-realistic
        //                  banking by functioning as a slider between a banking that is correspondingly
        //                  totally static (0.0) and totally dynamic (1.0). By "static" we mean that the
        //                  banking effect depends only on the vehicle's rotation about its roll-axis compared
        //                  to "dynamic" where the banking is also proportional to its velocity along its
        //                  roll-axis. Finding the best value of the "mixture" will probably require trial and error.
        //      The time it takes for the banking behavior to defeat a preexisting angular velocity about the
        //      world z-axis is determined by the VEHICLE_BANKING_TIMESCALE. So if you want the vehicle to
        //      bank quickly then give it a banking timescale of about a second or less, otherwise you can
        //      make a sluggish vehicle by giving it a timescale of several seconds.
        public void ComputeAngularBanking()
        {
            if (BSParam.VehicleEnableAngularBanking && _bankingEfficiency != 0 && _verticalAttractionTimescale < _verticalAttractionCutoff)
            {
                Vector3 bankingContributionV = Vector3.Zero;

                // Rotate a UnitZ vector (pointing up) to how the vehicle is oriented.
                // As the vehicle rolls to the right or left, the Y value will increase from
                //     zero (straight up) to 1 or -1 (full tilt right  or left)
                Vector3 rollComponents = Vector3.UnitZ * VehicleFrameOrientation;

                // Figure out the yaw value for this much roll.
                float yawAngle = _angularMotorDirection.X * _bankingEfficiency;
                //        actual error  =       static turn error            +           dynamic turn error
                float mixedYawAngle =yawAngle * (1f - _bankingMix) + yawAngle * _bankingMix * VehicleForwardSpeed;

                // TODO: the banking effect should not go to infinity but what to limit it to?
                //     And what should happen when this is being added to a user defined yaw that is already PI*4?
                mixedYawAngle = ClampInRange(-FourPI, mixedYawAngle, FourPI);

                // Build the force vector to change rotation from what it is to what it should be
                bankingContributionV.Z = -mixedYawAngle;

                // Don't do it all at once. Fudge because 1 second is too fast with most user defined roll as PI*4.
                bankingContributionV /= _bankingTimescale * BSParam.VehicleAngularBankingTimescaleFudge;

                VehicleRotationalVelocity += bankingContributionV;


                VDetailLog("{0},  MoveAngular,Banking,rollComp={1},speed={2},rollComp={3},yAng={4},mYAng={5},ret={6}",
                            ControllingPrim.LocalID, rollComponents, VehicleForwardSpeed, rollComponents, yawAngle, mixedYawAngle, bankingContributionV);
            }
        }

        // This is from previous instantiations of XXXDynamics.cs.
        // Applies roll reference frame.
        // TODO: is this the right way to separate the code to do this operation?
        //    Should this be in MoveAngular()?
        internal void LimitRotation(float timestep)
        {
            Quaternion rotq = VehicleOrientation;
            Quaternion _rot = rotq;
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
            }
            if ((_flags & VehicleFlag.LOCK_ROTATION) != 0)
            {
                _rot.X = 0;
                _rot.Y = 0;
            }
            if (rotq != _rot)
            {
                VehicleOrientation = _rot;
                VDetailLog("{0},  LimitRotation,done,orig={1},new={2}", ControllingPrim.LocalID, rotq, _rot);
            }

        }

        // Given a friction vector (reduction in seconds) and a timestep, return the factor to reduce
        //     some value by to apply this friction.
        private Vector3 ComputeFrictionFactor(Vector3 friction, float pTimestep)
        {
            Vector3 frictionFactor = Vector3.Zero;
            if (friction != BSMotor.InfiniteVector)
            {
                // frictionFactor = (Vector3.One / FrictionTimescale) * timeStep;
                // Individual friction components can be 'infinite' so compute each separately.
                frictionFactor.X = friction.X == BSMotor.Infinite ? 0f : 1f / friction.X;
                frictionFactor.Y = friction.Y == BSMotor.Infinite ? 0f : 1f / friction.Y;
                frictionFactor.Z = friction.Z == BSMotor.Infinite ? 0f : 1f / friction.Z;
                frictionFactor *= pTimestep;
            }
            return frictionFactor;
        }

        private float SortedClampInRange(float clampa, float val, float clampb)
        {
            if (clampa > clampb)
            {
                float temp = clampa;
                clampa = clampb;
                clampb = temp;
            }
           return ClampInRange(clampa, val, clampb);

        }

        //Given a Vector and a unit vector will return the amount of the vector is on the same axis as the unit.
        private Vector3 ProjectVector(Vector3 vector, Vector3 onNormal)
        {
            float vectorDot = Vector3.Dot(vector, onNormal);
            return onNormal * vectorDot;

        }

        private float ClampInRange(float low, float val, float high)
        {
            return Math.Max(low, Math.Min(val, high));
            // return Utils.Clamp(val, low, high);
        }

        // Invoke the detailed logger and output something if it's enabled.
        private void VDetailLog(string msg, params object[] args)
        {
            if (ControllingPrim.PhysScene.VehicleLoggingEnabled)
                ControllingPrim.PhysScene.DetailLog(msg, args);
        }
    }
}
