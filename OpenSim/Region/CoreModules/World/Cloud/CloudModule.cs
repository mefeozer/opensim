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
using System.Threading;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "CloudModule")]
    public class CloudModule : ICloudModule, INonSharedRegionModule
    {
//        private static readonly log4net.ILog _log
//            = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private uint _frame = 0;
        private int _frameUpdateRate = 1000;
        private Random _rndnums;
        private Scene _scene = null;
        private bool _ready = false;
        private bool _enabled = false;
        private float _cloudDensity = 1.0F;
        private readonly float[] cloudCover = new float[16 * 16];
        private int _dataVersion;
        private bool _busy;
        private readonly object cloudlock = new object();


        public void Initialise(IConfigSource config)
        {
            IConfig cloudConfig = config.Configs["Cloud"];

            if (cloudConfig != null)
            {
                _enabled = cloudConfig.GetBoolean("enabled", false);
                _cloudDensity = cloudConfig.GetFloat("density", 0.5F);
                _frameUpdateRate = cloudConfig.GetInt("cloud_update_rate", 1000);
            }

        }

        public void AddRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _scene = scene;

            scene.RegisterModuleInterface<ICloudModule>(this);
            int seed = Environment.TickCount;
            seed += (int)(scene.RegionInfo.RegionLocX << 16);
            seed += (int)scene.RegionInfo.RegionLocY;
            _rndnums = new Random(seed);

            GenerateCloudCover();
            _dataVersion = _scene.AllocateIntId();

            scene.EventManager.OnNewClient += CloudsToClient;
            scene.EventManager.OnFrame += CloudUpdate;

            _ready = true;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!_enabled)
                return;

            _ready = false;
            //  Remove our hooks
            _scene.EventManager.OnNewClient -= CloudsToClient;
            _scene.EventManager.OnFrame -= CloudUpdate;
            _scene.UnregisterModuleInterface<ICloudModule>(this);
            _scene = null;
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public string Name => "CloudModule";

        public Type ReplaceableInterface => null;

        public float CloudCover(int x, int y, int z)
        {
            float cover = 0f;
            x /= 16;
            y /= 16;
            if (x < 0) x = 0;
            if (x > 15) x = 15;
            if (y < 0) y = 0;
            if (y > 15) y = 15;

            if (cloudCover != null)
            {
                lock(cloudlock)
                    cover = cloudCover[y * 16 + x];
            }

            return cover;
        }

        private void UpdateCloudCover()
        {
            float[] newCover = new float[16 * 16];
            int rowAbove = new int();
            int rowBelow = new int();
            int columnLeft = new int();
            int columnRight = new int();
            for (int x = 0; x < 16; x++)
            {
                if (x == 0)
                {
                    columnRight = x + 1;
                    columnLeft = 15;
                }
                else if (x == 15)
                {
                    columnRight = 0;
                    columnLeft = x - 1;
                }
                else
                {
                    columnRight = x + 1;
                    columnLeft = x - 1;
                }
                for (int y = 0; y< 16; y++)
                {
                    if (y == 0)
                    {
                        rowAbove = y + 1;
                        rowBelow = 15;
                    }
                    else if (y == 15)
                    {
                        rowAbove = 0;
                        rowBelow = y - 1;
                    }
                    else
                    {
                        rowAbove = y + 1;
                        rowBelow = y - 1;
                    }
                    float neighborAverage = (cloudCover[rowBelow * 16 + columnLeft] +
                                             cloudCover[y * 16 + columnLeft] +
                                             cloudCover[rowAbove * 16 + columnLeft] +
                                             cloudCover[rowBelow * 16 + x] +
                                             cloudCover[rowAbove * 16 + x] +
                                             cloudCover[rowBelow * 16 + columnRight] +
                                             cloudCover[y * 16 + columnRight] +
                                             cloudCover[rowAbove * 16 + columnRight] +
                                             cloudCover[y * 16 + x]) / 9;
                    newCover[y * 16 + x] = (neighborAverage / _cloudDensity + 0.175f) % 1.0f;
                    newCover[y * 16 + x] *= _cloudDensity;
                }
            }
            Array.Copy(newCover, cloudCover, 16 * 16);
            _dataVersion++;
        }

        private void CloudUpdate()
        {
            if (!_ready ||  _busy || _cloudDensity == 0 ||
                _frame++ % _frameUpdateRate != 0)
                return;

            if(Monitor.TryEnter(cloudlock))
            {
                _busy = true;
                Util.FireAndForget(delegate
                    {
                        try
                        {
                            lock(cloudlock)
                            {
                                UpdateCloudCover();
                                _scene.ForEachClient(delegate(IClientAPI client)
                                {
                                    client.SendCloudData(_dataVersion, cloudCover);
                                });
                            }
                        }
                        finally
                        {
                            _busy = false;
                        }
                    },
                    null, "CloudModuleUpdate");
                Monitor.Exit(cloudlock);
            }
        }

        public void CloudsToClient(IClientAPI client)
        {
            if (_ready)
            {
                lock(cloudlock)
                       client.SendCloudData(_dataVersion, cloudCover);
            }
        }


        /// <summary>
        /// Calculate the cloud cover over the region.
        /// </summary>
        private void GenerateCloudCover()
        {
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    cloudCover[y * 16 + x] = (float)_rndnums.NextDouble(); // 0 to 1
                    cloudCover[y * 16 + x] *= _cloudDensity;
                }
            }
        }
    }
}
