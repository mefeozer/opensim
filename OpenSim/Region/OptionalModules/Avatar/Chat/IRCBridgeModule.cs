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
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using Nwc.XmlRpc;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.OptionalModules.Avatar.Chat
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "IRCBridgeModule")]
    public class IRCBridgeModule : INonSharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        internal static bool Enabled = false;
        internal static IConfig _config = null;

        internal static List<ChannelState> _channels = new List<ChannelState>();
        internal static List<RegionState> _regions = new List<RegionState>();

        internal static string _password = string.Empty;
        internal RegionState _region = null;

        #region INonSharedRegionModule Members

        public Type ReplaceableInterface => null;

        public string Name => "IRCBridgeModule";

        public void Initialise(IConfigSource config)
        {
            _config = config.Configs["IRC"];
            if (_config == null)
            {
                //                _log.InfoFormat("[IRC-Bridge] module not configured");
                return;
            }

            if (!_config.GetBoolean("enabled", false))
            {
                //                _log.InfoFormat("[IRC-Bridge] module disabled in configuration");
                _config = null;
                return;
            }

            if (config.Configs["RemoteAdmin"] != null)
            {
                _password = config.Configs["RemoteAdmin"].GetString("access_password", _password);
            }

            Enabled = true;

            _log.InfoFormat("[IRC-Bridge]: Module is enabled");
        }

        public void AddRegion(Scene scene)
        {
            if (Enabled)
            {
                try
                {
                    _log.InfoFormat("[IRC-Bridge] Connecting region {0}", scene.RegionInfo.RegionName);

                    if (!string.IsNullOrEmpty(_password))
                        MainServer.Instance.AddXmlRPCHandler("irc_admin", XmlRpcAdminMethod, false);

                    _region = new RegionState(scene, _config);
                    lock (_regions)
                        _regions.Add(_region);
                    _region.Open();
                }
                catch (Exception e)
                {
                    _log.WarnFormat("[IRC-Bridge] Region {0} not connected to IRC : {1}", scene.RegionInfo.RegionName, e.Message);
                    _log.Debug(e);
                }
            }
            else
            {
                //_log.DebugFormat("[IRC-Bridge] Not enabled. Connect for region {0} ignored", scene.RegionInfo.RegionName);
            }
        }


        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            if (!Enabled)
                return;

            if (_region == null)
                return;

            if (!string.IsNullOrEmpty(_password))
                MainServer.Instance.RemoveXmlRPCHandler("irc_admin");

            _region.Close();

            if (_regions.Contains(_region))
            {
                lock (_regions) _regions.Remove(_region);
            }
        }

        public void Close()
        {
        }
        #endregion

        public static XmlRpcResponse XmlRpcAdminMethod(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            _log.Debug("[IRC-Bridge]: XML RPC Admin Entry");

            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            try
            {
                Hashtable requestData = (Hashtable)request.Params[0];
                bool found = false;
                string region = string.Empty;

                if (!string.IsNullOrEmpty(_password))
                {
                    if (!requestData.ContainsKey("password"))
                        throw new Exception("Invalid request");
                    if ((string)requestData["password"] != _password)
                        throw new Exception("Invalid request");
                }

                if (!requestData.ContainsKey("region"))
                    throw new Exception("No region name specified");
                region = (string)requestData["region"];

                foreach (RegionState rs in _regions)
                {
                    if (rs.Region == region)
                    {
                        responseData["server"] = rs.cs.Server;
                        responseData["port"] = (int)rs.cs.Port;
                        responseData["user"] = rs.cs.User;
                        responseData["channel"] = rs.cs.IrcChannel;
                        responseData["enabled"] = rs.cs.irc.Enabled;
                        responseData["connected"] = rs.cs.irc.Connected;
                        responseData["nickname"] = rs.cs.irc.Nick;
                        found = true;
                        break;
                    }
                }

                if (!found) throw new Exception(string.Format("Region <{0}> not found", region));

                responseData["success"] = true;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[IRC-Bridge] XML RPC Admin request failed : {0}", e.Message);

                responseData["success"] = "false";
                responseData["error"] = e.Message;
            }
            finally
            {
                response.Value = responseData;
            }

            _log.Debug("[IRC-Bridge]: XML RPC Admin Exit");

            return response;
        }
    }
}
