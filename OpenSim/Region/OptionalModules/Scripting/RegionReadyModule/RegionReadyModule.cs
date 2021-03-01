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
using System.Collections.Generic;
using System.Reflection;
using System.Runtime;
using System.Net;
using System.IO;
using System.Text;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.OptionalModules.Scripting.RegionReady
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RegionReadyModule")]
    public class RegionReadyModule : IRegionReadyModule, INonSharedRegionModule
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private IConfig _config = null;
        private bool _firstEmptyCompileQueue;
        private bool _oarFileLoading;
        private bool _lastOarLoadedOk;
        private int _channelNotify = -1000;
        private bool _enabled = false;
        private bool _disable_logins;
        private string _uri = string.Empty;

        Scene _scene;

        #region INonSharedRegionModule interface

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource config)
        {
            _config = config.Configs["RegionReady"];
            if (_config != null)
            {
                _enabled = _config.GetBoolean("enabled", false);

                if (_enabled)
                {
                    _channelNotify = _config.GetInt("channel_notify", _channelNotify);
                    _disable_logins = _config.GetBoolean("login_disable", false);
                    _uri = _config.GetString("alert_uri",string.Empty);
                }
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _scene = scene;

            _scene.RegisterModuleInterface<IRegionReadyModule>(this);

            _firstEmptyCompileQueue = true;
            _oarFileLoading = false;
            _lastOarLoadedOk = true;

            _scene.EventManager.OnOarFileLoaded += OnOarFileLoaded;

            _log.DebugFormat("[RegionReady]: Enabled for region {0}", scene.RegionInfo.RegionName);

            if (_disable_logins)
            {
                _scene.LoginLock = true;
                _scene.EventManager.OnEmptyScriptCompileQueue += OnEmptyScriptCompileQueue;

                // This should always show up to the user but should not trigger warn/errors as these messages are
                // expected and are not simulator problems.  Ideally, there would be a status level in log4net but
                // failing that, we will print out to console instead.
                MainConsole.Instance.Output("Region {0} - LOGINS DISABLED DURING INITIALIZATION.", _scene.Name);

                if (!string.IsNullOrEmpty(_uri))
                {
                    RRAlert("disabled");
                }
            }
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _scene.EventManager.OnOarFileLoaded -= OnOarFileLoaded;

            if (_disable_logins)
                _scene.EventManager.OnEmptyScriptCompileQueue -= OnEmptyScriptCompileQueue;

            if (!string.IsNullOrEmpty(_uri))
                RRAlert("shutdown");

            _scene = null;
        }

        public void Close()
        {
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public string Name => "RegionReadyModule";

        #endregion

        void OnEmptyScriptCompileQueue(int numScriptsFailed, string message)
        {
            _log.DebugFormat("[RegionReady]: Script compile queue empty!");

            if (_firstEmptyCompileQueue || _oarFileLoading)
            {
                OSChatMessage c = new OSChatMessage();
                if (_firstEmptyCompileQueue)
                    c.Message = "server_startup,";
                else
                    c.Message = "oar_file_load,";
                _firstEmptyCompileQueue = false;
                _oarFileLoading = false;

                _scene.Backup(false);

                c.From = "RegionReady";
                if (_lastOarLoadedOk)
                    c.Message += "1,";
                else
                    c.Message += "0,";
                c.Channel = _channelNotify;
                c.Message += numScriptsFailed.ToString() + "," + message;
                c.Type = ChatTypeEnum.Region;
                if (_scene != null)
                    c.Position = new Vector3(_scene.RegionInfo.RegionSizeX * 0.5f, _scene.RegionInfo.RegionSizeY * 0.5f, 30);
                else
                    c.Position = new Vector3((int)Constants.RegionSize * 0.5f, (int)Constants.RegionSize * 0.5f, 30);
                c.Sender = null;
                c.SenderUUID = UUID.Zero;
                c.Scene = _scene;

                _log.DebugFormat("[RegionReady]: Region \"{0}\" is ready: \"{1}\" on channel {2}",
                                 _scene.RegionInfo.RegionName, c.Message, _channelNotify);

                _scene.EventManager.TriggerOnChatBroadcast(this, c);

                TriggerRegionReady(_scene);
            }
        }

        void OnOarFileLoaded(Guid requestId, List<UUID> loadedScenes, string message)
        {
            _oarFileLoading = true;

            if (string.IsNullOrEmpty(message))
            {
                _lastOarLoadedOk = true;
            }
            else
            {
                _log.WarnFormat("[RegionReady]: Oar file load errors: {0}", message);
                _lastOarLoadedOk = false;
            }
        }

        /// <summary>
        /// This will be triggered by Scene directly if it contains no scripts on startup.  Otherwise it is triggered
        /// when the script compile queue is empty after initial region startup.
        /// </summary>
        /// <param name='scene'></param>
        public void TriggerRegionReady(IScene scene)
        {
            _scene.EventManager.OnEmptyScriptCompileQueue -= OnEmptyScriptCompileQueue;
            _scene.LoginLock = false;

            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;

            if (!_scene.StartDisabled)
            {
                _scene.LoginsEnabled = true;

                // _log.InfoFormat("[RegionReady]: Logins enabled for {0}, Oar {1}",
                //                 _scene.RegionInfo.RegionName, _oarFileLoading.ToString());

                // Putting this out to console to make it eye-catching for people who are running OpenSimulator
                // without info log messages enabled.  Making this a warning is arguably misleading since it isn't a
                // warning, and monitor scripts looking for warn/error/fatal messages will received false positives.
                // Arguably, log4net needs a status log level (like Apache).
                MainConsole.Instance.Output("INITIALIZATION COMPLETE FOR {0} - LOGINS ENABLED", _scene.Name);
            }

            _scene.SceneGridService.InformNeighborsThatRegionisUp(
                _scene.RequestModuleInterface<INeighbourService>(), _scene.RegionInfo);

            if (!string.IsNullOrEmpty(_uri))
            {
                RRAlert("enabled");
            }

            _scene.Ready = true;
        }

        public void OarLoadingAlert(string msg)
        {
            // Let's bypass this for now until some better feedback can be established
            //

//            if (msg == "load")
//            {
//                _scene.EventManager.OnEmptyScriptCompileQueue += OnEmptyScriptCompileQueue;
//                _scene.EventManager.OnOarFileLoaded += OnOarFileLoaded;
//                _scene.EventManager.OnLoginsEnabled += OnLoginsEnabled;
//                _scene.EventManager.OnRezScript  += OnRezScript;
//                _oarFileLoading = true;
//                _firstEmptyCompileQueue = true;
//
//                _scene.LoginsDisabled = true;
//                _scene.LoginLock = true;
//                if ( _uri != string.Empty )
//                {
//                    RRAlert("loading oar");
//                    RRAlert("disabled");
//                }
//            }
        }

        public void RRAlert(string status)
        {
            string request_method = "POST";
            string content_type = "application/json";
            OSDMap RRAlert = new OSDMap();

            RRAlert["alert"] = "region_ready";
            RRAlert["login"] = status;
            RRAlert["region_name"] = _scene.RegionInfo.RegionName;
            RRAlert["region_id"] = _scene.RegionInfo.RegionID;

            string strBuffer = "";
            byte[] buffer = new byte[1];
            try
            {
                strBuffer = OSDParser.SerializeJsonString(RRAlert);
                Encoding str = Util.UTF8;
                buffer = str.GetBytes(strBuffer);

            }
            catch (Exception e)
            {
                _log.WarnFormat("[RegionReady]: Exception thrown on alert: {0}", e.Message);
            }

            WebRequest request = WebRequest.Create(_uri);
            request.Method = request_method;
            request.ContentType = content_type;

            Stream os = null;
            try
            {
                request.ContentLength = buffer.Length;
                os = request.GetRequestStream();
                os.Write(buffer, 0, strBuffer.Length);
            }
            catch(Exception e)
            {
                _log.WarnFormat("[RegionReady]: Exception thrown sending alert: {0}", e.Message);
            }
            finally
            {
                if (os != null)
                    os.Dispose();
            }
        }
    }
}
