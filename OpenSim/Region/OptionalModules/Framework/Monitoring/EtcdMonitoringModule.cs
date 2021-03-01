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
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using netcd;
using netcd.Serialization;
using netcd.Advanced;
using netcd.Advanced.Requests;

namespace OpenSim.Region.OptionalModules.Framework.Monitoring
{
    /// <summary>
    /// Allows to store monitoring data in etcd, a high availability
    /// name-value store.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "EtcdMonitoringModule")]
    public class EtcdMonitoringModule : INonSharedRegionModule, IEtcdModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Scene _scene;
        protected IEtcdClient _client;
        protected bool _enabled = false;
        protected string _etcdBasePath = string.Empty;
        protected bool _appendRegionID = true;

        public string Name => "EtcdMonitoringModule";

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            if (source.Configs["Etcd"] == null)
                return;

            IConfig etcdConfig = source.Configs["Etcd"];

            string etcdUrls = etcdConfig.GetString("EtcdUrls", string.Empty);
            if (string.IsNullOrEmpty(etcdUrls))
                return;

            _etcdBasePath = etcdConfig.GetString("BasePath", _etcdBasePath);
            _appendRegionID = etcdConfig.GetBoolean("AppendRegionID", _appendRegionID);

            if (!_etcdBasePath.EndsWith("/"))
                _etcdBasePath += "/";

            try
            {
                string[] endpoints = etcdUrls.Split(new char[] {','});
                List<Uri> uris = new List<Uri>();
                foreach (string endpoint in endpoints)
                    uris.Add(new Uri(endpoint.Trim()));

                _client = new EtcdClient(uris.ToArray(), new DefaultSerializer(), new DefaultSerializer());
            }
            catch (Exception e)
            {
                _log.DebugFormat("[ETCD]: Error initializing connection: " + e.ToString());
                return;
            }

            _log.DebugFormat("[ETCD]: Etcd module configured");
            _enabled = true;
        }

        public void Close()
        {
            //_client = null;
            _scene = null;
        }

        public void AddRegion(Scene scene)
        {
            _scene = scene;

            if (_enabled)
            {
                if (_appendRegionID)
                    _etcdBasePath += _scene.RegionInfo.RegionID.ToString() + "/";

                _log.DebugFormat("[ETCD]: Using base path {0} for all keys", _etcdBasePath);

                try
                {
                    _client.Advanced.CreateDirectory(new CreateDirectoryRequest() {Key = _etcdBasePath});
                }
                catch (Exception e)
                {
                    _log.ErrorFormat("Exception trying to create base path {0}: " + e.ToString(), _etcdBasePath);
                }

                scene.RegisterModuleInterface<IEtcdModule>(this);
            }
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public bool Store(string k, string v)
        {
            return Store(k, v, 0);
        }

        public bool Store(string k, string v, int ttl)
        {
            Response resp = _client.Advanced.SetKey(new SetKeyRequest() { Key = _etcdBasePath + k, Value = v, TimeToLive = ttl });

            if (resp == null)
                return false;

            if (resp.ErrorCode.HasValue)
            {
                _log.DebugFormat("[ETCD]: Error {0} ({1}) storing {2} => {3}", resp.Cause, (int)resp.ErrorCode, _etcdBasePath + k, v);

                return false;
            }

            return true;
        }

        public string Get(string k)
        {
            Response resp = _client.Advanced.GetKey(new GetKeyRequest() { Key = _etcdBasePath + k });

            if (resp == null)
                return string.Empty;

            if (resp.ErrorCode.HasValue)
            {
                _log.DebugFormat("[ETCD]: Error {0} ({1}) getting {2}", resp.Cause, (int)resp.ErrorCode, _etcdBasePath + k);

                return string.Empty;
            }

            return resp.Node.Value;
        }

        public void Delete(string k)
        {
            _client.Advanced.DeleteKey(new DeleteKeyRequest() { Key = _etcdBasePath + k });
        }

        public void Watch(string k, Action<string> callback)
        {
            _client.Advanced.WatchKey(new WatchKeyRequest() { Key = _etcdBasePath + k, Callback = (x) => { callback(x.Node.Value); } });
        }
    }
}
