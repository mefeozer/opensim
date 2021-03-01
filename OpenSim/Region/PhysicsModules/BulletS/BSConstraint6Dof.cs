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
using OpenMetaverse;

namespace OpenSim.Region.PhysicsModule.BulletS
{

    public class BSConstraint6Dof : BSConstraint
{
    private static readonly string LogHeader = "[BULLETSIM 6DOF CONSTRAINT]";

    public override ConstraintType Type => ConstraintType.D6_CONSTRAINT_TYPE;

    public BSConstraint6Dof(BulletWorld world, BulletBody obj1, BulletBody obj2) :base(world)
    {
        _body1 = obj1;
        _body2 = obj2;
        _enabled = false;
    }

    // Create a btGeneric6DofConstraint
    public BSConstraint6Dof(BulletWorld world, BulletBody obj1, BulletBody obj2,
                    Vector3 frame1, Quaternion frame1rot,
                    Vector3 frame2, Quaternion frame2rot,
                    bool useLinearReferenceFrameA, bool disableCollisionsBetweenLinkedBodies)
        : base(world)
    {
        _body1 = obj1;
        _body2 = obj2;
        _constraint = PhysicsScene.PE.Create6DofConstraint(_world, _body1, _body2,
                                frame1, frame1rot,
                                frame2, frame2rot,
                                useLinearReferenceFrameA, disableCollisionsBetweenLinkedBodies);
        _enabled = true;
        PhysicsScene.DetailLog("{0},BS6DofConstraint,create,wID={1}, rID={2}, rBody={3}, cID={4}, cBody={5}",
                            _body1.ID, world.worldID,
                            obj1.ID, obj1.AddrString, obj2.ID, obj2.AddrString);
        PhysicsScene.DetailLog("{0},BS6DofConstraint,create,  f1Loc={1},f1Rot={2},f2Loc={3},f2Rot={4},usefA={5},disCol={6}",
                            _body1.ID, frame1, frame1rot, frame2, frame2rot, useLinearReferenceFrameA, disableCollisionsBetweenLinkedBodies);
    }

    // 6 Dof constraint based on a midpoint between the two constrained bodies
    public BSConstraint6Dof(BulletWorld world, BulletBody obj1, BulletBody obj2,
                    Vector3 joinPoint,
                    bool useLinearReferenceFrameA, bool disableCollisionsBetweenLinkedBodies)
        : base(world)
    {
        _body1 = obj1;
        _body2 = obj2;
        if (!obj1.HasPhysicalBody || !obj2.HasPhysicalBody)
        {
            world.physicsScene.DetailLog("{0},BS6DOFConstraint,badBodyPtr,wID={1}, rID={2}, rBody={3}, cID={4}, cBody={5}",
                            BSScene.DetailLogZero, world.worldID,
                            obj1.ID, obj1.AddrString, obj2.ID, obj2.AddrString);
            world.physicsScene.Logger.ErrorFormat("{0} Attempt to build 6DOF constraint with missing bodies: wID={1}, rID={2}, rBody={3}, cID={4}, cBody={5}",
                            LogHeader, world.worldID, obj1.ID, obj1.AddrString, obj2.ID, obj2.AddrString);
            _enabled = false;
        }
        else
        {
            _constraint = PhysicsScene.PE.Create6DofConstraintToPoint(_world, _body1, _body2,
                                    joinPoint,
                                    useLinearReferenceFrameA, disableCollisionsBetweenLinkedBodies);

            PhysicsScene.DetailLog("{0},BS6DofConstraint,createMidPoint,wID={1}, csrt={2}, rID={3}, rBody={4}, cID={5}, cBody={6}",
                                _body1.ID, world.worldID, _constraint.AddrString,
                                obj1.ID, obj1.AddrString, obj2.ID, obj2.AddrString);

            if (!_constraint.HasPhysicalConstraint)
            {
                world.physicsScene.Logger.ErrorFormat("{0} Failed creation of 6Dof constraint. rootID={1}, childID={2}",
                                LogHeader, obj1.ID, obj2.ID);
                _enabled = false;
            }
            else
            {
                _enabled = true;
            }
        }
    }

    // A 6 Dof constraint that is fixed in the world and constrained to a on-the-fly created static object
    public BSConstraint6Dof(BulletWorld world, BulletBody obj1, Vector3 frameInBloc, Quaternion frameInBrot,
                    bool useLinearReferenceFrameB, bool disableCollisionsBetweenLinkedBodies)
        : base(world)
    {
        _body1 = obj1;
        _body2 = obj1; // Look out for confusion down the road
        _constraint = PhysicsScene.PE.Create6DofConstraintFixed(_world, _body1,
                                frameInBloc, frameInBrot,
                                useLinearReferenceFrameB, disableCollisionsBetweenLinkedBodies);
        _enabled = true;
        PhysicsScene.DetailLog("{0},BS6DofConstraint,createFixed,wID={1},rID={2},rBody={3}",
                                    _body1.ID, world.worldID, obj1.ID, obj1.AddrString);
        PhysicsScene.DetailLog("{0},BS6DofConstraint,createFixed,  fBLoc={1},fBRot={2},usefA={3},disCol={4}",
                            _body1.ID, frameInBloc, frameInBrot, useLinearReferenceFrameB, disableCollisionsBetweenLinkedBodies);
    }

    public bool SetFrames(Vector3 frameA, Quaternion frameArot, Vector3 frameB, Quaternion frameBrot)
    {
        bool ret = false;
        if (_enabled)
        {
            PhysicsScene.PE.SetFrames(_constraint, frameA, frameArot, frameB, frameBrot);
            ret = true;
        }
        return ret;
    }

    public bool SetCFMAndERP(float cfm, float erp)
    {
        bool ret = false;
        if (_enabled)
        {
            PhysicsScene.PE.SetConstraintParam(_constraint, ConstraintParams.BT_CONSTRAINT_STOP_CFM, cfm, ConstraintParamAxis.AXIS_ALL);
            PhysicsScene.PE.SetConstraintParam(_constraint, ConstraintParams.BT_CONSTRAINT_STOP_ERP, erp, ConstraintParamAxis.AXIS_ALL);
            PhysicsScene.PE.SetConstraintParam(_constraint, ConstraintParams.BT_CONSTRAINT_CFM, cfm, ConstraintParamAxis.AXIS_ALL);
            ret = true;
        }
        return ret;
    }

    public bool UseFrameOffset(bool useOffset)
    {
        bool ret = false;
        float onOff = useOffset ? ConfigurationParameters.numericTrue : ConfigurationParameters.numericFalse;
        if (_enabled)
            ret = PhysicsScene.PE.UseFrameOffset(_constraint, onOff);
        return ret;
    }

    public bool TranslationalLimitMotor(bool enable, float targetVelocity, float maxMotorForce)
    {
        bool ret = false;
        float onOff = enable ? ConfigurationParameters.numericTrue : ConfigurationParameters.numericFalse;
        if (_enabled)
        {
            ret = PhysicsScene.PE.TranslationalLimitMotor(_constraint, onOff, targetVelocity, maxMotorForce);
            _world.physicsScene.DetailLog("{0},BS6DOFConstraint,TransLimitMotor,enable={1},vel={2},maxForce={3}",
                            BSScene.DetailLogZero, enable, targetVelocity, maxMotorForce);
        }
        return ret;
    }

    public bool SetBreakingImpulseThreshold(float threshold)
    {
        bool ret = false;
        if (_enabled)
            ret = PhysicsScene.PE.SetBreakingImpulseThreshold(_constraint, threshold);
        return ret;
    }
}
}
