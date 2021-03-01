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
using OpenSim.Services.Connectors.Friends;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Server.Base;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;

using OpenMetaverse;
using log4net;
using Nini.Config;

namespace OpenSim.Services.HypergridService
{
    /// <summary>
    /// W2W social networking
    /// </summary>
    public class HGFriendsService : IHGFriendsService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        static bool _Initialized = false;

        protected static IGridUserService _GridUserService;
        protected static IGridService _GridService;
        protected static IGatekeeperService _GatekeeperService;
        protected static IFriendsService _FriendsService;
        protected static IPresenceService _PresenceService;
        protected static IUserAccountService _UserAccountService;
        protected static IFriendsSimConnector _FriendsLocalSimConnector; // standalone, points to HGFriendsModule
        protected static FriendsSimConnector _FriendsSimConnector; // grid

        private static string _ConfigName = "HGFriendsService";

        public HGFriendsService(IConfigSource config, string configName, IFriendsSimConnector localSimConn)
        {
            if (_FriendsLocalSimConnector == null)
                _FriendsLocalSimConnector = localSimConn;

            if (!_Initialized)
            {
                _Initialized = true;

                if (!string.IsNullOrEmpty(configName))
                    _ConfigName = configName;

                object[] args = new object[] { config };

                IConfig serverConfig = config.Configs[_ConfigName];
                if (serverConfig == null)
                    throw new Exception(string.Format("No section {0} in config file", _ConfigName));

                string theService = serverConfig.GetString("FriendsService", string.Empty);
                if (string.IsNullOrEmpty(theService))
                    throw new Exception("No FriendsService in config file " + _ConfigName);
                _FriendsService = ServerUtils.LoadPlugin<IFriendsService>(theService, args);

                theService = serverConfig.GetString("UserAccountService", string.Empty);
                if (string.IsNullOrEmpty(theService))
                    throw new Exception("No UserAccountService in " + _ConfigName);
                _UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(theService, args);

                theService = serverConfig.GetString("GridService", string.Empty);
                if (string.IsNullOrEmpty(theService))
                    throw new Exception("No GridService in " + _ConfigName);
                _GridService = ServerUtils.LoadPlugin<IGridService>(theService, args);

                theService = serverConfig.GetString("PresenceService", string.Empty);
                if (string.IsNullOrEmpty(theService))
                    throw new Exception("No PresenceService in " + _ConfigName);
                _PresenceService = ServerUtils.LoadPlugin<IPresenceService>(theService, args);

                _FriendsSimConnector = new FriendsSimConnector();

                _log.DebugFormat("[HGFRIENDS SERVICE]: Starting...");

            }
        }

        #region IHGFriendsService

        public int GetFriendPerms(UUID userID, UUID friendID)
        {
            FriendInfo[] friendsInfo = _FriendsService.GetFriends(userID);
            foreach (FriendInfo finfo in friendsInfo)
            {
                if (finfo.Friend.StartsWith(friendID.ToString()))
                    return finfo.TheirFlags;
            }
            return -1;
        }

        public bool NewFriendship(FriendInfo friend, bool verified)
        {
            UUID friendID;
            string tmp = string.Empty, url = string.Empty, first = string.Empty, last = string.Empty;
            if (!Util.ParseUniversalUserIdentifier(friend.Friend, out friendID, out url, out first, out last, out tmp))
                return false;

            _log.DebugFormat("[HGFRIENDS SERVICE]: New friendship {0} {1} ({2})", friend.PrincipalID, friend.Friend, verified);

            // Does the friendship already exist?
            FriendInfo[] finfos = _FriendsService.GetFriends(friend.PrincipalID);
            foreach (FriendInfo finfo in finfos)
            {
                if (finfo.Friend.StartsWith(friendID.ToString()))
                    return false;
            }
            // Verified user session. But the user needs to confirm friendship when he gets home
            if (verified)
                return _FriendsService.StoreFriend(friend.PrincipalID.ToString(), friend.Friend, 0);

            // Does the reverted friendship exist? meaning that this user initiated the request
            finfos = _FriendsService.GetFriends(friendID);
            bool userInitiatedOffer = false;
            foreach (FriendInfo finfo in finfos)
            {
                if (friend.Friend.StartsWith(finfo.PrincipalID.ToString()) && finfo.Friend.StartsWith(friend.PrincipalID.ToString()) && finfo.TheirFlags == -1)
                {
                    userInitiatedOffer = true;
                    // Let's delete the existing friendship relations that was stored
                    _FriendsService.Delete(friendID, finfo.Friend);
                    break;
                }
            }

            if (userInitiatedOffer)
            {
                _FriendsService.StoreFriend(friend.PrincipalID.ToString(), friend.Friend, 1);
                _FriendsService.StoreFriend(friend.Friend, friend.PrincipalID.ToString(), 1);
                // notify the user
                ForwardToSim("ApproveFriendshipRequest", friendID, Util.UniversalName(first, last, url), "", friend.PrincipalID, "");
                return true;
            }
            return false;
        }

