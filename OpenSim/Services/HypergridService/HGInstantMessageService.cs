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

using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors.InstantMessage;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Server.Base;

using OpenMetaverse;
using log4net;
using Nini.Config;

namespace OpenSim.Services.HypergridService
{
    /// <summary>
    /// Inter-grid IM
    /// </summary>
    public class HGInstantMessageService : IInstantMessage
    {
        private static readonly ILog _log = LogManager.GetLogger( MethodBase.GetCurrentMethod().DeclaringType);

        private const int REGIONCACHE_EXPIRATION = 300000;

        static bool _Initialized = false;

        protected static IGridService _GridService;
        protected static IPresenceService _PresenceService;
        protected static IUserAgentService _UserAgentService;
        protected static IOfflineIMService _OfflineIMService;

        protected static IInstantMessageSimConnector _IMSimConnector;

        protected static readonly Dictionary<UUID, object> _UserLocationMap = new Dictionary<UUID, object>();
        private static readonly ExpiringCacheOS<UUID, GridRegion> _RegionCache = new ExpiringCacheOS<UUID, GridRegion>(60000);

        private static bool _ForwardOfflineGroupMessages;
        private static bool _InGatekeeper;
        private readonly string _messageKey;

        public HGInstantMessageService(IConfigSource config)
            : this(config, null)
        {
        }

        public HGInstantMessageService(IConfigSource config, IInstantMessageSimConnector imConnector)
        {
            if (imConnector != null)
                _IMSimConnector = imConnector;

            if (!_Initialized)
            {
                _Initialized = true;

                IConfig serverConfig = config.Configs["HGInstantMessageService"];
                if (serverConfig == null)
                    throw new Exception(string.Format("No section HGInstantMessageService in config file"));

                string gridService = serverConfig.GetString("GridService", string.Empty);
                string presenceService = serverConfig.GetString("PresenceService", string.Empty);
                string userAgentService = serverConfig.GetString("UserAgentService", string.Empty);
                _InGatekeeper = serverConfig.GetBoolean("InGatekeeper", false);
                _log.DebugFormat("[HG IM SERVICE]: Starting... InRobust? {0}", _InGatekeeper);

                if (string.IsNullOrEmpty(gridService) || string.IsNullOrEmpty(presenceService))
                    throw new Exception(string.Format("Incomplete specifications, InstantMessage Service cannot function."));

                object[] args = new object[] { config };
                _GridService = ServerUtils.LoadPlugin<IGridService>(gridService, args);
                _PresenceService = ServerUtils.LoadPlugin<IPresenceService>(presenceService, args);
                try
                {
                    _UserAgentService = ServerUtils.LoadPlugin<IUserAgentService>(userAgentService, args);
                }
                catch
                {
                    _log.WarnFormat("[HG IM SERVICE]: Unable to create User Agent Service. Missing config var  in [HGInstantMessageService]?");
                }

                IConfig cnf = config.Configs["Messaging"];
                if (cnf == null)
                {
                    return;
                }

                _messageKey = cnf.GetString("MessageKey", string.Empty);
                _ForwardOfflineGroupMessages = cnf.GetBoolean("ForwardOfflineGroupMessages", false);

                if (_InGatekeeper)
                {
                    string offlineIMService = cnf.GetString("OfflineIMService", string.Empty);
                    if (!string.IsNullOrEmpty(offlineIMService))
                        _OfflineIMService = ServerUtils.LoadPlugin<IOfflineIMService>(offlineIMService, args);
                }
            }
        }

        public bool IncomingInstantMessage(GridInstantMessage im)
        {
//            _log.DebugFormat("[HG IM SERVICE]: Received message from {0} to {1}", im.fromAgentID, im.toAgentID);
//            UUID toAgentID = new UUID(im.toAgentID);

            bool success = false;
            if (_IMSimConnector != null)
            {
                //_log.DebugFormat("[XXX] SendIMToRegion local im connector");
                success = _IMSimConnector.SendInstantMessage(im);
            }
            else
            {
                success = TrySendInstantMessage(im, "", true, false);
            }

            if (!success && _InGatekeeper) // we do this only in the Gatekeeper IM service
                UndeliveredMessage(im);

            return success;
        }

        public bool OutgoingInstantMessage(GridInstantMessage im, string url, bool foreigner)
        {
//            _log.DebugFormat("[HG IM SERVICE]: Sending message from {0} to {1}@{2}", im.fromAgentID, im.toAgentID, url);
            if (!string.IsNullOrEmpty(url))
                return TrySendInstantMessage(im, url, true, foreigner);
            else
            {
                PresenceInfo upd = new PresenceInfo
                {
                    RegionID = UUID.Zero
                };
                return TrySendInstantMessage(im, upd, true, foreigner);
            }

        }

