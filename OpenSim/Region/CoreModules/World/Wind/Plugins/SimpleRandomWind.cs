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

using OpenMetaverse;
using Mono.Addins;

using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.World.Wind.Plugins
{
    [Extension(Path = "/OpenSim/WindModule", NodeName = "WindModel", Id = "SimpleRandomWind")]
    class SimpleRandomWind : Mono.Addins.TypeExtensionNode, IWindModelPlugin
    {
        private Vector2[] _windSpeeds = new Vector2[16 * 16];
        private float _strength = 1.0f;
        private readonly Random _rndnums = new Random(Environment.TickCount);

        #region IPlugin Members

        public string Version => "1.0.0.0";

        public string Name => "SimpleRandomWind";

        public void Initialise()
        {
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            _windSpeeds = null;
        }

        #endregion

        #region IWindModelPlugin Members

        public void WindConfig(OpenSim.Region.Framework.Scenes.Scene scene, Nini.Config.IConfig windConfig)
        {
            if (windConfig != null)
            {
                if (windConfig.Contains("strength"))
                {
                    _strength = windConfig.GetFloat("strength", 1.0F);
                }
            }
        }

        public bool WindUpdate(uint frame)
        {
            //Make sure our object is valid (we haven't been disposed of yet)
            if (_windSpeeds == null)
                return false;

            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    _windSpeeds[y * 16 + x].X = (float)(_rndnums.NextDouble() * 2d - 1d); // -1 to 1
                    _windSpeeds[y * 16 + x].Y = (float)(_rndnums.NextDouble() * 2d - 1d); // -1 to 1
                    _windSpeeds[y * 16 + x].X *= _strength;
                    _windSpeeds[y * 16 + x].Y *= _strength;
                }
            }
            return true;
        }

        public Vector3 WindSpeed(float fX, float fY, float fZ)
        {
            Vector3 windVector = new Vector3(0.0f, 0.0f, 0.0f);

            int x = (int)fX / 16;
            int y = (int)fY / 16;

            if (x < 0) x = 0;
            if (x > 15) x = 15;
            if (y < 0) y = 0;
            if (y > 15) y = 15;

            if (_windSpeeds != null)
            {
                windVector.X = _windSpeeds[y * 16 + x].X;
                windVector.Y = _windSpeeds[y * 16 + x].Y;
            }

            return windVector;
        }

        public Vector2[] WindLLClientArray()
        {
            return _windSpeeds;
        }

        public string Description => "Provides a simple wind model that creates random wind of a given strength in 16m x 16m patches.";

        public System.Collections.Generic.Dictionary<string, string> WindParams()
        {
            Dictionary<string, string> Params = new Dictionary<string, string>();

            Params.Add("strength", "wind strength");

            return Params;
        }

        public void WindParamSet(string param, float value)
        {
            switch (param)
            {
                case "strength":
                    _strength = value;
                    break;
            }
        }

        public float WindParamGet(string param)
        {
            switch (param)
            {
                case "strength":
                    return _strength;
                default:
                    throw new Exception(string.Format("Unknown {0} parameter {1}", this.Name, param));
            }
        }

        #endregion

    }
}