        public bool DeleteFriendship(FriendInfo friend, string secret)
        {
            FriendInfo[] finfos = _FriendsService.GetFriends(friend.PrincipalID);
            foreach (FriendInfo finfo in finfos)
            {
                // We check the secret here. Or if the friendship request was initiated here, and was declined
                if (finfo.Friend.StartsWith(friend.Friend) && finfo.Friend.EndsWith(secret))
                {
                    _log.DebugFormat("[HGFRIENDS SERVICE]: Delete friendship {0} {1}", friend.PrincipalID, friend.Friend);
                    _FriendsService.Delete(friend.PrincipalID, finfo.Friend);
                    _FriendsService.Delete(finfo.Friend, friend.PrincipalID.ToString());

                    return true;
                }
            }

            return false;
        }

        public bool FriendshipOffered(UUID fromID, string fromName, UUID toID, string message)
        {
            UserAccount account = _UserAccountService.GetUserAccount(UUID.Zero, toID);
            if (account == null)
                return false;

            // OK, we have that user here.
            // So let's send back the call, but start a thread to continue
            // with the verification and the actual action.

            Util.FireAndForget(
                o => ProcessFriendshipOffered(fromID, fromName, toID, message), null, "HGFriendsService.ProcessFriendshipOffered");

            return true;
        }

        public bool ValidateFriendshipOffered(UUID fromID, UUID toID)
        {
            FriendInfo[] finfos = _FriendsService.GetFriends(toID.ToString());
            foreach (FriendInfo fi in finfos)
            {
                if (fi.Friend.StartsWith(fromID.ToString()) && fi.TheirFlags == -1)
                    return true;
            }
            return false;
        }

        public List<UUID> StatusNotification(List<string> friends, UUID foreignUserID, bool online)
        {
            if (_FriendsService == null || _PresenceService == null)
            {
                _log.WarnFormat("[HGFRIENDS SERVICE]: Unable to perform status notifications because friends or presence services are missing");
                return new List<UUID>();
            }

            // Let's unblock the caller right now, and take it from here async

            List<UUID> localFriendsOnline = new List<UUID>();

            _log.DebugFormat("[HGFRIENDS SERVICE]: Status notification: foreign user {0} wants to notify {1} local friends of {2} status",
                foreignUserID, friends.Count, online ? "online" : "offline");

            // First, let's double check that the reported friends are, indeed, friends of that user
            // And let's check that the secret matches
            List<string> usersToBeNotified = new List<string>();
            foreach (string uui in friends)
            {
                UUID localUserID;
                string secret = string.Empty, tmp = string.Empty;
                if (Util.ParseUniversalUserIdentifier(uui, out localUserID, out tmp, out tmp, out tmp, out secret))
                {
                    FriendInfo[] friendInfos = _FriendsService.GetFriends(localUserID);
                    foreach (FriendInfo finfo in friendInfos)
                    {
                        if (finfo.Friend.StartsWith(foreignUserID.ToString()) && finfo.Friend.EndsWith(secret))
                        {
                            // great!
                            usersToBeNotified.Add(localUserID.ToString());
                        }
                    }
                }
            }

            // Now, let's send the notifications
            //_log.DebugFormat("[HGFRIENDS SERVICE]: Status notification: user has {0} local friends", usersToBeNotified.Count);

            // First, let's send notifications to local users who are online in the home grid
            PresenceInfo[] friendSessions = _PresenceService.GetAgents(usersToBeNotified.ToArray());
            if (friendSessions != null && friendSessions.Length > 0)
            {
                PresenceInfo friendSession = null;
                foreach (PresenceInfo pinfo in friendSessions)
                    if (pinfo.RegionID != UUID.Zero) // let's guard against traveling agents
                    {
                        friendSession = pinfo;
                        break;
                    }

                if (friendSession != null)
                {
                    ForwardStatusNotificationToSim(friendSession.RegionID, foreignUserID, friendSession.UserID, online);
                    usersToBeNotified.Remove(friendSession.UserID.ToString());
                    UUID id;
                    if (UUID.TryParse(friendSession.UserID, out id))
                        localFriendsOnline.Add(id);

                }
            }

//            // Lastly, let's notify the rest who may be online somewhere else
//            foreach (string user in usersToBeNotified)
//            {
//                UUID id = new UUID(user);
//                //_UserAgentService.LocateUser(id);
//                //etc...
//                //if (_TravelingAgents.ContainsKey(id) && _TravelingAgents[id].GridExternalName != _GridName)
//                //{
//                //    string url = _TravelingAgents[id].GridExternalName;
//                //    // forward
//                //}
//                //_log.WarnFormat("[HGFRIENDS SERVICE]: User {0} is visiting another grid. HG Status notifications still not implemented.", user);
//            }

            // and finally, let's send the online friends
            if (online)
            {
                return localFriendsOnline;
            }
            else
                return new List<UUID>();
        }

