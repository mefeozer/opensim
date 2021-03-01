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
using System.Text.RegularExpressions;

using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Server.Base;
using OpenSim.Services.Connectors.InstantMessage;
using OpenSim.Services.Connectors.Hypergrid;
using OpenMetaverse;

using Nini.Config;
using log4net;

namespace OpenSim.Services.HypergridService
{
    public class GatekeeperService : IGatekeeperService
    {
        private static readonly ILog _log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private static bool _Initialized = false;

        private static IGridService _GridService;
        private static IPresenceService _PresenceService;
        private static IUserAccountService _UserAccountService;
        private static IUserAgentService _UserAgentService;
        private static ISimulationService _SimulationService;
        private static IGridUserService _GridUserService;
        private static IBansService _BansService;

        private static string _AllowedClients = string.Empty;
        private static string _DeniedClients = string.Empty;
        private static string _DeniedMacs = string.Empty;
        private static bool _ForeignAgentsAllowed = true;
        private static readonly List<string> _ForeignsAllowedExceptions = new List<string>();
        private static readonly List<string> _ForeignsDisallowedExceptions = new List<string>();

        private static UUID _ScopeID;
        private static bool _AllowTeleportsToAnyRegion;

        private static OSHHTPHost _gatekeeperHost;
        private static string _gatekeeperURL;
        private readonly HashSet<OSHHTPHost> _gateKeeperAlias;

        private static GridRegion _DefaultGatewayRegion;
        private readonly bool _allowDuplicatePresences = false;
        private static string _messageKey;

