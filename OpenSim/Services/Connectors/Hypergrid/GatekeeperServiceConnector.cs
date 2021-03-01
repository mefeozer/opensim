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
using System.Collections;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenMetaverse;
using OpenMetaverse.Imaging;
using Nwc.XmlRpc;
using log4net;

using OpenSim.Services.Connectors.Simulation;

namespace OpenSim.Services.Connectors.Hypergrid
{
    public class GatekeeperServiceConnector : SimulationServiceConnector
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly UUID _HGMapImage = new UUID("00000000-0000-1111-9999-000000000013");

        private readonly IAssetService _AssetService;

        public GatekeeperServiceConnector()
            : base()
        {
        }

        public GatekeeperServiceConnector(IAssetService assService)
        {
            _AssetService = assService;
        }

        protected override string AgentPath()
        {
            return "foreignagent/";
        }

        protected override string ObjectPath()
        {
            return "foreignobject/";
        }

        public bool LinkRegion(GridRegion info, out UUID regionID, out ulong realHandle, out string externalName, out string imageURL, out string reason, out int sizeX, out int sizeY)
        {
            regionID = UUID.Zero;
            imageURL = string.Empty;
            realHandle = 0;
            externalName = string.Empty;
            reason = string.Empty;
            sizeX = (int)Constants.RegionSize;
            sizeY = (int)Constants.RegionSize;

            Hashtable hash = new Hashtable();
            hash["region_name"] = info.RegionName;

            IList paramList = new ArrayList();
            paramList.Add(hash);

            XmlRpcRequest request = new XmlRpcRequest("link_region", paramList);
            _log.Debug("[GATEKEEPER SERVICE CONNECTOR]: Linking to " + info.ServerURI);
            XmlRpcResponse response = null;
            try
            {
                response = request.Send(info.ServerURI, 10000);
            }
            catch (Exception e)
            {
                _log.Debug("[GATEKEEPER SERVICE CONNECTOR]: Exception " + e.Message);
                reason = "Error contacting remote server";
                return false;
            }

            if (response.IsFault)
            {
                reason = response.FaultString;
                _log.ErrorFormat("[GATEKEEPER SERVICE CONNECTOR]: remote call returned an error: {0}", response.FaultString);
                return false;
            }

            hash = (Hashtable)response.Value;
            //foreach (Object o in hash)
            //    _log.Debug(">> " + ((DictionaryEntry)o).Key + ":" + ((DictionaryEntry)o).Value);
            try
            {
                bool success = false;
                bool.TryParse((string)hash["result"], out success);
                if (success)
                {
                    UUID.TryParse((string)hash["uuid"], out regionID);
                    //_log.Debug(">> HERE, uuid: " + regionID);
                    if ((string)hash["handle"] != null)
                    {
                        realHandle = Convert.ToUInt64((string)hash["handle"]);
                        //_log.Debug(">> HERE, realHandle: " + realHandle);
                    }
                    if (hash["region_image"] != null)
                    {
                        imageURL = (string)hash["region_image"];
                        //_log.Debug(">> HERE, imageURL: " + imageURL);
                    }
                    if (hash["external_name"] != null)
                    {
                        externalName = (string)hash["external_name"];
                        //_log.Debug(">> HERE, externalName: " + externalName);
                    }
                    if (hash["size_x"] != null)
                    {
                        int.TryParse((string)hash["size_x"], out sizeX);
                    }
                    if (hash["size_y"] != null)
                    {
                        int.TryParse((string)hash["size_y"], out sizeY);
                    }
                }
            }
            catch (Exception e)
            {
                reason = "Error parsing return arguments";
                _log.Error("[GATEKEEPER SERVICE CONNECTOR]: Got exception while parsing hyperlink response " + e.StackTrace);
                return false;
            }

            return true;
        }

        public UUID GetMapImage(UUID regionID, string imageURL, string storagePath)
        {
            if (_AssetService == null)
            {
                _log.DebugFormat("[GATEKEEPER SERVICE CONNECTOR]: No AssetService defined. Map tile not retrieved.");
                return _HGMapImage;
            }

            UUID mapTile = _HGMapImage;
            string filename = string.Empty;

            try
            {
                //_log.Debug("JPEG: " + imageURL);
                string name = regionID.ToString();
                filename = Path.Combine(storagePath, name + ".jpg");
                _log.DebugFormat("[GATEKEEPER SERVICE CONNECTOR]: Map image at {0}, cached at {1}", imageURL, filename);
                if (!File.Exists(filename))
                {
                    _log.DebugFormat("[GATEKEEPER SERVICE CONNECTOR]: downloading...");
                    using(WebClient c = new WebClient())
                        c.DownloadFile(imageURL, filename);
                }
                else
                {
                    _log.DebugFormat("[GATEKEEPER SERVICE CONNECTOR]: using cached image");
                }

                byte[] imageData = null;

                using (Bitmap bitmap = new Bitmap(filename))
                {
                    //_log.Debug("Size: " + m.PhysicalDimension.Height + "-" + m.PhysicalDimension.Width);
                    imageData = OpenJPEG.EncodeFromImage(bitmap, false);
                }

                AssetBase ass = new AssetBase(UUID.Random(), "region " + name, (sbyte)AssetType.Texture, regionID.ToString())
                {

                    // !!! for now
                    //info.RegionSettings.TerrainImageID = ass.FullID;

                    Data = imageData
                };

                _AssetService.Store(ass);

                // finally
                mapTile = ass.FullID;
            }
            catch // LEGIT: Catching problems caused by OpenJPEG p/invoke
            {
                _log.Info("[GATEKEEPER SERVICE CONNECTOR]: Failed getting/storing map image, because it is probably already in the cache");
            }
            return mapTile;
        }

