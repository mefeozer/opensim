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
using System.Timers;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Serialization.External;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.World.Archiver
{
    /// <summary>
    /// Encapsulate the asynchronous requests for the assets required for an archive operation
    /// </summary>
    class AssetsRequest
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Method called when all the necessary assets for an archive request have been received.
        /// </summary>
        public delegate void AssetsRequestCallback(
            ICollection<UUID> assetsFoundUuids, ICollection<UUID> assetsNotFoundUuids, bool timedOut);

        enum RequestState
        {
            Initial,
            Running,
            Completed,
            Aborted
        };

        /// <value>
        /// uuids to request
        /// </value>
        protected IDictionary<UUID, sbyte> _uuids;
        private readonly int _previousErrorsCount;

        /// <value>
        /// Callback used when all the assets requested have been received.
        /// </value>
        protected AssetsRequestCallback _assetsRequestCallback;

        /// <value>
        /// List of assets that were found.  This will be passed back to the requester.
        /// </value>
        protected List<UUID> _foundAssetUuids = new List<UUID>();

        /// <value>
        /// Maintain a list of assets that could not be found.  This will be passed back to the requester.
        /// </value>
        protected List<UUID> _notFoundAssetUuids = new List<UUID>();

        /// <value>
        /// Record the number of asset replies required so we know when we've finished
        /// </value>
        private readonly int _repliesRequired;

        private System.Timers.Timer _timeOutTimer;
        private bool _timeout;

        /// <value>
        /// Asset service used to request the assets
        /// </value>
        protected IAssetService _assetService;
        protected IUserAccountService _userAccountService;
        protected UUID _scopeID; // the grid ID

        protected AssetsArchiver _assetsArchiver;

        protected Dictionary<string, object> _options;

        protected internal AssetsRequest(
            AssetsArchiver assetsArchiver, IDictionary<UUID, sbyte> uuids,
            int previousErrorsCount,
            IAssetService assetService, IUserAccountService userService,
            UUID scope, Dictionary<string, object> options,
            AssetsRequestCallback assetsRequestCallback)
        {
            _assetsArchiver = assetsArchiver;
            _uuids = uuids;
            _previousErrorsCount = previousErrorsCount;
            _assetsRequestCallback = assetsRequestCallback;
            _assetService = assetService;
            _userAccountService = userService;
            _scopeID = scope;
            _options = options;
            _repliesRequired = uuids.Count;
        }

        protected internal void Execute()
        {
            Culture.SetCurrentCulture();
            // We can stop here if there are no assets to fetch
            if (_repliesRequired == 0)
            {
                PerformAssetsRequestCallback(false);
                return;
            }

            _timeOutTimer = new System.Timers.Timer(90000)
            {
                AutoReset = false
            };
            _timeOutTimer.Elapsed += OnTimeout;
            _timeout = false;
            int gccontrol = 0;

            foreach (KeyValuePair<UUID, sbyte> kvp in _uuids)
            {

                string thiskey = kvp.Key.ToString();
                try
                {
                    _timeOutTimer.Enabled = true;
                    AssetBase asset = _assetService.Get(thiskey);
                    if(_timeout)
                        break;

                    _timeOutTimer.Enabled = false;

                    if(asset == null)
                    {
                        _notFoundAssetUuids.Add(new UUID(thiskey));
                        continue;
                    }

                    sbyte assetType = kvp.Value;
                    if (asset != null && assetType == (sbyte)AssetType.Unknown)
                    {
                        _log.InfoFormat("[ARCHIVER]: Rewriting broken asset type for {0} to {1}", thiskey, SLUtil.AssetTypeFromCode(assetType));
                        asset.Type = assetType;
                    }

                    _foundAssetUuids.Add(asset.FullID);
                    _assetsArchiver.WriteAsset(PostProcess(asset));
                    if(++gccontrol > 10000)
                    {
                        gccontrol = 0;
                        GC.Collect();
                    }
                }

                catch (Exception e)
                {
                    _log.ErrorFormat("[ARCHIVER]: Execute failed with {0}", e);
                }
            }

            _timeOutTimer.Dispose();
            int totalerrors = _notFoundAssetUuids.Count + _previousErrorsCount;

            if(_timeout)
                _log.DebugFormat("[ARCHIVER]: Aborted because AssetService request timeout. Successfully added {0} assets", _foundAssetUuids.Count);
            else if(totalerrors == 0)
                _log.DebugFormat("[ARCHIVER]: Successfully added all {0} assets", _foundAssetUuids.Count);
            else
                _log.DebugFormat("[ARCHIVER]: Successfully added {0} assets ({1} of total possible assets requested were not found, were damaged or were not assets)",
                            _foundAssetUuids.Count, totalerrors);

            GC.Collect();
            PerformAssetsRequestCallback(_timeout);
        }
  
        private void OnTimeout(object source, ElapsedEventArgs args)
        {
            _timeout = true;
        }

        /// <summary>
        /// Perform the callback on the original requester of the assets
        /// </summary>
        private void PerformAssetsRequestCallback(object o)
        {
            if(_assetsRequestCallback == null)
                return;
            Culture.SetCurrentCulture();

            bool timedOut = (bool)o;

            try
            {
                _assetsRequestCallback(_foundAssetUuids, _notFoundAssetUuids, timedOut);
            }
            catch (Exception e)
            {
                _log.ErrorFormat(
                    "[ARCHIVER]: Terminating archive creation since asset requster callback failed with {0}", e);
            }
        }

        private AssetBase PostProcess(AssetBase asset)
        {
            if (asset.Type == (sbyte)AssetType.Object && asset.Data != null && _options.ContainsKey("home"))
            {
                //_log.DebugFormat("[ARCHIVER]: Rewriting object data for {0}", asset.ID);
                string xml = ExternalRepresentationUtils.RewriteSOP(Utils.BytesToString(asset.Data), string.Empty, _options["home"].ToString(), _userAccountService, _scopeID);
                asset.Data = Utils.StringToBytes(xml);
            }
            return asset;
        }
    }
}