        public GatekeeperService(IConfigSource config, ISimulationService simService)
        {
            if (!_Initialized)
            {
                _Initialized = true;

                IConfig serverConfig = config.Configs["GatekeeperService"];
                if (serverConfig == null)
                    throw new Exception(string.Format("No section GatekeeperService in config file"));

                string accountService = serverConfig.GetString("UserAccountService", string.Empty);
                string homeUsersService = serverConfig.GetString("UserAgentService", string.Empty);
                string gridService = serverConfig.GetString("GridService", string.Empty);
                string presenceService = serverConfig.GetString("PresenceService", string.Empty);
                string simulationService = serverConfig.GetString("SimulationService", string.Empty);
                string gridUserService = serverConfig.GetString("GridUserService", string.Empty);
                string bansService = serverConfig.GetString("BansService", string.Empty);
                // These are mandatory, the others aren't
                if (string.IsNullOrEmpty(gridService) || string.IsNullOrEmpty(presenceService))
                    throw new Exception("Incomplete specifications, Gatekeeper Service cannot function.");

                string scope = serverConfig.GetString("ScopeID", UUID.Zero.ToString());
                UUID.TryParse(scope, out _ScopeID);
                //_WelcomeMessage = serverConfig.GetString("WelcomeMessage", "Welcome to OpenSim!");
                _AllowTeleportsToAnyRegion = serverConfig.GetBoolean("AllowTeleportsToAnyRegion", true);

                string[] sections = new string[] { "Const, Startup", "Hypergrid", "GatekeeperService" };
                string externalName = Util.GetConfigVarFromSections<string>(config, "GatekeeperURI", sections, string.Empty);
                if(string.IsNullOrEmpty(externalName))
                    externalName = serverConfig.GetString("ExternalName", string.Empty);

                _gatekeeperHost = new OSHHTPHost(externalName, true);
                if (!_gatekeeperHost.IsResolvedHost)
                {
                    _log.Error((_gatekeeperHost.IsValidHost ? "Could not resolve GatekeeperURI" : "GatekeeperURI is a invalid host ") + externalName ?? "");
                    throw new Exception("GatekeeperURI is invalid");
                }
                _gatekeeperURL = _gatekeeperHost.URIwEndSlash;

                string gatekeeperURIAlias = Util.GetConfigVarFromSections<string>(config, "GatekeeperURIAlias", sections, string.Empty);

                if (!string.IsNullOrWhiteSpace(gatekeeperURIAlias))
                {
                    string[] alias = gatekeeperURIAlias.Split(',');
                    for (int i = 0; i < alias.Length; ++i)
                    {
                        OSHHTPHost tmp = new OSHHTPHost(alias[i].Trim(), false);
                        if (tmp.IsValidHost)
                        {
                            if (_gateKeeperAlias == null)
                                _gateKeeperAlias = new HashSet<OSHHTPHost>();
                            _gateKeeperAlias.Add(tmp);
                        }
                    }
                }

                object[] args = new object[] { config };
                _GridService = ServerUtils.LoadPlugin<IGridService>(gridService, args);
                _PresenceService = ServerUtils.LoadPlugin<IPresenceService>(presenceService, args);

                if (!string.IsNullOrEmpty(accountService))
                    _UserAccountService = ServerUtils.LoadPlugin<IUserAccountService>(accountService, args);
                if (!string.IsNullOrEmpty(homeUsersService))
                    _UserAgentService = ServerUtils.LoadPlugin<IUserAgentService>(homeUsersService, args);
                if (!string.IsNullOrEmpty(gridUserService))
                    _GridUserService = ServerUtils.LoadPlugin<IGridUserService>(gridUserService, args);
                if (!string.IsNullOrEmpty(bansService))
                    _BansService = ServerUtils.LoadPlugin<IBansService>(bansService, args);

                if (simService != null)
                    _SimulationService = simService;
                else if (!string.IsNullOrEmpty(simulationService))
                        _SimulationService = ServerUtils.LoadPlugin<ISimulationService>(simulationService, args);

                string[] possibleAccessControlConfigSections = new string[] { "AccessControl", "GatekeeperService" };
                _AllowedClients = Util.GetConfigVarFromSections<string>(
                        config, "AllowedClients", possibleAccessControlConfigSections, string.Empty);
                _DeniedClients = Util.GetConfigVarFromSections<string>(
                        config, "DeniedClients", possibleAccessControlConfigSections, string.Empty);
                _DeniedMacs = Util.GetConfigVarFromSections<string>(
                        config, "DeniedMacs", possibleAccessControlConfigSections, string.Empty);
                _ForeignAgentsAllowed = serverConfig.GetBoolean("ForeignAgentsAllowed", true);

                LoadDomainExceptionsFromConfig(serverConfig, "AllowExcept", _ForeignsAllowedExceptions);
                LoadDomainExceptionsFromConfig(serverConfig, "DisallowExcept", _ForeignsDisallowedExceptions);

                if (_GridService == null || _PresenceService == null || _SimulationService == null)
                    throw new Exception("Unable to load a required plugin, Gatekeeper Service cannot function.");

                IConfig presenceConfig = config.Configs["PresenceService"];
                if (presenceConfig != null)
                {
                    _allowDuplicatePresences = presenceConfig.GetBoolean("AllowDuplicatePresences", _allowDuplicatePresences);
                }

                IConfig messagingConfig = config.Configs["Messaging"];
                if (messagingConfig != null)
                    _messageKey = messagingConfig.GetString("MessageKey", string.Empty);
                _log.Debug("[GATEKEEPER SERVICE]: Starting...");
            }
        }

        public GatekeeperService(IConfigSource config)
            : this(config, null)
        {
        }

        protected void LoadDomainExceptionsFromConfig(IConfig config, string variable, List<string> exceptions)
        {
            string value = config.GetString(variable, string.Empty);
            string[] parts = value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string s in parts)
                exceptions.Add(s.Trim());
        }

        public bool LinkRegion(string regionName, out UUID regionID, out ulong regionHandle, out string externalName, out string imageURL, out string reason, out int sizeX, out int sizeY)
        {
            regionID = UUID.Zero;
            regionHandle = 0;
            sizeX = (int)Constants.RegionSize;
            sizeY = (int)Constants.RegionSize;
            externalName = _gatekeeperURL + (!string.IsNullOrEmpty(regionName) ? " " + regionName : "");
            imageURL = string.Empty;
            reason = string.Empty;
            GridRegion region = null;

            //_log.DebugFormat("[GATEKEEPER SERVICE]: Request to link to {0}", (regionName == string.Empty)? "default region" : regionName);
            if (!_AllowTeleportsToAnyRegion || string.IsNullOrEmpty(regionName))
            {
                List<GridRegion> defs = _GridService.GetDefaultHypergridRegions(_ScopeID);
                if (defs != null && defs.Count > 0)
                {
                    region = defs[0];
                    _DefaultGatewayRegion = region;
                }
                else
                {
                    reason = "Grid setup problem. Try specifying a particular region here.";
                    _log.DebugFormat("[GATEKEEPER SERVICE]: Unable to send information. Please specify a default region for this grid!");
                    return false;
                }
            }
            else
            {
                region = _GridService.GetRegionByName(_ScopeID, regionName);
                if (region == null)
                {
                    reason = "Region not found";
                    return false;
                }
            }

            regionID = region.RegionID;
            regionHandle = region.RegionHandle;
            sizeX = region.RegionSizeX;
            sizeY = region.RegionSizeY;

            string regionimage = "regionImage" + regionID.ToString();
            regionimage = regionimage.Replace("-", "");
            imageURL = region.ServerURI + "index.php?method=" + regionimage;

            return true;
        }

