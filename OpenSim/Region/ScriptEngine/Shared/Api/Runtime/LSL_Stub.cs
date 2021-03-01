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

using System;
using System.Diagnostics; //for [DebuggerNonUserCode]
using OpenSim.Region.ScriptEngine.Interfaces;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;

using LSL_Float = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLFloat;
using LSL_Integer = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLInteger;
using LSL_Key = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_List = OpenSim.Region.ScriptEngine.Shared.LSL_Types.list;
using LSL_Rotation = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Quaternion;
using LSL_String = OpenSim.Region.ScriptEngine.Shared.LSL_Types.LSLString;
using LSL_Vector = OpenSim.Region.ScriptEngine.Shared.LSL_Types.Vector3;

namespace OpenSim.Region.ScriptEngine.Shared.ScriptBase
{
    public partial class ScriptBaseClass : MarshalByRefObject
    {
        public ILSL_Api _LSL_Functions;

        public void ApiTypeLSL(IScriptApi api)
        {
            if (!(api is ILSL_Api))
                return;

            _LSL_Functions = (ILSL_Api)api;
        }

        public void state(string newState)
        {
            _LSL_Functions.state(newState);
        }

        //
        // Script functions
        //
        public LSL_Integer llAbs(LSL_Integer i)
        {
            return _LSL_Functions.llAbs(i);
        }

        public LSL_Float llAcos(LSL_Float val)
        {
            return _LSL_Functions.llAcos(val);
        }

        public void llAddToLandBanList(LSL_Key avatar, LSL_Float hours)
        {
            _LSL_Functions.llAddToLandBanList(avatar, hours);
        }

        public void llAddToLandPassList(LSL_Key avatar, LSL_Float hours)
        {
            _LSL_Functions.llAddToLandPassList(avatar, hours);
        }

        public void llAdjustSoundVolume(LSL_Float volume)
        {
            _LSL_Functions.llAdjustSoundVolume(volume);
        }

        public void llAllowInventoryDrop(LSL_Integer add)
        {
            _LSL_Functions.llAllowInventoryDrop(add);
        }

        public LSL_Float llAngleBetween(LSL_Rotation a, LSL_Rotation b)
        {
            return _LSL_Functions.llAngleBetween(a, b);
        }

        public void llApplyImpulse(LSL_Vector force, LSL_Integer local)
        {
            _LSL_Functions.llApplyImpulse(force, local);
        }

        public void llApplyRotationalImpulse(LSL_Vector force, int local)
        {
            _LSL_Functions.llApplyRotationalImpulse(force, local);
        }

        public LSL_Float llAsin(LSL_Float val)
        {
            return _LSL_Functions.llAsin(val);
        }

        public LSL_Float llAtan2(LSL_Float x, LSL_Float y)
        {
            return _LSL_Functions.llAtan2(x, y);
        }

        public void llAttachToAvatar(LSL_Integer attachment)
        {
            _LSL_Functions.llAttachToAvatar(attachment);
        }

        public void llAttachToAvatarTemp(LSL_Integer attachment)
        {
            _LSL_Functions.llAttachToAvatarTemp(attachment);
        }

        public LSL_Key llAvatarOnSitTarget()
        {
            return _LSL_Functions.llAvatarOnSitTarget();
        }

        public LSL_Key llAvatarOnLinkSitTarget(LSL_Integer linknum)
        {
            return _LSL_Functions.llAvatarOnLinkSitTarget(linknum);
        }

        public LSL_Rotation llAxes2Rot(LSL_Vector fwd, LSL_Vector left, LSL_Vector up)
        {
            return _LSL_Functions.llAxes2Rot(fwd, left, up);
        }

        public LSL_Rotation llAxisAngle2Rot(LSL_Vector axis, double angle)
        {
            return _LSL_Functions.llAxisAngle2Rot(axis, angle);
        }

        public LSL_Integer llBase64ToInteger(string str)
        {
            return _LSL_Functions.llBase64ToInteger(str);
        }

        public LSL_String llBase64ToString(string str)
        {
            return _LSL_Functions.llBase64ToString(str);
        }

        public void llBreakAllLinks()
        {
            _LSL_Functions.llBreakAllLinks();
        }

        public void llBreakLink(int linknum)
        {
            _LSL_Functions.llBreakLink(linknum);
        }

        public LSL_Integer llCeil(double f)
        {
            return _LSL_Functions.llCeil(f);
        }

        public void llClearCameraParams()
        {
            _LSL_Functions.llClearCameraParams();
        }

        public void llCloseRemoteDataChannel(string channel)
        {
            _LSL_Functions.llCloseRemoteDataChannel(channel);
        }

        public LSL_Float llCloud(LSL_Vector offset)
        {
            return _LSL_Functions.llCloud(offset);
        }

        public void llCollisionFilter(LSL_String name, LSL_Key id, LSL_Integer accept)
        {
            _LSL_Functions.llCollisionFilter(name, id, accept);
        }

        public void llCollisionSound(LSL_String impact_sound, LSL_Float impact_volume)
        {
            _LSL_Functions.llCollisionSound(impact_sound, impact_volume);
        }

        public void llCollisionSprite(LSL_String impact_sprite)
        {
            _LSL_Functions.llCollisionSprite(impact_sprite);
        }

        public LSL_Float llCos(double f)
        {
            return _LSL_Functions.llCos(f);
        }

        public void llCreateLink(LSL_Key target, LSL_Integer parent)
        {
            _LSL_Functions.llCreateLink(target, parent);
        }

        public LSL_List llCSV2List(string src)
        {
            return _LSL_Functions.llCSV2List(src);
        }

        public LSL_List llDeleteSubList(LSL_List src, int start, int end)
        {
            return _LSL_Functions.llDeleteSubList(src, start, end);
        }

        public LSL_String llDeleteSubString(string src, int start, int end)
        {
            return _LSL_Functions.llDeleteSubString(src, start, end);
        }

        public void llDetachFromAvatar()
        {
            _LSL_Functions.llDetachFromAvatar();
        }

        public LSL_Vector llDetectedGrab(int number)
        {
            return _LSL_Functions.llDetectedGrab(number);
        }

        public LSL_Integer llDetectedGroup(int number)
        {
            return _LSL_Functions.llDetectedGroup(number);
        }

        public LSL_Key llDetectedKey(int number)
        {
            return _LSL_Functions.llDetectedKey(number);
        }

        public LSL_Integer llDetectedLinkNumber(int number)
        {
            return _LSL_Functions.llDetectedLinkNumber(number);
        }

        public LSL_String llDetectedName(int number)
        {
            return _LSL_Functions.llDetectedName(number);
        }

        public LSL_Key llDetectedOwner(int number)
        {
            return _LSL_Functions.llDetectedOwner(number);
        }

        public LSL_Vector llDetectedPos(int number)
        {
            return _LSL_Functions.llDetectedPos(number);
        }

        public LSL_Rotation llDetectedRot(int number)
        {
            return _LSL_Functions.llDetectedRot(number);
        }

        public LSL_Integer llDetectedType(int number)
        {
            return _LSL_Functions.llDetectedType(number);
        }

        public LSL_Vector llDetectedTouchBinormal(int index)
        {
            return _LSL_Functions.llDetectedTouchBinormal(index);
        }

        public LSL_Integer llDetectedTouchFace(int index)
        {
            return _LSL_Functions.llDetectedTouchFace(index);
        }