        public GridRegion GetHyperlinkRegion(GridRegion gatekeeper, UUID regionID, UUID agentID, string agentHomeURI, out string message)
        {
            Hashtable hash = new Hashtable();
            hash["region_uuid"] = regionID.ToString();
            if (agentID != UUID.Zero)
            {
                hash["agent_id"] = agentID.ToString();
                if (agentHomeURI != null)
                    hash["agent_home_uri"] = agentHomeURI;
            }

            IList paramList = new ArrayList();
            paramList.Add(hash);

            XmlRpcRequest request = new XmlRpcRequest("get_region", paramList);
            _log.Debug("[GATEKEEPER SERVICE CONNECTOR]: contacting " + gatekeeper.ServerURI);
            XmlRpcResponse response = null;
            try
            {
                response = request.Send(gatekeeper.ServerURI, 10000);
            }
            catch (Exception e)
            {
                message = "Error contacting grid.";
                _log.Debug("[GATEKEEPER SERVICE CONNECTOR]: Exception " + e.Message);
                return null;
            }

            if (response.IsFault)
            {
                message = "Error contacting grid.";
                _log.ErrorFormat("[GATEKEEPER SERVICE CONNECTOR]: remote call returned an error: {0}", response.FaultString);
                return null;
            }

            hash = (Hashtable)response.Value;
            //foreach (Object o in hash)
            //    _log.Debug(">> " + ((DictionaryEntry)o).Key + ":" + ((DictionaryEntry)o).Value);
            try
            {
                bool success = false;
                bool.TryParse((string)hash["result"], out success);

                if (hash["message"] != null)
                    message = (string)hash["message"];
                else if (success)
                    message = null;
                else
                    message = "The teleport destination could not be found.";   // probably the dest grid is old and doesn't send 'message', but the most common problem is that the region is unavailable

                if (success)
                {
                    GridRegion region = new GridRegion();

                    UUID.TryParse((string)hash["uuid"], out region.RegionID);
                    //_log.Debug(">> HERE, uuid: " + region.RegionID);
                    int n = 0;
                    if (hash["x"] != null)
                    {
                        int.TryParse((string)hash["x"], out n);
                        region.RegionLocX = n;
                        //_log.Debug(">> HERE, x: " + region.RegionLocX);
                    }
                    if (hash["y"] != null)
                    {
                        int.TryParse((string)hash["y"], out n);
                        region.RegionLocY = n;
                        //_log.Debug(">> HERE, y: " + region.RegionLocY);
                    }
                    if (hash["size_x"] != null)
                    {
                        int.TryParse((string)hash["size_x"], out n);
                        region.RegionSizeX = n;
                        //_log.Debug(">> HERE, x: " + region.RegionLocX);
                    }
                    if (hash["size_y"] != null)
                    {
                        int.TryParse((string)hash["size_y"], out n);
                        region.RegionSizeY = n;
                        //_log.Debug(">> HERE, y: " + region.RegionLocY);
                    }
                    if (hash["region_name"] != null)
                    {
                        region.RegionName = (string)hash["region_name"];
                        //_log.Debug(">> HERE, region_name: " + region.RegionName);
                    }
                    if (hash["hostname"] != null)
                    {
                        region.ExternalHostName = (string)hash["hostname"];
                        //_log.Debug(">> HERE, hostname: " + region.ExternalHostName);
                    }
                    if (hash["http_port"] != null)
                    {
                        uint p = 0;
                        uint.TryParse((string)hash["http_port"], out p);
                        region.HttpPort = p;
                        //_log.Debug(">> HERE, http_port: " + region.HttpPort);
                    }
                    if (hash["internal_port"] != null)
                    {
                        int p = 0;
                        int.TryParse((string)hash["internal_port"], out p);
                        region.InternalEndPoint = new IPEndPoint(IPAddress.Parse("0.0.0.0"), p);
                        //_log.Debug(">> HERE, internal_port: " + region.InternalEndPoint);
                    }

                    if (hash["server_uri"] != null)
                    {
                        region.ServerURI = (string)hash["server_uri"];
                        //_log.Debug(">> HERE, server_uri: " + region.ServerURI);
                    }

                    // Successful return
                    return region;
                }

            }
            catch (Exception e)
            {
                message = "Error parsing response from grid.";
                _log.Error("[GATEKEEPER SERVICE CONNECTOR]: Got exception while parsing hyperlink response " + e.StackTrace);
                return null;
            }

            return null;
        }
    }
}
