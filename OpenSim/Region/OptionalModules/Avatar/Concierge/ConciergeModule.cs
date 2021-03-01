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
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using log4net;
using Mono.Addins;
using Nini.Config;
using Nwc.XmlRpc;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.CoreModules.Avatar.Chat;

namespace OpenSim.Region.OptionalModules.Avatar.Concierge
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "ConciergeModule")]
    public class ConciergeModule : ChatModule, ISharedRegionModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

//        private const int DEBUG_CHANNEL = 2147483647; use base value

        private new readonly List<IScene> _scenes = new List<IScene>();
        private readonly List<IScene> _conciergedScenes = new List<IScene>();

        private bool _replacingChatModule = false;

        private string _whoami = "conferencier";
        private Regex _regions = null;
        private string _welcomes = null;
        private int _conciergeChannel = 42;
        private string _announceEntering = "{0} enters {1} (now {2} visitors in this region)";
        private string _announceLeaving = "{0} leaves {1} (back to {2} visitors in this region)";
        private string _xmlRpcPassword = string.Empty;
        private string _brokerURI = string.Empty;
        private int _brokerUpdateTimeout = 300;

        internal new object _syncy = new object();

        internal new bool _enabled = false;

        #region ISharedRegionModule Members
        public override void Initialise(IConfigSource configSource)
        {
            IConfig config = configSource.Configs["Concierge"];

            if (config == null)
                return;

            if (!config.GetBoolean("enabled", false))
                return;

            _enabled = true;

            // check whether ChatModule has been disabled: if yes,
            // then we'll "stand in"
            try
            {
                if (configSource.Configs["Chat"] == null)
                {
                    // if Chat module has not been configured it's
                    // enabled by default, so we are not going to
                    // replace it.
                    _replacingChatModule = false;
                }
                else
                {
                    _replacingChatModule  = !configSource.Configs["Chat"].GetBoolean("enabled", true);
                }
            }
            catch (Exception)
            {
                _replacingChatModule = false;
            }

            _log.InfoFormat("[Concierge] {0} ChatModule", _replacingChatModule ? "replacing" : "not replacing");

            // take note of concierge channel and of identity
            _conciergeChannel = configSource.Configs["Concierge"].GetInt("concierge_channel", _conciergeChannel);
            _whoami = config.GetString("whoami", "conferencier");
            _welcomes = config.GetString("welcomes", _welcomes);
            _announceEntering = config.GetString("announce_entering", _announceEntering);
            _announceLeaving = config.GetString("announce_leaving", _announceLeaving);
            _xmlRpcPassword = config.GetString("password", _xmlRpcPassword);
            _brokerURI = config.GetString("broker", _brokerURI);
            _brokerUpdateTimeout = config.GetInt("broker_timeout", _brokerUpdateTimeout);

            _log.InfoFormat("[Concierge] reporting as \"{0}\" to our users", _whoami);

            // calculate regions Regex
            if (_regions == null)
            {
                string regions = config.GetString("regions", string.Empty);
                if (!string.IsNullOrEmpty(regions))
                {
                    _regions = new Regex(@regions, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
            }
        }

        public override void AddRegion(Scene scene)
        {
            if (!_enabled) return;

            MainServer.Instance.AddXmlRPCHandler("concierge_update_welcome", XmlRpcUpdateWelcomeMethod, false);

            lock (_syncy)
            {
                if (!_scenes.Contains(scene))
                {
                    _scenes.Add(scene);

                    if (_regions == null || _regions.IsMatch(scene.RegionInfo.RegionName))
                        _conciergedScenes.Add(scene);

                    // subscribe to NewClient events
                    scene.EventManager.OnNewClient += OnNewClient;

                    // subscribe to *Chat events
                    scene.EventManager.OnChatFromWorld += OnChatFromWorld;
                    if (!_replacingChatModule)
                        scene.EventManager.OnChatFromClient += OnChatFromClient;
                    scene.EventManager.OnChatBroadcast += OnChatBroadcast;

                    // subscribe to agent change events
                    scene.EventManager.OnMakeRootAgent += OnMakeRootAgent;
                    scene.EventManager.OnMakeChildAgent += OnMakeChildAgent;
                }
            }
            _log.InfoFormat("[Concierge]: initialized for {0}", scene.RegionInfo.RegionName);
        }

        public override void RemoveRegion(Scene scene)
        {
            if (!_enabled) return;

            MainServer.Instance.RemoveXmlRPCHandler("concierge_update_welcome");

            lock (_syncy)
            {
                // unsubscribe from NewClient events
                scene.EventManager.OnNewClient -= OnNewClient;

                // unsubscribe from *Chat events
                scene.EventManager.OnChatFromWorld -= OnChatFromWorld;
                if (!_replacingChatModule)
                    scene.EventManager.OnChatFromClient -= OnChatFromClient;
                scene.EventManager.OnChatBroadcast -= OnChatBroadcast;

                // unsubscribe from agent change events
                scene.EventManager.OnMakeRootAgent -= OnMakeRootAgent;
                scene.EventManager.OnMakeChildAgent -= OnMakeChildAgent;

                if (_scenes.Contains(scene))
                {
                    _scenes.Remove(scene);
                }

                if (_conciergedScenes.Contains(scene))
                {
                    _conciergedScenes.Remove(scene);
                }
            }
            _log.InfoFormat("[Concierge]: removed {0}", scene.RegionInfo.RegionName);
        }

        public override void PostInitialise()
        {
        }

        public override void Close()
        {
        }

        new public Type ReplaceableInterface => null;

        public override string Name => "ConciergeModule";

        #endregion

        #region ISimChat Members
        public override void OnChatBroadcast(object sender, OSChatMessage c)
        {
            if (_replacingChatModule)
            {
                // distribute chat message to each and every avatar in
                // the region
                base.OnChatBroadcast(sender, c);
            }

            // TODO: capture logic
            return;
        }

        public override void OnChatFromClient(object sender, OSChatMessage c)
        {
            if (_replacingChatModule)
            {
                // replacing ChatModule: need to redistribute
                // ChatFromClient to interested subscribers
                c = FixPositionOfChatMessage(c);

                Scene scene = (Scene)c.Scene;
                scene.EventManager.TriggerOnChatFromClient(sender, c);

                if (_conciergedScenes.Contains(c.Scene))
                {
                    // when we are replacing ChatModule, we treat
                    // OnChatFromClient like OnChatBroadcast for
                    // concierged regions, effectively extending the
                    // range of chat to cover the whole
                    // region. however, we don't do this for whisper
                    // (got to have some privacy)
                    if (c.Type != ChatTypeEnum.Whisper)
                    {
                        base.OnChatBroadcast(sender, c);
                        return;
                    }
                }

                // redistribution will be done by base class
                base.OnChatFromClient(sender, c);
            }

            // TODO: capture chat
            return;
        }

        public override void OnChatFromWorld(object sender, OSChatMessage c)
        {
            if (_replacingChatModule)
            {
                if (_conciergedScenes.Contains(c.Scene))
                {
                    // when we are replacing ChatModule, we treat
                    // OnChatFromClient like OnChatBroadcast for
                    // concierged regions, effectively extending the
                    // range of chat to cover the whole
                    // region. however, we don't do this for whisper
                    // (got to have some privacy)
                    if (c.Type != ChatTypeEnum.Whisper)
                    {
                        base.OnChatBroadcast(sender, c);
                        return;
                    }
                }

                base.OnChatFromWorld(sender, c);
            }
            return;
        }
        #endregion


        public override void OnNewClient(IClientAPI client)
        {
            client.OnLogout += OnClientLoggedOut;

            if (_replacingChatModule)
                client.OnChatFromClient += OnChatFromClient;
        }



        public void OnClientLoggedOut(IClientAPI client)
        {
            client.OnLogout -= OnClientLoggedOut;
            client.OnConnectionClosed -= OnClientLoggedOut;

            if (_conciergedScenes.Contains(client.Scene))
            {
                Scene scene = client.Scene as Scene;
                _log.DebugFormat("[Concierge]: {0} logs off from {1}", client.Name, scene.RegionInfo.RegionName);
                AnnounceToAgentsRegion(scene, string.Format(_announceLeaving, client.Name, scene.RegionInfo.RegionName, scene.GetRootAgentCount()));
                UpdateBroker(scene);
            }
        }


        public void OnMakeRootAgent(ScenePresence agent)
        {
            if (_conciergedScenes.Contains(agent.Scene))
            {
                Scene scene = agent.Scene;
                _log.DebugFormat("[Concierge]: {0} enters {1}", agent.Name, scene.RegionInfo.RegionName);
                WelcomeAvatar(agent, scene);
                AnnounceToAgentsRegion(scene, string.Format(_announceEntering, agent.Name,
                                                            scene.RegionInfo.RegionName, scene.GetRootAgentCount()));
                UpdateBroker(scene);
            }
        }


        public void OnMakeChildAgent(ScenePresence agent)
        {
            if (_conciergedScenes.Contains(agent.Scene))
            {
                Scene scene = agent.Scene;
                _log.DebugFormat("[Concierge]: {0} leaves {1}", agent.Name, scene.RegionInfo.RegionName);
                AnnounceToAgentsRegion(scene, string.Format(_announceLeaving, agent.Name,
                                                            scene.RegionInfo.RegionName, scene.GetRootAgentCount()));
                UpdateBroker(scene);
            }
        }

        internal class BrokerState
        {
            public string Uri;
            public string Payload;
            public HttpWebRequest Poster;
            public Timer Timer;

            public BrokerState(string uri, string payload, HttpWebRequest poster)
            {
                Uri = uri;
                Payload = payload;
                Poster = poster;
            }
        }

        protected void UpdateBroker(Scene scene)
        {
            if (string.IsNullOrEmpty(_brokerURI))
                return;

            string uri = string.Format(_brokerURI, scene.RegionInfo.RegionName, scene.RegionInfo.RegionID);

            // create XML sniplet
            StringBuilder list = new StringBuilder();
            list.Append(string.Format("<avatars count=\"{0}\" region_name=\"{1}\" region_uuid=\"{2}\" timestamp=\"{3}\">\n",
                                          scene.GetRootAgentCount(), scene.RegionInfo.RegionName,
                                          scene.RegionInfo.RegionID,
                                          DateTime.UtcNow.ToString("s")));

            scene.ForEachRootScenePresence(delegate(ScenePresence sp)
            {
                list.Append(string.Format("    <avatar name=\"{0}\" uuid=\"{1}\" />\n", sp.Name, sp.UUID));
            });

            list.Append("</avatars>");
            string payload = list.ToString();

            // post via REST to broker
            HttpWebRequest updatePost = WebRequest.Create(uri) as HttpWebRequest;
            updatePost.Method = "POST";
            updatePost.ContentType = "text/xml";
            updatePost.ContentLength = payload.Length;
            updatePost.UserAgent = "OpenSim.Concierge";


            BrokerState bs = new BrokerState(uri, payload, updatePost);
            bs.Timer = new Timer(delegate(object state)
                                 {
                                     BrokerState b = state as BrokerState;
                                     b.Poster.Abort();
                                     b.Timer.Dispose();
                                     _log.Debug("[Concierge]: async broker POST abort due to timeout");
                                 }, bs, _brokerUpdateTimeout * 1000, Timeout.Infinite);

            try
            {
                updatePost.BeginGetRequestStream(UpdateBrokerSend, bs);
                _log.DebugFormat("[Concierge] async broker POST to {0} started", uri);
            }
            catch (WebException we)
            {
                _log.ErrorFormat("[Concierge] async broker POST to {0} failed: {1}", uri, we.Status);
            }
        }

        private void UpdateBrokerSend(IAsyncResult result)
        {
            BrokerState bs = null;
            try
            {
                bs = result.AsyncState as BrokerState;
                string payload = bs.Payload;
                HttpWebRequest updatePost = bs.Poster;

                using (StreamWriter payloadStream = new StreamWriter(updatePost.EndGetRequestStream(result)))
                {
                    payloadStream.Write(payload);
                    payloadStream.Close();
                }
                updatePost.BeginGetResponse(UpdateBrokerDone, bs);
            }
            catch (WebException we)
            {
                _log.DebugFormat("[Concierge]: async broker POST to {0} failed: {1}", bs.Uri, we.Status);
            }
            catch (Exception)
            {
                _log.DebugFormat("[Concierge]: async broker POST to {0} failed", bs.Uri);
            }
        }

        private void UpdateBrokerDone(IAsyncResult result)
        {
            BrokerState bs = null;
            try
            {
                bs = result.AsyncState as BrokerState;
                HttpWebRequest updatePost = bs.Poster;
                using (HttpWebResponse response = updatePost.EndGetResponse(result) as HttpWebResponse)
                {
                    _log.DebugFormat("[Concierge] broker update: status {0}", response.StatusCode);
                }
                bs.Timer.Dispose();
            }
            catch (WebException we)
            {
                _log.ErrorFormat("[Concierge] broker update to {0} failed with status {1}", bs.Uri, we.Status);
                if (null != we.Response)
                {
                    using (HttpWebResponse resp = we.Response as HttpWebResponse)
                    {
                        _log.ErrorFormat("[Concierge] response from {0} status code: {1}", bs.Uri, resp.StatusCode);
                        _log.ErrorFormat("[Concierge] response from {0} status desc: {1}", bs.Uri, resp.StatusDescription);
                        _log.ErrorFormat("[Concierge] response from {0} server:      {1}", bs.Uri, resp.Server);

                        if (resp.ContentLength > 0)
                        {
                            using(StreamReader content = new StreamReader(resp.GetResponseStream()))
                                _log.ErrorFormat("[Concierge] response from {0} content:     {1}", bs.Uri, content.ReadToEnd());
                        }
                    }
                }
            }
        }

        protected void WelcomeAvatar(ScenePresence agent, Scene scene)
        {
            // welcome mechanics: check whether we have a welcomes
            // directory set and wether there is a region specific
            // welcome file there: if yes, send it to the agent
            if (!string.IsNullOrEmpty(_welcomes))
            {
                string[] welcomes = new string[] {
                    Path.Combine(_welcomes, agent.Scene.RegionInfo.RegionName),
                    Path.Combine(_welcomes, "DEFAULT")};
                foreach (string welcome in welcomes)
                {
                    if (File.Exists(welcome))
                    {
                        try
                        {
                            string[] welcomeLines = File.ReadAllLines(welcome);
                            foreach (string l in welcomeLines)
                            {
                                AnnounceToAgent(agent, string.Format(l, agent.Name, scene.RegionInfo.RegionName, _whoami));
                            }
                        }
                        catch (IOException ioe)
                        {
                            _log.ErrorFormat("[Concierge]: run into trouble reading welcome file {0} for region {1} for avatar {2}: {3}",
                                             welcome, scene.RegionInfo.RegionName, agent.Name, ioe);
                        }
                        catch (FormatException fe)
                        {
                            _log.ErrorFormat("[Concierge]: welcome file {0} is malformed: {1}", welcome, fe);
                        }
                    }
                    return;
                }
                _log.DebugFormat("[Concierge]: no welcome message for region {0}", scene.RegionInfo.RegionName);
            }
        }

        static private readonly Vector3 PosOfGod = new Vector3(128, 128, 9999);

        // protected void AnnounceToAgentsRegion(Scene scene, string msg)
        // {
        //     ScenePresence agent = null;
        //     if ((client.Scene is Scene) && (client.Scene as Scene).TryGetScenePresence(client.AgentId, out agent))
        //         AnnounceToAgentsRegion(agent, msg);
        //     else
        //         _log.DebugFormat("[Concierge]: could not find an agent for client {0}", client.Name);
        // }

        protected void AnnounceToAgentsRegion(IScene scene, string msg)
        {
            OSChatMessage c = new OSChatMessage
            {
                Message = msg,
                Type = ChatTypeEnum.Say,
                Channel = 0,
                Position = PosOfGod,
                From = _whoami,
                Sender = null,
                SenderUUID = UUID.Zero,
                Scene = scene
            };

            if (scene is Scene)
                (scene as Scene).EventManager.TriggerOnChatBroadcast(this, c);
        }

        protected void AnnounceToAgent(ScenePresence agent, string msg)
        {
            OSChatMessage c = new OSChatMessage
            {
                Message = msg,
                Type = ChatTypeEnum.Say,
                Channel = 0,
                Position = PosOfGod,
                From = _whoami,
                Sender = null,
                SenderUUID = UUID.Zero,
                Scene = agent.Scene
            };

            agent.ControllingClient.SendChatMessage(
                msg, (byte) ChatTypeEnum.Say, PosOfGod, _whoami, UUID.Zero, UUID.Zero,
                 (byte)ChatSourceType.Object, (byte)ChatAudibleLevel.Fully);
        }

        private static void checkStringParameters(XmlRpcRequest request, string[] param)
        {
            Hashtable requestData = (Hashtable) request.Params[0];
            foreach (string p in param)
            {
                if (!requestData.Contains(p))
                    throw new Exception(string.Format("missing string parameter {0}", p));
                if (string.IsNullOrEmpty((string)requestData[p]))
                    throw new Exception(string.Format("parameter {0} is empty", p));
            }
        }

        public XmlRpcResponse XmlRpcUpdateWelcomeMethod(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            _log.Info("[Concierge]: processing UpdateWelcome request");
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            try
            {
                Hashtable requestData = (Hashtable)request.Params[0];
                checkStringParameters(request, new string[] { "password", "region", "welcome" });

                // check password
                if (!string.IsNullOrEmpty(_xmlRpcPassword) &&
                    (string)requestData["password"] != _xmlRpcPassword) throw new Exception("wrong password");

                if (string.IsNullOrEmpty(_welcomes))
                    throw new Exception("welcome templates are not enabled, ask your OpenSim operator to set the \"welcomes\" option in the [Concierge] section of OpenSim.ini");

                string msg = (string)requestData["welcome"];
                if (string.IsNullOrEmpty(msg))
                    throw new Exception("empty parameter \"welcome\"");

                string regionName = (string)requestData["region"];
                IScene scene = _scenes.Find(delegate(IScene s) { return s.RegionInfo.RegionName == regionName; });
                if (scene == null)
                    throw new Exception(string.Format("unknown region \"{0}\"", regionName));

                if (!_conciergedScenes.Contains(scene))
                    throw new Exception(string.Format("region \"{0}\" is not a concierged region.", regionName));

                string welcome = Path.Combine(_welcomes, regionName);
                if (File.Exists(welcome))
                {
                    _log.InfoFormat("[Concierge]: UpdateWelcome: updating existing template \"{0}\"", welcome);
                    string welcomeBackup = string.Format("{0}~", welcome);
                    if (File.Exists(welcomeBackup))
                        File.Delete(welcomeBackup);
                    File.Move(welcome, welcomeBackup);
                }
                File.WriteAllText(welcome, msg);

                responseData["success"] = "true";
                response.Value = responseData;
            }
            catch (Exception e)
            {
                _log.InfoFormat("[Concierge]: UpdateWelcome failed: {0}", e.Message);

                responseData["success"] = "false";
                responseData["error"] = e.Message;

                response.Value = responseData;
            }
            _log.Debug("[Concierge]: done processing UpdateWelcome request");
            return response;
        }
    }
}