        public LSL_Vector llDetectedTouchNormal(int index)
        {
            return _LSL_Functions.llDetectedTouchNormal(index);
        }

        public LSL_Vector llDetectedTouchPos(int index)
        {
            return _LSL_Functions.llDetectedTouchPos(index);
        }

        public LSL_Vector llDetectedTouchST(int index)
        {
            return _LSL_Functions.llDetectedTouchST(index);
        }

        public LSL_Vector llDetectedTouchUV(int index)
        {
            return _LSL_Functions.llDetectedTouchUV(index);
        }

        public LSL_Vector llDetectedVel(int number)
        {
            return _LSL_Functions.llDetectedVel(number);
        }

        public void llDialog(LSL_Key avatar, LSL_String message, LSL_List buttons, int chat_channel)
        {
            _LSL_Functions.llDialog(avatar, message, buttons, chat_channel);
        }

        [DebuggerNonUserCode]
        public void llDie()
        {
            _LSL_Functions.llDie();
        }

        public LSL_String llDumpList2String(LSL_List src, string seperator)
        {
            return _LSL_Functions.llDumpList2String(src, seperator);
        }

        public LSL_Integer llEdgeOfWorld(LSL_Vector pos, LSL_Vector dir)
        {
            return _LSL_Functions.llEdgeOfWorld(pos, dir);
        }

        public void llEjectFromLand(LSL_Key pest)
        {
            _LSL_Functions.llEjectFromLand(pest);
        }

        public void llEmail(string address, string subject, string message)
        {
            _LSL_Functions.llEmail(address, subject, message);
        }

        public LSL_String llEscapeURL(string url)
        {
            return _LSL_Functions.llEscapeURL(url);
        }

        public LSL_Rotation llEuler2Rot(LSL_Vector v)
        {
            return _LSL_Functions.llEuler2Rot(v);
        }

        public LSL_Float llFabs(double f)
        {
            return _LSL_Functions.llFabs(f);
        }

        public LSL_Integer llFloor(double f)
        {
            return _LSL_Functions.llFloor(f);
        }

        public void llForceMouselook(int mouselook)
        {
            _LSL_Functions.llForceMouselook(mouselook);
        }

        public LSL_Float llFrand(double mag)
        {
            return _LSL_Functions.llFrand(mag);
        }

        public LSL_Key llGenerateKey()
        {
            return _LSL_Functions.llGenerateKey();
        }

        public LSL_Vector llGetAccel()
        {
            return _LSL_Functions.llGetAccel();
        }

        public LSL_Integer llGetAgentInfo(LSL_Key id)
        {
            return _LSL_Functions.llGetAgentInfo(id);
        }

        public LSL_String llGetAgentLanguage(LSL_Key id)
        {
            return _LSL_Functions.llGetAgentLanguage(id);
        }

        public LSL_List llGetAgentList(LSL_Integer scope, LSL_List options)
        {
            return _LSL_Functions.llGetAgentList(scope, options);
        }

        public LSL_Vector llGetAgentSize(LSL_Key id)
        {
            return _LSL_Functions.llGetAgentSize(id);
        }

        public LSL_Float llGetAlpha(int face)
        {
            return _LSL_Functions.llGetAlpha(face);
        }

        public LSL_Float llGetAndResetTime()
        {
            return _LSL_Functions.llGetAndResetTime();
        }

        public LSL_String llGetAnimation(LSL_Key id)
        {
            return _LSL_Functions.llGetAnimation(id);
        }

        public LSL_List llGetAnimationList(LSL_Key id)
        {
            return _LSL_Functions.llGetAnimationList(id);
        }

        public LSL_Integer llGetAttached()
        {
            return _LSL_Functions.llGetAttached();
        }

        public LSL_List llGetAttachedList(LSL_Key id)
        {
            return _LSL_Functions.llGetAttachedList(id);
        }

        public LSL_List llGetBoundingBox(string obj)
        {
            return _LSL_Functions.llGetBoundingBox(obj);
        }

        public LSL_Vector llGetCameraPos()
        {
            return _LSL_Functions.llGetCameraPos();
        }

        public LSL_Rotation llGetCameraRot()
        {
            return _LSL_Functions.llGetCameraRot();
        }

        public LSL_Vector llGetCenterOfMass()
        {
            return _LSL_Functions.llGetCenterOfMass();
        }

        public LSL_Vector llGetColor(int face)
        {
            return _LSL_Functions.llGetColor(face);
        }

        public LSL_Key llGetCreator()
        {
            return _LSL_Functions.llGetCreator();
        }

        public LSL_String llGetDate()
        {
            return _LSL_Functions.llGetDate();
        }

        public LSL_Float llGetEnergy()
        {
            return _LSL_Functions.llGetEnergy();
        }

        public LSL_String llGetEnv(LSL_String name)
        {
            return _LSL_Functions.llGetEnv(name);
        }

        public LSL_Vector llGetForce()
        {
            return _LSL_Functions.llGetForce();
        }

        public LSL_Integer llGetFreeMemory()
        {
            return _LSL_Functions.llGetFreeMemory();
        }

        public LSL_Integer llGetUsedMemory()
        {
            return _LSL_Functions.llGetUsedMemory();
        }

        public LSL_Integer llGetFreeURLs()
        {
            return _LSL_Functions.llGetFreeURLs();
        }

        public LSL_Vector llGetGeometricCenter()
        {
            return _LSL_Functions.llGetGeometricCenter();
        }

        public LSL_Float llGetGMTclock()
        {
            return _LSL_Functions.llGetGMTclock();
        }

        public LSL_String llGetHTTPHeader(LSL_Key request_id, string header)
        {
            return _LSL_Functions.llGetHTTPHeader(request_id, header);
        }

        public LSL_Key llGetInventoryCreator(string item)
        {
            return _LSL_Functions.llGetInventoryCreator(item);
        }

        public LSL_Key llGetInventoryKey(string name)
        {
            return _LSL_Functions.llGetInventoryKey(name);
        }

        public LSL_String llGetInventoryName(int type, int number)
        {
            return _LSL_Functions.llGetInventoryName(type, number);
        }

        public LSL_Integer llGetInventoryNumber(int type)
        {
            return _LSL_Functions.llGetInventoryNumber(type);
        }

        public LSL_Integer llGetInventoryPermMask(string item, int mask)
        {
            return _LSL_Functions.llGetInventoryPermMask(item, mask);
        }

        public LSL_Integer llGetInventoryType(string name)
        {
            return _LSL_Functions.llGetInventoryType(name);
        }

        public LSL_Key llGetKey()
        {
            return _LSL_Functions.llGetKey();
        }

        public LSL_Key llGetLandOwnerAt(LSL_Vector pos)
        {
            return _LSL_Functions.llGetLandOwnerAt(pos);
        }

        public LSL_Key llGetLinkKey(int linknum)
        {
            return _LSL_Functions.llGetLinkKey(linknum);
        }

        public LSL_String llGetLinkName(int linknum)
        {
            return _LSL_Functions.llGetLinkName(linknum);
        }

        public LSL_Integer llGetLinkNumber()
        {
            return _LSL_Functions.llGetLinkNumber();
        }

        public LSL_Integer llGetLinkNumberOfSides(int link)
        {
            return _LSL_Functions.llGetLinkNumberOfSides(link);
        }

