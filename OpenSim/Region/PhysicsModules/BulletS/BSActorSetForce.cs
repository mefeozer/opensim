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
    public class BSActorSetForce : BSActor
{
    BSFMotor _forceMotor;

    public BSActorSetForce(BSScene physicsScene, BSPhysObject pObj, string actorName)
        : base(physicsScene, pObj, actorName)
    {
        _forceMotor = null;
        _physicsScene.DetailLog("{0},BSActorSetForce,constructor", _controllingPrim.LocalID);
    }

    // BSActor.isActive
    public override bool isActive => Enabled && _controllingPrim.IsPhysicallyActive;

    // Release any connections and resources used by the actor.
    // BSActor.Dispose()
    public override void Dispose()
    {
        Enabled = false;
        DeactivateSetForce();
    }

    // Called when physical parameters (properties set in Bullet) need to be re-applied.
    // Called at taint-time.
    // BSActor.Refresh()
    public override void Refresh()
    {
        _physicsScene.DetailLog("{0},BSActorSetForce,refresh", _controllingPrim.LocalID);

        // If not active any more, get rid of me (shouldn't ever happen, but just to be safe)
        if (_controllingPrim.RawForce == OMV.Vector3.Zero)
        {
            _physicsScene.DetailLog("{0},BSActorSetForce,refresh,notSetForce,removing={1}", _controllingPrim.LocalID, ActorName);
            Enabled = false;
            return;
        }

        // If the object is physically active, add the hoverer prestep action
        if (isActive)
        {
            ActivateSetForce();
        }
        else
        {
            DeactivateSetForce();
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

    // If a hover motor has not been created, create one and start the hovering.
    private void ActivateSetForce()
    {
        if (_forceMotor == null)
        {
            // A fake motor that might be used someday
            _forceMotor = new BSFMotor("setForce", 1f, 1f, 1f);

            _physicsScene.BeforeStep += Mover;
        }
    }

    private void DeactivateSetForce()
    {
        if (_forceMotor != null)
        {
            _physicsScene.BeforeStep -= Mover;
            _forceMotor = null;
        }
    }

    // Called just before the simulation step. Update the vertical position for hoverness.
    private void Mover(float timeStep)
    {
        // Don't do force while the object is selected.
        if (!isActive)
            return;

        _physicsScene.DetailLog("{0},BSActorSetForce,preStep,force={1}", _controllingPrim.LocalID, _controllingPrim.RawForce);
        if (_controllingPrim.PhysBody.HasPhysicalBody)
        {
            _physicsScene.PE.ApplyCentralForce(_controllingPrim.PhysBody, _controllingPrim.RawForce);
            _controllingPrim.ActivateIfPhysical(false);
        }

        // TODO:
    }
}
}