        protected bool TrySendInstantMessage(GridInstantMessage im, object previousLocation, bool firstTime, bool foreigner)
        {
            UUID toAgentID = new UUID(im.toAgentID);

            PresenceInfo upd = null;
            string url = string.Empty;

            bool lookupAgent = false;

            lock (_UserLocationMap)
            {
                if (_UserLocationMap.TryGetValue(toAgentID, out object o))
                {
                    if (o is PresenceInfo)
                        upd = (PresenceInfo)o;
                    else if (o is string)
                        url = (string)o;

                    // We need to compare the current location with the previous
                    // or the recursive loop will never end because it will never try to lookup the agent again
                    if (!firstTime)
                    {
                        lookupAgent = true;
                        upd = null;
                    }
                }
                else
                {
                    lookupAgent = true;
                }
            }

            //_log.DebugFormat("[XXX] Neeed lookup ? {0}", (lookupAgent ? "yes" : "no"));

            // Are we needing to look-up an agent?
            if (lookupAgent)
            {
                // Non-cached user agent lookup.
                PresenceInfo[] presences = _PresenceService.GetAgents(new string[] { toAgentID.ToString() });
                if (presences != null && presences.Length > 0)
                {
                    foreach (PresenceInfo p in presences)
                    {
                        if (p.RegionID != UUID.Zero)
                        {
                            //_log.DebugFormat("[XXX]: Found presence in {0}", p.RegionID);
                            upd = p;
                            break;
                        }
                    }
                }

                if (upd == null && !foreigner)
                {
                    // Let's check with the UAS if the user is elsewhere
                    _log.DebugFormat("[HG IM SERVICE]: User is not present. Checking location with User Agent service");
                    try
                    {
                        url = _UserAgentService.LocateUser(toAgentID);
                    }
                    catch (Exception e)
                    {
                        _log.Warn("[HG IM SERVICE]: LocateUser call failed ", e);
                        url = string.Empty;
                    }
                }

                // check if we've tried this before..
                // This is one way to end the recursive loop
                //
                if (!firstTime && (previousLocation is PresenceInfo && upd != null && upd.RegionID == ((PresenceInfo)previousLocation).RegionID ||
                                    previousLocation is string && upd == null && previousLocation.Equals(url)))
                {
                    // _log.Error("[GRID INSTANT MESSAGE]: Unable to deliver an instant message");
                    _log.DebugFormat("[HG IM SERVICE]: Fail 2 {0} {1}", previousLocation, url);

                    return false;
                }
            }

            if (upd != null)
            {
                // ok, the user is around somewhere. Let's send back the reply with "success"
                // even though the IM may still fail. Just don't keep the caller waiting for
                // the entire time we're trying to deliver the IM
                return SendIMToRegion(upd, im, toAgentID, foreigner);
            }
            else if (!string.IsNullOrEmpty(url))
            {
                // ok, the user is around somewhere. Let's send back the reply with "success"
                // even though the IM may still fail. Just don't keep the caller waiting for
                // the entire time we're trying to deliver the IM
                return ForwardIMToGrid(url, im, toAgentID, foreigner);
            }
            else if (firstTime && previousLocation is string && !string.IsNullOrEmpty((string)previousLocation))
            {
                return ForwardIMToGrid((string)previousLocation, im, toAgentID, foreigner);
            }
            else
                _log.DebugFormat("[HG IM SERVICE]: Unable to locate user {0}", toAgentID);
            return false;
        }

        bool SendIMToRegion(PresenceInfo upd, GridInstantMessage im, UUID toAgentID, bool foreigner)
        {
            GridRegion reginfo = null;
            if (!_RegionCache.TryGetValue(upd.RegionID, REGIONCACHE_EXPIRATION, out reginfo) )
            {
                reginfo = _GridService.GetRegionByUUID(UUID.Zero /*!!!*/, upd.RegionID);
                _RegionCache.AddOrUpdate(upd.RegionID, reginfo, reginfo == null ? 60000 : REGIONCACHE_EXPIRATION);
            }

            if (reginfo == null)
                return false;

            bool imresult = InstantMessageServiceConnector.SendInstantMessage(reginfo.ServerURI, im, _messageKey);

            if (imresult)
            {
                // IM delivery successful, so store the Agent's location in our local cache.
                lock (_UserLocationMap)
                    _UserLocationMap[toAgentID] = upd;
                return true;
            }
            else
            {
                // try again, but lookup user this time.
                // Warning, this must call the Async version
                // of this method or we'll be making thousands of threads
                // The version within the spawned thread is SendGridInstantMessageViaXMLRPCAsync
                // The version that spawns the thread is SendGridInstantMessageViaXMLRPC

                // This is recursive!!!!!
                return TrySendInstantMessage(im, upd, false, foreigner);
            }
        }

        bool ForwardIMToGrid(string url, GridInstantMessage im, UUID toAgentID, bool foreigner)
        {
            if (InstantMessageServiceConnector.SendInstantMessage(url, im, _messageKey))
            {
                // IM delivery successful, so store the Agent's location in our local cache.
                lock (_UserLocationMap)
                    _UserLocationMap[toAgentID] = url;

                return true;
            }
            else
            {
                // try again, but lookup user this time.

                // This is recursive!!!!!
                return TrySendInstantMessage(im, url, false, foreigner);
            }
        }

        private bool UndeliveredMessage(GridInstantMessage im)
        {
            if (_OfflineIMService == null)
                return false;

            if (im.dialog != (byte)InstantMessageDialog.MessageFromObject &&
                im.dialog != (byte)InstantMessageDialog.MessageFromAgent &&
                im.dialog != (byte)InstantMessageDialog.GroupNotice &&
                im.dialog != (byte)InstantMessageDialog.GroupInvitation &&
                im.dialog != (byte)InstantMessageDialog.InventoryOffered)
            {
                return false;
            }

            if (!_ForwardOfflineGroupMessages)
            {
                if (im.dialog == (byte)InstantMessageDialog.GroupNotice ||
                    im.dialog == (byte)InstantMessageDialog.GroupInvitation)
                    return false;
            }

//                _log.DebugFormat("[HG IM SERVICE]: Message saved");
            string reason = string.Empty;
            return _OfflineIMService.StoreMessage(im, out reason);
        }
    }
}