        public LSL_Integer llGetListEntryType(LSL_List src, int index)
        {
            return _LSL_Functions.llGetListEntryType(src, index);
        }

        public LSL_Integer llGetListLength(LSL_List src)
        {
            return _LSL_Functions.llGetListLength(src);
        }

        public LSL_Vector llGetLocalPos()
        {
            return _LSL_Functions.llGetLocalPos();
        }

        public LSL_Rotation llGetLocalRot()
        {
            return _LSL_Functions.llGetLocalRot();
        }

        public LSL_Float llGetMass()
        {
            return _LSL_Functions.llGetMass();
        }

        public LSL_Float llGetMassMKS()
        {
            return _LSL_Functions.llGetMassMKS();
        }

        public LSL_Integer llGetMemoryLimit()
        {
            return _LSL_Functions.llGetMemoryLimit();
        }

        public void llGetNextEmail(string address, string subject)
        {
            _LSL_Functions.llGetNextEmail(address, subject);
        }

        public LSL_Key llGetNotecardLine(string name, int line)
        {
            return _LSL_Functions.llGetNotecardLine(name, line);
        }

        public LSL_Key llGetNumberOfNotecardLines(string name)
        {
            return _LSL_Functions.llGetNumberOfNotecardLines(name);
        }

        public LSL_Integer llGetNumberOfPrims()
        {
            return _LSL_Functions.llGetNumberOfPrims();
        }

        public LSL_Integer llGetNumberOfSides()
        {
            return _LSL_Functions.llGetNumberOfSides();
        }

        public LSL_String llGetObjectDesc()
        {
            return _LSL_Functions.llGetObjectDesc();
        }

        public LSL_List llGetObjectDetails(LSL_Key id, LSL_List args)
        {
            return _LSL_Functions.llGetObjectDetails(id, args);
        }

        public LSL_Float llGetObjectMass(string id)
        {
            return _LSL_Functions.llGetObjectMass(id);
        }

        public LSL_String llGetObjectName()
        {
            return _LSL_Functions.llGetObjectName();
        }

        public LSL_Integer llGetObjectPermMask(int mask)
        {
            return _LSL_Functions.llGetObjectPermMask(mask);
        }

        public LSL_Integer llGetObjectPrimCount(LSL_Key object_id)
        {
            return _LSL_Functions.llGetObjectPrimCount(object_id);
        }

        public LSL_Vector llGetOmega()
        {
            return _LSL_Functions.llGetOmega();
        }

        public LSL_Key llGetOwner()
        {
            return _LSL_Functions.llGetOwner();
        }

        public LSL_Key llGetOwnerKey(string id)
        {
            return _LSL_Functions.llGetOwnerKey(id);
        }

        public LSL_List llGetParcelDetails(LSL_Vector pos, LSL_List param)
        {
            return _LSL_Functions.llGetParcelDetails(pos, param);
        }

        public LSL_Integer llGetParcelFlags(LSL_Vector pos)
        {
            return _LSL_Functions.llGetParcelFlags(pos);
        }

        public LSL_Integer llGetParcelMaxPrims(LSL_Vector pos, int si_wide)
        {
            return _LSL_Functions.llGetParcelMaxPrims(pos, si_wide);
        }

        public LSL_String llGetParcelMusicURL()
        {
            return _LSL_Functions.llGetParcelMusicURL();
        }

        public LSL_Integer llGetParcelPrimCount(LSL_Vector pos, int category, int si_wide)
        {
            return _LSL_Functions.llGetParcelPrimCount(pos, category, si_wide);
        }

        public LSL_List llGetParcelPrimOwners(LSL_Vector pos)
        {
            return _LSL_Functions.llGetParcelPrimOwners(pos);
        }

        public LSL_Integer llGetPermissions()
        {
            return _LSL_Functions.llGetPermissions();
        }

        public LSL_Key llGetPermissionsKey()
        {
            return _LSL_Functions.llGetPermissionsKey();
        }

        public LSL_Vector llGetPos()
        {
            return _LSL_Functions.llGetPos();
        }

        public LSL_List llGetPrimitiveParams(LSL_List rules)
        {
            return _LSL_Functions.llGetPrimitiveParams(rules);
        }

        public LSL_List llGetLinkPrimitiveParams(int linknum, LSL_List rules)
        {
            return _LSL_Functions.llGetLinkPrimitiveParams(linknum, rules);
        }

        public LSL_Integer llGetRegionAgentCount()
        {
            return _LSL_Functions.llGetRegionAgentCount();
        }

        public LSL_Vector llGetRegionCorner()
        {
            return _LSL_Functions.llGetRegionCorner();
        }

        public LSL_Integer llGetRegionFlags()
        {
            return _LSL_Functions.llGetRegionFlags();
        }

        public LSL_Float llGetRegionFPS()
        {
            return _LSL_Functions.llGetRegionFPS();
        }

        public LSL_String llGetRegionName()
        {
            return _LSL_Functions.llGetRegionName();
        }

        public LSL_Float llGetRegionTimeDilation()
        {
            return _LSL_Functions.llGetRegionTimeDilation();
        }

        public LSL_Vector llGetRootPosition()
        {
            return _LSL_Functions.llGetRootPosition();
        }

        public LSL_Rotation llGetRootRotation()
        {
            return _LSL_Functions.llGetRootRotation();
        }

        public LSL_Rotation llGetRot()
        {
            return _LSL_Functions.llGetRot();
        }

        public LSL_Vector llGetScale()
        {
            return _LSL_Functions.llGetScale();
        }

        public LSL_String llGetScriptName()
        {
            return _LSL_Functions.llGetScriptName();
        }

        public LSL_Integer llGetScriptState(string name)
        {
            return _LSL_Functions.llGetScriptState(name);
        }

        public LSL_String llGetSimulatorHostname()
        {
            return _LSL_Functions.llGetSimulatorHostname();
        }

        public LSL_Integer llGetSPMaxMemory()
        {
            return _LSL_Functions.llGetSPMaxMemory();
        }

        public LSL_Integer llGetStartParameter()
        {
            return _LSL_Functions.llGetStartParameter();
        }

        public LSL_Integer llGetStatus(int status)
        {
            return _LSL_Functions.llGetStatus(status);
        }

        public LSL_String llGetSubString(string src, int start, int end)
        {
            return _LSL_Functions.llGetSubString(src, start, end);
        }

        public LSL_String llGetTexture(int face)
        {
            return _LSL_Functions.llGetTexture(face);
        }

        public LSL_Vector llGetTextureOffset(int face)
        {
            return _LSL_Functions.llGetTextureOffset(face);
        }

        public LSL_Float llGetTextureRot(int side)
        {
            return _LSL_Functions.llGetTextureRot(side);
        }

        public LSL_Vector llGetTextureScale(int side)
        {
            return _LSL_Functions.llGetTextureScale(side);
        }

        public LSL_Float llGetTime()
        {
            return _LSL_Functions.llGetTime();
        }

        public LSL_Float llGetTimeOfDay()
        {
            return _LSL_Functions.llGetTimeOfDay();
        }

        public LSL_String llGetTimestamp()
        {
            return _LSL_Functions.llGetTimestamp();
        }

        public LSL_Vector llGetTorque()
        {
            return _LSL_Functions.llGetTorque();
        }

        public LSL_Integer llGetUnixTime()
        {
            return _LSL_Functions.llGetUnixTime();
        }

