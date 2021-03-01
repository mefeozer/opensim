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
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.IO;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework.Servers;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Framework.Servers.HttpServer;

namespace OpenSim.Region.OptionalModules.World.WorldView
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "WorldViewModule")]
    public class WorldViewModule : INonSharedRegionModule
    {
        private static readonly ILog _log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


        private bool _Enabled = false;
        private IMapImageGenerator _Generator;

        public void Initialise(IConfigSource config)
        {
            IConfig moduleConfig = config.Configs["Modules"];
            if (moduleConfig == null)
                return;

            if (moduleConfig.GetString("WorldViewModule", string.Empty) != Name)
                return;

            _Enabled = true;
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_Enabled)
                return;

            _Generator = scene.RequestModuleInterface<IMapImageGenerator>();
            if (_Generator == null)
            {
                _Enabled = false;
                return;
            }

            _log.Info("[WORLDVIEW]: Configured and enabled");

            IHttpServer server = MainServer.GetHttpServer(0);
            server.AddStreamHandler(new WorldViewRequestHandler(this,
                    scene.RegionInfo.RegionID.ToString()));
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public string Name => "WorldViewModule";

        public Type ReplaceableInterface => null;

        public void Close()
        {
        }

        public byte[] GenerateWorldView(Vector3 pos, Vector3 rot, float fov,
                int width, int height, bool usetex)
        {
            if (!_Enabled)
                return new byte[0];

            using (Bitmap bmp = _Generator.CreateViewImage(pos, rot, fov, width, height, usetex))
            {
                using (MemoryStream str = new MemoryStream())
                {
                    bmp.Save(str, ImageFormat.Jpeg);

                    return str.ToArray();
                }
            }
        }
    }
}