        public GridRegion GetHyperlinkRegion(UUID regionID, UUID agentID, string agentHomeURI, out string message)
        {
            message = null;

            if (!_AllowTeleportsToAnyRegion)
            {
                // Don't even check the given regionID
                _log.DebugFormat(
                    "[GATEKEEPER SERVICE]: Returning gateway region {0} {1} @ {2} to user {3}{4} as teleporting to arbitrary regions is not allowed.",
                    _DefaultGatewayRegion.RegionName,
                    _DefaultGatewayRegion.RegionID,
                    _DefaultGatewayRegion.ServerURI,
                    agentID,
                    agentHomeURI == null ? "" : " @ " + agentHomeURI);

                message = "Teleporting to the default region.";
                return _DefaultGatewayRegion;
            }

            GridRegion region = _GridService.GetRegionByUUID(_ScopeID, regionID);

            if (region == null)
            {
                _log.DebugFormat(
                    "[GATEKEEPER SERVICE]: Could not find region with ID {0} as requested by user {1}{2}.  Returning null.",
                    regionID, agentID, agentHomeURI == null ? "" : " @ " + agentHomeURI);

                message = "The teleport destination could not be found.";
                return null;
            }

            _log.DebugFormat(
                "[GATEKEEPER SERVICE]: Returning region {0} {1} @ {2} to user {3}{4}.",
                region.RegionName,
                region.RegionID,
                region.ServerURI,
                agentID,
                agentHomeURI == null ? "" : " @ " + agentHomeURI);

            return region;
        }