        public LSL_Vector llGetVel()
        {
            return _LSL_Functions.llGetVel();
        }

        public LSL_Float llGetWallclock()
        {
            return _LSL_Functions.llGetWallclock();
        }

        public void llGiveInventory(LSL_Key destination, LSL_String inventory)
        {
            _LSL_Functions.llGiveInventory(destination, inventory);
        }

        public void llGiveInventoryList(LSL_Key destination, LSL_String category, LSL_List inventory)
        {
            _LSL_Functions.llGiveInventoryList(destination, category, inventory);
        }

        public LSL_Integer llGiveMoney(LSL_Key destination, LSL_Integer amount)
        {
            return _LSL_Functions.llGiveMoney(destination, amount);
        }

        public LSL_Key llTransferLindenDollars(LSL_Key destination, LSL_Integer amount)
        {
            return _LSL_Functions.llTransferLindenDollars(destination, amount);
        }

        public void llGodLikeRezObject(LSL_String inventory, LSL_Vector pos)
        {
            _LSL_Functions.llGodLikeRezObject(inventory, pos);
        }

        public LSL_Float llGround(LSL_Vector offset)
        {
            return _LSL_Functions.llGround(offset);
        }

        public LSL_Vector llGroundContour(LSL_Vector offset)
        {
            return _LSL_Functions.llGroundContour(offset);
        }

        public LSL_Vector llGroundNormal(LSL_Vector offset)
        {
            return _LSL_Functions.llGroundNormal(offset);
        }

        public void llGroundRepel(double height, int water, double tau)
        {
            _LSL_Functions.llGroundRepel(height, water, tau);
        }

        public LSL_Vector llGroundSlope(LSL_Vector offset)
        {
            return _LSL_Functions.llGroundSlope(offset);
        }

        public LSL_Key llHTTPRequest(LSL_String url, LSL_List parameters, LSL_String body)
        {
            return _LSL_Functions.llHTTPRequest(url, parameters, body);
        }

        public void llHTTPResponse(LSL_Key id, int status, LSL_String body)
        {
            _LSL_Functions.llHTTPResponse(id, status, body);
        }

        public LSL_String llInsertString(LSL_String dst, int position, LSL_String src)
        {
            return _LSL_Functions.llInsertString(dst, position, src);
        }

        public void llInstantMessage(LSL_String user, LSL_String message)
        {
            _LSL_Functions.llInstantMessage(user, message);
        }

        public LSL_String llIntegerToBase64(int number)
        {
            return _LSL_Functions.llIntegerToBase64(number);
        }

        public LSL_String llKey2Name(LSL_Key id)
        {
            return _LSL_Functions.llKey2Name(id);
        }

        public LSL_String llGetUsername(LSL_Key id)
        {
            return _LSL_Functions.llGetUsername(id);
        }

        public LSL_Key llRequestUsername(LSL_Key id)
        {
            return _LSL_Functions.llRequestUsername(id);
        }

        public LSL_String llGetDisplayName(LSL_Key id)
        {
            return _LSL_Functions.llGetDisplayName(id);
        }

        public LSL_Key llRequestDisplayName(LSL_Key id)
        {
            return _LSL_Functions.llRequestDisplayName(id);
        }

        public LSL_List llCastRay(LSL_Vector start, LSL_Vector end, LSL_List options)
        {
            return _LSL_Functions.llCastRay(start, end, options);
        }

        public void llLinkParticleSystem(int linknum, LSL_List rules)
        {
            _LSL_Functions.llLinkParticleSystem(linknum, rules);
        }

        public LSL_String llList2CSV(LSL_List src)
        {
            return _LSL_Functions.llList2CSV(src);
        }

        public LSL_Float llList2Float(LSL_List src, int index)
        {
            return _LSL_Functions.llList2Float(src, index);
        }

        public LSL_Integer llList2Integer(LSL_List src, int index)
        {
            return _LSL_Functions.llList2Integer(src, index);
        }

        public LSL_Key llList2Key(LSL_List src, int index)
        {
            return _LSL_Functions.llList2Key(src, index);
        }

        public LSL_List llList2List(LSL_List src, int start, int end)
        {
            return _LSL_Functions.llList2List(src, start, end);
        }

        public LSL_List llList2ListStrided(LSL_List src, int start, int end, int stride)
        {
            return _LSL_Functions.llList2ListStrided(src, start, end, stride);
        }

        public LSL_Rotation llList2Rot(LSL_List src, int index)
        {
            return _LSL_Functions.llList2Rot(src, index);
        }

        public LSL_String llList2String(LSL_List src, int index)
        {
            return _LSL_Functions.llList2String(src, index);
        }

        public LSL_Vector llList2Vector(LSL_List src, int index)
        {
            return _LSL_Functions.llList2Vector(src, index);
        }

        public LSL_Integer llListen(int channelID, string name, string ID, string msg)
        {
            return _LSL_Functions.llListen(channelID, name, ID, msg);
        }

        public void llListenControl(int number, int active)
        {
            _LSL_Functions.llListenControl(number, active);
        }

        public void llListenRemove(int number)
        {
            _LSL_Functions.llListenRemove(number);
        }

        public LSL_Integer llListFindList(LSL_List src, LSL_List test)
        {
            return _LSL_Functions.llListFindList(src, test);
        }

        public LSL_List llListInsertList(LSL_List dest, LSL_List src, int start)
        {
            return _LSL_Functions.llListInsertList(dest, src, start);
        }

        public LSL_List llListRandomize(LSL_List src, int stride)
        {
            return _LSL_Functions.llListRandomize(src, stride);
        }

        public LSL_List llListReplaceList(LSL_List dest, LSL_List src, int start, int end)
        {
            return _LSL_Functions.llListReplaceList(dest, src, start, end);
        }

        public LSL_List llListSort(LSL_List src, int stride, int ascending)
        {
            return _LSL_Functions.llListSort(src, stride, ascending);
        }

        public LSL_Float llListStatistics(int operation, LSL_List src)
        {
            return _LSL_Functions.llListStatistics(operation, src);
        }

        public void llLoadURL(string avatar_id, string message, string url)
        {
            _LSL_Functions.llLoadURL(avatar_id, message, url);
        }

        public LSL_Float llLog(double val)
        {
            return _LSL_Functions.llLog(val);
        }

        public LSL_Float llLog10(double val)
        {
            return _LSL_Functions.llLog10(val);
        }

        public void llLookAt(LSL_Vector target, double strength, double damping)
        {
            _LSL_Functions.llLookAt(target, strength, damping);
        }

        public void llLoopSound(string sound, double volume)
        {
            _LSL_Functions.llLoopSound(sound, volume);
        }

        public void llLoopSoundMaster(string sound, double volume)
        {
            _LSL_Functions.llLoopSoundMaster(sound, volume);
        }

        public void llLoopSoundSlave(string sound, double volume)
        {
            _LSL_Functions.llLoopSoundSlave(sound, volume);
        }

        public LSL_Integer llManageEstateAccess(int action, string avatar)
        {
            return _LSL_Functions.llManageEstateAccess(action, avatar);
        }

        public void llMakeExplosion(int particles, double scale, double vel, double lifetime, double arc, string texture, LSL_Vector offset)
        {
            _LSL_Functions.llMakeExplosion(particles, scale, vel, lifetime, arc, texture, offset);
        }