        #endregion IHGFriendsService

        #region Aux

        private void ProcessFriendshipOffered(UUID fromID, string fromName, UUID toID, string message)
        {
            // Great, it's a genuine request. Let's proceed.
            // But now we need to confirm that the requester is who he says he is
            // before we act on the friendship request.

            if (!fromName.Contains("@"))
                return;

            string[] parts = fromName.Split(new char[] {'@'});
            if (parts.Length != 2)
                return;

            string uriStr = "http://" + parts[1];
            try
            {
                new Uri(uriStr);
            }
            catch (UriFormatException)
            {
                return;
            }

            UserAgentServiceConnector uasConn = new UserAgentServiceConnector(uriStr);
            Dictionary<string, object> servers = uasConn.GetServerURLs(fromID);
            if (!servers.ContainsKey("FriendsServerURI"))
                return;

            HGFriendsServicesConnector friendsConn = new HGFriendsServicesConnector(servers["FriendsServerURI"].ToString());
            if (!friendsConn.ValidateFriendshipOffered(fromID, toID))
            {
                _log.WarnFormat("[HGFRIENDS SERVICE]: Friendship request from {0} to {1} is invalid. Impersonations?", fromID, toID);
                return;
            }

            string fromUUI = Util.UniversalIdentifier(fromID, parts[0], "@" + parts[1], uriStr);
            // OK, we're good!
            ForwardToSim("FriendshipOffered", fromID, fromName, fromUUI, toID, message);
        }

        private bool ForwardToSim(string op, UUID fromID, string name, string fromUUI, UUID toID, string message)
        {
            PresenceInfo session = null;
            GridRegion region = null;
            PresenceInfo[] sessions = _PresenceService.GetAgents(new string[] { toID.ToString() });
            if (sessions != null && sessions.Length > 0)
                session = sessions[0];
            if (session != null)
                region = _GridService.GetRegionByUUID(UUID.Zero, session.RegionID);

            switch (op)
            {
                case "FriendshipOffered":
                    // Let's store backwards
                    string secret = UUID.Random().ToString().Substring(0, 8);
                    _FriendsService.StoreFriend(toID.ToString(), fromUUI + ";" + secret, 0);
                    if (_FriendsLocalSimConnector != null) // standalone
                    {
                        GridInstantMessage im = new GridInstantMessage(null, fromID, name, toID,
                            (byte)InstantMessageDialog.FriendshipOffered, message, false, Vector3.Zero);
                        // !! HACK
                        im.imSessionID = im.fromAgentID;
                        return _FriendsLocalSimConnector.LocalFriendshipOffered(toID, im);
                    }
                    else if (region != null) // grid
                        return _FriendsSimConnector.FriendshipOffered(region, fromID, toID, message, name);
                    break;
                case "ApproveFriendshipRequest":
                    if (_FriendsLocalSimConnector != null) // standalone
                        return _FriendsLocalSimConnector.LocalFriendshipApproved(fromID, name, toID);
                    else if (region != null) //grid
                        return _FriendsSimConnector.FriendshipApproved(region, fromID, name, toID);
                    break;
            }

            return false;
        }

        protected void ForwardStatusNotificationToSim(UUID regionID, UUID foreignUserID, string user, bool online)
        {
            UUID userID;
            if (UUID.TryParse(user, out userID))
            {
                if (_FriendsLocalSimConnector != null)
                {
                    _log.DebugFormat("[HGFRIENDS SERVICE]: Local Notify, user {0} is {1}", foreignUserID, online ? "online" : "offline");
                    _FriendsLocalSimConnector.StatusNotify(foreignUserID, userID, online);
                }
                else
                {
                    GridRegion region = _GridService.GetRegionByUUID(UUID.Zero /* !!! */, regionID);
                    if (region != null)
                    {
                        _log.DebugFormat("[HGFRIENDS SERVICE]: Remote Notify to region {0}, user {1} is {2}", region.RegionName, foreignUserID, online ? "online" : "offline");
                        _FriendsSimConnector.StatusNotify(region, foreignUserID, userID.ToString(), online);
                    }
                }
            }
        }

        #endregion Aux
    }
}
