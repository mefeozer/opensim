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
using System.Net;
using System.Reflection;
using System.Timers;
using log4net;
using Nini.Config;
using Mono.Addins;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Capabilities;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "UploadBakedTextureModule")]
    public class UploadBakedTextureModule : ISharedRegionModule
    {
       private static readonly ILog _log =LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private int _nscenes;
        IAssetCache _assetCache = null;

        private string _URL;

        public void Initialise(IConfigSource source)
        {
            IConfig config = source.Configs["ClientStack.LindenCaps"];
            if (config == null)
                return;

            _URL = config.GetString("Cap_UploadBakedTexture", string.Empty);
        }

        public void AddRegion(Scene s)
        {
        }

        public void RemoveRegion(Scene s)
        {
            s.EventManager.OnRegisterCaps -= RegisterCaps;
            --_nscenes;
            if(_nscenes <= 0)
                _assetCache = null;
        }

        public void RegionLoaded(Scene s)
        {
            if (_assetCache == null)
                _assetCache = s.RequestModuleInterface <IAssetCache>();
            if (_assetCache != null)
            {
                ++_nscenes;
                s.EventManager.OnRegisterCaps += RegisterCaps;
            }
        }

        public void PostInitialise()
        {
        }

        public void Close() { }

        public string Name => "UploadBakedTextureModule";

        public Type ReplaceableInterface => null;

        public void RegisterCaps(UUID agentID, Caps caps)
        {
            if (_URL == "localhost")
            {
                caps.RegisterSimpleHandler("UploadBakedTexture",
                    new SimpleStreamHandler("/" + UUID.Random(), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
                    {
                        UploadBakedTexture(httpRequest, httpResponse, agentID, caps, _assetCache);
                    }));
            }
            else if(!string.IsNullOrWhiteSpace(_URL))
            {
                caps.RegisterHandler("UploadBakedTexture", _URL);
            }
        }

        public void UploadBakedTexture(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, UUID agentID, Caps caps, IAssetCache cache)
        {
            if(httpRequest.HttpMethod != "POST")
            {
                httpResponse.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            try
            {
                string capsBase = "/" + UUID.Random()+"-BK";
                string protocol = caps.SSLCaps ? "https://" : "http://";
                string uploaderURL = protocol + caps.HostName + ":" + caps.Port.ToString() + capsBase;

                LLSDAssetUploadResponse uploadResponse = new LLSDAssetUploadResponse
                {
                    uploader = uploaderURL,
                    state = "upload"
                };

                BakedTextureUploader uploader =
                    new BakedTextureUploader(capsBase, caps.HttpListener, agentID, cache, httpRequest.RemoteIPEndPoint.Address);

                var uploaderHandler = new SimpleBinaryHandler("POST", capsBase, uploader.process)
                {
                    MaxDataSize = 6000000 // change per asset type?
                };

                caps.HttpListener.AddSimpleStreamHandler(uploaderHandler);

                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(uploadResponse));
                httpResponse.StatusCode = (int)HttpStatusCode.OK;
                return;
            }
            catch (Exception e)
            {
                _log.ErrorFormat("[UPLOAD BAKED TEXTURE HANDLER]: {0}{1}", e.Message, e.StackTrace);
            }
            httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
        }
    }

    class BakedTextureUploader
    {
        // private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly string _uploaderPath = string.Empty;
        private readonly IHttpServer _httpListener;
        private UUID _agentID = UUID.Zero;
        private readonly IPAddress _remoteAddress;
        private readonly IAssetCache _assetCache;
        private readonly Timer _timeout;

        public BakedTextureUploader(string path, IHttpServer httpServer, UUID agentID, IAssetCache cache, IPAddress remoteAddress)
        {
            _uploaderPath = path;
            _httpListener = httpServer;
            _agentID = agentID;
            _remoteAddress = remoteAddress;
            _assetCache = cache;
            _timeout = new Timer();
            _timeout.Elapsed += Timeout;
            _timeout.AutoReset = false;
            _timeout.Interval = 30000;
            _timeout.Start();
        }

        private void Timeout(object source, ElapsedEventArgs e)
        {
            _httpListener.RemoveSimpleStreamHandler(_uploaderPath);
            _timeout.Dispose();
        }

        /// <summary>
        /// Handle raw uploaded baked texture data.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <returns></returns>
        public void process(IOSHttpRequest httpRequest, IOSHttpResponse httpResponse, byte[] data)
        {
            _timeout.Stop();
            _httpListener.RemoveSimpleStreamHandler(_uploaderPath);
            _timeout.Dispose();

            if (!httpRequest.RemoteIPEndPoint.Address.Equals(_remoteAddress))
            {
                httpResponse.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            // need to check if data is a baked
            try
            {
                UUID newAssetID = UUID.Random();
                AssetBase asset = new AssetBase(newAssetID, "Baked Texture", (sbyte)AssetType.Texture, _agentID.ToString())
                {
                    Data = data,
                    Temporary = true,
                    Local = true
                };
                //asset.Flags = AssetFlags.AvatarBake;
                _assetCache.Cache(asset);

                LLSDAssetUploadComplete uploadComplete = new LLSDAssetUploadComplete
                {
                    new_asset = newAssetID.ToString(),
                    new_inventory_item = UUID.Zero,
                    state = "complete"
                };

                httpResponse.RawBuffer = Util.UTF8NBGetbytes(LLSDHelpers.SerialiseLLSDReply(uploadComplete));
                httpResponse.StatusCode = (int)HttpStatusCode.OK;
                return;
            }
            catch { }
            httpResponse.StatusCode = (int)HttpStatusCode.BadRequest;
        }
    }
}