        public void llMakeFire(int particles, double scale, double vel, double lifetime, double arc, string texture, LSL_Vector offset)
        {
            _LSL_Functions.llMakeFire(particles, scale, vel, lifetime, arc, texture, offset);
        }

        public void llMakeFountain(int particles, double scale, double vel, double lifetime, double arc, int bounce, string texture, LSL_Vector offset, double bounce_offset)
        {
            _LSL_Functions.llMakeFountain(particles, scale, vel, lifetime, arc, bounce, texture, offset, bounce_offset);
        }

        public void llMakeSmoke(int particles, double scale, double vel, double lifetime, double arc, string texture, LSL_Vector offset)
        {
            _LSL_Functions.llMakeSmoke(particles, scale, vel, lifetime, arc, texture, offset);
        }

        public void llMapDestination(string simname, LSL_Vector pos, LSL_Vector look_at)
        {
            _LSL_Functions.llMapDestination(simname, pos, look_at);
        }

        public LSL_String llMD5String(string src, int nonce)
        {
            return _LSL_Functions.llMD5String(src, nonce);
        }

        public LSL_String llSHA1String(string src)
        {
            return _LSL_Functions.llSHA1String(src);
        }

        public void llMessageLinked(int linknum, int num, string str, string id)
        {
            _LSL_Functions.llMessageLinked(linknum, num, str, id);
        }

        public void llMinEventDelay(double delay)
        {
            _LSL_Functions.llMinEventDelay(delay);
        }

        public void llModifyLand(int action, int brush)
        {
            _LSL_Functions.llModifyLand(action, brush);
        }

        public LSL_Integer llModPow(int a, int b, int c)
        {
            return _LSL_Functions.llModPow(a, b, c);
        }

        public void llMoveToTarget(LSL_Vector target, double tau)
        {
            _LSL_Functions.llMoveToTarget(target, tau);
        }

        public LSL_Key llName2Key(LSL_String name)
        {
            return _LSL_Functions.llName2Key(name);
        }

        public void llOffsetTexture(double u, double v, int face)
        {
            _LSL_Functions.llOffsetTexture(u, v, face);
        }

        public void llOpenRemoteDataChannel()
        {
            _LSL_Functions.llOpenRemoteDataChannel();
        }

        public LSL_Integer llOverMyLand(string id)
        {
            return _LSL_Functions.llOverMyLand(id);
        }

        public void llOwnerSay(string msg)
        {
            _LSL_Functions.llOwnerSay(msg);
        }

        public void llParcelMediaCommandList(LSL_List commandList)
        {
            _LSL_Functions.llParcelMediaCommandList(commandList);
        }

        public LSL_List llParcelMediaQuery(LSL_List aList)
        {
            return _LSL_Functions.llParcelMediaQuery(aList);
        }

        public LSL_List llParseString2List(string str, LSL_List separators, LSL_List spacers)
        {
            return _LSL_Functions.llParseString2List(str, separators, spacers);
        }

        public LSL_List llParseStringKeepNulls(string src, LSL_List seperators, LSL_List spacers)
        {
            return _LSL_Functions.llParseStringKeepNulls(src, seperators, spacers);
        }

        public void llParticleSystem(LSL_List rules)
        {
            _LSL_Functions.llParticleSystem(rules);
        }

        public void llPassCollisions(int pass)
        {
            _LSL_Functions.llPassCollisions(pass);
        }

        public void llPassTouches(int pass)
        {
            _LSL_Functions.llPassTouches(pass);
        }

        public void llPlaySound(string sound, double volume)
        {
            _LSL_Functions.llPlaySound(sound, volume);
        }

        public void llPlaySoundSlave(string sound, double volume)
        {
            _LSL_Functions.llPlaySoundSlave(sound, volume);
        }

        public void llPointAt(LSL_Vector pos)
        {
            _LSL_Functions.llPointAt(pos);
        }

        public LSL_Float llPow(double fbase, double fexponent)
        {
            return _LSL_Functions.llPow(fbase, fexponent);
        }

        public void llPreloadSound(string sound)
        {
            _LSL_Functions.llPreloadSound(sound);
        }

        public void llPushObject(string target, LSL_Vector impulse, LSL_Vector ang_impulse, int local)
        {
            _LSL_Functions.llPushObject(target, impulse, ang_impulse, local);
        }

        public void llRefreshPrimURL()
        {
            _LSL_Functions.llRefreshPrimURL();
        }

        public void llRegionSay(int channelID, string text)
        {
            _LSL_Functions.llRegionSay(channelID, text);
        }

        public void llRegionSayTo(string key, int channelID, string text)
        {
            _LSL_Functions.llRegionSayTo(key, channelID, text);
        }

        public void llReleaseCamera(string avatar)
        {
            _LSL_Functions.llReleaseCamera(avatar);
        }

        public void llReleaseURL(string url)
        {
            _LSL_Functions.llReleaseURL(url);
        }

        public void llReleaseControls()
        {
            _LSL_Functions.llReleaseControls();
        }

        public void llRemoteDataReply(string channel, string message_id, string sdata, int idata)
        {
            _LSL_Functions.llRemoteDataReply(channel, message_id, sdata, idata);
        }

        public void llRemoteDataSetRegion()
        {
            _LSL_Functions.llRemoteDataSetRegion();
        }

        public void llRemoteLoadScript(string target, string name, int running, int start_param)
        {
            _LSL_Functions.llRemoteLoadScript(target, name, running, start_param);
        }

        public void llRemoteLoadScriptPin(string target, string name, int pin, int running, int start_param)
        {
            _LSL_Functions.llRemoteLoadScriptPin(target, name, pin, running, start_param);
        }

        public void llRemoveFromLandBanList(string avatar)
        {
            _LSL_Functions.llRemoveFromLandBanList(avatar);
        }

        public void llRemoveFromLandPassList(string avatar)
        {
            _LSL_Functions.llRemoveFromLandPassList(avatar);
        }

        public void llRemoveInventory(string item)
        {
            _LSL_Functions.llRemoveInventory(item);
        }

        public void llRemoveVehicleFlags(int flags)
        {
            _LSL_Functions.llRemoveVehicleFlags(flags);
        }

        public LSL_Key llRequestUserKey(LSL_String username)
        {
            return _LSL_Functions.llRequestUserKey(username);
        }

        public LSL_Key llRequestAgentData(string id, int data)
        {
            return _LSL_Functions.llRequestAgentData(id, data);
        }

        public LSL_Key llRequestInventoryData(LSL_String name)
        {
            return _LSL_Functions.llRequestInventoryData(name);
        }

        public void llRequestPermissions(string agent, int perm)
        {
            _LSL_Functions.llRequestPermissions(agent, perm);
        }

        public LSL_Key llRequestSecureURL()
        {
            return _LSL_Functions.llRequestSecureURL();
        }

        public LSL_Key llRequestSimulatorData(string simulator, int data)
        {
            return _LSL_Functions.llRequestSimulatorData(simulator, data);
        }
        public LSL_Key llRequestURL()
        {
            return _LSL_Functions.llRequestURL();
        }

        public void llResetLandBanList()
        {
            _LSL_Functions.llResetLandBanList();
        }

        public void llResetLandPassList()
        {
            _LSL_Functions.llResetLandPassList();
        }

        public void llResetOtherScript(string name)
        {
            _LSL_Functions.llResetOtherScript(name);
        }

        public void llResetScript()
        {
            _LSL_Functions.llResetScript();
        }

