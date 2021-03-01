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

using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.PhysicsModules.SharedBase;

namespace OpenSim.Region.PhysicsModule.POS
{
    public class POSCharacter : PhysicsActor
    {
        private Vector3 _position;
        public Vector3 _velocity;
        public Vector3 _target_velocity = Vector3.Zero;
        public Vector3 _size = Vector3.Zero;
        private Vector3 _acceleration;
        private Vector3 _rotationalVelocity = Vector3.Zero;
        private bool flying;
        private bool isColliding;

        public override int PhysicsActorType
        {
            get => (int) ActorTypes.Agent;
            set { return; }
        }

        public override Vector3 RotationalVelocity
        {
            get => _rotationalVelocity;
            set => _rotationalVelocity = value;
        }

        public override bool SetAlwaysRun
        {
            get => false;
            set { return; }
        }

        public override uint LocalID
        {
            set { return; }
        }

        public override bool Grabbed
        {
            set { return; }
        }

        public override bool Selected
        {
            set { return; }
        }

        public override float Buoyancy
        {
            get => 0f;
            set { return; }
        }

        public override bool FloatOnWater
        {
            set { return; }
        }

        public override bool IsPhysical
        {
            get => false;
            set { return; }
        }

        public override bool ThrottleUpdates
        {
            get => false;
            set { return; }
        }

        public override bool Flying
        {
            get => flying;
            set => flying = value;
        }

        public override bool IsColliding
        {
            get => isColliding;
            set => isColliding = value;
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

        public override bool Stopped => false;

        public override Vector3 Position
        {
            get => _position;
            set => _position = value;
        }

        public override Vector3 Size
        {
            get => _size;
            set
            {
                _size = value;
                _size.Z = _size.Z / 2.0f;
            }
        }

        public override float Mass => 0f;

        public override Vector3 Force
        {
            get => Vector3.Zero;
            set { return; }
        }

        public override int VehicleType
        {
            get => 0;
            set { return; }
        }

        public override void VehicleFloatParam(int param, float value)
        {

        }

        public override void VehicleVectorParam(int param, Vector3 value)
        {

        }

        public override void VehicleRotationParam(int param, Quaternion rotation)
        {

        }

        public override void VehicleFlags(int param, bool remove) { }

        public override void SetVolumeDetect(int param)
        {

        }

        public override Vector3 CenterOfMass => Vector3.Zero;

        public override Vector3 GeometricCenter => Vector3.Zero;

        public override PrimitiveBaseShape Shape
        {
            set { return; }
        }

        public override Vector3 Velocity
        {
            get => _velocity;
            set => _target_velocity = value;
        }

        public override Vector3 Torque
        {
            get => Vector3.Zero;
            set { return; }
        }

        public override float CollisionScore
        {
            get => 0f;
            set { }
        }

        public override Quaternion Orientation
        {
            get => Quaternion.Identity;
            set { }
        }

        public override Vector3 Acceleration
        {
            get => _acceleration;
            set => _acceleration = value;
        }

        public override bool Kinematic
        {
            get => true;
            set { }
        }

        public override void link(PhysicsActor obj)
        {
        }

        public override void delink()
        {
        }

        public override void LockAngularMotion(byte axislocks)
        {
        }

        public override void AddForce(Vector3 force, bool pushforce)
        {
        }

        public override void AddAngularForce(Vector3 force, bool pushforce)
        {
        }

        public override void SetMomentum(Vector3 momentum)
        {
        }

        public override void CrossingFailure()
        {
        }

        public override Vector3 PIDTarget
        {
            set { return; }
        }

        public override bool PIDActive
        {
            get => false;
            set { return; }
        }

        public override float PIDTau
        {
            set { return; }
        }

        public override float PIDHoverHeight
        {
            set { return; }
        }

        public override bool PIDHoverActive
        {
            get => false;
            set { return; }
        }

        public override PIDHoverType PIDHoverType
        {
            set { return; }
        }

        public override float PIDHoverTau
        {
            set { return; }
        }

        public override Quaternion APIDTarget
        {
            set { return; }
        }

        public override bool APIDActive
        {
            set { return; }
        }

        public override float APIDStrength
        {
            set { return; }
        }

        public override float APIDDamping
        {
            set { return; }
        }


        public override void SubscribeEvents(int ms)
        {
        }

        public override void UnSubscribeEvents()
        {
        }

        public override bool SubscribedEvents()
        {
            return false;
        }
    }
}
