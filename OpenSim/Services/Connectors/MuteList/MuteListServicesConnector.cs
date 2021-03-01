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
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;
using OpenMetaverse;

namespace OpenSim.Services.Connectors
{
    public class MuteListServicesConnector : BaseServiceConnector, IMuteListService
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private string _ServerURI = string.Empty;

        public MuteListServicesConnector()
        {
        }

        public MuteListServicesConnector(string serverURI)
        {
            _ServerURI = serverURI.TrimEnd('/') + "/mutelist";
        }

        public MuteListServicesConnector(IConfigSource source)
        {
            Initialise(source);
        }

        public virtual void Initialise(IConfigSource source)
        {
            IConfig gridConfig = source.Configs["MuteListService"];
            if (gridConfig == null)
            {
                _log.Error("[MUTELIST CONNECTOR]: MuteListService missing from configuration");
                throw new Exception("MuteList connector init error");
            }

            string serviceURI = gridConfig.GetString("MuteListServerURI",
                    string.Empty);

            if (string.IsNullOrEmpty(serviceURI))
            {
                _log.Error("[MUTELIST CONNECTOR]: No Server URI named in section GridUserService");
                throw new Exception("MuteList connector init error");
            }
            _ServerURI = serviceURI + "/mutelist";;
            base.Initialise(source, "MuteListService");
        }

        #region IMuteListService
        public byte[] MuteListRequest(UUID agentID, uint crc)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["METHOD"] = "get";
            sendData["agentid"] = agentID.ToString();
            sendData["mutecrc"] = crc.ToString();

            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", _ServerURI,
                                    ServerUtils.BuildQueryString(sendData), _Auth);
                if (!string.IsNullOrEmpty(reply))
                {
                    Dictionary<string, object> replyData = ServerUtils.ParseXmlResponse(reply);

                    if (replyData.ContainsKey("result"))
                    {
                        string datastr = replyData["result"].ToString();
                        if(string.IsNullOrWhiteSpace(datastr))
                            return null;
                        return Convert.FromBase64String(datastr);
                    }
                    else
                        _log.DebugFormat("[MUTELIST CONNECTOR]: get reply data does not contain result field");
                }
                else
                    _log.DebugFormat("[MUTELIST CONNECTOR]: get received empty reply");
            }
            catch (Exception e)
            {
                _log.DebugFormat("[MUTELIST CONNECTOR]: Exception when contacting server at {0}: {1}", _ServerURI, e.Message);
            }

            return null;
        }

        public bool UpdateMute(MuteData mute)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["METHOD"] = "update";
            sendData["agentid"] = mute.AgentID.ToString();
            sendData["muteid"] = mute.MuteID.ToString();
            if(mute.MuteType != 0)
                sendData["mutetype"] = mute.MuteType.ToString();
            if(mute.MuteFlags != 0)
                sendData["muteflags"] = mute.MuteFlags.ToString();
            sendData["mutestamp"] = mute.Stamp.ToString();
            if(!string.IsNullOrEmpty(mute.MuteName))
                sendData["mutename"] = mute.MuteName;

            return doSimplePost(ServerUtils.BuildQueryString(sendData), "update");
         }

        public bool RemoveMute(UUID agentID, UUID muteID, string muteName)
        {
            Dictionary<string, object> sendData = new Dictionary<string, object>();
            sendData["METHOD"] = "delete";
            sendData["agentid"] = agentID.ToString();
            sendData["muteid"] = muteID.ToString();
            if(!string.IsNullOrEmpty(muteName))
                sendData["mutename"] = muteName;

            return doSimplePost(ServerUtils.BuildQueryString(sendData), "remove");
        }

        #endregion IMuteListService

        private bool doSimplePost(string reqString, string meth)
        {
            try
            {
                string reply = SynchronousRestFormsRequester.MakeRequest("POST", _ServerURI, reqString, _Auth);
                if (!string.IsNullOrEmpty(reply))
                {
                    int indx = reply.IndexOf("success", StringComparison.InvariantCultureIgnoreCase);
                    if (indx > 0)
                        return true;
                    return false;
                }
                else
                    _log.DebugFormat("[MUTELIST CONNECTOR]: {0} received empty reply", meth);
            }
            catch (Exception e)
            {
                _log.DebugFormat("[MUTELIST CONNECTOR]: Exception when contacting server at {0}: {1}", _ServerURI, e.Message);
            }

            return false;
        }
    }
}