        public void llResetTime()
        {
            _LSL_Functions.llResetTime();
        }

        public void llRezAtRoot(string inventory, LSL_Vector position, LSL_Vector velocity, LSL_Rotation rot, int param)
        {
            _LSL_Functions.llRezAtRoot(inventory, position, velocity, rot, param);
        }

        public void llRezObject(string inventory, LSL_Vector pos, LSL_Vector vel, LSL_Rotation rot, int param)
        {
            _LSL_Functions.llRezObject(inventory, pos, vel, rot, param);
        }

        public LSL_Float llRot2Angle(LSL_Rotation rot)
        {
            return _LSL_Functions.llRot2Angle(rot);
        }

        public LSL_Vector llRot2Axis(LSL_Rotation rot)
        {
            return _LSL_Functions.llRot2Axis(rot);
        }

        public LSL_Vector llRot2Euler(LSL_Rotation r)
        {
            return _LSL_Functions.llRot2Euler(r);
        }

        public LSL_Vector llRot2Fwd(LSL_Rotation r)
        {
            return _LSL_Functions.llRot2Fwd(r);
        }

        public LSL_Vector llRot2Left(LSL_Rotation r)
        {
            return _LSL_Functions.llRot2Left(r);
        }

        public LSL_Vector llRot2Up(LSL_Rotation r)
        {
            return _LSL_Functions.llRot2Up(r);
        }

        public void llRotateTexture(double rotation, int face)
        {
            _LSL_Functions.llRotateTexture(rotation, face);
        }

        public LSL_Rotation llRotBetween(LSL_Vector start, LSL_Vector end)
        {
            return _LSL_Functions.llRotBetween(start, end);
        }

        public void llRotLookAt(LSL_Rotation target, double strength, double damping)
        {
            _LSL_Functions.llRotLookAt(target, strength, damping);
        }

        public LSL_Integer llRotTarget(LSL_Rotation rot, double error)
        {
            return _LSL_Functions.llRotTarget(rot, error);
        }

        public void llRotTargetRemove(int number)
        {
            _LSL_Functions.llRotTargetRemove(number);
        }

        public LSL_Integer llRound(double f)
        {
            return _LSL_Functions.llRound(f);
        }

        public LSL_Integer llSameGroup(string agent)
        {
            return _LSL_Functions.llSameGroup(agent);
        }

        public void llSay(int channelID, string text)
        {
            _LSL_Functions.llSay(channelID, text);
        }

        public LSL_Integer llScaleByFactor(double scaling_factor)
        {
            return _LSL_Functions.llScaleByFactor(scaling_factor);
        }

        public LSL_Float llGetMaxScaleFactor()
        {
            return _LSL_Functions.llGetMaxScaleFactor();
        }

        public LSL_Float llGetMinScaleFactor()
        {
            return _LSL_Functions.llGetMinScaleFactor();
        }

        public void llScaleTexture(double u, double v, int face)
        {
            _LSL_Functions.llScaleTexture(u, v, face);
        }

        public LSL_Integer llScriptDanger(LSL_Vector pos)
        {
            return _LSL_Functions.llScriptDanger(pos);
        }

        public void llScriptProfiler(LSL_Integer flags)
        {
            _LSL_Functions.llScriptProfiler(flags);
        }

        public LSL_Key llSendRemoteData(string channel, string dest, int idata, string sdata)
        {
            return _LSL_Functions.llSendRemoteData(channel, dest, idata, sdata);
        }

        public void llSensor(string name, string id, int type, double range, double arc)
        {
            _LSL_Functions.llSensor(name, id, type, range, arc);
        }

        public void llSensorRemove()
        {
            _LSL_Functions.llSensorRemove();
        }

        public void llSensorRepeat(string name, string id, int type, double range, double arc, double rate)
        {
            _LSL_Functions.llSensorRepeat(name, id, type, range, arc, rate);
        }

        public void llSetAlpha(double alpha, int face)
        {
            _LSL_Functions.llSetAlpha(alpha, face);
        }

        public void llSetBuoyancy(double buoyancy)
        {
            _LSL_Functions.llSetBuoyancy(buoyancy);
        }

        public void llSetCameraAtOffset(LSL_Vector offset)
        {
            _LSL_Functions.llSetCameraAtOffset(offset);
        }

        public void llSetCameraEyeOffset(LSL_Vector offset)
        {
            _LSL_Functions.llSetCameraEyeOffset(offset);
        }

        public void llSetLinkCamera(LSL_Integer link, LSL_Vector eye, LSL_Vector at)
        {
            _LSL_Functions.llSetLinkCamera(link, eye, at);
        }

        public void llSetCameraParams(LSL_List rules)
        {
            _LSL_Functions.llSetCameraParams(rules);
        }

        public void llSetClickAction(int action)
        {
            _LSL_Functions.llSetClickAction(action);
        }

        public void llSetColor(LSL_Vector color, int face)
        {
            _LSL_Functions.llSetColor(color, face);
        }

        public void llSetContentType(LSL_Key id, LSL_Integer type)
        {
            _LSL_Functions.llSetContentType(id, type);
        }

        public void llSetDamage(double damage)
        {
            _LSL_Functions.llSetDamage(damage);
        }

        public void llSetForce(LSL_Vector force, int local)
        {
            _LSL_Functions.llSetForce(force, local);
        }

        public void llSetForceAndTorque(LSL_Vector force, LSL_Vector torque, int local)
        {
            _LSL_Functions.llSetForceAndTorque(force, torque, local);
        }

        public void llSetVelocity(LSL_Vector force, int local)
        {
            _LSL_Functions.llSetVelocity(force, local);
        }


        public void llSetAngularVelocity(LSL_Vector force, int local)
        {
            _LSL_Functions.llSetAngularVelocity(force, local);
        }

        public void llSetHoverHeight(double height, int water, double tau)
        {
            _LSL_Functions.llSetHoverHeight(height, water, tau);
        }

        public void llSetInventoryPermMask(string item, int mask, int value)
        {
            _LSL_Functions.llSetInventoryPermMask(item, mask, value);
        }

        public void llSetLinkAlpha(int linknumber, double alpha, int face)
        {
            _LSL_Functions.llSetLinkAlpha(linknumber, alpha, face);
        }

        public void llSetLinkColor(int linknumber, LSL_Vector color, int face)
        {
            _LSL_Functions.llSetLinkColor(linknumber, color, face);
        }

        public void llSetLinkPrimitiveParams(int linknumber, LSL_List rules)
        {
            _LSL_Functions.llSetLinkPrimitiveParams(linknumber, rules);
        }

        public void llSetLinkTexture(int linknumber, string texture, int face)
        {
            _LSL_Functions.llSetLinkTexture(linknumber, texture, face);
        }

        public void llSetLinkTextureAnim(int linknum, int mode, int face, int sizex, int sizey, double start, double length, double rate)
        {
            _LSL_Functions.llSetLinkTextureAnim(linknum, mode, face, sizex, sizey, start, length, rate);
        }

        public void llSetLocalRot(LSL_Rotation rot)
        {
            _LSL_Functions.llSetLocalRot(rot);
        }

        public LSL_Integer llSetMemoryLimit(LSL_Integer limit)
        {
            return _LSL_Functions.llSetMemoryLimit(limit);
        }

        public void llSetObjectDesc(string desc)
        {
            _LSL_Functions.llSetObjectDesc(desc);
        }

