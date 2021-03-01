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
using OpenMetaverse;
using Mono.Addins;

using OpenSim.Region.Framework.Interfaces;

namespace OpenSim.Region.CoreModules.World.Wind.Plugins
{
    [Extension(Path = "/OpenSim/WindModule", NodeName = "WindModel", Id = "ConfigurableWind")]
    class ConfigurableWind : Mono.Addins.TypeExtensionNode, IWindModelPlugin
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Vector2[] _windSpeeds = new Vector2[16 * 16];
        //private Random _rndnums = new Random(Environment.TickCount);

        private float _avgStrength = 5.0f; // Average magnitude of the wind vector
        private float _avgDirection = 0.0f; // Average direction of the wind in degrees
        private float _varStrength = 5.0f; // Max Strength  Variance
        private float _varDirection = 30.0f;// Max Direction Variance
        private float _rateChange = 1.0f; //

        private Vector2 _curPredominateWind = new Vector2();



        #region IPlugin Members

        public string Version => "1.0.0.0";

        public string Name => "ConfigurableWind";

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
                // Uses strength value if avg_strength not specified
                _avgStrength = windConfig.GetFloat("strength", 5.0F);
                _avgStrength = windConfig.GetFloat("avg_strength", 5.0F);

                _avgDirection = windConfig.GetFloat("avg_direction", 0.0F);
                _varStrength  = windConfig.GetFloat("var_strength", 5.0F);
                _varDirection = windConfig.GetFloat("var_direction", 30.0F);
                _rateChange   = windConfig.GetFloat("rate_change", 1.0F);

                LogSettings();
            }
        }

        public bool WindUpdate(uint frame)
        {
            double avgAng = _avgDirection * (Math.PI/180.0f);
            double varDir = _varDirection * (Math.PI/180.0f);

            // Prevailing wind algorithm
            // Inspired by Kanker Greenacre

            // TODO:
            // * This should probably be based on in-world time.
            // * should probably move all these local variables to class members and constants
            double time = DateTime.Now.TimeOfDay.Seconds / 86400.0f;

            double theta = time * (2 * Math.PI) * _rateChange;

            double offset = Math.Sin(theta) * Math.Sin(theta*2) * Math.Sin(theta*9) * Math.Cos(theta*4);

            double windDir = avgAng + varDir * offset;

            offset = Math.Sin(theta) * Math.Sin(theta*4) + Math.Sin(theta*13) / 3;
            double windSpeed = _avgStrength + _varStrength * offset;

            if (windSpeed < 0)
                windSpeed = -windSpeed;

            _curPredominateWind.X = (float)Math.Cos(windDir);
            _curPredominateWind.Y = (float)Math.Sin(windDir);

            _curPredominateWind.Normalize();
            _curPredominateWind.X *= (float)windSpeed;
            _curPredominateWind.Y *= (float)windSpeed;

            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    _windSpeeds[y * 16 + x] = _curPredominateWind;
                }
            }
            return true;
        }

        public Vector3 WindSpeed(float fX, float fY, float fZ)
        {
            return new Vector3(_curPredominateWind, 0.0f);
        }

        public Vector2[] WindLLClientArray()
        {
            return _windSpeeds;
        }

        public string Description => "Provides a predominate wind direction that can change within configured variances for direction and speed.";

        public System.Collections.Generic.Dictionary<string, string> WindParams()
        {
            Dictionary<string, string> Params = new Dictionary<string, string>();

            Params.Add("avgStrength", "average wind strength");
            Params.Add("avgDirection", "average wind direction in degrees");
            Params.Add("varStrength", "allowable variance in wind strength");
            Params.Add("varDirection", "allowable variance in wind direction in +/- degrees");
            Params.Add("rateChange", "rate of change");

            return Params;
        }

        public void WindParamSet(string param, float value)
        {
            switch (param)
            {
                case "avgStrength":
                     _avgStrength = value;
                     break;
                case "avgDirection":
                     _avgDirection = value;
                     break;
                 case "varStrength":
                     _varStrength = value;
                     break;
                 case "varDirection":
                     _varDirection = value;
                     break;
                 case "rateChange":
                     _rateChange = value;
                     break;
            }
        }

        public float WindParamGet(string param)
        {
            switch (param)
            {
                case "avgStrength":
                    return _avgStrength;
                case "avgDirection":
                    return _avgDirection;
                case "varStrength":
                    return _varStrength;
                case "varDirection":
                    return _varDirection;
                case "rateChange":
                    return _rateChange;
                default:
                    throw new Exception(string.Format("Unknown {0} parameter {1}", this.Name, param));

            }
        }



        #endregion


        private void LogSettings()
        {
            _log.InfoFormat("[ConfigurableWind] Average Strength   : {0}", _avgStrength);
            _log.InfoFormat("[ConfigurableWind] Average Direction  : {0}", _avgDirection);
            _log.InfoFormat("[ConfigurableWind] Varience Strength  : {0}", _varStrength);
            _log.InfoFormat("[ConfigurableWind] Varience Direction : {0}", _varDirection);
            _log.InfoFormat("[ConfigurableWind] Rate Change        : {0}", _rateChange);
        }

        #region IWindModelPlugin Members


        #endregion
    }
}
