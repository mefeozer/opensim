/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyrightD
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
using System.Collections.Generic;
using OpenSim.Region.PhysicsModules.SharedBase;

using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    public class BSActorAvatarMove : BSActor
{
    BSVMotor _velocityMotor;

    // Set to true if we think we're going up stairs.
    //    This state is remembered because collisions will turn on and off as we go up stairs.
    int _walkingUpStairs;
    // The amount the step up is applying. Used to smooth stair walking.
    float _lastStepUp;

    // There are times the velocity or force is set but we don't want to inforce
    //    stationary until some tick in the future and the real velocity drops.
    int _waitingForLowVelocityForStationary = 0;

    public BSActorAvatarMove(BSScene physicsScene, BSPhysObject pObj, string actorName)
        : base(physicsScene, pObj, actorName)
    {
        _velocityMotor = null;
        _walkingUpStairs = 0;
        _physicsScene.DetailLog("{0},BSActorAvatarMove,constructor", _controllingPrim.LocalID);
    }

    // BSActor.isActive
    public override bool isActive => Enabled && _controllingPrim.IsPhysicallyActive;

    // Release any connections and resources used by the actor.
    // BSActor.Dispose()
    public override void Dispose()
    {
        base.SetEnabled(false);
        DeactivateAvatarMove();
    }

    // Called when physical parameters (properties set in Bullet) need to be re-applied.
    // Called at taint-time.
    // BSActor.Refresh()
    public override void Refresh()
    {
        _physicsScene.DetailLog("{0},BSActorAvatarMove,refresh", _controllingPrim.LocalID);

        // If the object is physically active, add the hoverer prestep action
        if (isActive)
        {
            ActivateAvatarMove();
        }
        else
        {
            DeactivateAvatarMove();
        }
    }

    // The object's physical representation is being rebuilt so pick up any physical dependencies (constraints, ...).
    //     Register a prestep action to restore physical requirements before the next simulation step.
    // Called at taint-time.
    // BSActor.RemoveDependencies()
    public override void RemoveDependencies()
    {
        // Nothing to do for the hoverer since it is all software at pre-step action time.
    }

    // Usually called when target velocity changes to set the current velocity and the target
    //     into the movement motor.
    public void SetVelocityAndTarget(OMV.Vector3 vel, OMV.Vector3 targ, bool inTaintTime)
    {
        _physicsScene.TaintedObject(inTaintTime, _controllingPrim.LocalID, "BSActorAvatarMove.setVelocityAndTarget", delegate()
        {
            if (_velocityMotor != null)
            {
                _velocityMotor.Reset();
                _velocityMotor.SetTarget(targ);
                _velocityMotor.SetCurrent(vel);
                _velocityMotor.Enabled = true;
                _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,SetVelocityAndTarget,vel={1}, targ={2}",
                            _controllingPrim.LocalID, vel, targ);
                _waitingForLowVelocityForStationary = 0;
            }
        });
    }

    public void SuppressStationayCheckUntilLowVelocity()
    {
        _waitingForLowVelocityForStationary = 1;
    }
    public void SuppressStationayCheckUntilLowVelocity(int waitTicks)
    {
        _waitingForLowVelocityForStationary = waitTicks;
    }

    // If a movement motor has not been created, create one and start the movement
    private void ActivateAvatarMove()
    {
        if (_velocityMotor == null)
        {
                // Infinite decay and timescale values so motor only changes current to target values.
                _velocityMotor = new BSVMotor("BSCharacter.Velocity",
                                                    0.2f,                       // time scale
                                                    BSMotor.Infinite,           // decay time scale
                                                    1f                          // efficiency
                )
                {
                    ErrorZeroThreshold = BSParam.AvatarStopZeroThreshold
                };
                // _velocityMotor.PhysicsScene = _controllingPrim.PhysScene; // DEBUG DEBUG so motor will output detail log messages.
                SetVelocityAndTarget(_controllingPrim.RawVelocity, _controllingPrim.TargetVelocity, true /* inTaintTime */);

            _physicsScene.BeforeStep += Mover;
            _controllingPrim.OnPreUpdateProperty += Process_OnPreUpdateProperty;

            _walkingUpStairs = 0;
            _waitingForLowVelocityForStationary = 0;
        }
    }

    private void DeactivateAvatarMove()
    {
        if (_velocityMotor != null)
        {
            _controllingPrim.OnPreUpdateProperty -= Process_OnPreUpdateProperty;
            _physicsScene.BeforeStep -= Mover;
            _velocityMotor = null;
        }
    }

    // Called just before the simulation step.
    private void Mover(float timeStep)
    {
        // Don't do movement while the object is selected.
        if (!isActive)
            return;

        // TODO: Decide if the step parameters should be changed depending on the avatar's
        //     state (flying, colliding, ...). There is code in ODE to do this.

        // COMMENTARY: when the user is making the avatar walk, except for falling, the velocity
        //   specified for the avatar is the one that should be used. For falling, if the avatar
        //   is not flying and is not colliding then it is presumed to be falling and the Z
        //   component is not fooled with (thus allowing gravity to do its thing).
        // When the avatar is standing, though, the user has specified a velocity of zero and
        //   the avatar should be standing. But if the avatar is pushed by something in the world
        //   (raising elevator platform, moving vehicle, ...) the avatar should be allowed to
        //   move. Thus, the velocity cannot be forced to zero. The problem is that small velocity
        //   errors can creap in and the avatar will slowly float off in some direction.
        // So, the problem is that, when an avatar is standing, we cannot tell creaping error
        //   from real pushing.
        // The code below uses whether the collider is static or moving to decide whether to zero motion.

        _velocityMotor.Step(timeStep);
        _controllingPrim.IsStationary = false;

        // If we're not supposed to be moving, make sure things are zero.
        if (_velocityMotor.ErrorIsZero() && _velocityMotor.TargetValue == OMV.Vector3.Zero)
        {
            // The avatar shouldn't be moving
            _velocityMotor.Zero();

            if (_controllingPrim.IsColliding)
            {
                // if colliding with something stationary and we're not doing volume detect .
                if (!_controllingPrim.ColliderIsMoving && !_controllingPrim.ColliderIsVolumeDetect)
                {
                    if (_waitingForLowVelocityForStationary-- <= 0)
                    {
                        // if waiting for velocity to drop and it has finally dropped, we can be stationary
                        // _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,waitingForLowVelocity {1}",
                        //             _controllingPrim.LocalID, _waitingForLowVelocityForStationary);
                        if (_controllingPrim.RawVelocity.LengthSquared() < BSParam.AvatarStopZeroThresholdSquared)
                        {
                            _waitingForLowVelocityForStationary = 0;
                        }
                    }
                    if (_waitingForLowVelocityForStationary <= 0)
                    {
                        _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,collidingWithStationary,zeroingMotion", _controllingPrim.LocalID);
                        _controllingPrim.IsStationary = true;
                        _controllingPrim.ZeroMotion(true /* inTaintTime */);
                    }
                    else
                    {
                        _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,waitingForLowVel,rawvel={1}",
                                    _controllingPrim.LocalID, _controllingPrim.RawVelocity.Length());
                    }
                }

                // Standing has more friction on the ground
                if (_controllingPrim.Friction != BSParam.AvatarStandingFriction)
                {
                    _controllingPrim.Friction = BSParam.AvatarStandingFriction;
                    _physicsScene.PE.SetFriction(_controllingPrim.PhysBody, _controllingPrim.Friction);
                }
            }
            else
            {
                if (_controllingPrim.Flying)
                {
                    // Flying and not colliding and velocity nearly zero.
                    _controllingPrim.ZeroMotion(true /* inTaintTime */);
                }
                else
                {
                    //We are falling but are not touching any keys make sure not falling too fast
                    if (_controllingPrim.RawVelocity.Z < BSParam.AvatarTerminalVelocity)
                    {

                        OMV.Vector3 slowingForce = new OMV.Vector3(0f, 0f, BSParam.AvatarTerminalVelocity - _controllingPrim.RawVelocity.Z) * _controllingPrim.Mass;
                        _physicsScene.PE.ApplyCentralImpulse(_controllingPrim.PhysBody, slowingForce);
                    }

                }
            }

            _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,taint,stopping,target={1},colliding={2},isStationary={3}",
                            _controllingPrim.LocalID, _velocityMotor.TargetValue, _controllingPrim.IsColliding,_controllingPrim.IsStationary);
        }
        else
        {
            // Supposed to be moving.
            OMV.Vector3 stepVelocity = _velocityMotor.CurrentValue;

            if (_controllingPrim.Friction != BSParam.AvatarFriction)
            {
                // Probably starting to walk. Set friction to moving friction.
                _controllingPrim.Friction = BSParam.AvatarFriction;
                _physicsScene.PE.SetFriction(_controllingPrim.PhysBody, _controllingPrim.Friction);
            }

            // '_velocityMotor is used for walking, flying, and jumping and will thus have the correct values
            //    for Z. But in come cases it must be over-ridden. Like when falling or jumping.

            float realVelocityZ = _controllingPrim.RawVelocity.Z;

            // If not flying and falling, we over-ride the stepping motor so we can fall to the ground
            if (!_controllingPrim.Flying && realVelocityZ < 0)
            {
                // Can't fall faster than this
                if (realVelocityZ < BSParam.AvatarTerminalVelocity)
                {
                    realVelocityZ = BSParam.AvatarTerminalVelocity;
                }

                stepVelocity.Z = realVelocityZ;
            }
            // _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,DEBUG,motorCurrent={1},realZ={2},flying={3},collid={4},jFrames={5}",
            //     _controllingPrim.LocalID, _velocityMotor.CurrentValue, realVelocityZ, _controllingPrim.Flying, _controllingPrim.IsColliding, _jumpFrames);

            //Alicia: Maintain minimum height when flying.
            // SL has a flying effect that keeps the avatar flying above the ground by some margin
            if (_controllingPrim.Flying)
            {
                float hover_height = _physicsScene.TerrainManager.GetTerrainHeightAtXYZ(_controllingPrim.RawPosition)
                                                        + BSParam.AvatarFlyingGroundMargin;

                if( _controllingPrim.Position.Z < hover_height)
                {
                    _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,addingUpforceForGroundMargin,height={1},hoverHeight={2}",
                                _controllingPrim.LocalID, _controllingPrim.Position.Z, hover_height);
                    stepVelocity.Z += BSParam.AvatarFlyingGroundUpForce;
                }
            }

            // 'stepVelocity' is now the speed we'd like the avatar to move in. Turn that into an instantanous force.
            OMV.Vector3 moveForce = (stepVelocity - _controllingPrim.RawVelocity) * _controllingPrim.Mass;

            // Add special movement force to allow avatars to walk up stepped surfaces.
            moveForce += WalkUpStairs();

            _physicsScene.DetailLog("{0},BSCharacter.MoveMotor,move,stepVel={1},vel={2},mass={3},moveForce={4}",
                            _controllingPrim.LocalID, stepVelocity, _controllingPrim.RawVelocity, _controllingPrim.Mass, moveForce);
            _physicsScene.PE.ApplyCentralImpulse(_controllingPrim.PhysBody, moveForce);
        }
    }

    // Called just as the property update is received from the physics engine.
    // Do any mode necessary for avatar movement.
    private void Process_OnPreUpdateProperty(ref EntityProperties entprop)
    {
        // Don't change position if standing on a stationary object.
        if (_controllingPrim.IsStationary)
        {
            entprop.Position = _controllingPrim.RawPosition;
            entprop.Velocity = OMV.Vector3.Zero;
            _physicsScene.PE.SetTranslation(_controllingPrim.PhysBody, entprop.Position, entprop.Rotation);
        }

    }

    // Decide if the character is colliding with a low object and compute a force to pop the
    //    avatar up so it can walk up and over the low objects.
    private OMV.Vector3 WalkUpStairs()
    {
        OMV.Vector3 ret = OMV.Vector3.Zero;

        _physicsScene.DetailLog("{0},BSCharacter.WalkUpStairs,IsColliding={1},flying={2},targSpeed={3},collisions={4},avHeight={5}",
                        _controllingPrim.LocalID, _controllingPrim.IsColliding, _controllingPrim.Flying,
                        _controllingPrim.TargetVelocitySpeed, _controllingPrim.CollisionsLastTick.Count, _controllingPrim.Size.Z);

        // Check for stairs climbing if colliding, not flying and moving forward
        if ( _controllingPrim.IsColliding
                    && !_controllingPrim.Flying
                    && _controllingPrim.TargetVelocitySpeed > 0.1f )
        {
            // The range near the character's feet where we will consider stairs
            // float nearFeetHeightMin = _controllingPrim.RawPosition.Z - (_controllingPrim.Size.Z / 2f) + 0.05f;
            // Note: there is a problem with the computation of the capsule height. Thus RawPosition is off
            //    from the height. Revisit size and this computation when height is scaled properly.
            float nearFeetHeightMin = _controllingPrim.RawPosition.Z - _controllingPrim.Size.Z / 2f - BSParam.AvatarStepGroundFudge;
            float nearFeetHeightMax = nearFeetHeightMin + BSParam.AvatarStepHeight;

            // Look for a collision point that is near the character's feet and is oriented the same as the charactor is.
            // Find the highest 'good' collision.
            OMV.Vector3 highestTouchPosition = OMV.Vector3.Zero;
            foreach (KeyValuePair<uint, ContactPoint> kvp in _controllingPrim.CollisionsLastTick._objCollisionList)
            {
                // Don't care about collisions with the terrain
                if (kvp.Key > _physicsScene.TerrainManager.HighestTerrainID)
                {
                    BSPhysObject collisionObject;
                    if (_physicsScene.PhysObjects.TryGetValue(kvp.Key, out collisionObject))
                    {
                        if (!collisionObject.IsVolumeDetect)
                        {
                            OMV.Vector3 touchPosition = kvp.Value.Position;
                            _physicsScene.DetailLog("{0},BSCharacter.WalkUpStairs,min={1},max={2},touch={3}",
                                            _controllingPrim.LocalID, nearFeetHeightMin, nearFeetHeightMax, touchPosition);
                            if (touchPosition.Z >= nearFeetHeightMin && touchPosition.Z <= nearFeetHeightMax)
                            {
                                // This contact is within the 'near the feet' range.
                                // The step is presumed to be more or less vertical. Thus the Z component should
                                //    be nearly horizontal.
                                OMV.Vector3 directionFacing = OMV.Vector3.UnitX * _controllingPrim.RawOrientation;
                                OMV.Vector3 touchNormal = OMV.Vector3.Normalize(kvp.Value.SurfaceNormal);
                                const float PIOver2 = 1.571f; // Used to make unit vector axis into approx radian angles
                                // _physicsScene.DetailLog("{0},BSCharacter.WalkUpStairs,avNormal={1},colNormal={2},diff={3}",
                                //             _controllingPrim.LocalID, directionFacing, touchNormal,
                                //             Math.Abs(OMV.Vector3.Distance(directionFacing, touchNormal)) );
                                if (Math.Abs(directionFacing.Z) * PIOver2 < BSParam.AvatarStepAngle
                                    && Math.Abs(touchNormal.Z) * PIOver2 < BSParam.AvatarStepAngle)
                                {
                                    // The normal should be our contact point to the object so it is pointing away
                                    //    thus the difference between our facing orientation and the normal should be small.
                                    float diff = Math.Abs(OMV.Vector3.Distance(directionFacing, touchNormal));
                                    if (diff < BSParam.AvatarStepApproachFactor)
                                    {
                                        if (highestTouchPosition.Z < touchPosition.Z)
                                            highestTouchPosition = touchPosition;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            _walkingUpStairs = 0;
            // If there is a good step sensing, move the avatar over the step.
            if (highestTouchPosition != OMV.Vector3.Zero)
            {
                // Remember that we are going up stairs. This is needed because collisions
                //    will stop when we move up so this smoothes out that effect.
                _walkingUpStairs = BSParam.AvatarStepSmoothingSteps;

                _lastStepUp = highestTouchPosition.Z - nearFeetHeightMin;
                ret = ComputeStairCorrection(_lastStepUp);
                _physicsScene.DetailLog("{0},BSCharacter.WalkUpStairs,touchPos={1},nearFeetMin={2},ret={3}",
                        _controllingPrim.LocalID, highestTouchPosition, nearFeetHeightMin, ret);
            }
        }
        else
        {
            // If we used to be going up stairs but are not now, smooth the case where collision goes away while
            //    we are bouncing up the stairs.
            if (_walkingUpStairs > 0)
            {
                _walkingUpStairs--;
                ret = ComputeStairCorrection(_lastStepUp);
            }
        }

        return ret;
    }

    private OMV.Vector3 ComputeStairCorrection(float stepUp)
    {
        OMV.Vector3 ret = OMV.Vector3.Zero;
        OMV.Vector3 displacement = OMV.Vector3.Zero;

        if (stepUp > 0f)
        {
            // Found the stairs contact point. Push up a little to raise the character.
            if (BSParam.AvatarStepForceFactor > 0f)
            {
                float upForce = stepUp * _controllingPrim.Mass * BSParam.AvatarStepForceFactor;
                ret = new OMV.Vector3(0f, 0f, upForce);
            }

            // Also move the avatar up for the new height
            if (BSParam.AvatarStepUpCorrectionFactor > 0f)
            {
                // Move the avatar up related to the height of the collision
                displacement = new OMV.Vector3(0f, 0f, stepUp * BSParam.AvatarStepUpCorrectionFactor);
                _controllingPrim.ForcePosition = _controllingPrim.RawPosition + displacement;
            }
            else
            {
                if (BSParam.AvatarStepUpCorrectionFactor < 0f)
                {
                    // Move the avatar up about the specified step height
                    displacement = new OMV.Vector3(0f, 0f, BSParam.AvatarStepHeight);
                    _controllingPrim.ForcePosition = _controllingPrim.RawPosition + displacement;
                }
            }
            _physicsScene.DetailLog("{0},BSCharacter.WalkUpStairs.ComputeStairCorrection,stepUp={1},isp={2},force={3}",
                                        _controllingPrim.LocalID, stepUp, displacement, ret);

        }
        return ret;
    }
}
}