        public void llSetObjectName(string name)
        {
            _LSL_Functions.llSetObjectName(name);
        }

        public void llSetObjectPermMask(int mask, int value)
        {
            _LSL_Functions.llSetObjectPermMask(mask, value);
        }

        public void llSetParcelMusicURL(string url)
        {
            _LSL_Functions.llSetParcelMusicURL(url);
        }

        public void llSetPayPrice(int price, LSL_List quick_pay_buttons)
        {
            _LSL_Functions.llSetPayPrice(price, quick_pay_buttons);
        }

        public void llSetPos(LSL_Vector pos)
        {
            _LSL_Functions.llSetPos(pos);
        }

        public LSL_Integer llSetRegionPos(LSL_Vector pos)
        {
            return _LSL_Functions.llSetRegionPos(pos);
        }

        public void llSetPrimitiveParams(LSL_List rules)
        {
            _LSL_Functions.llSetPrimitiveParams(rules);
        }

        public void llSetLinkPrimitiveParamsFast(int linknum, LSL_List rules)
        {
            _LSL_Functions.llSetLinkPrimitiveParamsFast(linknum, rules);
        }

        public void llSetPrimURL(string url)
        {
            _LSL_Functions.llSetPrimURL(url);
        }

        public void llSetRemoteScriptAccessPin(int pin)
        {
            _LSL_Functions.llSetRemoteScriptAccessPin(pin);
        }

        public void llSetRot(LSL_Rotation rot)
        {
            _LSL_Functions.llSetRot(rot);
        }

        public void llSetScale(LSL_Vector scale)
        {
            _LSL_Functions.llSetScale(scale);
        }

        public void llSetScriptState(string name, int run)
        {
            _LSL_Functions.llSetScriptState(name, run);
        }

        public void llSetSitText(string text)
        {
            _LSL_Functions.llSetSitText(text);
        }

        public void llSetSoundQueueing(int queue)
        {
            _LSL_Functions.llSetSoundQueueing(queue);
        }

        public void llSetSoundRadius(double radius)
        {
            _LSL_Functions.llSetSoundRadius(radius);
        }

        public void llSetStatus(int status, int value)
        {
            _LSL_Functions.llSetStatus(status, value);
        }

        public void llSetText(string text, LSL_Vector color, double alpha)
        {
            _LSL_Functions.llSetText(text, color, alpha);
        }

        public void llSetTexture(string texture, int face)
        {
            _LSL_Functions.llSetTexture(texture, face);
        }

        public void llSetTextureAnim(int mode, int face, int sizex, int sizey, double start, double length, double rate)
        {
            _LSL_Functions.llSetTextureAnim(mode, face, sizex, sizey, start, length, rate);
        }

        public void llSetTimerEvent(double sec)
        {
            _LSL_Functions.llSetTimerEvent(sec);
        }

        public void llSetTorque(LSL_Vector torque, int local)
        {
            _LSL_Functions.llSetTorque(torque, local);
        }

        public void llSetTouchText(string text)
        {
            _LSL_Functions.llSetTouchText(text);
        }

        public void llSetVehicleFlags(int flags)
        {
            _LSL_Functions.llSetVehicleFlags(flags);
        }

        public void llSetVehicleFloatParam(int param, LSL_Float value)
        {
            _LSL_Functions.llSetVehicleFloatParam(param, value);
        }

        public void llSetVehicleRotationParam(int param, LSL_Rotation rot)
        {
            _LSL_Functions.llSetVehicleRotationParam(param, rot);
        }

        public void llSetVehicleType(int type)
        {
            _LSL_Functions.llSetVehicleType(type);
        }

        public void llSetVehicleVectorParam(int param, LSL_Vector vec)
        {
            _LSL_Functions.llSetVehicleVectorParam(param, vec);
        }

        public void llShout(int channelID, string text)
        {
            _LSL_Functions.llShout(channelID, text);
        }

        public LSL_Float llSin(double f)
        {
            return _LSL_Functions.llSin(f);
        }

        public void llSitTarget(LSL_Vector offset, LSL_Rotation rot)
        {
            _LSL_Functions.llSitTarget(offset, rot);
        }

        public void llLinkSitTarget(LSL_Integer link, LSL_Vector offset, LSL_Rotation rot)
        {
            _LSL_Functions.llLinkSitTarget(link, offset, rot);
        }

        public void llSleep(double sec)
        {
            _LSL_Functions.llSleep(sec);
        }

        public void llSound(string sound, double volume, int queue, int loop)
        {
            _LSL_Functions.llSound(sound, volume, queue, loop);
        }

        public void llSoundPreload(string sound)
        {
            _LSL_Functions.llSoundPreload(sound);
        }

        public LSL_Float llSqrt(double f)
        {
            return _LSL_Functions.llSqrt(f);
        }

        public void llStartAnimation(string anim)
        {
            _LSL_Functions.llStartAnimation(anim);
        }

        public void llStopAnimation(string anim)
        {
            _LSL_Functions.llStopAnimation(anim);
        }

        public void llStartObjectAnimation(string anim)
        {
            _LSL_Functions.llStartObjectAnimation(anim);
        }

        public void llStopObjectAnimation(string anim)
        {
            _LSL_Functions.llStopObjectAnimation(anim);
        }

        public LSL_List llGetObjectAnimationNames()
        {
            return _LSL_Functions.llGetObjectAnimationNames();
        }

        public void llStopHover()
        {
            _LSL_Functions.llStopHover();
        }

        public void llStopLookAt()
        {
            _LSL_Functions.llStopLookAt();
        }

        public void llStopMoveToTarget()
        {
            _LSL_Functions.llStopMoveToTarget();
        }

        public void llStopPointAt()
        {
            _LSL_Functions.llStopPointAt();
        }

        public void llStopSound()
        {
            _LSL_Functions.llStopSound();
        }

        public LSL_Integer llStringLength(string str)
        {
            return _LSL_Functions.llStringLength(str);
        }

        public LSL_String llStringToBase64(string str)
        {
            return _LSL_Functions.llStringToBase64(str);
        }

        public LSL_String llStringTrim(LSL_String src, LSL_Integer type)
        {
            return _LSL_Functions.llStringTrim(src, type);
        }

        public LSL_Integer llSubStringIndex(string source, string pattern)
        {
            return _LSL_Functions.llSubStringIndex(source, pattern);
        }

        public void llTakeCamera(string avatar)
        {
            _LSL_Functions.llTakeCamera(avatar);
        }

        public void llTakeControls(int controls, int accept, int pass_on)
        {
            _LSL_Functions.llTakeControls(controls, accept, pass_on);
        }

        public LSL_Float llTan(double f)
        {
            return _LSL_Functions.llTan(f);
        }

        public LSL_Integer llTarget(LSL_Vector position, double range)
        {
            return _LSL_Functions.llTarget(position, range);
        }

        public void llTargetOmega(LSL_Vector axis, double spinrate, double gain)
        {
            _LSL_Functions.llTargetOmega(axis, spinrate, gain);
        }

        public void llTargetRemove(int number)
        {
            _LSL_Functions.llTargetRemove(number);
        }

        public void llTargetedEmail(LSL_Integer target, LSL_String subject, LSL_String message)
        {
            _LSL_Functions.llTargetedEmail(target, subject, message);
        }