        #region Login Agent
        public bool LoginAgent(GridRegion source, AgentCircuitData aCircuit, GridRegion destination, out string reason)
        {
            reason = string.Empty;

            string authURL = string.Empty;
            if (aCircuit.ServiceURLs.ContainsKey("HomeURI"))
                authURL = aCircuit.ServiceURLs["HomeURI"].ToString();

            _log.InfoFormat("[GATEKEEPER SERVICE]: Login request for {0} {1} @ {2} ({3}) at {4} using viewer {5}, channel {6}, IP {7}, Mac {8}, Id0 {9}, Teleport Flags: {10}. From region {11}",
                aCircuit.firstname, aCircuit.lastname, authURL, aCircuit.AgentID, destination.RegionID,
                aCircuit.Viewer, aCircuit.Channel, aCircuit.IPAddress, aCircuit.Mac, aCircuit.Id0, (TeleportFlags)aCircuit.teleportFlags,
                source == null ? "Unknown" : string.Format("{0} ({1}){2}", source.RegionName, source.RegionID, source.RawServerURI == null ? "" : " @ " + source.ServerURI));

            string curViewer = Util.GetViewerName(aCircuit);
            string curMac = aCircuit.Mac.ToString();


            //
            // Check client
            //
            if (!string.IsNullOrWhiteSpace(_AllowedClients))
            {
                Regex arx = new Regex(_AllowedClients);
                Match am = arx.Match(curViewer);

                if (!am.Success)
                {
                    reason = "Login failed: client " + curViewer + " is not allowed";
                    _log.InfoFormat("[GATEKEEPER SERVICE]: Login failed, reason: client {0} is not allowed", curViewer);
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(_DeniedClients))
            {
                Regex drx = new Regex(_DeniedClients);
                Match dm = drx.Match(curViewer);

                if (dm.Success)
                {
                    reason = "Login failed: client " + curViewer + " is denied";
                    _log.InfoFormat("[GATEKEEPER SERVICE]: Login failed, reason: client {0} is denied", curViewer);
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(_DeniedMacs))
            {
                _log.InfoFormat("[GATEKEEPER SERVICE]: Checking users Mac {0} against list of denied macs {1} ...", curMac, _DeniedMacs);
                if (_DeniedMacs.Contains(curMac))
                {
                    reason = "Login failed: client with Mac " + curMac + " is denied";
                    _log.InfoFormat("[GATEKEEPER SERVICE]: Login failed, reason: client with mac {0} is denied", curMac);
                    return false;
                }
            }

            //
            // Authenticate the user
            //
            if (!Authenticate(aCircuit))
            {
                reason = "Unable to verify identity";
                _log.InfoFormat("[GATEKEEPER SERVICE]: Unable to verify identity of agent {0} {1}. Refusing service.", aCircuit.firstname, aCircuit.lastname);
                return false;
            }
            _log.DebugFormat("[GATEKEEPER SERVICE]: Identity verified for {0} {1} @ {2}", aCircuit.firstname, aCircuit.lastname, authURL);

            //
            // Check for impersonations
            //
            UserAccount account = null;
            if (_UserAccountService != null)
            {
                // Check to see if we have a local user with that UUID
                account = _UserAccountService.GetUserAccount(_ScopeID, aCircuit.AgentID);
                if (account != null)
                {
                    // Make sure this is the user coming home, and not a foreign user with same UUID as a local user
                    if (_UserAgentService != null)
                    {
                        if (!_UserAgentService.IsAgentComingHome(aCircuit.SessionID, _gatekeeperURL))
                        {
                            // Can't do, sorry
                            reason = "Unauthorized";
                            _log.InfoFormat("[GATEKEEPER SERVICE]: Foreign agent {0} {1} has same ID as local user. Refusing service.",
                                aCircuit.firstname, aCircuit.lastname);
                            return false;

                        }
                    }
                }
            }

            //
            // Foreign agents allowed? Exceptions?
            //
            if (account == null)
            {
                bool allowed = _ForeignAgentsAllowed;

                if (_ForeignAgentsAllowed && IsException(aCircuit, _ForeignsAllowedExceptions))
                    allowed = false;

                if (!_ForeignAgentsAllowed && IsException(aCircuit, _ForeignsDisallowedExceptions))
                    allowed = true;

                if (!allowed)
                {
                    reason = "Destination does not allow visitors from your world";
                    _log.InfoFormat("[GATEKEEPER SERVICE]: Foreign agents are not permitted {0} {1} @ {2}. Refusing service.",
                        aCircuit.firstname, aCircuit.lastname, aCircuit.ServiceURLs["HomeURI"]);
                    return false;
                }
            }

            //
            // Is the user banned?
            // This uses a Ban service that's more powerful than the configs
            //
            string uui = account != null ? aCircuit.AgentID.ToString() : Util.ProduceUserUniversalIdentifier(aCircuit);
            if (_BansService != null && _BansService.IsBanned(uui, aCircuit.IPAddress, aCircuit.Id0, authURL))
            {
                reason = "You are banned from this world";
                _log.InfoFormat("[GATEKEEPER SERVICE]: Login failed, reason: user {0} is banned", uui);
                return false;
            }

            UUID agentID = aCircuit.AgentID;
            if(agentID == new UUID("6571e388-6218-4574-87db-f9379718315e"))
            {
                // really?
                reason = "Invalid account ID";
                return false;
            }

            if(_GridUserService != null)
            {
                string PrincipalIDstr = agentID.ToString();
                GridUserInfo guinfo = _GridUserService.GetGridUserInfo(PrincipalIDstr);

                if(!_allowDuplicatePresences)
                {
                    if(guinfo != null && guinfo.Online && guinfo.LastRegionID != UUID.Zero)
                    {
                        if(SendAgentGodKillToRegion(UUID.Zero, agentID, guinfo))
                        {
                            if(account != null)
                                _log.InfoFormat(
                                    "[GATEKEEPER SERVICE]: Login failed for {0} {1}, reason: already logged in",
                                    account.FirstName, account.LastName);
                            reason = "You appear to be already logged in on the destination grid " +
                                    "Please wait a a minute or two and retry. " +
                                    "If this takes longer than a few minutes please contact the grid owner.";
                            return false;
                        }
                    }
                }
            }

            _log.DebugFormat("[GATEKEEPER SERVICE]: User {0} is ok", aCircuit.Name);

            bool isFirstLogin = false;
            //
            // Login the presence, if it's not there yet (by the login service)
            //
            PresenceInfo presence = _PresenceService.GetAgent(aCircuit.SessionID);
            if (presence != null) // it has been placed there by the login service
                isFirstLogin = true;

            else
            {
                if (!_PresenceService.LoginAgent(aCircuit.AgentID.ToString(), aCircuit.SessionID, aCircuit.SecureSessionID))
                {
                    reason = "Unable to login presence";
                    _log.InfoFormat("[GATEKEEPER SERVICE]: Presence login failed for foreign agent {0} {1}. Refusing service.",
                        aCircuit.firstname, aCircuit.lastname);
                    return false;
                }

            }

            //
            // Get the region
            //
            destination = _GridService.GetRegionByUUID(_ScopeID, destination.RegionID);
            if (destination == null)
            {
                reason = "Destination region not found";
                return false;
            }

            _log.DebugFormat(
                "[GATEKEEPER SERVICE]: Destination {0} is ok for {1}", destination.RegionName, aCircuit.Name);

            //
            // Adjust the visible name
            //
            if (account != null)
            {
                aCircuit.firstname = account.FirstName;
                aCircuit.lastname = account.LastName;
            }
            if (account == null)
            {
                if (!aCircuit.lastname.StartsWith("@"))
                    aCircuit.firstname = aCircuit.firstname + "." + aCircuit.lastname;
                try
                {
                    Uri uri = new Uri(aCircuit.ServiceURLs["HomeURI"].ToString());
                    aCircuit.lastname = "@" + uri.Authority;
                }
                catch
                {
                    _log.WarnFormat("[GATEKEEPER SERVICE]: Malformed HomeURI (this should never happen): {0}", aCircuit.ServiceURLs["HomeURI"]);
                    aCircuit.lastname = "@" + aCircuit.ServiceURLs["HomeURI"].ToString();
                }
            }

            //
            // Finally launch the agent at the destination
            //
            Constants.TeleportFlags loginFlag = isFirstLogin ? Constants.TeleportFlags.ViaLogin : Constants.TeleportFlags.ViaHGLogin;

            // Preserve our TeleportFlags we have gathered so-far
            loginFlag |= (Constants.TeleportFlags) aCircuit.teleportFlags;

            _log.DebugFormat("[GATEKEEPER SERVICE]: Launching {0}, Teleport Flags: {1}", aCircuit.Name, loginFlag);

            EntityTransferContext ctx = new EntityTransferContext();

            if (!_SimulationService.QueryAccess(
                destination, aCircuit.AgentID, aCircuit.ServiceURLs["HomeURI"].ToString(),
                true, aCircuit.startpos, new List<UUID>(), ctx, out reason))
                return false;

            bool didit = _SimulationService.CreateAgent(source, destination, aCircuit, (uint)loginFlag, ctx, out reason);

            if(didit)
            {
                _log.DebugFormat("[GATEKEEPER SERVICE]: Login presence {0} is ok", aCircuit.Name);

                if(!isFirstLogin && _GridUserService != null && account == null) 
                {
                    // Also login foreigners with GridUser service
                    string userId = aCircuit.AgentID.ToString();
                    string first = aCircuit.firstname, last = aCircuit.lastname;
                    if (last.StartsWith("@"))
                    {
                        string[] parts = aCircuit.firstname.Split('.');
                        if (parts.Length >= 2)
                        {
                            first = parts[0];
                            last = parts[1];
                        }
                    }

                    userId += ";" + aCircuit.ServiceURLs["HomeURI"] + ";" + first + " " + last;
                    _GridUserService.LoggedIn(userId);
                }
            }

            return didit;
        }

        protected bool Authenticate(AgentCircuitData aCircuit)
        {
            if (!CheckAddress(aCircuit.ServiceSessionID))
                return false;

            if (string.IsNullOrEmpty(aCircuit.IPAddress))
            {
                _log.DebugFormat("[GATEKEEPER SERVICE]: Agent did not provide a client IP address.");
                return false;
            }

            string userURL = string.Empty;
            if (aCircuit.ServiceURLs.ContainsKey("HomeURI"))
                userURL = aCircuit.ServiceURLs["HomeURI"].ToString();

            OSHHTPHost userHomeHost = new OSHHTPHost(userURL, true);
            if(!userHomeHost.IsResolvedHost)
            {
                _log.DebugFormat("[GATEKEEPER SERVICE]: Agent did not provide an authentication server URL");
                return false;
            }

            if (_gatekeeperHost.Equals(userHomeHost))
            {
                return _UserAgentService.VerifyAgent(aCircuit.SessionID, aCircuit.ServiceSessionID);
            }
            else
            {
                IUserAgentService userAgentService = new UserAgentServiceConnector(userURL);

                try
                {
                    return userAgentService.VerifyAgent(aCircuit.SessionID, aCircuit.ServiceSessionID);
                }
                catch
                {
                    _log.DebugFormat("[GATEKEEPER SERVICE]: Unable to contact authentication service at {0}", userURL);
                    return false;
                }
            }
        }

        // Check that the service token was generated for *this* grid.
        // If it wasn't then that's a fake agent.
        protected bool CheckAddress(string serviceToken)
        {
            string[] parts = serviceToken.Split(new char[] { ';' });
            if (parts.Length < 2)
                return false;

            OSHHTPHost reqGrid = new OSHHTPHost(parts[0], false);
            if(!reqGrid.IsValidHost)
            {
                _log.DebugFormat("[GATEKEEPER SERVICE]: Visitor provided malformed gird address {0}", parts[0]);
                return false;
            }

            _log.DebugFormat("[GATEKEEPER SERVICE]: Verifying grid {0} against {1}", reqGrid.URI, _gatekeeperHost.URI);

            if(_gatekeeperHost.Equals(reqGrid))
                return true;
            if (_gateKeeperAlias != null && _gateKeeperAlias.Contains(reqGrid))
                return true;
            return false;
        }

        #endregion


        #region Misc

        private bool IsException(AgentCircuitData aCircuit, List<string> exceptions)
        {
            bool exception = false;
            if (exceptions.Count > 0) // we have exceptions
            {
                // Retrieve the visitor's origin
                string userURL = aCircuit.ServiceURLs["HomeURI"].ToString();
                if (!userURL.EndsWith("/"))
                    userURL += "/";

                if (exceptions.Find(delegate(string s)
                {
                    if (!s.EndsWith("/"))
                        s += "/";
                    return s == userURL;
                }) != null)
                    exception = true;
            }

            return exception;
        }

        private bool SendAgentGodKillToRegion(UUID scopeID, UUID agentID , GridUserInfo guinfo)
        {
            UUID regionID = guinfo.LastRegionID;
            GridRegion regInfo = _GridService.GetRegionByUUID(scopeID, regionID);
            if(regInfo == null)
                return false;

            string regURL = regInfo.ServerURI;
            if(string.IsNullOrEmpty(regURL))
                return false;

            GridInstantMessage msg = new GridInstantMessage
            {
                imSessionID = UUID.Zero.Guid,
                fromAgentID = Constants.servicesGodAgentID.Guid,
                toAgentID = agentID.Guid,
                timestamp = (uint)Util.UnixTimeSinceEpoch(),
                fromAgentName = "GRID",
                message = string.Format("New login detected"),
                dialog = 250, // God kick
                fromGroup = false,
                offline = (byte)0,
                ParentEstateID = 0,
                Position = Vector3.Zero,
                RegionID = scopeID.Guid,
                binaryBucket = new byte[1] { 0 }
            };
            InstantMessageServiceConnector.SendInstantMessage(regURL,msg, _messageKey);

            _GridUserService.LoggedOut(agentID.ToString(),
                UUID.Zero, guinfo.LastRegionID, guinfo.LastPosition, guinfo.LastLookAt);

            return true;
        }
        #endregion
    }
}
