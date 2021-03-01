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

using log4net;
using System;
using System.Collections;
using System.Reflection;
using OpenSim.Framework;

using OpenSim.Services.Interfaces;
using OpenMetaverse;
using Nwc.XmlRpc;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

namespace OpenSim.Services.Connectors
{
    public class LandServicesConnector : ILandService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        protected IGridService _GridService = null;

        public LandServicesConnector()
        {
        }

        public LandServicesConnector(IGridService gridServices)
        {
            Initialise(gridServices);
        }

        public virtual void Initialise(IGridService gridServices)
        {
            _GridService = gridServices;
        }

        public virtual LandData GetLandData(UUID scopeID, ulong regionHandle, uint x, uint y, out byte regionAccess)
        {
            LandData landData = null;

            IList paramList = new ArrayList();
            regionAccess = 42; // Default to adult. Better safe...

            try
            {
                uint xpos = 0, ypos = 0;
                Util.RegionHandleToWorldLoc(regionHandle, out xpos, out ypos);

                GridRegion info = _GridService.GetRegionByPosition(scopeID, (int)xpos, (int)ypos);
                if (info != null) // just to be sure
                {
                    string targetHandlestr = info.RegionHandle.ToString();
                    if( ypos == 0 ) //HG proxy?
                    {
                        // this is real region handle on hg proxies hack
                        targetHandlestr = info.RegionSecret;
                    }

                    Hashtable hash = new Hashtable();
                    hash["region_handle"] = targetHandlestr;
                    hash["x"] = x.ToString();
                    hash["y"] = y.ToString();
                    paramList.Add(hash);

                    XmlRpcRequest request = new XmlRpcRequest("land_data", paramList);
                    XmlRpcResponse response = request.Send(info.ServerURI, 10000);
                    if (response.IsFault)
                    {
                        _log.ErrorFormat("[LAND CONNECTOR]: remote call returned an error: {0}", response.FaultString);
                    }
                    else
                    {
                        hash = (Hashtable)response.Value;
                        try
                        {
                            landData = new LandData
                            {
                                AABBMax = Vector3.Parse((string)hash["AABBMax"]),
                                AABBMin = Vector3.Parse((string)hash["AABBMin"]),
                                Area = Convert.ToInt32(hash["Area"]),
                                AuctionID = Convert.ToUInt32(hash["AuctionID"]),
                                Description = (string)hash["Description"],
                                Flags = Convert.ToUInt32(hash["Flags"]),
                                GlobalID = new UUID((string)hash["GlobalID"]),
                                Name = (string)hash["Name"],
                                OwnerID = new UUID((string)hash["OwnerID"]),
                                SalePrice = Convert.ToInt32(hash["SalePrice"]),
                                SnapshotID = new UUID((string)hash["SnapshotID"]),
                                UserLocation = Vector3.Parse((string)hash["UserLocation"])
                            };
                            if (hash["RegionAccess"] != null)
                                regionAccess = (byte)Convert.ToInt32((string)hash["RegionAccess"]);
                            if(hash["Dwell"] != null)
                                landData.Dwell = Convert.ToSingle((string)hash["Dwell"]);
                            //_log.DebugFormat("[LAND CONNECTOR]: Got land data for parcel {0}", landData.Name);
                        }
                        catch (Exception e)
                        {
                            _log.ErrorFormat(
                                "[LAND CONNECTOR]: Got exception while parsing land-data: {0} {1}",
                                e.Message, e.StackTrace);
                        }
                    }
                }
                else
                    _log.WarnFormat("[LAND CONNECTOR]: Couldn't find region with handle {0}", regionHandle);
            }
            catch (Exception e)
            {
                _log.ErrorFormat(
                    "[LAND CONNECTOR]: Couldn't contact region {0}: {1} {2}", regionHandle, e.Message, e.StackTrace);
            }

            return landData;
        }
    }
}
