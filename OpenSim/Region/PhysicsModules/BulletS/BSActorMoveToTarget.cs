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

using OMV = OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{
    public class BSActorMoveToTarget : BSActor
{
    private BSVMotor _targetMotor;

    public BSActorMoveToTarget(BSScene physicsScene, BSPhysObject pObj, string actorName)
        : base(physicsScene, pObj, actorName)
    {
        _targetMotor = null;
        _physicsScene.DetailLog("{0},BSActorMoveToTarget,constructor", _controllingPrim.LocalID);
    }

    // BSActor.isActive
    public override bool isActive =>
        // MoveToTarget only works on physical prims
        Enabled && _controllingPrim.IsPhysicallyActive;

    // Release any connections and resources used by the actor.
    // BSActor.Dispose()
    public override void Dispose()
    {
        Enabled = false;
        DeactivateMoveToTarget();
    }

    // Called when physical parameters (properties set in Bullet) need to be re-applied.
    // Called at taint-time.
    // BSActor.Refresh()
    public override void Refresh()
    {
        _physicsScene.DetailLog("{0},BSActorMoveToTarget,refresh,enabled={1},active={2},target={3},tau={4}",
            _controllingPrim.LocalID, Enabled, _controllingPrim.MoveToTargetActive,
            _controllingPrim.MoveToTargetTarget, _controllingPrim.MoveToTargetTau );

        // If not active any more...
        if (!_controllingPrim.MoveToTargetActive)
        {
            Enabled = false;
        }

        if (isActive)
        {
            ActivateMoveToTarget();
        }
        else
        {
            DeactivateMoveToTarget();
        }
    }

    // The object's physical representation is being rebuilt so pick up any physical dependencies (constraints, ...).
    //     Register a prestep action to restore physical requirements before the next simulation step.
    // Called at taint-time.
    // BSActor.RemoveDependencies()
    public override void RemoveDependencies()
    {
        // Nothing to do for the moveToTarget since it is all software at pre-step action time.
    }

    // If a hover motor has not been created, create one and start the hovering.
    private void ActivateMoveToTarget()
    {
        if (_targetMotor == null)
        {
            // We're taking over after this.
            _controllingPrim.ZeroMotion(true);

                /* Someday use the PID controller
                _targetMotor = new BSPIDVMotor("BSActorMoveToTarget-" + _controllingPrim.LocalID.ToString());
                _targetMotor.TimeScale = _controllingPrim.MoveToTargetTau;
                _targetMotor.Efficiency = 1f;
                 */
                _targetMotor = new BSVMotor("BSActorMoveToTarget-" + _controllingPrim.LocalID.ToString(),
                                            _controllingPrim.MoveToTargetTau,  // timeScale
                                            BSMotor.Infinite,                   // decay time scale
                                            1f                                  // efficiency
                )
                {
                    PhysicsScene = _physicsScene // DEBUG DEBUG so motor will output detail log messages.
                };
                _targetMotor.SetTarget(_controllingPrim.MoveToTargetTarget);
            _targetMotor.SetCurrent(_controllingPrim.RawPosition);

            // _physicsScene.BeforeStep += Mover;
            _physicsScene.BeforeStep += Mover2;
        }
        else
        {
            // If already allocated, make sure the target and other paramters are current
            _targetMotor.SetTarget(_controllingPrim.MoveToTargetTarget);
            _targetMotor.SetCurrent(_controllingPrim.RawPosition);
        }
    }

    private void DeactivateMoveToTarget()
    {
        if (_targetMotor != null)
        {
            // _physicsScene.BeforeStep -= Mover;
            _physicsScene.BeforeStep -= Mover2;
            _targetMotor = null;
        }
    }

    // Origional mover that set the objects position to move to the target.
    // The problem was that gravity would keep trying to push the object down so
    //    the overall downward velocity would increase to infinity.
    // Called just before the simulation step.
    private void Mover(float timeStep)
    {
        // Don't do hovering while the object is selected.
        if (!isActive)
            return;

        OMV.Vector3 origPosition = _controllingPrim.RawPosition;     // DEBUG DEBUG (for printout below)

        // 'movePosition' is where we'd like the prim to be at this moment.
        OMV.Vector3 movePosition = _controllingPrim.RawPosition + _targetMotor.Step(timeStep);

        // If we are very close to our target, turn off the movement motor.
        if (_targetMotor.ErrorIsZero())
        {
            _physicsScene.DetailLog("{0},BSActorMoveToTarget.Mover,zeroMovement,movePos={1},pos={2},mass={3}",
                                    _controllingPrim.LocalID, movePosition, _controllingPrim.RawPosition, _controllingPrim.Mass);
            _controllingPrim.ForcePosition = _targetMotor.TargetValue;
            _controllingPrim.ForceVelocity = OMV.Vector3.Zero;
            // Setting the position does not cause the physics engine to generate a property update. Force it.
            _physicsScene.PE.PushUpdate(_controllingPrim.PhysBody);
        }
        else
        {
            _controllingPrim.ForcePosition = movePosition;
            // Setting the position does not cause the physics engine to generate a property update. Force it.
            _physicsScene.PE.PushUpdate(_controllingPrim.PhysBody);
        }
        _physicsScene.DetailLog("{0},BSActorMoveToTarget.Mover,move,fromPos={1},movePos={2}",
                                        _controllingPrim.LocalID, origPosition, movePosition);
    }

    // Version of mover that applies forces to move the physical object to the target.
    // Also overcomes gravity so the object doesn't just drop to the ground.
    // Called just before the simulation step.
    private void Mover2(float timeStep)
    {
        // Don't do hovering while the object is selected.
        if (!isActive)
            return;

        OMV.Vector3 origPosition = _controllingPrim.RawPosition;     // DEBUG DEBUG (for printout below)
        OMV.Vector3 addedForce = OMV.Vector3.Zero;

        // CorrectionVector is the movement vector required this step
        OMV.Vector3 correctionVector = _targetMotor.Step(timeStep, _controllingPrim.RawPosition);

        // If we are very close to our target, turn off the movement motor.
        if (_targetMotor.ErrorIsZero())
        {
            _physicsScene.DetailLog("{0},BSActorMoveToTarget.Mover3,zeroMovement,pos={1},mass={2}",
                                    _controllingPrim.LocalID, _controllingPrim.RawPosition, _controllingPrim.Mass);
            _controllingPrim.ForcePosition = _targetMotor.TargetValue;
            _controllingPrim.ForceVelocity = OMV.Vector3.Zero;
            // Setting the position does not cause the physics engine to generate a property update. Force it.
            _physicsScene.PE.PushUpdate(_controllingPrim.PhysBody);
        }
        else
        {
            // First force to move us there -- the motor return a timestep scaled value.
            addedForce = correctionVector / timeStep;
            // Remove the existing velocity (only the moveToTarget force counts)
            addedForce -= _controllingPrim.RawVelocity;
            // Overcome gravity.
            addedForce -= _controllingPrim.Gravity;

            // Add enough force to overcome the mass of the object
            addedForce *= _controllingPrim.Mass;

            _controllingPrim.AddForce(true /* inTaintTime */, addedForce);
        }
        _physicsScene.DetailLog("{0},BSActorMoveToTarget.Mover3,move,fromPos={1},addedForce={2}",
                                        _controllingPrim.LocalID, origPosition, addedForce);
    }
}
}
