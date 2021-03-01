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
using System.Net;
using System.Reflection;

using OpenSim.Data;
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
    /// This service is for HG1.5 only, to make up for the fact that clients don't
    /// keep any private information in themselves, and that their 'home service'
    /// needs to do it for them.
    /// Once we have better clients, this shouldn't be needed.
    /// </summary>
    public class UserAgentService : UserAgentServiceBase, IUserAgentService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        // This will need to go into a DB table
        //static Dictionary<UUID, TravelingAgentInfo> _Database = new Dictionary<UUID, TravelingAgentInfo>();

        static bool _Initialized = false;

        protected static IGridUserService _GridUserService;
        protected static IGridService _GridService;
        protected static GatekeeperServiceConnector _GatekeeperConnector;
        protected static IGatekeeperService _GatekeeperService;
        protected static IFriendsService _FriendsService;
        protected static IPresenceService _PresenceService;
        protected static IUserAccountService _UserAccountService;
        protected static IFriendsSimConnector _FriendsLocalSimConnector; // standalone, points to HGFriendsModule
        protected static FriendsSimConnector _FriendsSimConnector; // grid

        protected static string _GridName;
        protected static string _MyExternalIP = "";

        protected static int _LevelOutsideContacts;
        protected static bool _ShowDetails;

        protected static bool _BypassClientVerification;

        private static readonly Dictionary<int, bool> _ForeignTripsAllowed = new Dictionary<int, bool>();
        private static readonly Dictionary<int, List<string>> _TripsAllowedExceptions = new Dictionary<int, List<string>>();
        private static readonly Dictionary<int, List<string>> _TripsDisallowedExceptions = new Dictionary<int, List<string>>();

        public UserAgentService(IConfigSource config) : this(config, null)
        {
        }

        public UserAgentService(IConfigSource config, IFriendsSimConnector friendsConnector)
            : base(config)
        {
            // Let's set this always, because we don't know the sequence
            // of instantiations
            if (friendsConnector != null)
                _FriendsLocalSimConnector = friendsConnector;

            if (!_Initialized)
            {
                _Initialized = true;

                _log.DebugFormat("[HOME USERS SECURITY]: Starting...");

                _FriendsSimConnector = new FriendsSimConnector();

                IConfig serverConfig = config.Configs["UserAgentService"];
                if (serverConfig == null)
                    throw new Exception(string.Format("No section UserAgentService in config file"));

                string gridService = serverConfig.GetString("GridService", string.Empty);
                string gridUserService = serverConfig.GetString("GridUserService", string.Empty);
                string gatekeeperService = serverConfig.GetString("GatekeeperService", string.Empty);
                string friendsService = serverConfig.GetString("FriendsService", string.Empty);
                string presenceService = serverConfig.GetString("PresenceService", string.Empty);
                string userAccountService = serverConfig.GetString("UserAccountService", string.Empty);

                _BypassClientVerification = serverConfig.GetBoolean("BypassClientVerification", false);

                if (string.IsNullOrEmpty(gridService) || string.IsNullOrEmpty(gridUserService) || string.IsNullOrEmpty(gatekeeperService))
                    throw new Exception(string.Format("Incomplete specifications, UserAgent Service cannot function."));

                object[] args = new object[] { config };
                _GridService = ServerUtils.LoadPlugin<IGridService>(gridService, args);
                _GridUserService = ServerUtils.LoadPlugin<IGridUserService>(gridUserService, args);
                _GatekeeperConnector = new GatekeeperServiceConnector();
                _GatekeeperService = ServerUtils.LoadPlugin<IGatekeeperService>(gatekeeperService, args);
                _FriendsService = ServerUtils.LoadPlugin<IFriendsService>(friendsService, args);
                _PresenceService = ServerUtils.LoadPlugin<IPresenceService>(presenceService, args);
                _UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(userAccountService, args);

                _LevelOutsideContacts = serverConfig.GetInt("LevelOutsideContacts", 0);
                _ShowDetails = serverConfig.GetBoolean("ShowUserDetailsInHGProfile", true);

                LoadTripPermissionsFromConfig(serverConfig, "ForeignTripsAllowed");
                LoadDomainExceptionsFromConfig(serverConfig, "AllowExcept", _TripsAllowedExceptions);
                LoadDomainExceptionsFromConfig(serverConfig, "DisallowExcept", _TripsDisallowedExceptions);

                _GridName = Util.GetConfigVarFromSections<string>(config, "GatekeeperURI",
                    new string[] { "Startup", "Hypergrid", "UserAgentService" }, string.Empty);
                if (string.IsNullOrEmpty(_GridName)) // Legacy. Remove soon.
                {
                    _GridName = serverConfig.GetString("ExternalName", string.Empty);
                    if (string.IsNullOrEmpty(_GridName))
                    {
                        serverConfig = config.Configs["GatekeeperService"];
                        _GridName = serverConfig.GetString("ExternalName", string.Empty);
                    }
                }

                if (!string.IsNullOrEmpty(_GridName))
                {
                    _GridName = _GridName.ToLowerInvariant();
                    if (!_GridName.EndsWith("/"))
                        _GridName = _GridName + "/";
                    Uri gateURI;
                    if(!Uri.TryCreate(_GridName, UriKind.Absolute, out gateURI))
                        throw new Exception(string.Format("[UserAgentService] could not parse gatekeeper uri"));
                    string host = gateURI.DnsSafeHost;
                    IPAddress ip = Util.GetHostFromDNS(host);
                    if(ip == null)
                        throw new Exception(string.Format("[UserAgentService] failed to resolve gatekeeper host"));
                    _MyExternalIP = ip.ToString();
                }
                // Finally some cleanup
                _Database.DeleteOld();

            }
        }

        protected void LoadTripPermissionsFromConfig(IConfig config, string variable)
        {
            foreach (string keyName in config.GetKeys())
            {
                if (keyName.StartsWith(variable + "_Level_"))
                {
                    int level = 0;
                    if (int.TryParse(keyName.Replace(variable + "_Level_", ""), out level))
                        _ForeignTripsAllowed.Add(level, config.GetBoolean(keyName, true));
                }
            }
        }

        protected void LoadDomainExceptionsFromConfig(IConfig config, string variable, Dictionary<int, List<string>> exceptions)
        {
            foreach (string keyName in config.GetKeys())
            {
                if (keyName.StartsWith(variable + "_Level_"))
                {
                    int level = 0;
                    if (int.TryParse(keyName.Replace(variable + "_Level_", ""), out level) && !exceptions.ContainsKey(level))
                    {
                        exceptions.Add(level, new List<string>());
                        string value = config.GetString(keyName, string.Empty);
                        string[] parts = value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (string s in parts)
                            exceptions[level].Add(s.Trim());
                    }
                }
            }
        }

        public GridRegion GetHomeRegion(UUID userID, out Vector3 position, out Vector3 lookAt)
        {
            position = new Vector3(128, 128, 0); lookAt = Vector3.UnitY;

            _log.DebugFormat("[USER AGENT SERVICE]: Request to get home region of user {0}", userID);

            GridRegion home = null;
            GridUserInfo uinfo = _GridUserService.GetGridUserInfo(userID.ToString());
            if (uinfo != null)
            {
                if (uinfo.HomeRegionID != UUID.Zero)
                {
                    home = _GridService.GetRegionByUUID(UUID.Zero, uinfo.HomeRegionID);
                    position = uinfo.HomePosition;
                    lookAt = uinfo.HomeLookAt;
                }
                if (home == null)
                {
                    List<GridRegion> defs = _GridService.GetDefaultRegions(UUID.Zero);
                    if (defs != null && defs.Count > 0)
                        home = defs[0];
                }
            }

            return home;
        }

        public bool LoginAgentToGrid(GridRegion source, AgentCircuitData agentCircuit, GridRegion gatekeeper, GridRegion finalDestination, bool fromLogin, out string reason)
        {
            _log.DebugFormat("[USER AGENT SERVICE]: Request to login user {0} {1} (@{2}) to grid {3}",
                agentCircuit.firstname, agentCircuit.lastname, fromLogin ? agentCircuit.IPAddress : "stored IP", gatekeeper.ServerURI);

            string gridName = gatekeeper.ServerURI.ToLowerInvariant();

            UserAccount account = _UserAccountService.GetUserAccount(UUID.Zero, agentCircuit.AgentID);
            if (account == null)
            {
                _log.WarnFormat("[USER AGENT SERVICE]: Someone attempted to lauch a foreign user from here {0} {1}", agentCircuit.firstname, agentCircuit.lastname);
                reason = "Forbidden to launch your agents from here";
                return false;
            }

            // Is this user allowed to go there?
            if (_GridName != gridName)
            {
                if (_ForeignTripsAllowed.ContainsKey(account.UserLevel))
                {
                    bool allowed = _ForeignTripsAllowed[account.UserLevel];

                    if (_ForeignTripsAllowed[account.UserLevel] && IsException(gridName, account.UserLevel, _TripsAllowedExceptions))
                        allowed = false;

                    if (!_ForeignTripsAllowed[account.UserLevel] && IsException(gridName, account.UserLevel, _TripsDisallowedExceptions))
                        allowed = true;

                    if (!allowed)
                    {
                        reason = "Your world does not allow you to visit the destination";
                        _log.InfoFormat("[USER AGENT SERVICE]: Agents not permitted to visit {0}. Refusing service.", gridName);
                        return false;
                    }
                }
            }

            // Take the IP address + port of the gatekeeper (reg) plus the info of finalDestination
            GridRegion region = new GridRegion(gatekeeper)
            {
                ServerURI = gatekeeper.ServerURI,
                ExternalHostName = finalDestination.ExternalHostName,
                InternalEndPoint = finalDestination.InternalEndPoint,
                RegionName = finalDestination.RegionName,
                RegionID = finalDestination.RegionID,
                RegionLocX = finalDestination.RegionLocX,
                RegionLocY = finalDestination.RegionLocY
            };

            // Generate a new service session
            agentCircuit.ServiceSessionID = region.ServerURI + ";" + UUID.Random();
            TravelingAgentInfo old = null;
            TravelingAgentInfo travel = CreateTravelInfo(agentCircuit, region, fromLogin, out old);

            if(!fromLogin && old != null && !string.IsNullOrEmpty(old.ClientIPAddress))
            {
                _log.DebugFormat("[USER AGENT SERVICE]: stored IP = {0}. Old circuit IP: {1}", old.ClientIPAddress, agentCircuit.IPAddress);
                agentCircuit.IPAddress = old.ClientIPAddress;
            }

            bool success = false;

            _log.DebugFormat("[USER AGENT SERVICE]: this grid: {0}, desired grid: {1}, desired region: {2}", _GridName, gridName, region.RegionID);

            if (_GridName.Equals(gridName, StringComparison.InvariantCultureIgnoreCase))
            {
                success = _GatekeeperService.LoginAgent(source, agentCircuit, finalDestination, out reason);
            }
            else
            {
                //TODO: Should there not be a call to QueryAccess here?
                EntityTransferContext ctx = new EntityTransferContext();
                success = _GatekeeperConnector.CreateAgent(source, region, agentCircuit, (uint)Constants.TeleportFlags.ViaLogin, ctx, out reason);
            }

            if (!success)
            {
                _log.DebugFormat("[USER AGENT SERVICE]: Unable to login user {0} {1} to grid {2}, reason: {3}",
                    agentCircuit.firstname, agentCircuit.lastname, region.ServerURI, reason);

                if (old != null)
                    StoreTravelInfo(old);
                else
                    _Database.Delete(agentCircuit.SessionID);

                return false;
            }

            // Everything is ok

            StoreTravelInfo(travel);

            return true;
        }

        public bool LoginAgentToGrid(GridRegion source, AgentCircuitData agentCircuit, GridRegion gatekeeper, GridRegion finalDestination, out string reason)
        {
            reason = string.Empty;
            return LoginAgentToGrid(source, agentCircuit, gatekeeper, finalDestination, false, out reason);
        }

        TravelingAgentInfo CreateTravelInfo(AgentCircuitData agentCircuit, GridRegion region, bool fromLogin, out TravelingAgentInfo existing)
        {
            HGTravelingData hgt = _Database.Get(agentCircuit.SessionID);
            existing = null;

            if (hgt != null)
            {
                // Very important! Override whatever this agent comes with.
                // UserAgentService always sets the IP for every new agent
                // with the original IP address.
                existing = new TravelingAgentInfo(hgt);
                agentCircuit.IPAddress = existing.ClientIPAddress;
            }

            TravelingAgentInfo travel = new TravelingAgentInfo(existing)
            {
                SessionID = agentCircuit.SessionID,
                UserID = agentCircuit.AgentID,
                GridExternalName = region.ServerURI,
                ServiceToken = agentCircuit.ServiceSessionID
            };

            if (fromLogin)
                travel.ClientIPAddress = agentCircuit.IPAddress;

            StoreTravelInfo(travel);

            return travel;
        }

        public void LogoutAgent(UUID userID, UUID sessionID)
        {
            _log.DebugFormat("[USER AGENT SERVICE]: User {0} logged out", userID);

            _Database.Delete(sessionID);

            GridUserInfo guinfo = _GridUserService.GetGridUserInfo(userID.ToString());
            if (guinfo != null)
                _GridUserService.LoggedOut(userID.ToString(), sessionID, guinfo.LastRegionID, guinfo.LastPosition, guinfo.LastLookAt);
        }

        // We need to prevent foreign users with the same UUID as a local user
        public bool IsAgentComingHome(UUID sessionID, string thisGridExternalName)
        {
            HGTravelingData hgt = _Database.Get(sessionID);
            if (hgt == null)
                return false;

            TravelingAgentInfo travel = new TravelingAgentInfo(hgt);

            return travel.GridExternalName.ToLower() == thisGridExternalName.ToLower();
        }

        public bool VerifyClient(UUID sessionID, string reportedIP)
        {
            if (_BypassClientVerification)
                return true;

            _log.DebugFormat("[USER AGENT SERVICE]: Verifying Client session {0} with reported IP {1}.",
                sessionID, reportedIP);

            HGTravelingData hgt = _Database.Get(sessionID);
            if (hgt == null)
                return false;

            TravelingAgentInfo travel = new TravelingAgentInfo(hgt);

            bool result = travel.ClientIPAddress == reportedIP;
            if(!result && !string.IsNullOrEmpty(_MyExternalIP))
                result = reportedIP == _MyExternalIP; // NATed

            _log.DebugFormat("[USER AGENT SERVICE]: Comparing {0} with login IP {1} and MyIP {2}; result is {3}",
                                reportedIP, travel.ClientIPAddress, _MyExternalIP, result);

            return result;
        }

        public bool VerifyAgent(UUID sessionID, string token)
        {
            HGTravelingData hgt = _Database.Get(sessionID);
            if (hgt == null)
            {
                _log.DebugFormat("[USER AGENT SERVICE]: Token verification for session {0}: no such session", sessionID);
                return false;
            }

            TravelingAgentInfo travel = new TravelingAgentInfo(hgt);
            _log.DebugFormat("[USER AGENT SERVICE]: Verifying agent token {0} against {1}", token, travel.ServiceToken);
            return travel.ServiceToken == token;
        }

        [Obsolete]
        public List<UUID> StatusNotification(List<string> friends, UUID foreignUserID, bool online)
        {
            if (_FriendsService == null || _PresenceService == null)
            {
                _log.WarnFormat("[USER AGENT SERVICE]: Unable to perform status notifications because friends or presence services are missing");
                return new List<UUID>();
            }

            List<UUID> localFriendsOnline = new List<UUID>();

            _log.DebugFormat("[USER AGENT SERVICE]: Status notification: foreign user {0} wants to notify {1} local friends", foreignUserID, friends.Count);

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
            _log.DebugFormat("[USER AGENT SERVICE]: Status notification: user has {0} local friends", usersToBeNotified.Count);

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

            //// Lastly, let's notify the rest who may be online somewhere else
            //foreach (string user in usersToBeNotified)
            //{
            //    UUID id = new UUID(user);
            //    if (_Database.ContainsKey(id) && _Database[id].GridExternalName != _GridName)
            //    {
            //        string url = _Database[id].GridExternalName;
            //        // forward
            //        _log.WarnFormat("[USER AGENT SERVICE]: User {0} is visiting {1}. HG Status notifications still not implemented.", user, url);
            //    }
            //}

            // and finally, let's send the online friends
            if (online)
            {
                return localFriendsOnline;
            }
            else
                return new List<UUID>();
        }

        [Obsolete]
        protected void ForwardStatusNotificationToSim(UUID regionID, UUID foreignUserID, string user, bool online)
        {
            UUID userID;
            if (UUID.TryParse(user, out userID))
            {
                if (_FriendsLocalSimConnector != null)
                {
                    _log.DebugFormat("[USER AGENT SERVICE]: Local Notify, user {0} is {1}", foreignUserID, online ? "online" : "offline");
                    _FriendsLocalSimConnector.StatusNotify(foreignUserID, userID, online);
                }
                else
                {
                    GridRegion region = _GridService.GetRegionByUUID(UUID.Zero /* !!! */, regionID);
                    if (region != null)
                    {
                        _log.DebugFormat("[USER AGENT SERVICE]: Remote Notify to region {0}, user {1} is {2}", region.RegionName, foreignUserID, online ? "online" : "offline");
                        _FriendsSimConnector.StatusNotify(region, foreignUserID, userID.ToString(), online);
                    }
                }
            }
        }

        public List<UUID> GetOnlineFriends(UUID foreignUserID, List<string> friends)
        {
            List<UUID> online = new List<UUID>();

            if (_FriendsService == null || _PresenceService == null)
            {
                _log.WarnFormat("[USER AGENT SERVICE]: Unable to get online friends because friends or presence services are missing");
                return online;
            }

            _log.DebugFormat("[USER AGENT SERVICE]: Foreign user {0} wants to know status of {1} local friends", foreignUserID, friends.Count);

            // First, let's double check that the reported friends are, indeed, friends of that user
            // And let's check that the secret matches and the rights
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
                        if (finfo.Friend.StartsWith(foreignUserID.ToString()) && finfo.Friend.EndsWith(secret) &&
                            (finfo.TheirFlags & (int)FriendRights.CanSeeOnline) != 0 && finfo.TheirFlags != -1)
                        {
                            // great!
                            usersToBeNotified.Add(localUserID.ToString());
                        }
                    }
                }
            }

            // Now, let's find out their status
            _log.DebugFormat("[USER AGENT SERVICE]: GetOnlineFriends: user has {0} local friends with status rights", usersToBeNotified.Count);

            // First, let's send notifications to local users who are online in the home grid
            PresenceInfo[] friendSessions = _PresenceService.GetAgents(usersToBeNotified.ToArray());
            if (friendSessions != null && friendSessions.Length > 0)
            {
                foreach (PresenceInfo pi in friendSessions)
                {
                    UUID presenceID;
                    if (UUID.TryParse(pi.UserID, out presenceID))
                        online.Add(presenceID);
                }
            }

            return online;
        }

        public Dictionary<string, object> GetUserInfo(UUID  userID)
        {
            Dictionary<string, object> info = new Dictionary<string, object>();

            if (_UserAccountService == null)
            {
                _log.WarnFormat("[USER AGENT SERVICE]: Unable to get user flags because user account service is missing");
                info["result"] = "fail";
                info["message"] = "UserAccountService is missing!";
                return info;
            }

            UserAccount account = _UserAccountService.GetUserAccount(UUID.Zero /*!!!*/, userID);

            if (account != null)
            {
                info.Add("user_firstname", account.FirstName);
                info.Add("user_lastname", account.LastName);
                info.Add("result", "success");

                if (_ShowDetails)
                {
                    info.Add("user_flags", account.UserFlags);
                    info.Add("user_created", account.Created);
                    info.Add("user_title", account.UserTitle);
                }
                else
                {
                    info.Add("user_flags", 0);
                    info.Add("user_created", 0);
                    info.Add("user_title", string.Empty);
                }
            }

            return info;
        }

        public Dictionary<string, object> GetServerURLs(UUID userID)
        {
            if (_UserAccountService == null)
            {
                _log.WarnFormat("[USER AGENT SERVICE]: Unable to get server URLs because user account service is missing");
                return new Dictionary<string, object>();
            }
            UserAccount account = _UserAccountService.GetUserAccount(UUID.Zero /*!!!*/, userID);
            if (account != null)
                return account.ServiceURLs;

            return new Dictionary<string, object>();
        }

        public string LocateUser(UUID userID)
        {
            HGTravelingData[] hgts = _Database.GetSessions(userID);
            if (hgts == null)
                return string.Empty;

            foreach (HGTravelingData t in hgts)
                if (t.Data.ContainsKey("GridExternalName") && !_GridName.Equals(t.Data["GridExternalName"]))
                    return t.Data["GridExternalName"];

            return string.Empty;
        }

        public string GetUUI(UUID userID, UUID targetUserID)
        {
            // Let's see if it's a local user
            UserAccount account = _UserAccountService.GetUserAccount(UUID.Zero, targetUserID);
            if (account != null)
                return targetUserID.ToString() + ";" + _GridName + ";" + account.FirstName + " " + account.LastName ;

            // Let's try the list of friends
            if(_FriendsService != null)
            {
                FriendInfo[] friends = _FriendsService.GetFriends(userID);
                if (friends != null && friends.Length > 0)
                {
                    foreach (FriendInfo f in friends)
                        if (f.Friend.StartsWith(targetUserID.ToString()))
                        {
                            // Let's remove the secret
                            UUID id; string tmp = string.Empty, secret = string.Empty;
                            if (Util.ParseUniversalUserIdentifier(f.Friend, out id, out tmp, out tmp, out tmp, out secret))
                                return f.Friend.Replace(secret, "0");
                        }
                }
            }
            return string.Empty;
        }

        public UUID GetUUID(string first, string last)
        {
            // Let's see if it's a local user
            UserAccount account = _UserAccountService.GetUserAccount(UUID.Zero, first, last);
            if (account != null)
            {
                // check user level
                if (account.UserLevel < _LevelOutsideContacts)
                    return UUID.Zero;
                else
                    return account.PrincipalID;
            }
            else
                return UUID.Zero;
        }

        #region Misc

        private bool IsException(string dest, int level, Dictionary<int, List<string>> exceptions)
        {
            if (!exceptions.ContainsKey(level))
                return false;

            bool exception = false;
            if (exceptions[level].Count > 0) // we have exceptions
            {
                string destination = dest;
                if (!destination.EndsWith("/"))
                    destination += "/";

                if (exceptions[level].Find(delegate(string s)
                {
                    if (!s.EndsWith("/"))
                        s += "/";
                    return s == destination;
                }) != null)
                    exception = true;
            }

            return exception;
        }

        private void StoreTravelInfo(TravelingAgentInfo travel)
        {
            if (travel == null)
                return;

            HGTravelingData hgt = new HGTravelingData
            {
                SessionID = travel.SessionID,
                UserID = travel.UserID,
                Data = new Dictionary<string, string>()
            };
            hgt.Data["GridExternalName"] = travel.GridExternalName;
            hgt.Data["ServiceToken"] = travel.ServiceToken;
            hgt.Data["ClientIPAddress"] = travel.ClientIPAddress;

            _Database.Store(hgt);
        }
        #endregion

    }

    class TravelingAgentInfo
    {
        public UUID SessionID;
        public UUID UserID;
        public string GridExternalName = string.Empty;
        public string ServiceToken = string.Empty;
        public string ClientIPAddress = string.Empty; // as seen from this user agent service

        public TravelingAgentInfo(HGTravelingData t)
        {
            if (t.Data != null)
            {
                SessionID = new UUID(t.SessionID);
                UserID = new UUID(t.UserID);
                GridExternalName = t.Data["GridExternalName"];
                ServiceToken = t.Data["ServiceToken"];
                ClientIPAddress = t.Data["ClientIPAddress"];
            }
        }

        public TravelingAgentInfo(TravelingAgentInfo old)
        {
            if (old != null)
            {
                SessionID = old.SessionID;
                UserID = old.UserID;
                GridExternalName = old.GridExternalName;
                ServiceToken = old.ServiceToken;
                ClientIPAddress = old.ClientIPAddress;
            }
        }
    }

}
