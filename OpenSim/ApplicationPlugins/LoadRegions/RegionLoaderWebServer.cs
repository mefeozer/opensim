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
using System.IO;
using System.Net;
using System.Reflection;
using System.Xml;
using log4net;
using Nini.Config;
using OpenSim.Framework;

namespace OpenSim.ApplicationPlugins.LoadRegions
{
    public class RegionLoaderWebServer : IRegionLoader
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IConfigSource _configSource;

        public void SetIniConfigSource(IConfigSource configSource)
        {
            _configSource = configSource;
        }

        public RegionInfo[] LoadRegions()
        {
            int tries = 3;
            int wait = 2000;

            if (_configSource == null)
            {
                _log.Error("[WEBLOADER]: Unable to load configuration source!");
                return null;
            }
            else
            {
                IConfig startupConfig = (IConfig)_configSource.Configs["Startup"];
                string url = startupConfig.GetString("regionload_webserver_url", string.Empty).Trim();
                bool allowRegionless = startupConfig.GetBoolean("allow_regionless", false);

                if (string.IsNullOrEmpty(url))
                {
                    _log.Error("[WEBLOADER]: Unable to load webserver URL - URL was empty.");
                    return null;
                }
                else
                {
                    while (tries > 0)
                    {
                        RegionInfo[] regionInfos = new RegionInfo[] { };
                        int regionCount = 0;
                        HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(url);
                        webRequest.Timeout = 30000; //30 Second Timeout
                        _log.DebugFormat("[WEBLOADER]: Sending download request to {0}", url);

                        try
                        {
                            HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse();
                            _log.Debug("[WEBLOADER]: Downloading region information...");
                            StreamReader reader = new StreamReader(webResponse.GetResponseStream());
                            string xmlSource = string.Empty;
                            string tempStr = reader.ReadLine();
                            while (tempStr != null)
                            {
                                xmlSource = xmlSource + tempStr;
                                tempStr = reader.ReadLine();
                            }
                            _log.Debug("[WEBLOADER]: Done downloading region information from server. Total Bytes: " +
                                        xmlSource.Length);
                            XmlDocument xmlDoc = new XmlDocument();
                            xmlDoc.LoadXml(xmlSource);
                            if (xmlDoc.FirstChild.Name == "Nini")
                            {
                                regionCount = xmlDoc.FirstChild.ChildNodes.Count;

                                if (regionCount > 0)
                                {
                                    regionInfos = new RegionInfo[regionCount];
                                    int i;
                                    for (i = 0; i < xmlDoc.FirstChild.ChildNodes.Count; i++)
                                    {
                                        _log.Debug(xmlDoc.FirstChild.ChildNodes[i].OuterXml);
                                        regionInfos[i] =
                                            new RegionInfo("REGION CONFIG #" + (i + 1), xmlDoc.FirstChild.ChildNodes[i], false, _configSource);
                                    }
                                }
                            }
                        }
                        catch (WebException ex)
                        {
                            if (((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.NotFound)
                            {
                                if (!allowRegionless)
                                    throw ex;
                            }
                            else
                                throw ex;
                        }

                        if (regionCount > 0 || allowRegionless)
                            return regionInfos;

                        _log.Debug("[WEBLOADER]: Request yielded no regions.");
                        tries--;
                        if (tries > 0)
                        {
                            _log.Debug("[WEBLOADER]: Retrying");
                            System.Threading.Thread.Sleep(wait);
                        }
                    }

                    _log.Error("[WEBLOADER]: No region configs were available.");
                    return null;
                }
            }
        }
    }
}