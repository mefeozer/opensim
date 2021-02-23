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
using System.Web;
using System.Reflection;
using Nini.Config;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers.Base;
using log4net;

namespace OpenSim.Server.Handlers.Freeswitch
{
    public class FreeswitchServerConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly IFreeswitchService m_FreeswitchService;
        private readonly string m_ConfigName = "FreeswitchService";
        protected readonly string m_freeSwitchAPIPrefix = "/fsapi";

        public FreeswitchServerConnector(IConfigSource config, IHttpServer server, string configName) :
                base(config, server, configName)
        {
            if (!string.IsNullOrEmpty(configName))
                m_ConfigName = configName;

            IConfig serverConfig = config.Configs[m_ConfigName];
            if (serverConfig == null)
                throw new Exception(string.Format("No section '{0}' in config file", m_ConfigName));

            string freeswitchService = serverConfig.GetString("LocalServiceModule",
                    string.Empty);

            if (string.IsNullOrEmpty(freeswitchService))
                throw new Exception("No LocalServiceModule in config file");

            object[] args = new object[] { config };
            m_FreeswitchService =
                    ServerUtils.LoadPlugin<IFreeswitchService>(freeswitchService, args);

            server.AddHTTPHandler(string.Format("{0}/freeswitch-config", m_freeSwitchAPIPrefix), FreeSwitchConfigHTTPHandler);
            server.AddHTTPHandler(string.Format("{0}/region-config", m_freeSwitchAPIPrefix), RegionConfigHTTPHandler);
        }

        public Hashtable FreeSwitchConfigHTTPHandler(Hashtable request)
        {
            Hashtable response = new Hashtable();
            response["str_response_string"] = string.Empty;
            response["content_type"] = "text/plain";
            response["keepalive"] = false;
            response["int_response_code"] = 500;

            Hashtable requestBody = ParseRequestBody((string) request["body"]);

            string section = (string) requestBody["section"];

            if (section == "directory")
                response = m_FreeswitchService.HandleDirectoryRequest(requestBody);
            else if (section == "dialplan")
                response = m_FreeswitchService.HandleDialplanRequest(requestBody);
            else
                m_log.WarnFormat("[FreeSwitchVoice]: section was {0}", section);

            return response;
        }

        private Hashtable ParseRequestBody(string body)
        {
            Hashtable bodyParams = new Hashtable();
            // split string
            string [] nvps = body.Split(new char[] {'&'});

            foreach (string s in nvps)
            {
                if (s.Trim() != "")
                {
                    string [] nvp = s.Split(new char[] {'='});
                    bodyParams.Add(HttpUtility.UrlDecode(nvp[0]), HttpUtility.UrlDecode(nvp[1]));
                }
            }

            return bodyParams;
        }

        public Hashtable RegionConfigHTTPHandler(Hashtable request)
        {
            Hashtable response = new Hashtable();
            response["content_type"] = "text/json";
            response["keepalive"] = false;
            response["int_response_code"] = 200;

            response["str_response_string"] = m_FreeswitchService.GetJsonConfig();

            return response;
        }

    }
}