        public void llTeleportAgent(string agent, string simname, LSL_Vector pos, LSL_Vector lookAt)
        {
            _LSL_Functions.llTeleportAgent(agent, simname, pos, lookAt);
        }

        public void llTeleportAgentGlobalCoords(string agent, LSL_Vector global, LSL_Vector pos, LSL_Vector lookAt)
        {
            _LSL_Functions.llTeleportAgentGlobalCoords(agent, global, pos, lookAt);
        }

        public void llTeleportAgentHome(string agent)
        {
            _LSL_Functions.llTeleportAgentHome(agent);
        }

        public void llTextBox(string avatar, string message, int chat_channel)
        {
            _LSL_Functions.llTextBox(avatar, message, chat_channel);
        }

        public LSL_String llToLower(string source)
        {
            return _LSL_Functions.llToLower(source);
        }

        public LSL_String llToUpper(string source)
        {
            return _LSL_Functions.llToUpper(source);
        }

        public void llTriggerSound(string sound, double volume)
        {
            _LSL_Functions.llTriggerSound(sound, volume);
        }

        public void llTriggerSoundLimited(string sound, double volume, LSL_Vector top_north_east, LSL_Vector botto_south_west)
        {
            _LSL_Functions.llTriggerSoundLimited(sound, volume, top_north_east, botto_south_west);
        }

        public LSL_String llUnescapeURL(string url)
        {
            return _LSL_Functions.llUnescapeURL(url);
        }

        public void llUnSit(string id)
        {
            _LSL_Functions.llUnSit(id);
        }

        public LSL_Float llVecDist(LSL_Vector a, LSL_Vector b)
        {
            return _LSL_Functions.llVecDist(a, b);
        }

        public LSL_Float llVecMag(LSL_Vector v)
        {
            return _LSL_Functions.llVecMag(v);
        }

        public LSL_Vector llVecNorm(LSL_Vector v)
        {
            return _LSL_Functions.llVecNorm(v);
        }

        public void llVolumeDetect(int detect)
        {
            _LSL_Functions.llVolumeDetect(detect);
        }

        public LSL_Float llWater(LSL_Vector offset)
        {
            return _LSL_Functions.llWater(offset);
        }

        public void llWhisper(int channelID, string text)
        {
            _LSL_Functions.llWhisper(channelID, text);
        }

        public LSL_Vector llWind(LSL_Vector offset)
        {
            return _LSL_Functions.llWind(offset);
        }

        public LSL_String llXorBase64(string str1, string str2)
        {
            return _LSL_Functions.llXorBase64(str1, str2);
        }

        public LSL_String llXorBase64Strings(string str1, string str2)
        {
            return _LSL_Functions.llXorBase64Strings(str1, str2);
        }

        public LSL_String llXorBase64StringsCorrect(string str1, string str2)
        {
            return _LSL_Functions.llXorBase64StringsCorrect(str1, str2);
        }

        public LSL_List llGetPrimMediaParams(int face, LSL_List rules)
        {
            return _LSL_Functions.llGetPrimMediaParams(face, rules);
        }

        public LSL_List llGetLinkMedia(LSL_Integer link, LSL_Integer face, LSL_List rules)
        {
            return _LSL_Functions.llGetLinkMedia(link, face, rules);
        }

        public LSL_Integer llSetPrimMediaParams(int face, LSL_List rules)
        {
            return _LSL_Functions.llSetPrimMediaParams(face, rules);
        }

        public LSL_Integer llSetLinkMedia(LSL_Integer link, LSL_Integer face, LSL_List rules)
        {
            return _LSL_Functions.llSetLinkMedia(link, face, rules);
        }

        public LSL_Integer llClearPrimMedia(LSL_Integer face)
        {
            return _LSL_Functions.llClearPrimMedia(face);
        }

        public LSL_Integer llClearLinkMedia(LSL_Integer link, LSL_Integer face)
        {
            return _LSL_Functions.llClearLinkMedia(link, face);
        }

        public LSL_Integer llGetLinkNumberOfSides(LSL_Integer link)
        {
            return _LSL_Functions.llGetLinkNumberOfSides(link);
        }

        public void llSetKeyframedMotion(LSL_List frames, LSL_List options)
        {
            _LSL_Functions.llSetKeyframedMotion(frames, options);
        }

        public void llSetPhysicsMaterial(int material_bits, LSL_Float material_gravity_modifier, LSL_Float material_restitution, LSL_Float material_friction, LSL_Float material_density)
        {
            _LSL_Functions.llSetPhysicsMaterial(material_bits, material_gravity_modifier, material_restitution, material_friction, material_density);
        }

        public LSL_List llGetPhysicsMaterial()
        {
            return _LSL_Functions.llGetPhysicsMaterial();
        }

        public void llSetAnimationOverride(LSL_String animState, LSL_String anim)
        {
            _LSL_Functions.llSetAnimationOverride(animState, anim);
        }

        public void llResetAnimationOverride(LSL_String ani_state)
        {
            _LSL_Functions.llResetAnimationOverride(ani_state);
        }

        public LSL_String llGetAnimationOverride(LSL_String ani_state)
        {
            return _LSL_Functions.llGetAnimationOverride(ani_state);
        }

        public LSL_String llJsonGetValue(LSL_String json, LSL_List specifiers)
        {
            return _LSL_Functions.llJsonGetValue(json, specifiers);
        }

        public LSL_List llJson2List(LSL_String json)
        {
            return _LSL_Functions.llJson2List(json);
        }

        public LSL_String llList2Json(LSL_String type, LSL_List values)
        {
            return _LSL_Functions.llList2Json(type, values);
        }

        public LSL_String llJsonSetValue(LSL_String json, LSL_List specifiers, LSL_String value)
        {
            return _LSL_Functions.llJsonSetValue(json, specifiers, value);
        }

        public LSL_String llJsonValueType(LSL_String json, LSL_List specifiers)
        {
            return _LSL_Functions.llJsonValueType(json, specifiers);
        }

        public LSL_Integer llGetDayLength()
        {
            return _LSL_Functions.llGetDayLength();
        }

        public LSL_Integer llGetRegionDayLength()
        {
            return _LSL_Functions.llGetRegionDayLength();
        }

        public LSL_Integer llGetDayOffset()
        {
            return _LSL_Functions.llGetDayOffset();
        }

        public LSL_Integer llGetRegionDayOffset()
        {
            return _LSL_Functions.llGetRegionDayOffset();
        }

        public LSL_Vector llGetSunDirection()
        {
            return _LSL_Functions.llGetSunDirection();
        }

        public LSL_Vector llGetRegionSunDirection()
        {
            return _LSL_Functions.llGetRegionSunDirection();
        }

        public LSL_Vector llGetMoonDirection()
        {
            return _LSL_Functions.llGetMoonDirection();
        }

        public LSL_Vector llGetRegionMoonDirection()
        {
            return _LSL_Functions.llGetRegionMoonDirection();
        }

        public LSL_Rotation llGetSunRotation()
        {
            return _LSL_Functions.llGetSunRotation();
        }

        public LSL_Rotation llGetRegionSunRotation()
        {
            return _LSL_Functions.llGetRegionSunRotation();
        }

        public LSL_Rotation llGetMoonRotation()
        {
            return _LSL_Functions.llGetMoonRotation();
        }

        public LSL_Rotation llGetRegionMoonRotation()
        {
            return _LSL_Functions.llGetRegionMoonRotation();
        }
    }
}
