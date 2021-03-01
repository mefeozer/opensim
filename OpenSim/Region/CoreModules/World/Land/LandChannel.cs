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

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Land
{
    public class LandChannel : ILandChannel
    {
        #region Constants

        //Land types set with flags in ParcelOverlay.
        //Only one of these can be used.

        //RequestResults (I think these are right, they seem to work):
        public const int LAND_RESULT_MULTIPLE = 1; // The request they made contained more than a single peice of land
        public const int LAND_RESULT_SINGLE = 0; // The request they made contained only a single piece of land

        //ParcelSelectObjects
        public const int LAND_SELECT_OBJECTS_OWNER = 2;
        public const int LAND_SELECT_OBJECTS_GROUP = 4;
        public const int LAND_SELECT_OBJECTS_OTHER = 8;


        public const byte LAND_TYPE_PUBLIC = 0; //Equals 00000000
        // types 1 to 7 are exclusive
        public const byte LAND_TYPE_OWNED_BY_OTHER = 1; //Equals 00000001
        public const byte LAND_TYPE_OWNED_BY_GROUP = 2; //Equals 00000010
        public const byte LAND_TYPE_OWNED_BY_REQUESTER = 3; //Equals 00000011
        public const byte LAND_TYPE_IS_FOR_SALE = 4; //Equals 00000100
        public const byte LAND_TYPE_IS_BEING_AUCTIONED = 5; //Equals 00000101
        public const byte LAND_TYPE_unused6 = 6;
        public const byte LAND_TYPE_unused7 = 7;
        // next are flags
        public const byte LAND_FLAG_unused8 = 0x08; // this may become excluside in future
        public const byte LAND_FLAG_HIDEAVATARS = 0x10;
        public const byte LAND_FLAG_LOCALSOUND = 0x20;
        public const byte LAND_FLAG_PROPERTY_BORDER_WEST = 0x40; //Equals 01000000
        public const byte LAND_FLAG_PROPERTY_BORDER_SOUTH = 0x80; //Equals 10000000


        //These are other constants. Yay!
        public const int START_LAND_LOCAL_ID = 1;

        #endregion

        private readonly Scene _scene;
        private readonly LandManagementModule _landManagementModule;

        private float _BanLineSafeHeight = 100.0f;
        public float BanLineSafeHeight
        {
            get => _BanLineSafeHeight;
            private set
            {
                if (value >= 20f && value <= 5000f)
                    _BanLineSafeHeight = value;
                else
                    _BanLineSafeHeight = 100.0f;
            }
        }

        public LandChannel(Scene scene, LandManagementModule landManagementMod)
        {
            _scene = scene;
            _landManagementModule = landManagementMod;
            if(landManagementMod != null)
                _BanLineSafeHeight = landManagementMod.BanLineSafeHeight;
        }

        #region ILandChannel Members

        public ILandObject GetLandObject(float x_float, float y_float)
        {
            return _landManagementModule != null ? _landManagementModule.GetLandObject(x_float, y_float) : null;
        }

        public ILandObject GetLandObject(int localID)
        {
            return _landManagementModule != null ? _landManagementModule.GetLandObject(localID) : null;
        }

        public ILandObject GetLandObject(UUID GlobalID)
        {
                return _landManagementModule != null ? _landManagementModule.GetLandObject(GlobalID) : null;
        }

        public ILandObject GetLandObject(Vector3 position)
        {
            return GetLandObject(position.X, position.Y);
        }

        public ILandObject GetLandObject(int x, int y)
        {
            return _landManagementModule != null ? _landManagementModule.GetLandObject(x, y) : null;
        }

        public ILandObject GetLandObjectClippedXY(float x, float y)
        {
            return _landManagementModule != null ? _landManagementModule.GetLandObjectClippedXY(x, y) : null;
        }

        public List<ILandObject> AllParcels()
        {
            return _landManagementModule != null ? _landManagementModule.AllParcels() : new List<ILandObject>();
        }

        public void Clear(bool setupDefaultParcel)
        {
             _landManagementModule?.Clear(setupDefaultParcel);
        }

        public List<ILandObject> ParcelsNearPoint(Vector3 position)
        {
            return _landManagementModule != null ? _landManagementModule.ParcelsNearPoint(position) : new List<ILandObject>();
        }

        public bool IsForcefulBansAllowed()
        {
            return _landManagementModule != null ? _landManagementModule.AllowedForcefulBans : false;
        }

        public void UpdateLandObject(int localID, LandData data)
        {
            _landManagementModule?.UpdateLandObject(localID, data);
        }

        public void SendParcelsOverlay(IClientAPI client)
        {
            _landManagementModule?.SendParcelOverlay(client);
        }

        public void Join(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id)
        {
            _landManagementModule?.Join(start_x, start_y, end_x, end_y, attempting_user_id);
        }

        public void Subdivide(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id)
        {
            _landManagementModule?.Subdivide(start_x, start_y, end_x, end_y, attempting_user_id);
        }

        public void ReturnObjectsInParcel(int localID, uint returnType, UUID[] agentIDs, UUID[] taskIDs, IClientAPI remoteClient)
        {
            _landManagementModule?.ReturnObjectsInParcel(localID, returnType, agentIDs, taskIDs, remoteClient);
        }

        public void setParcelObjectMaxOverride(overrideParcelMaxPrimCountDelegate overrideDel)
        {
            _landManagementModule?.setParcelObjectMaxOverride(overrideDel);
        }

        public void setSimulatorObjectMaxOverride(overrideSimulatorMaxPrimCountDelegate overrideDel)
        {
            _landManagementModule?.setSimulatorObjectMaxOverride(overrideDel);
        }

        public void SetParcelOtherCleanTime(IClientAPI remoteClient, int localID, int otherCleanTime)
        {
            _landManagementModule?.SetParcelOtherCleanTime(remoteClient, localID, otherCleanTime);
        }

        public void sendClientInitialLandInfo(IClientAPI remoteClient, bool overlay)
        {
            _landManagementModule?.sendClientInitialLandInfo(remoteClient, overlay);
        }

        public void ClearAllEnvironments()
        {
            List<ILandObject> parcels = AllParcels();
            for(int i=0; i< parcels.Count; ++i)
                parcels[i].StoreEnvironment(null);
        }
        #endregion
    }
}
