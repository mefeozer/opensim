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
using System.Text;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenMetaverse.Messages.Linden;
using Mono.Addins;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Monitoring;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Caps = OpenSim.Framework.Capabilities.Caps;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;

namespace OpenSim.Region.CoreModules.World.Land
{
    // used for caching
    internal class ExtendedLandData
    {
        public LandData LandData;
        public ulong RegionHandle;
        public uint X, Y;
        public byte RegionAccess;
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "LandManagementModule")]
    public class LandManagementModule : INonSharedRegionModule , ILandChannel
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[LAND MANAGEMENT MODULE]";

        /// <summary>
        /// Minimum land unit size in region co-ordinates.
        /// </summary>

        public const int LandUnit = 4;

        private Scene _scene;
        //private LandChannel _landChannel;

        private ulong _regionHandler;
        private int _regionSizeX;
        private int _regionSizeY;

        protected IGroupsModule _groupManager;
        protected IUserManagement _userManager;
        protected IPrimCountModule _primCountModule;
        protected IDialogModule _Dialog;

        /// <value>
        /// Local land ids at specified region co-ordinates (region size / 4)
        /// </value>
        private int[,] _landIDList;

        /// <value>
        /// Land objects keyed by local id
        /// </value>

        private readonly Dictionary<int, ILandObject> _landList = new Dictionary<int, ILandObject>();
        private readonly Dictionary<UUID, int> _landGlobalIDs = new Dictionary<UUID, int>();
        private readonly Dictionary<UUID, int> _landFakeIDs = new Dictionary<UUID, int>();

        private int _lastLandLocalID = LandChannel.START_LAND_LOCAL_ID - 1;

        private bool _allowedForcefulBans = true;
        private bool _showBansLines = true;
        private UUID DefaultGodParcelGroup;
        private string DefaultGodParcelName;
        private UUID DefaultGodParcelOwner;

        // caches ExtendedLandData
        static private readonly ExpiringCacheOS<UUID,ExtendedLandData> _parcelInfoCache = new ExpiringCacheOS<UUID, ExtendedLandData>(10000);

        /// <summary>
        /// Record positions that avatar's are currently being forced to move to due to parcel entry restrictions.
        /// </summary>
        private readonly HashSet<UUID> forcedPosition = new HashSet<UUID>();


        // Enables limiting parcel layer info transmission when doing simple updates
        private bool shouldLimitParcelLayerInfoToViewDistance { get; set; }
        // "View distance" for sending parcel layer info if asked for from a view point in the region
        private int parcelLayerViewDistance { get; set; }

        private float _BanLineSafeHeight = 100.0f;
        public float BanLineSafeHeight
        {
            get => _BanLineSafeHeight;
            private set
            {
                if (value > 20f && value <= 5000f)
                    _BanLineSafeHeight = value;
                else
                    _BanLineSafeHeight = 100.0f;
            }
        }

        #region INonSharedRegionModule Members

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            shouldLimitParcelLayerInfoToViewDistance = true;
            parcelLayerViewDistance = 128;
            IConfig landManagementConfig = source.Configs["LandManagement"];
            if (landManagementConfig != null)
            {
                shouldLimitParcelLayerInfoToViewDistance = landManagementConfig.GetBoolean("LimitParcelLayerUpdateDistance", shouldLimitParcelLayerInfoToViewDistance);
                parcelLayerViewDistance = landManagementConfig.GetInt("ParcelLayerViewDistance", parcelLayerViewDistance);
                DefaultGodParcelGroup = new UUID(landManagementConfig.GetString("DefaultAdministratorGroupUUID", UUID.Zero.ToString()));
                DefaultGodParcelName = landManagementConfig.GetString("DefaultAdministratorParcelName", "Admin Parcel");
                DefaultGodParcelOwner = new UUID(landManagementConfig.GetString("DefaultAdministratorOwnerUUID", UUID.Zero.ToString()));
                bool disablebans = landManagementConfig.GetBoolean("DisableParcelBans", !_allowedForcefulBans);
                _allowedForcefulBans = !disablebans;
                _showBansLines = landManagementConfig.GetBoolean("ShowParcelBansLines", _showBansLines);
                _BanLineSafeHeight = landManagementConfig.GetFloat("BanLineSafeHeight", _BanLineSafeHeight);
            }
        }

        public void AddRegion(Scene scene)
        {
            _scene = scene;
            _regionHandler = _scene.RegionInfo.RegionHandle;
            _regionSizeX = (int)_scene.RegionInfo.RegionSizeX;
            _regionSizeY = (int)_scene.RegionInfo.RegionSizeY;
            _landIDList = new int[_regionSizeX / LandUnit, _regionSizeY / LandUnit];

            _scene.LandChannel = this;

            _scene.EventManager.OnObjectAddedToScene += EventManagerOnParcelPrimCountAdd;
            _scene.EventManager.OnParcelPrimCountAdd += EventManagerOnParcelPrimCountAdd;

            _scene.EventManager.OnObjectBeingRemovedFromScene += EventManagerOnObjectBeingRemovedFromScene;
            _scene.EventManager.OnParcelPrimCountUpdate += EventManagerOnParcelPrimCountUpdate;
            _scene.EventManager.OnRequestParcelPrimCountUpdate += EventManagerOnRequestParcelPrimCountUpdate;

            _scene.EventManager.OnAvatarEnteringNewParcel += EventManagerOnAvatarEnteringNewParcel;
            _scene.EventManager.OnClientMovement += EventManagerOnClientMovement;
            _scene.EventManager.OnValidateLandBuy += EventManagerOnValidateLandBuy;
            _scene.EventManager.OnLandBuy += EventManagerOnLandBuy;
            _scene.EventManager.OnNewClient += EventManagerOnNewClient;
            _scene.EventManager.OnMakeChildAgent += EventMakeChildAgent;
            _scene.EventManager.OnSignificantClientMovement += EventManagerOnSignificantClientMovement;
            _scene.EventManager.OnNoticeNoLandDataFromStorage += EventManagerOnNoLandDataFromStorage;
            _scene.EventManager.OnIncomingLandDataFromStorage += EventManagerOnIncomingLandDataFromStorage;
            _scene.EventManager.OnSetAllowForcefulBan += EventManagerOnSetAllowedForcefulBan;
            _scene.EventManager.OnRegisterCaps += EventManagerOnRegisterCaps;

            RegisterCommands();
        }

        public void RegionLoaded(Scene scene)
        {
            _userManager = _scene.RequestModuleInterface<IUserManagement>();
            _groupManager = _scene.RequestModuleInterface<IGroupsModule>();
            _primCountModule = _scene.RequestModuleInterface<IPrimCountModule>();
            _Dialog = _scene.RequestModuleInterface<IDialogModule>();
        }

        public void RemoveRegion(Scene scene)
        {
            // TODO: Release event manager listeners here
        }

//        private bool OnVerifyUserConnection(ScenePresence scenePresence, out string reason)
//        {
//            ILandObject nearestParcel = _scene.GetNearestAllowedParcel(scenePresence.UUID, scenePresence.AbsolutePosition.X, scenePresence.AbsolutePosition.Y);
//            reason = "You are not allowed to enter this sim.";
//            return nearestParcel != null;
//        }

        void EventManagerOnNewClient(IClientAPI client)
        {
            //Register some client events
            client.OnParcelPropertiesRequest += ClientOnParcelPropertiesRequest;
            client.OnParcelDivideRequest += ClientOnParcelDivideRequest;
            client.OnParcelJoinRequest += ClientOnParcelJoinRequest;
            client.OnParcelPropertiesUpdateRequest += ClientOnParcelPropertiesUpdateRequest;
            client.OnParcelSelectObjects += ClientOnParcelSelectObjects;
            client.OnParcelObjectOwnerRequest += ClientOnParcelObjectOwnerRequest;
            client.OnParcelAccessListRequest += ClientOnParcelAccessListRequest;
            client.OnParcelAccessListUpdateRequest += ClientOnParcelAccessListUpdateRequest;
            client.OnParcelAbandonRequest += ClientOnParcelAbandonRequest;
            client.OnParcelGodForceOwner += ClientOnParcelGodForceOwner;
            client.OnParcelReclaim += ClientOnParcelReclaim;
            client.OnParcelInfoRequest += ClientOnParcelInfoRequest;
            client.OnParcelDeedToGroup += ClientOnParcelDeedToGroup;
            client.OnParcelEjectUser += ClientOnParcelEjectUser;
            client.OnParcelFreezeUser += ClientOnParcelFreezeUser;
            client.OnSetStartLocationRequest += ClientOnSetHome;
            client.OnParcelBuyPass += ClientParcelBuyPass;
            client.OnParcelGodMark += ClientOnParcelGodMark;
        }

        public void EventMakeChildAgent(ScenePresence avatar)
        {
            avatar.currentParcelUUID = UUID.Zero;
        }

        public void Close()
        {
        }

        public string Name => "LandManagementModule";

        #endregion

        #region Parcel Add/Remove/Get/Create

        public void EventManagerOnSetAllowedForcefulBan(bool forceful)
        {
            AllowedForcefulBans = forceful;
        }

        public void UpdateLandObject(int local_id, LandData data)
        {
            LandData newData = data.Copy();
            newData.LocalID = local_id;

            ILandObject land;
            lock (_landList)
            {
                if (_landList.TryGetValue(local_id, out land))
                {
                    _landGlobalIDs.Remove(land.LandData.GlobalID);
                    if (land.LandData.FakeID != UUID.Zero)
                        _landFakeIDs.Remove(land.LandData.FakeID);
                    land.LandData = newData;
                    _landGlobalIDs[newData.GlobalID] = local_id;
                    if (newData.FakeID != UUID.Zero)
                        _landFakeIDs[newData.FakeID] = local_id;
                }
            }

            if (land != null)
                _scene.EventManager.TriggerLandObjectUpdated((uint)local_id, land);
        }

        public bool IsForcefulBansAllowed()
        {
            return AllowedForcefulBans;
        }

        public bool AllowedForcefulBans
        {
            get => _allowedForcefulBans;
            set => _allowedForcefulBans = value;
        }

        /// <summary>
        /// Resets the sim to the default land object (full sim piece of land owned by the default user)
        /// </summary>
        public void ResetSimLandObjects()
        {
            //Remove all the land objects in the sim and add a blank, full sim land object set to public
            lock (_landList)
            {
                foreach(ILandObject parcel in _landList.Values)
                    parcel.Clear();

                _landList.Clear();
                _landGlobalIDs.Clear();
                _landFakeIDs.Clear();
                _lastLandLocalID = LandChannel.START_LAND_LOCAL_ID - 1;

                _landIDList = new int[_regionSizeX / LandUnit, _regionSizeY / LandUnit];
            }
        }

        /// <summary>
        /// Create a default parcel that spans the entire region and is owned by the estate owner.
        /// </summary>
        /// <returns>The parcel created.</returns>
        protected ILandObject CreateDefaultParcel()
        {
            _log.DebugFormat("{0} Creating default parcel for region {1}", LogHeader, _scene.RegionInfo.RegionName);

            ILandObject fullSimParcel = new LandObject(UUID.Zero, false, _scene);

            fullSimParcel.SetLandBitmap(fullSimParcel.GetSquareLandBitmap(0, 0, _regionSizeX, _regionSizeY));
            LandData ldata = fullSimParcel.LandData;
            ldata.SimwideArea = ldata.Area;
            ldata.OwnerID = _scene.RegionInfo.EstateSettings.EstateOwner;
            ldata.ClaimDate = Util.UnixTimeSinceEpoch();

            return AddLandObject(fullSimParcel);
        }

        public List<ILandObject> AllParcels()
        {
            lock (_landList)
            {
                return new List<ILandObject>(_landList.Values);
            }
        }

        public List<ILandObject> ParcelsNearPoint(Vector3 position)
        {
            List<ILandObject> parcelsNear = new List<ILandObject>();
            for (int x = -8; x <= 8; x += 4)
            {
                for (int y = -8; y <= 8; y += 4)
                {
                    ILandObject check = GetLandObject(position.X + x, position.Y + y);
                    if (check != null)
                    {
                        if (!parcelsNear.Contains(check))
                        {
                            parcelsNear.Add(check);
                        }
                    }
                }
            }

            return parcelsNear;
        }

        // checks and enforces bans or restrictions
        // returns true if enforced
        public bool EnforceBans(ILandObject land, ScenePresence avatar)
        {
            Vector3 agentpos = avatar.AbsolutePosition;
            float h = _scene.GetGroundHeight(agentpos.X, agentpos.Y) + _scene.LandChannel.BanLineSafeHeight;
            float zdif = avatar.AbsolutePosition.Z - h;
            if (zdif > 0 )
            {
                forcedPosition.Remove(avatar.UUID);
                avatar.lastKnownAllowedPosition = agentpos;
                return false;
            }

            bool ban = false;
            string reason = "";
            if (land.IsRestrictedFromLand(avatar.UUID))
            {
                reason = "You do not have access to the parcel";
                ban = true;
            }

            if (land.IsBannedFromLand(avatar.UUID))
            {
                if ( _allowedForcefulBans)
                {
                   reason ="You are banned from parcel";
                   ban = true;
                }
                else if(!ban)
                {
                    if (forcedPosition.Contains(avatar.UUID))
                        avatar.ControllingClient.SendAlertMessage("You are banned from parcel, please leave by your own will");
                    forcedPosition.Remove(avatar.UUID);
                    avatar.lastKnownAllowedPosition = agentpos;
                    return false;
                }
            }

            if(ban)
            {
                if (!forcedPosition.Contains(avatar.UUID))
                    avatar.ControllingClient.SendAlertMessage(reason);

                if(zdif > -4f)
                {

                    agentpos.Z = h + 4.0f;
                    ForceAvatarToPosition(avatar, agentpos);
                    return true;
                }

                if (land.ContainsPoint((int)avatar.lastKnownAllowedPosition.X,
                            (int) avatar.lastKnownAllowedPosition.Y))
                {
                    Vector3? pos = _scene.GetNearestAllowedPosition(avatar);
                    if (pos == null)
                    {
                         forcedPosition.Remove(avatar.UUID);
                         _scene.TeleportClientHome(avatar.UUID, avatar.ControllingClient);
                    }
                    else
                        ForceAvatarToPosition(avatar, (Vector3)pos);
                }
                else
                {
                    ForceAvatarToPosition(avatar, avatar.lastKnownAllowedPosition);
                }
                return true;
            }
            else
            {
                forcedPosition.Remove(avatar.UUID);
                avatar.lastKnownAllowedPosition = agentpos;
                return false;
            }
        }

        private void ForceAvatarToPosition(ScenePresence avatar, Vector3? position)
        {
            if (_scene.Permissions.IsGod(avatar.UUID)) return;

            if (!position.HasValue)
                return;

            if(avatar.MovingToTarget)
                avatar.ResetMoveToTarget();
            avatar.AbsolutePosition = position.Value;
            avatar.lastKnownAllowedPosition = position.Value;
            avatar.Velocity = Vector3.Zero;
            if(avatar.IsSitting)
                avatar.StandUp();
            forcedPosition.Add(avatar.UUID);
        }

        public void EventManagerOnAvatarEnteringNewParcel(ScenePresence avatar, int localLandID, UUID regionID)
        {
            if (_scene.RegionInfo.RegionID == regionID)
            {
                ILandObject parcelAvatarIsEntering;
                lock (_landList)
                {
                    parcelAvatarIsEntering = _landList[localLandID];
                }

                if (parcelAvatarIsEntering != null &&
                    avatar.currentParcelUUID != parcelAvatarIsEntering.LandData.GlobalID)
                {
                    SendLandUpdate(avatar, parcelAvatarIsEntering);
                    avatar.currentParcelUUID = parcelAvatarIsEntering.LandData.GlobalID;
                    EnforceBans(parcelAvatarIsEntering, avatar);
                }
            }
        }

        public void SendOutNearestBanLine(IClientAPI client)
        {
            ScenePresence sp = _scene.GetScenePresence(client.AgentId);
            if (sp == null || sp.IsDeleted)
                return;

            List<ILandObject> checkLandParcels = ParcelsNearPoint(sp.AbsolutePosition);
            foreach (ILandObject checkBan in checkLandParcels)
            {
                if (checkBan.IsBannedFromLand(client.AgentId))
                {
                    checkBan.SendLandProperties((int)ParcelPropertiesStatus.CollisionBanned, false, (int)ParcelResult.Single, client);
                    return; //Only send one
                }
                if (checkBan.IsRestrictedFromLand(client.AgentId))
                {
                    checkBan.SendLandProperties((int)ParcelPropertiesStatus.CollisionNotOnAccessList, false, (int)ParcelResult.Single, client);
                    return; //Only send one
                }
            }
            return;
        }

        public void sendClientInitialLandInfo(IClientAPI remoteClient, bool overlay)
        {
            ScenePresence avatar;

            if (!_scene.TryGetScenePresence(remoteClient.AgentId, out avatar))
                return;

            if (!avatar.IsChildAgent)
            {
                ILandObject over = GetLandObject(avatar.AbsolutePosition.X, avatar.AbsolutePosition.Y);
                if (over == null)
                    return;

                avatar.currentParcelUUID = over.LandData.GlobalID;
                over.SendLandUpdateToClient(avatar.ControllingClient);
            }
            if(overlay)
                SendParcelOverlay(remoteClient);
        }

        public void SendLandUpdate(ScenePresence avatar, ILandObject over)
        {
            if (avatar.IsChildAgent)
                return;

            if (over != null)
            {
                   over.SendLandUpdateToClient(avatar.ControllingClient);
// sl doesnt seem to send this now, as it used 2
//                    SendParcelOverlay(avatar.ControllingClient);
            }
        }

        public void EventManagerOnSignificantClientMovement(ScenePresence avatar)
        {
            if (avatar.IsChildAgent)
                return;

            if ( _allowedForcefulBans && _showBansLines && !_scene.RegionInfo.EstateSettings.TaxFree)
                SendOutNearestBanLine(avatar.ControllingClient);
        }

        /// <summary>
        /// Like handleEventManagerOnSignificantClientMovement, but called with an AgentUpdate regardless of distance.
        /// </summary>
        /// <param name="avatar"></param>
        public void EventManagerOnClientMovement(ScenePresence avatar)
        {
            if (avatar.IsChildAgent)
                return;

            Vector3 pos = avatar.AbsolutePosition;
            ILandObject over = GetLandObject(pos.X, pos.Y);
            if (over != null)
            {
                EnforceBans(over, avatar);
                pos = avatar.AbsolutePosition;
                ILandObject newover = GetLandObject(pos.X, pos.Y);
                if(over != newover || avatar.currentParcelUUID != newover.LandData.GlobalID)
                {
                    _scene.EventManager.TriggerAvatarEnteringNewParcel(avatar,
                            newover.LandData.LocalID, _scene.RegionInfo.RegionID);
                }
            }
        }

        public void ClientParcelBuyPass(IClientAPI remote_client, UUID targetID, int landLocalID)
        {
            ILandObject land;
            lock (_landList)
            {
                _landList.TryGetValue(landLocalID, out land);
            }
            // trivial checks
            if(land == null)
                return;

            LandData ldata = land.LandData;

            if(ldata == null)
                return;

            if(ldata.OwnerID == targetID)
                return;

            if(ldata.PassHours == 0)
                return;

            if (_scene.RegionInfo.EstateSettings.TaxFree)
                return;

            // don't allow passes on group owned until we can give money to groups
            if (ldata.IsGroupOwned)
            {
                remote_client.SendAgentAlertMessage("pass to group owned parcel not suported", false);
                return;
            }

            if((ldata.Flags & (uint)ParcelFlags.UsePassList) == 0)
                return;

            int cost = ldata.PassPrice;

            int idx = land.LandData.ParcelAccessList.FindIndex(
                delegate(LandAccessEntry e)
                {
                    if (e.AgentID == targetID && e.Flags == AccessList.Access)
                        return true;
                    return false;
                });
            int now = Util.UnixTimeSinceEpoch();
            int expires = (int)(3600.0 * ldata.PassHours + 0.5f);
            int currenttime = -1;
            if (idx != -1)
            {
                if(ldata.ParcelAccessList[idx].Expires == 0)
                {
                    remote_client.SendAgentAlertMessage("You already have access to parcel", false);
                    return;
                }

                currenttime = ldata.ParcelAccessList[idx].Expires - now;
                if(currenttime > (int)(0.25f * expires + 0.5f))
                {
                    if(currenttime > 3600)
                        remote_client.SendAgentAlertMessage(string.Format("You already have a pass valid for {0:0.###} hours",
                                    currenttime/3600f), false);
                   else if(currenttime > 60)
                        remote_client.SendAgentAlertMessage(string.Format("You already have a pass valid for {0:0.##} minutes",
                                    currenttime/60f), false);
                   else
                        remote_client.SendAgentAlertMessage(string.Format("You already have a pass valid for {0:0.#} seconds",
                                    currenttime), false);
                    return;
                }
            }

            LandAccessEntry entry = new LandAccessEntry
            {
                AgentID = targetID,
                Flags = AccessList.Access,
                Expires = now + expires
            };
            if (currenttime > 0)
                entry.Expires += currenttime;
            IMoneyModule mm = _scene.RequestModuleInterface<IMoneyModule>();
            if(cost != 0 && mm != null)
            {
                WorkManager.RunInThreadPool(
                delegate
                {
                    string regionName = _scene.RegionInfo.RegionName;

                    if (!mm.AmountCovered(remote_client.AgentId, cost))
                    {
                        remote_client.SendAgentAlertMessage(string.Format("Insufficient funds in region '{0}' money system", regionName), true); 
                        return;
                    }

                    string payDescription = string.Format("Parcel '{0}' at region '{1} {2:0.###} hours access pass", ldata.Name, regionName, ldata.PassHours);

                    if(!mm.MoveMoney(remote_client.AgentId, ldata.OwnerID, cost,MoneyTransactionType.LandPassSale, payDescription))
                    {
                        remote_client.SendAgentAlertMessage("Sorry pass payment processing failed, please try again later", true); 
                        return;
                    }

                    if (idx != -1)
                        ldata.ParcelAccessList.RemoveAt(idx);
                    ldata.ParcelAccessList.Add(entry);
                    _scene.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
                    return;
                }, null, "ParcelBuyPass");
            }
            else
            {
                if (idx != -1)
                    ldata.ParcelAccessList.RemoveAt(idx);
                ldata.ParcelAccessList.Add(entry);
                _scene.EventManager.TriggerLandObjectUpdated((uint)land.LandData.LocalID, land);
            }
        }

        public void ClientOnParcelAccessListRequest(UUID agentID, UUID sessionID, uint flags, int sequenceID,
                                                    int landLocalID, IClientAPI remote_client)
        {
            ILandObject land;
            lock (_landList)
            {
                _landList.TryGetValue(landLocalID, out land);
            }

            if (land != null)
            {
                land.SendAccessList(agentID, sessionID, flags, sequenceID, remote_client);
            }
        }

        public void ClientOnParcelAccessListUpdateRequest(UUID agentID,
                uint flags, UUID transactionID, int landLocalID, List<LandAccessEntry> entries,
                IClientAPI remote_client)
        {
            if ((flags & 0x03) == 0)
                return; // we only have access and ban

            if(_scene.RegionInfo.EstateSettings.TaxFree)
                return;

            ILandObject land;
            lock (_landList)
            {
                _landList.TryGetValue(landLocalID, out land);
            }

            if (land != null)
            {
                GroupPowers requiredPowers = GroupPowers.None;
                if ((flags & (uint)AccessList.Access) != 0)
                    requiredPowers |= GroupPowers.LandManageAllowed;
                if ((flags & (uint)AccessList.Ban) != 0)
                    requiredPowers |= GroupPowers.LandManageBanned;

                if(requiredPowers == GroupPowers.None)
                    return;

                if (_scene.Permissions.CanEditParcelProperties(agentID,
                        land, requiredPowers, false))
                {
                    land.UpdateAccessList(flags, transactionID, entries);
                }
            }
            else
            {
                _log.WarnFormat("[LAND MANAGEMENT MODULE]: Invalid local land ID {0}", landLocalID);
            }
        }

        /// <summary>
        /// Adds a land object to the stored list and adds them to the landIDList to what they own
        /// </summary>
        /// <param name="new_land">
        /// The land object being added.
        /// Will return null if this overlaps with an existing parcel that has not had its bitmap adjusted.
        /// </param>
        public ILandObject AddLandObject(ILandObject new_land)
        {
            // Only now can we add the prim counts to the land object - we rely on the global ID which is generated
            // as a random UUID inside LandData initialization
            if (_primCountModule != null)
                new_land.PrimCounts = _primCountModule.GetPrimCounts(new_land.LandData.GlobalID);

            lock (_landList)
            {
                int newLandLocalID = _lastLandLocalID + 1;
                new_land.LandData.LocalID = newLandLocalID;

                bool[,] landBitmap = new_land.GetLandBitmap();
                if (landBitmap.GetLength(0) != _landIDList.GetLength(0) || landBitmap.GetLength(1) != _landIDList.GetLength(1))
                {
                    // Going to variable sized regions can cause mismatches
                    _log.ErrorFormat("{0} AddLandObject. Added land bitmap different size than region ID map. bitmapSize=({1},{2}), landIDSize=({3},{4})",
                        LogHeader, landBitmap.GetLength(0), landBitmap.GetLength(1), _landIDList.GetLength(0), _landIDList.GetLength(1));
                }
                else
                {
                    // If other land objects still believe that they occupy any parts of the same space,
                    // then do not allow the add to proceed.
                    for (int x = 0; x < landBitmap.GetLength(0); x++)
                    {
                        for (int y = 0; y < landBitmap.GetLength(1); y++)
                        {
                            if (landBitmap[x, y])
                            {
                                int lastRecordedLandId = _landIDList[x, y];

                                if (lastRecordedLandId > 0)
                                {
                                    ILandObject lastRecordedLo = _landList[lastRecordedLandId];

                                    if (lastRecordedLo.LandBitmap[x, y])
                                    {
                                        _log.ErrorFormat(
                                            "{0}: Cannot add parcel \"{1}\", local ID {2} at tile {3},{4} because this is still occupied by parcel \"{5}\", local ID {6} in {7}",
                                            LogHeader, new_land.LandData.Name, new_land.LandData.LocalID, x, y,
                                            lastRecordedLo.LandData.Name, lastRecordedLo.LandData.LocalID, _scene.Name);

                                        return null;
                                    }
                                }
                            }
                        }
                    }

                    for (int x = 0; x < landBitmap.GetLength(0); x++)
                    {
                        for (int y = 0; y < landBitmap.GetLength(1); y++)
                        {
                            if (landBitmap[x, y])
                            {
                                //                            _log.DebugFormat(
                                //                                "[LAND MANAGEMENT MODULE]: Registering parcel {0} for land co-ord ({1}, {2}) on {3}",
                                //                                new_land.LandData.Name, x, y, _scene.RegionInfo.RegionName);

                                _landIDList[x, y] = newLandLocalID;
                            }
                        }
                    }
                }
                
                _landList.Add(newLandLocalID, new_land);
                _landGlobalIDs[new_land.LandData.GlobalID] = newLandLocalID;
                _landFakeIDs[new_land.LandData.FakeID] = newLandLocalID;
                _lastLandLocalID++;
            }

            new_land.ForceUpdateLandInfo();
            _scene.EventManager.TriggerLandObjectAdded(new_land);

            return new_land;
        }

        /// <summary>
        /// Removes a land object from the list. Will not remove if local_id is still owning an area in landIDList
        /// </summary>
        /// <param name="local_id">Land.localID of the peice of land to remove.</param>
        public void removeLandObject(int local_id)
        {
            ILandObject land;
            UUID landGlobalID = UUID.Zero;
            lock (_landList)
            {
                for (int x = 0; x < _landIDList.GetLength(0); x++)
                {
                    for (int y = 0; y < _landIDList.GetLength(1); y++)
                    {
                        if (_landIDList[x, y] == local_id)
                        {
                            _log.WarnFormat("[LAND MANAGEMENT MODULE]: Not removing land object {0}; still being used at {1}, {2}",
                                             local_id, x, y);
                            return;
                            //throw new Exception("Could not remove land object. Still being used at " + x + ", " + y);
                        }
                    }
                }

                land = _landList[local_id];
                _landList.Remove(local_id);
                if(land != null && land.LandData != null)
                {
                    landGlobalID = land.LandData.GlobalID;
                    _landGlobalIDs.Remove(landGlobalID);
                    _landFakeIDs.Remove(land.LandData.FakeID);
                }
            }

            if(landGlobalID != UUID.Zero)
            {
                _scene.EventManager.TriggerLandObjectRemoved(landGlobalID);
                land.Clear();
            }
        }

        /// <summary>
        /// Clear the scene of all parcels
        /// </summary>
        public void Clear(bool setupDefaultParcel)
        {
            List<UUID> landworkList = new List<UUID>(_landList.Count);
            // move to work pointer since we are deleting it all
            lock (_landList)
            {
                foreach (ILandObject lo in _landList.Values)
                    landworkList.Add(lo.LandData.GlobalID);
            }

            // this 2 methods have locks (now)
            ResetSimLandObjects();

            if (setupDefaultParcel)
                CreateDefaultParcel();

            // fire outside events unlocked
            foreach (UUID id in landworkList)
            {
                //_scene.SimulationDataService.RemoveLandObject(lo.LandData.GlobalID);
                _scene.EventManager.TriggerLandObjectRemoved(id);
            }
            landworkList.Clear();
        }

        private void performFinalLandJoin(ILandObject master, ILandObject slave)
        {
            bool[,] landBitmapSlave = slave.GetLandBitmap();
            lock (_landList)
            {
                for (int x = 0; x < landBitmapSlave.GetLength(0); x++)
                {
                    for (int y = 0; y < landBitmapSlave.GetLength(1); y++)
                    {
                        if (landBitmapSlave[x, y])
                        {
                            _landIDList[x, y] = master.LandData.LocalID;
                        }
                    }
                }
            }
            master.LandData.Dwell += slave.LandData.Dwell;
            removeLandObject(slave.LandData.LocalID);
            UpdateLandObject(master.LandData.LocalID, master.LandData);
        }

        public ILandObject GetLandObject(UUID globalID)
        {
            lock (_landList)
            {
                int lid = -1;
                if (_landGlobalIDs.TryGetValue(globalID, out lid) && lid >= 0)
                {
                    if (_landList.ContainsKey(lid))
                    {
                        return _landList[lid];
                    }
                    else
                        _landGlobalIDs.Remove(globalID); // auto heal
                }
            }
            return null;
        }

        public ILandObject GetLandObjectByfakeID(UUID fakeID)
        {
            lock (_landList)
            {
                int lid = -1;
                if (_landFakeIDs.TryGetValue(fakeID, out lid) && lid >= 0)
                {
                    if (_landList.ContainsKey(lid))
                    {
                        return _landList[lid];
                    }
                    else
                        _landFakeIDs.Remove(fakeID); // auto heal
                }
            }
            if(Util.ParseFakeParcelID(fakeID, out ulong rhandle, out uint x, out uint y) && rhandle == _regionHandler)
            {
                return GetLandObjectClippedXY(x, y);
            }
            return null;
        }

        public ILandObject GetLandObject(int parcelLocalID)
        {
            lock (_landList)
            {
                if (_landList.ContainsKey(parcelLocalID))
                {
                    return _landList[parcelLocalID];
                }
            }
            return null;
        }

        /// <summary>
        /// Get the land object at the specified point
        /// </summary>
        /// <param name="x_float">Value between 0 - 256 on the x axis of the point</param>
        /// <param name="y_float">Value between 0 - 256 on the y axis of the point</param>
        /// <returns>Land object at the point supplied</returns>
        public ILandObject GetLandObject(float x_float, float y_float)
        {
            return GetLandObject((int)x_float, (int)y_float, true);
        }

        public ILandObject GetLandObject(Vector3 position)
        {
            return GetLandObject(position.X, position.Y);
        }

        // if x,y is off region this will return the parcel at cliped x,y
        // as did code it replaces
        public ILandObject GetLandObjectClippedXY(float x, float y)
        {
            //do clip inline
            int avx = (int)Math.Round(x);
            if (avx < 0)
                avx = 0;
            else if (avx >= _regionSizeX)
                avx = _regionSizeX - 1;

            int avy = (int)Math.Round(y);
            if (avy < 0)
                avy = 0;
            else if (avy >= _regionSizeY)
                avy = _regionSizeY - 1;

            lock (_landIDList)
            {
                try
                {
                    return _landList[_landIDList[avx / LandUnit, avy / LandUnit]];
                }
                catch (IndexOutOfRangeException)
                {
                    return null;
                }
            }
        }

        // Public entry.
        // Throws exception if land object is not found
        public ILandObject GetLandObject(int x, int y)
        {
            return GetLandObject(x, y, false /* returnNullIfLandObjectNotFound */);
        }

        public ILandObject GetLandObject(int x, int y, bool returnNullIfLandObjectOutsideBounds)
        {
            if (x >= _regionSizeX || y >= _regionSizeY || x < 0 || y < 0)
            {
                // These exceptions here will cause a lot of complaints from the users specifically because
                // they happen every time at border crossings
                if (returnNullIfLandObjectOutsideBounds)
                    return null;
                else
                    throw new Exception("Error: Parcel not found at point " + x + ", " + y);
            }

            if(_landList.Count == 0  || _landIDList == null)
                return null;

            lock (_landIDList)
            {
                try
                {
                        return _landList[_landIDList[x / LandUnit, y / LandUnit]];
                }
                catch (IndexOutOfRangeException)
                {
                        return null;
                }
            }
        }

        public ILandObject GetLandObjectinLandUnits(int x, int y)
        {
            if (_landList.Count == 0 || _landIDList == null)
                return null;

            lock (_landIDList)
            {
                try
                {
                    return _landList[_landIDList[x, y]];
                }
                catch (IndexOutOfRangeException)
                {
                    return null;
                }
            }
        }

        public ILandObject GetLandObjectinLandUnitsInt(int x, int y)
        {
            lock (_landIDList)
            {
                try
                {
                    return _landList[_landIDList[x, y]];
                }
                catch (IndexOutOfRangeException)
                {
                    return null;
                }
            }
        }

        public int GetLandObjectIDinLandUnits(int x, int y)
        {
            lock (_landIDList)
            {
                try
                {
                    return _landIDList[x, y];
                }
                catch (IndexOutOfRangeException)
                {
                    return -1;
                }
            }
        }

        // Create a 'parcel is here' bitmap for the parcel identified by the passed landID
        private bool[,] CreateBitmapForID(int landID)
        {
            bool[,] ret = new bool[_landIDList.GetLength(0), _landIDList.GetLength(1)];

            for (int xx = 0; xx < _landIDList.GetLength(0); xx++)
                for (int yy = 0; yy < _landIDList.GetLength(1); yy++)
                    if (_landIDList[xx, yy] == landID)
                        ret[xx, yy] = true;

            return ret;
        }

        #endregion

        #region Parcel Modification

        public void ResetOverMeRecords()
        {
            lock (_landList)
            {
                foreach (LandObject p in _landList.Values)
                {
                    p.ResetOverMeRecord();
                }
            }
        }

        public void EventManagerOnParcelPrimCountAdd(SceneObjectGroup obj)
        {
            Vector3 position = obj.AbsolutePosition;
            ILandObject landUnderPrim = GetLandObject(position.X, position.Y);
            if (landUnderPrim != null)
            {
                ((LandObject)landUnderPrim).AddPrimOverMe(obj);
            }
        }

        public void EventManagerOnObjectBeingRemovedFromScene(SceneObjectGroup obj)
        {
            lock (_landList)
            {
                foreach (LandObject p in _landList.Values)
                {
                    p.RemovePrimFromOverMe(obj);
                }
            }
        }

        private void FinalizeLandPrimCountUpdate()
        {
            //Get Simwide prim count for owner
            Dictionary<UUID, List<LandObject>> landOwnersAndParcels = new Dictionary<UUID, List<LandObject>>();
            lock (_landList)
            {
                foreach (LandObject p in _landList.Values)
                {
                    if (!landOwnersAndParcels.ContainsKey(p.LandData.OwnerID))
                    {
                        List<LandObject> tempList = new List<LandObject>();
                        tempList.Add(p);
                        landOwnersAndParcels.Add(p.LandData.OwnerID, tempList);
                    }
                    else
                    {
                        landOwnersAndParcels[p.LandData.OwnerID].Add(p);
                    }
                }
            }

            foreach (UUID owner in landOwnersAndParcels.Keys)
            {
                int simArea = 0;
                int simPrims = 0;
                foreach (LandObject p in landOwnersAndParcels[owner])
                {
                    simArea += p.LandData.Area;
                    simPrims += p.PrimCounts.Total;
                }

                foreach (LandObject p in landOwnersAndParcels[owner])
                {
                    p.LandData.SimwideArea = simArea;
                    p.LandData.SimwidePrims = simPrims;
                }
            }
        }

        public void EventManagerOnParcelPrimCountUpdate()
        {
            //_log.DebugFormat(
            //    "[land management module]: triggered eventmanageronparcelprimcountupdate() for {0}",
            //    _scene.RegionInfo.RegionName);

            ResetOverMeRecords();
            EntityBase[] entities = _scene.Entities.GetEntities();
            foreach (EntityBase obj in entities)
            {
                if (obj != null)
                {
                    if (obj is SceneObjectGroup && !obj.IsDeleted && !((SceneObjectGroup) obj).IsAttachment)
                    {
                        _scene.EventManager.TriggerParcelPrimCountAdd((SceneObjectGroup) obj);
                    }
                }
            }
            FinalizeLandPrimCountUpdate();
        }

        public void EventManagerOnRequestParcelPrimCountUpdate()
        {
            ResetOverMeRecords();
            _scene.EventManager.TriggerParcelPrimCountUpdate();
            FinalizeLandPrimCountUpdate();
        }

        /// <summary>
        /// Subdivides a piece of land
        /// </summary>
        /// <param name="start_x">West Point</param>
        /// <param name="start_y">South Point</param>
        /// <param name="end_x">East Point</param>
        /// <param name="end_y">North Point</param>
        /// <param name="attempting_user_id">UUID of user who is trying to subdivide</param>
        /// <returns>Returns true if successful</returns>
        public void Subdivide(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id)
        {
            //First, lets loop through the points and make sure they are all in the same peice of land
            //Get the land object at start

            ILandObject startLandObject = GetLandObject(start_x, start_y);

            if (startLandObject == null)
                return;

            if (!_scene.Permissions.CanEditParcelProperties(attempting_user_id, startLandObject, GroupPowers.LandDivideJoin, true))
            {
                return;
            }

            //Loop through the points
            try
            {
                for (int y = start_y; y < end_y; y++)
                {
                    for (int x = start_x; x < end_x; x++)
                    {
                        ILandObject tempLandObject = GetLandObject(x, y);
                        if (tempLandObject == null)
                            return;
                        if (tempLandObject != startLandObject)
                            return;
                    }
                }
            }
            catch (Exception)
            {
                return;
            }

             //Lets create a new land object with bitmap activated at that point (keeping the old land objects info)
            ILandObject newLand = startLandObject.Copy();

            newLand.LandData.Name = newLand.LandData.Name;
            newLand.LandData.GlobalID = UUID.Random();
            newLand.LandData.Dwell = 0;
            // Clear "Show in search" on the cut out parcel to prevent double-charging
            newLand.LandData.Flags &= ~(uint)ParcelFlags.ShowDirectory;
            // invalidate landing point
            newLand.LandData.LandingType = (byte)LandingType.Direct;
            newLand.LandData.UserLocation = Vector3.Zero;
            newLand.LandData.UserLookAt = Vector3.Zero;

            newLand.SetLandBitmap(newLand.GetSquareLandBitmap(start_x, start_y, end_x, end_y));

            //lets set the subdivision area of the original to false
            int startLandObjectIndex = startLandObject.LandData.LocalID;
            lock (_landList)
            {
                _landList[startLandObjectIndex].SetLandBitmap(
                    newLand.ModifyLandBitmapSquare(startLandObject.GetLandBitmap(), start_x, start_y, end_x, end_y, false));
                _landList[startLandObjectIndex].ForceUpdateLandInfo();
            }

            //add the new land object
            ILandObject result = AddLandObject(newLand);

            UpdateLandObject(startLandObject.LandData.LocalID, startLandObject.LandData);

            if(startLandObject.LandData.LandingType == (byte)LandingType.LandingPoint)
            {
                int x = (int)startLandObject.LandData.UserLocation.X;
                int y = (int)startLandObject.LandData.UserLocation.Y;
                if(!startLandObject.ContainsPoint(x, y))
                {
                    startLandObject.LandData.LandingType = (byte)LandingType.Direct;
                    startLandObject.LandData.UserLocation = Vector3.Zero;
                    startLandObject.LandData.UserLookAt = Vector3.Zero;
                }
             }

            _scene.EventManager.TriggerParcelPrimCountTainted();

            result.SendLandUpdateToAvatarsOverMe();
            startLandObject.SendLandUpdateToAvatarsOverMe();
            _scene.ForEachClient(SendParcelOverlay);

        }

        /// <summary>
        /// Join 2 land objects together
        /// </summary>
        /// <param name="start_x">start x of selection area</param>
        /// <param name="start_y">start y of selection area</param>
        /// <param name="end_x">end x of selection area</param>
        /// <param name="end_y">end y of selection area</param>
        /// <param name="attempting_user_id">UUID of the avatar trying to join the land objects</param>
        /// <returns>Returns true if successful</returns>
        public void Join(int start_x, int start_y, int end_x, int end_y, UUID attempting_user_id)
        {
            int index = 0;
            int maxindex = -1;
            int maxArea = 0;

            List<ILandObject> selectedLandObjects = new List<ILandObject>();
            for (int x = start_x; x < end_x; x += 4)
            {
                for (int y = start_y; y < end_y; y += 4)
                {
                    ILandObject p = GetLandObject(x, y);

                    if (p != null)
                    {
                        if (!selectedLandObjects.Contains(p))
                        {
                            selectedLandObjects.Add(p);
                            if(p.LandData.Area > maxArea)
                            {
                                maxArea = p.LandData.Area;
                                maxindex = index;
                            }
                            index++;
                        }
                    }
                }
            }

            if(maxindex < 0 || selectedLandObjects.Count < 2)
                return;

            ILandObject masterLandObject = selectedLandObjects[maxindex];
            selectedLandObjects.RemoveAt(maxindex);

            if (!_scene.Permissions.CanEditParcelProperties(attempting_user_id, masterLandObject, GroupPowers.LandDivideJoin, true))
            {
                return;
            }

            UUID masterOwner = masterLandObject.LandData.OwnerID;
            foreach (ILandObject p in selectedLandObjects)
            {
                if (p.LandData.OwnerID != masterOwner)
                    return;
            }

            lock (_landList)
            {
                foreach (ILandObject slaveLandObject in selectedLandObjects)
                {
                    _landList[masterLandObject.LandData.LocalID].SetLandBitmap(
                        slaveLandObject.MergeLandBitmaps(masterLandObject.GetLandBitmap(), slaveLandObject.GetLandBitmap()));
                    performFinalLandJoin(masterLandObject, slaveLandObject);
                }
            }

            _scene.EventManager.TriggerParcelPrimCountTainted();
            masterLandObject.SendLandUpdateToAvatarsOverMe();
            _scene.ForEachClient(SendParcelOverlay);
        }
        #endregion

        #region Parcel Updating

        //legacy name
        public void SendParcelsOverlay(IClientAPI client)
        {
            SendParcelsOverlay(client);
        }

        /// <summary>
        /// Send the parcel overlay blocks to the client. 
        /// </summary>
        /// <param name="remote_client">The object representing the client</param>
        public void SendParcelOverlay(IClientAPI remote_client)
        {
            if (remote_client.SceneAgent.PresenceType == PresenceType.Npc)
                return;

            const int LAND_BLOCKS_PER_PACKET = 1024;

            int curID;
            int southID;

            byte[] byteArray = new byte[LAND_BLOCKS_PER_PACKET];
            int byteArrayCount = 0;
            int sequenceID = 0;

            int sx = _regionSizeX / LandUnit;
            byte curByte;
            byte tmpByte;

            // Layer data is in LandUnit (4m) chunks
            for (int y = 0; y < _regionSizeY / LandUnit; ++y)
            {
                for (int x = 0; x < sx;)
                {
                    curID = GetLandObjectIDinLandUnits(x,y);
                    if(curID < 0)
                        continue;

                    ILandObject currentParcel = GetLandObject(curID);
                    if (currentParcel == null)
                        continue;

                    // types
                    if (currentParcel.LandData.OwnerID == remote_client.AgentId)
                    {
                        //Owner Flag
                        curByte = LandChannel.LAND_TYPE_OWNED_BY_REQUESTER;
                    }
                    else if (currentParcel.LandData.IsGroupOwned && remote_client.IsGroupMember(currentParcel.LandData.GroupID))
                    {
                        curByte = LandChannel.LAND_TYPE_OWNED_BY_GROUP;
                    }
                    else if (currentParcel.LandData.SalePrice > 0 &&
                                (currentParcel.LandData.AuthBuyerID == UUID.Zero ||
                                currentParcel.LandData.AuthBuyerID == remote_client.AgentId))
                    {
                        //Sale type
                        curByte = LandChannel.LAND_TYPE_IS_FOR_SALE;
                    }
                    else if (currentParcel.LandData.OwnerID == UUID.Zero)
                    {
                        //Public type
                        curByte = LandChannel.LAND_TYPE_PUBLIC; // this does nothing, its zero
                    }
                    // LAND_TYPE_IS_BEING_AUCTIONED still unsuported
                    else
                    {
                        //Other 
                        curByte = LandChannel.LAND_TYPE_OWNED_BY_OTHER;
                    }

                    // now flags
                    // local sound
                    if ((currentParcel.LandData.Flags & (uint)ParcelFlags.SoundLocal) != 0)
                        curByte |= LandChannel.LAND_FLAG_LOCALSOUND;

                    // hide avatars
                    if (!currentParcel.LandData.SeeAVs)
                        curByte |= LandChannel.LAND_FLAG_HIDEAVATARS;

                    // border flags for current
                    if (y == 0)
                    {
                        curByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_SOUTH;
                        tmpByte = curByte;
                    }
                    else
                    {
                        tmpByte = curByte;
                        southID = GetLandObjectIDinLandUnits(x, y - 1);
                        if (southID >= 0 && southID != curID)
                            tmpByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_SOUTH;
                    }

                    tmpByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_WEST;
                    byteArray[byteArrayCount] = tmpByte;
                    byteArrayCount++;

                    if (byteArrayCount >= LAND_BLOCKS_PER_PACKET)
                    {
                        remote_client.SendLandParcelOverlay(byteArray, sequenceID);
                        byteArrayCount = 0;
                        sequenceID++;
                        byteArray = new byte[LAND_BLOCKS_PER_PACKET];
                    }
                    // keep adding while on same parcel, checking south border
                    if (y == 0)
                    {
                        // all have south border and that is already on curByte
                        while (++x < sx && GetLandObjectIDinLandUnits(x, y) == curID)
                        {
                            byteArray[byteArrayCount] = curByte;
                            byteArrayCount++;
                            if (byteArrayCount >= LAND_BLOCKS_PER_PACKET)
                            {
                                remote_client.SendLandParcelOverlay(byteArray, sequenceID);
                                byteArrayCount = 0;
                                sequenceID++;
                                byteArray = new byte[LAND_BLOCKS_PER_PACKET];
                            }
                        }
                    }
                    else
                    {
                        while (++x < sx && GetLandObjectIDinLandUnits(x, y) == curID)
                        {
                            // need to check south one by one
                            southID = GetLandObjectIDinLandUnits(x, y - 1);
                            if (southID >= 0 && southID != curID)
                            {
                                tmpByte = curByte;
                                tmpByte |= LandChannel.LAND_FLAG_PROPERTY_BORDER_SOUTH;
                                byteArray[byteArrayCount] = tmpByte;
                            }
                            else
                                byteArray[byteArrayCount] = curByte;

                            byteArrayCount++;
                            if (byteArrayCount >= LAND_BLOCKS_PER_PACKET)
                            {
                                remote_client.SendLandParcelOverlay(byteArray, sequenceID);
                                byteArrayCount = 0;
                                sequenceID++;
                                byteArray = new byte[LAND_BLOCKS_PER_PACKET];
                            }
                        }
                    }
                }
            }

            if (byteArrayCount > 0)
            {
                remote_client.SendLandParcelOverlay(byteArray, sequenceID);
            }
        }

        public void ClientOnParcelPropertiesRequest(int start_x, int start_y, int end_x, int end_y, int sequence_id,
                                                    bool snap_selection, IClientAPI remote_client)
        {
            if (_landList.Count == 0 || _landIDList == null)
                return;

            if (start_x < 0 || start_y < 0 || end_x < 0 || end_y < 0)
                return;
            if (start_x >= _regionSizeX || start_y >= _regionSizeX || end_x > _regionSizeX || end_y > _regionSizeY)
                return;

            if (end_x - start_x <= LandUnit &&
                end_y - start_y <= LandUnit)
            {
                ILandObject parcel = GetLandObject(start_x, start_y);
                if(parcel != null)
                    parcel.SendLandProperties(sequence_id, snap_selection, LandChannel.LAND_RESULT_SINGLE, remote_client);
                return;
            }

            start_x /= LandUnit;
            start_y /= LandUnit;
            end_x /= LandUnit;
            end_y /= LandUnit;

            //Get the land objects within the bounds
            Dictionary<int, ILandObject> temp = new Dictionary<int, ILandObject>();
            for (int x = start_x; x < end_x; ++x)
            {
                for (int y = start_y; y < end_y; ++y)
                {
                    ILandObject currentParcel = GetLandObjectinLandUnits(x, y);

                    if (currentParcel != null)
                    {
                        if (!temp.ContainsKey(currentParcel.LandData.LocalID))
                        {
                            if (!currentParcel.IsBannedFromLand(remote_client.AgentId))
                            {
                                temp[currentParcel.LandData.LocalID] = currentParcel;
                            }
                        }
                    }
                }
            }

            int requestResult = temp.Count > 1 ? LandChannel.LAND_RESULT_MULTIPLE : LandChannel.LAND_RESULT_SINGLE;

            foreach(ILandObject lo in temp.Values)
            {
                lo.SendLandProperties(sequence_id, snap_selection, requestResult, remote_client);
            }

//            SendParcelOverlay(remote_client);
        }

        public void UpdateLandProperties(ILandObject land, LandUpdateArgs args, IClientAPI remote_client)
        {
            bool snap_selection = false;
            bool needOverlay = false;
            if (land.UpdateLandProperties(args, remote_client, out snap_selection, out needOverlay))
            {
                UUID parcelID = land.LandData.GlobalID;
                _scene.ForEachScenePresence(delegate(ScenePresence avatar)
                {
                    if (avatar.IsDeleted || avatar.IsNPC)
                        return;

                    IClientAPI client = avatar.ControllingClient;
                    if (needOverlay)
                        SendParcelOverlay(client);

                    if (avatar.IsChildAgent)
                    {
                        land.SendLandProperties(-10000, false, LandChannel.LAND_RESULT_SINGLE, client);
                        return;
                    }

                    ILandObject aland = GetLandObject(avatar.AbsolutePosition.X, avatar.AbsolutePosition.Y);
                    if (aland != null)
                    {
                        if(land != aland)
                            land.SendLandProperties(-10000, false, LandChannel.LAND_RESULT_SINGLE, client);
                        else if (land == aland)
                             aland.SendLandProperties(0, true, LandChannel.LAND_RESULT_SINGLE, client);
                    }
                    if (avatar.currentParcelUUID == parcelID)
                        avatar.currentParcelUUID = parcelID; // force parcel flags review
                });
            }
        }

        public void ClientOnParcelPropertiesUpdateRequest(LandUpdateArgs args, int localID, IClientAPI remote_client)
        {
            ILandObject land;
            lock (_landList)
            {
                _landList.TryGetValue(localID, out land);
            }

            if (land != null)
            {
                UpdateLandProperties(land, args, remote_client);
                _scene.EventManager.TriggerOnParcelPropertiesUpdateRequest(args, localID, remote_client);
            }
        }

        public void ClientOnParcelDivideRequest(int west, int south, int east, int north, IClientAPI remote_client)
        {
            Subdivide(west, south, east, north, remote_client.AgentId);
        }

        public void ClientOnParcelJoinRequest(int west, int south, int east, int north, IClientAPI remote_client)
        {
            Join(west, south, east, north, remote_client.AgentId);
        }

        public void ClientOnParcelSelectObjects(int local_id, int request_type,
                                                List<UUID> returnIDs, IClientAPI remote_client)
        {
            _landList[local_id].SendForceObjectSelect(local_id, request_type, returnIDs, remote_client);
        }

        public void ClientOnParcelObjectOwnerRequest(int local_id, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (_landList)
            {
                _landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                _scene.EventManager.TriggerParcelPrimCountUpdate();
                land.SendLandObjectOwners(remote_client);
            }
            else
            {
                _log.WarnFormat("[LAND MANAGEMENT MODULE]: Invalid land object {0} passed for parcel object owner request", local_id);
            }
        }

        public void ClientOnParcelGodForceOwner(int local_id, UUID ownerID, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (_landList)
            {
                _landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                if (_scene.Permissions.IsGod(remote_client.AgentId))
                {
                    land.LandData.OwnerID = ownerID;
                    land.LandData.GroupID = UUID.Zero;
                    land.LandData.IsGroupOwned = false;
                    land.LandData.Flags &= ~(uint) (ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects | ParcelFlags.ShowDirectory);
                    UpdateLandObject(land.LandData.LocalID, land.LandData);
                    _scene.ForEachClient(SendParcelOverlay);
                    land.SendLandUpdateToClient(true, remote_client);
                }
            }
        }

        public void ClientOnParcelAbandonRequest(int local_id, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (_landList)
            {
                _landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                if (_scene.Permissions.CanAbandonParcel(remote_client.AgentId, land))
                {
                    land.LandData.OwnerID = _scene.RegionInfo.EstateSettings.EstateOwner;
                    land.LandData.GroupID = UUID.Zero;
                    land.LandData.IsGroupOwned = false;
                    land.LandData.Flags &= ~(uint) (ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects | ParcelFlags.ShowDirectory);

                    UpdateLandObject(land.LandData.LocalID, land.LandData);
                    _scene.ForEachClient(SendParcelOverlay);
                    land.SendLandUpdateToAvatars();
                }
            }
        }

        public void ClientOnParcelReclaim(int local_id, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (_landList)
            {
                _landList.TryGetValue(local_id, out land);
            }

            if (land != null)
            {
                if (_scene.Permissions.CanReclaimParcel(remote_client.AgentId, land))
                {
                    land.LandData.OwnerID = _scene.RegionInfo.EstateSettings.EstateOwner;
                    land.LandData.ClaimDate = Util.UnixTimeSinceEpoch();
                    land.LandData.GroupID = UUID.Zero;
                    land.LandData.IsGroupOwned = false;
                    land.LandData.SalePrice = 0;
                    land.LandData.AuthBuyerID = UUID.Zero;
                    land.LandData.SeeAVs = true;
                    land.LandData.AnyAVSounds = true;
                    land.LandData.GroupAVSounds = true;
                    land.LandData.Flags &= ~(uint) (ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects | ParcelFlags.ShowDirectory);
                    UpdateLandObject(land.LandData.LocalID, land.LandData);
                    _scene.ForEachClient(SendParcelOverlay);
                    land.SendLandUpdateToAvatars();
                }
            }
        }
        #endregion

        // If the economy has been validated by the economy module,
        // and land has been validated as well, this method transfers
        // the land ownership

        public void EventManagerOnLandBuy(object o, EventManager.LandBuyArgs e)
        {
            if (e.economyValidated && e.landValidated)
            {
                ILandObject land;
                lock (_landList)
                {
                    _landList.TryGetValue(e.parcelLocalID, out land);
                }

                if (land != null)
                {
                    land.UpdateLandSold(e.agentId, e.groupId, e.groupOwned, (uint)e.transactionID, e.parcelPrice, e.parcelArea);
                    _scene.ForEachClient(SendParcelOverlay);
                    land.SendLandUpdateToAvatars();
                }
            }
        }

        // After receiving a land buy packet, first the data needs to
        // be validated. This method validates the right to buy the
        // parcel

        public void EventManagerOnValidateLandBuy(object o, EventManager.LandBuyArgs e)
        {
            if (e.landValidated == false)
            {
                ILandObject lob = null;
                lock (_landList)
                {
                    _landList.TryGetValue(e.parcelLocalID, out lob);
                }

                if (lob != null)
                {
                    UUID AuthorizedID = lob.LandData.AuthBuyerID;
                    int saleprice = lob.LandData.SalePrice;
                    UUID pOwnerID = lob.LandData.OwnerID;

                    bool landforsale = (lob.LandData.Flags &
                                        (uint)(ParcelFlags.ForSale | ParcelFlags.ForSaleObjects | ParcelFlags.SellParcelObjects)) != 0;
                    if ((AuthorizedID == UUID.Zero || AuthorizedID == e.agentId) && e.parcelPrice >= saleprice && landforsale)
                    {
                        // TODO I don't think we have to lock it here, no?
                        //lock (e)
                        //{
                            e.parcelOwnerID = pOwnerID;
                            e.landValidated = true;
                        //}
                    }
                }
            }
        }

        void ClientOnParcelDeedToGroup(int parcelLocalID, UUID groupID, IClientAPI remote_client)
        {
            ILandObject land = null;
            lock (_landList)
            {
                _landList.TryGetValue(parcelLocalID, out land);
            }

            if (land != null)
            {
                if (!_scene.Permissions.CanDeedParcel(remote_client.AgentId, land))
                    return;
                land.DeedToGroup(groupID);
                _scene.ForEachClient(SendParcelOverlay);
                land.SendLandUpdateToAvatars();
            }
        }

        #region Land Object From Storage Functions

        private void EventManagerOnIncomingLandDataFromStorage(List<LandData> data)
        {
            lock (_landList)
            {
                for (int i = 0; i < data.Count; i++)
                    IncomingLandObjectFromStorage(data[i]);

                // Layer data is in LandUnit (4m) chunks
                for (int y = 0; y < _regionSizeY / Constants.TerrainPatchSize * (Constants.TerrainPatchSize / LandUnit); y++)
                {
                    for (int x = 0; x < _regionSizeX / Constants.TerrainPatchSize * (Constants.TerrainPatchSize / LandUnit); x++)
                    {
                        if (_landIDList[x, y] == 0)
                        {
                            if (_landList.Count == 1)
                            {
                                _log.DebugFormat(
                                    "[{0}]: Auto-extending land parcel as landID at {1},{2} is 0 and only one land parcel is present in {3}",
                                    LogHeader, x, y, _scene.Name);

                                int onlyParcelID = 0;
                                ILandObject onlyLandObject = null;
                                foreach (KeyValuePair<int, ILandObject> kvp in _landList)
                                {
                                    onlyParcelID = kvp.Key;
                                    onlyLandObject = kvp.Value;
                                    break;
                                }

                                // There is only one parcel. Grow it to fill all the unallocated spaces.
                                for (int xx = 0; xx < _landIDList.GetLength(0); xx++)
                                    for (int yy = 0; yy < _landIDList.GetLength(1); yy++)
                                        if (_landIDList[xx, yy] == 0)
                                            _landIDList[xx, yy] = onlyParcelID;

                                onlyLandObject.LandBitmap = CreateBitmapForID(onlyParcelID);
                            }
                            else if (_landList.Count > 1)
                            {
                                _log.DebugFormat(
                                    "{0}: Auto-creating land parcel as landID at {1},{2} is 0 and more than one land parcel is present in {3}",
                                    LogHeader, x, y, _scene.Name);

                                // There are several other parcels so we must create a new one for the unassigned space
                                ILandObject newLand = new LandObject(UUID.Zero, false, _scene);
                                // Claim all the unclaimed "0" ids
                                newLand.SetLandBitmap(CreateBitmapForID(0));
                                newLand.LandData.OwnerID = _scene.RegionInfo.EstateSettings.EstateOwner;
                                newLand.LandData.ClaimDate = Util.UnixTimeSinceEpoch();
                                newLand = AddLandObject(newLand);
                            }
                            else
                            {
                                // We should never reach this point as the separate code path when no land data exists should have fired instead.
                                _log.WarnFormat(
                                    "{0}: Ignoring request to auto-create parcel in {1} as there are no other parcels present",
                                    LogHeader, _scene.Name);
                            }
                        }
                    }
                }

                FinalizeLandPrimCountUpdate(); // update simarea information

                lock (_landList)
                {
                    foreach(LandObject lo in _landList.Values)
                        lo.SendLandUpdateToAvatarsOverMe();
                }
            }
        }

        private void IncomingLandObjectFromStorage(LandData data)
        {
            ILandObject new_land = new LandObject(data.OwnerID, data.IsGroupOwned, _scene, data);

            new_land.SetLandBitmapFromByteArray();
            AddLandObject(new_land);
        }

        public void ReturnObjectsInParcel(int localID, uint returnType, UUID[] agentIDs, UUID[] taskIDs, IClientAPI remoteClient)
        {
            if (localID != -1)
            {
                ILandObject selectedParcel = null;
                lock (_landList)
                {
                    _landList.TryGetValue(localID, out selectedParcel);
                }

                if (selectedParcel == null)
                    return;

                selectedParcel.ReturnLandObjects(returnType, agentIDs, taskIDs, remoteClient);
            }
            else
            {
                if (returnType != 1)
                {
                    _log.WarnFormat("[LAND MANAGEMENT MODULE]: ReturnObjectsInParcel: unknown return type {0}", returnType);
                    return;
                }

                // We get here when the user returns objects from the list of Top Colliders or Top Scripts.
                // In that case we receive specific object UUID's, but no parcel ID.

                Dictionary<UUID, HashSet<SceneObjectGroup>> returns = new Dictionary<UUID, HashSet<SceneObjectGroup>>();

                foreach (UUID groupID in taskIDs)
                {
                    SceneObjectGroup obj = _scene.GetSceneObjectGroup(groupID);
                    if (obj != null)
                    {
                        if (!returns.ContainsKey(obj.OwnerID))
                            returns[obj.OwnerID] = new HashSet<SceneObjectGroup>();
                        returns[obj.OwnerID].Add(obj);
                    }
                    else
                    {
                        _log.WarnFormat("[LAND MANAGEMENT MODULE]: ReturnObjectsInParcel: unknown object {0}", groupID);
                    }
                }

                int num = 0;
                foreach (HashSet<SceneObjectGroup> objs in returns.Values)
                    num += objs.Count;
                _log.DebugFormat("[LAND MANAGEMENT MODULE]: Returning {0} specific object(s)", num);

                foreach (HashSet<SceneObjectGroup> objs in returns.Values)
                {
                    List<SceneObjectGroup> objs2 = new List<SceneObjectGroup>(objs);
                    if (_scene.Permissions.CanReturnObjects(null, remoteClient, objs2))
                    {
                        _scene.returnObjects(objs2.ToArray(), remoteClient);
                    }
                    else
                    {
                        _log.WarnFormat("[LAND MANAGEMENT MODULE]: ReturnObjectsInParcel: not permitted to return {0} object(s) belonging to user {1}",
                            objs2.Count, objs2[0].OwnerID);
                    }
                }
            }
        }

        public void EventManagerOnNoLandDataFromStorage()
        {
            ResetSimLandObjects();
            CreateDefaultParcel();
        }

        #endregion

        public void setParcelObjectMaxOverride(overrideParcelMaxPrimCountDelegate overrideDel)
        {
            lock (_landList)
            {
                foreach (LandObject obj in _landList.Values)
                {
                    obj.SetParcelObjectMaxOverride(overrideDel);
                }
            }
        }

        public void setSimulatorObjectMaxOverride(overrideSimulatorMaxPrimCountDelegate overrideDel)
        {
        }

        #region CAPS handler

        private void EventManagerOnRegisterCaps(UUID agentID, Caps caps)
        {
            caps.RegisterSimpleHandler("RemoteParcelRequest", new SimpleOSDMapHandler("POST","/" + UUID.Random(), RemoteParcelRequest));

            caps.RegisterSimpleHandler("ParcelPropertiesUpdate", new SimpleStreamHandler("/" + UUID.Random(),
                delegate (IOSHttpRequest request, IOSHttpResponse response)
                {
                    ProcessPropertiesUpdate(request, response, agentID);
                }));
        }

        private void ProcessPropertiesUpdate(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            IClientAPI client;
            if (!_scene.TryGetClient(agentID, out client))
            {
                _log.WarnFormat("[LAND MANAGEMENT MODULE]: Unable to retrieve IClientAPI for {0}", agentID);
                response.StatusCode = (int)HttpStatusCode.Gone;
                return;
            }

            OSDMap args;
            ParcelPropertiesUpdateMessage properties;
            try
            {
                args = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);
                properties = new ParcelPropertiesUpdateMessage();
                properties.Deserialize(args);
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            int parcelID = properties.LocalID;

            ILandObject land = null;
            lock (_landList)
            {
                _landList.TryGetValue(parcelID, out land);
            }

            if (land == null)
            {
                _log.WarnFormat("[LAND MANAGEMENT MODULE]: Unable to find parcelID {0}", parcelID);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            try
            {
                LandUpdateArgs land_update = new LandUpdateArgs
                {
                    AuthBuyerID = properties.AuthBuyerID,
                    Category = properties.Category,
                    Desc = properties.Desc,
                    GroupID = properties.GroupID,
                    LandingType = (byte)properties.Landing,
                    MediaAutoScale = (byte)Convert.ToInt32(properties.MediaAutoScale),
                    MediaID = properties.MediaID,
                    MediaURL = properties.MediaURL,
                    MusicURL = properties.MusicURL,
                    Name = properties.Name,
                    ParcelFlags = (uint)properties.ParcelFlags,
                    PassHours = properties.PassHours,
                    PassPrice = (int)properties.PassPrice,
                    SalePrice = (int)properties.SalePrice,
                    SnapshotID = properties.SnapshotID,
                    UserLocation = properties.UserLocation,
                    UserLookAt = properties.UserLookAt,
                    MediaDescription = properties.MediaDesc,
                    MediaType = properties.MediaType,
                    MediaWidth = properties.MediaWidth,
                    MediaHeight = properties.MediaHeight,
                    MediaLoop = properties.MediaLoop,
                    ObscureMusic = properties.ObscureMusic,
                    ObscureMedia = properties.ObscureMedia
                };

                if (args.ContainsKey("see_avs"))
                {
                    land_update.SeeAVs = args["see_avs"].AsBoolean();
                    land_update.AnyAVSounds = args["any_av_sounds"].AsBoolean();
                    land_update.GroupAVSounds = args["group_av_sounds"].AsBoolean();
                }
                else
                {
                    land_update.SeeAVs = true;
                    land_update.AnyAVSounds = true;
                    land_update.GroupAVSounds = true;
                }

                UpdateLandProperties(land,land_update, client);
                _scene.EventManager.TriggerOnParcelPropertiesUpdateRequest(land_update, parcelID, client);
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return;
            }

            response.StatusCode = (int)HttpStatusCode.OK;
        }

        // we cheat here: As we don't have (and want) a grid-global parcel-store, we can't return the
        // "real" parcelID, because we wouldn't be able to map that to the region the parcel belongs to.
        // So, we create a "fake" parcelID by using the regionHandle (64 bit), and the local (integer) x
        // and y coordinate (each 8 bit), encoded in a UUID (128 bit).
        //
        // Request format:
        // <llsd>
        //   <map>
        //     <key>location</key>
        //     <array>
        //       <real>1.23</real>
        //       <real>45..6</real>
        //       <real>78.9</real>
        //     </array>
        //     <key>region_id</key>
        //     <uuid>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</uuid>
        //   </map>
        // </llsd>
        private void RemoteParcelRequest(IOSHttpRequest request, IOSHttpResponse response, OSDMap args)
        {
            UUID parcelID = UUID.Zero;
            OSD tmp;
            try
            {
                if (args.TryGetValue("location", out tmp) && tmp is OSDArray)
                {
                    UUID scope = _scene.RegionInfo.ScopeID;
                    OSDArray list = (OSDArray)tmp;
                    uint x = (uint)(double)list[0];
                    uint y = (uint)(double)list[1];
                    ulong myHandle = _scene.RegionInfo.RegionHandle;
                    if (args.TryGetValue("region_handle", out tmp) && tmp is OSDBinary)
                    {
                        // if you do a "About Landmark" on a landmark a second time, the viewer sends the
                        // region_handle it got earlier via RegionHandleRequest
                        ulong regionHandle = Util.BytesToUInt64Big(tmp);
                        if(regionHandle == myHandle)
                        {
                            ILandObject l = GetLandObjectClippedXY(x, y);
                            if (l != null)
                                parcelID = l.LandData.FakeID;
                            else
                                parcelID = Util.BuildFakeParcelID(myHandle, x, y);
                        }
                        else
                        {
                            uint wx;
                            uint wy;
                            Util.RegionHandleToWorldLoc(regionHandle, out wx, out wy);
                            GridRegion info = _scene.GridService.GetRegionByPosition(scope, (int)wx, (int)wy);
                            if (info != null)
                            {
                                wx -= (uint)info.RegionLocX;
                                wy -= (uint)info.RegionLocY;
                                wx += x;
                                wy += y;
                                if (wx >= info.RegionSizeX || wy >= info.RegionSizeY)
                                {
                                    wx = x;
                                    wy = y;
                                }
                                if (info.RegionHandle == myHandle)
                                {
                                    ILandObject l = GetLandObjectClippedXY(wx, wy);
                                    if (l != null)
                                        parcelID = l.LandData.FakeID;
                                    else
                                        parcelID = Util.BuildFakeParcelID(myHandle, wx, wy);
                                }
                                else
                                {
                                    parcelID = Util.BuildFakeParcelID(info.RegionHandle, wx, wy);
                                }
                            }
                        }
                    }
                    else if (args.TryGetValue("region_id", out tmp) && tmp is OSDUUID)
                    {
                        UUID regionID = tmp.AsUUID();
                        if (regionID == _scene.RegionInfo.RegionID)
                        {
                            ILandObject l = GetLandObjectClippedXY(x, y);
                            if (l != null)
                                parcelID = l.LandData.FakeID;
                            else
                                parcelID = Util.BuildFakeParcelID(myHandle, x, y);
                        }
                        else
                        {
                            // a parcel request for a parcel in another region. Ask the grid about the region
                            GridRegion info = _scene.GridService.GetRegionByUUID(scope, regionID);
                            if (info != null)
                                parcelID = Util.BuildFakeParcelID(info.RegionHandle, x, y);
                        }
                    }
                }
            }
            catch
            {
                _log.Error("[LAND MANAGEMENT MODULE]: RemoteParcelRequest failed");
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            //_log.DebugFormat("[LAND MANAGEMENT MODULE]: Got parcelID {0} {1}", parcelID, parcelID == UUID.Zero ? args.ToString() :"");
            osUTF8 sb = LLSDxmlEncode2.Start();
                LLSDxmlEncode2.AddMap(sb);
                  LLSDxmlEncode2.AddElem("parcel_id", parcelID,sb);
                LLSDxmlEncode2.AddEndMap(sb);
            response.RawBuffer = LLSDxmlEncode2.EndToBytes(sb);
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        #endregion

        private void ClientOnParcelInfoRequest(IClientAPI remoteClient, UUID parcelID)
        {
            if (parcelID == UUID.Zero)
                return;

            if(!_parcelInfoCache.TryGetValue(parcelID, 30000, out ExtendedLandData data))
            {
                data = null;
                ExtendedLandData extLandData = new ExtendedLandData();

                while(true)
                {
                    if(!Util.ParseFakeParcelID(parcelID, out extLandData.RegionHandle,
                                        out extLandData.X, out extLandData.Y))
                        break;

                    //_log.DebugFormat("[LAND MANAGEMENT MODULE]: Got parcelinfo request for regionHandle {0}, x/y {1}/{2}",
                    //                extLandData.RegionHandle, extLandData.X, extLandData.Y);

                    // for this region or for somewhere else?
                    if (extLandData.RegionHandle == _scene.RegionInfo.RegionHandle)
                    {
                        ILandObject extLandObject = GetLandObjectByfakeID(parcelID);
                        if (extLandObject == null)
                            break;

                        extLandData.LandData = extLandObject.LandData;
                        extLandData.RegionAccess = _scene.RegionInfo.AccessLevel;
                        if (extLandData.LandData != null)
                            data = extLandData;
                        break;
                    }
                    else
                    {
                        ILandService landService = _scene.RequestModuleInterface<ILandService>();
                        extLandData.LandData = landService.GetLandData(_scene.RegionInfo.ScopeID,
                                extLandData.RegionHandle, extLandData.X, extLandData.Y,
                                out extLandData.RegionAccess);
                        if (extLandData.LandData != null)
                            data = extLandData;
                        break;
                    }
                }
                _parcelInfoCache.Add(parcelID, data, 30000);
            }

            if (data != null)  // if we found some data, send it
            {
                GridRegion info;
                if (data.RegionHandle == _scene.RegionInfo.RegionHandle)
                {
                    info = new GridRegion(_scene.RegionInfo);
                    IDwellModule dwellModule = _scene.RequestModuleInterface<IDwellModule>();
                    if (dwellModule != null)
                        data.LandData.Dwell = dwellModule.GetDwell(data.LandData);
                }
                else
                {
                    // most likely still cached from building the extLandData entry
                    info = _scene.GridService.GetRegionByHandle(_scene.RegionInfo.ScopeID, data.RegionHandle);
                }
                // we need to transfer the fake parcelID, not the one in landData, so the viewer can match it to the landmark.
                //_log.DebugFormat("[LAND MANAGEMENT MODULE]: got parcelinfo for parcel {0} in region {1}; sending...",
                //                  data.LandData.Name, data.RegionHandle);

                // HACK for now
                RegionInfo r = new RegionInfo
                {
                    RegionName = info.RegionName,
                    RegionLocX = (uint)info.RegionLocX,
                    RegionLocY = (uint)info.RegionLocY
                };
                r.RegionSettings.Maturity = (int)Util.ConvertAccessLevelToMaturity(data.RegionAccess);
                remoteClient.SendParcelInfo(r, data.LandData, parcelID, data.X, data.Y);
            }
            else
                _log.Debug("[LAND MANAGEMENT MODULE]: got no parcelinfo; not sending");
        }

        public void SetParcelOtherCleanTime(IClientAPI remoteClient, int localID, int otherCleanTime)
        {
            ILandObject land = null;
            lock (_landList)
            {
                _landList.TryGetValue(localID, out land);
            }

            if (land == null) return;

            if (!_scene.Permissions.CanEditParcelProperties(remoteClient.AgentId, land, GroupPowers.LandOptions, false))
                return;

            land.LandData.OtherCleanTime = otherCleanTime;

            UpdateLandObject(localID, land.LandData);
        }

        public void ClientOnParcelGodMark(IClientAPI client, UUID god, int landID)
        {
            ScenePresence sp = null;
            ((Scene)client.Scene).TryGetScenePresence(client.AgentId, out sp);
            if (sp == null)
                return;
            if (sp.IsChildAgent || sp.IsDeleted || sp.IsInTransit || sp.IsNPC)
                return;
            if (!sp.IsGod)
            {
                client.SendAlertMessage("Request denied. You're not priviliged.");
                return;
            }

            ILandObject land = null;
            List<ILandObject> Lands = ((Scene)client.Scene).LandChannel.AllParcels();
            foreach (ILandObject landObject in Lands)
            {
                if (landObject.LandData.LocalID == landID)
                { 
                    land = landObject;
                    break;
                }
            }
            if (land == null)
                return;

            bool validParcelOwner = false;
            if (DefaultGodParcelOwner != UUID.Zero && _scene.UserAccountService.GetUserAccount(_scene.RegionInfo.ScopeID, DefaultGodParcelOwner) != null)
                validParcelOwner = true;

            bool validParcelGroup = false;
            if (_groupManager != null)
            {
                if (DefaultGodParcelGroup != UUID.Zero && _groupManager.GetGroupRecord(DefaultGodParcelGroup) != null)
                    validParcelGroup = true;
            }

            if (!validParcelOwner && !validParcelGroup)
            {
                client.SendAlertMessage("Please check ini files.\n[LandManagement] config section.");
                return;
            }

            land.LandData.AnyAVSounds = true;
            land.LandData.SeeAVs = true;
            land.LandData.GroupAVSounds = true;
            land.LandData.AuthBuyerID = UUID.Zero;
            land.LandData.Category = ParcelCategory.None;
            land.LandData.ClaimDate = Util.UnixTimeSinceEpoch();
            land.LandData.Description = string.Empty;
            land.LandData.Dwell = 0;
            land.LandData.Flags = (uint)ParcelFlags.AllowFly | (uint)ParcelFlags.AllowLandmark |
                                (uint)ParcelFlags.AllowAPrimitiveEntry |
                                (uint)ParcelFlags.AllowDeedToGroup |
                                (uint)ParcelFlags.CreateObjects | (uint)ParcelFlags.AllowOtherScripts |
                                (uint)ParcelFlags.AllowVoiceChat;
            land.LandData.LandingType = (byte)LandingType.Direct;
            land.LandData.LastDwellTimeMS = Util.GetTimeStampMS();
            land.LandData.MediaAutoScale = 0;
            land.LandData.MediaDescription = "";
            land.LandData.MediaHeight = 0;
            land.LandData.MediaID = UUID.Zero;
            land.LandData.MediaLoop = false;
            land.LandData.MediaType = "none/none";
            land.LandData.MediaURL = string.Empty;
            land.LandData.MediaWidth = 0;
            land.LandData.MusicURL = string.Empty;
            land.LandData.ObscureMedia = false;
            land.LandData.ObscureMusic = false;
            land.LandData.OtherCleanTime = 0;
            land.LandData.ParcelAccessList = new List<LandAccessEntry>();
            land.LandData.PassHours = 0;
            land.LandData.PassPrice = 0;
            land.LandData.SalePrice = 0;
            land.LandData.SnapshotID = UUID.Zero;
            land.LandData.Status = ParcelStatus.Leased;

            if (validParcelOwner)
            {
                land.LandData.OwnerID = DefaultGodParcelOwner;
                land.LandData.IsGroupOwned = false;
            }
            else
            {
                land.LandData.OwnerID = DefaultGodParcelGroup;
                land.LandData.IsGroupOwned = true;
            }

            if (validParcelGroup)
                land.LandData.GroupID = DefaultGodParcelGroup;
            else
                land.LandData.GroupID = UUID.Zero;

            land.LandData.Name = DefaultGodParcelName;
            UpdateLandObject(land.LandData.LocalID, land.LandData);
            //_scene.EventManager.TriggerParcelPrimCountUpdate();

            _scene.ForEachClient(SendParcelOverlay);
            land.SendLandUpdateToClient(true, client);
        }

        private void ClientOnSimWideDeletes(IClientAPI client, UUID agentID, int flags, UUID targetID)
        {
            ScenePresence SP;
            ((Scene)client.Scene).TryGetScenePresence(client.AgentId, out SP);
            List<SceneObjectGroup> returns = new List<SceneObjectGroup>();
            if (SP.GodController.UserLevel != 0)
            {
                if (flags == 0) //All parcels, scripted or not
                {
                    ((Scene)client.Scene).ForEachSOG(delegate(SceneObjectGroup e)
                    {
                        if (e.OwnerID == targetID)
                        {
                            returns.Add(e);
                        }
                    }
                                                    );
                }
                if (flags == 4) //All parcels, scripted object
                {
                    ((Scene)client.Scene).ForEachSOG(delegate(SceneObjectGroup e)
                    {
                        if (e.OwnerID == targetID)
                        {
                            if (e.ContainsScripts())
                            {
                                returns.Add(e);
                            }
                        }
                    });
                }
                if (flags == 4) //not target parcel, scripted object
                {
                    ((Scene)client.Scene).ForEachSOG(delegate(SceneObjectGroup e)
                    {
                        if (e.OwnerID == targetID)
                        {
                            ILandObject landobject = ((Scene)client.Scene).LandChannel.GetLandObject(e.AbsolutePosition.X, e.AbsolutePosition.Y);
                            if (landobject.LandData.OwnerID != e.OwnerID)
                            {
                                if (e.ContainsScripts())
                                {
                                    returns.Add(e);
                                }
                            }
                        }
                    });
                }
                foreach (SceneObjectGroup ol in returns)
                {
                    ReturnObject(ol, client);
                }
            }
        }
        public void ReturnObject(SceneObjectGroup obj, IClientAPI client)
        {
            SceneObjectGroup[] objs = new SceneObjectGroup[1];
            objs[0] = obj;
            ((Scene)client.Scene).returnObjects(objs, client);
        }

        readonly Dictionary<UUID, System.Threading.Timer> Timers = new Dictionary<UUID, System.Threading.Timer>();

        public void ClientOnParcelFreezeUser(IClientAPI client, UUID parcelowner, uint flags, UUID target)
        {
            ScenePresence targetAvatar = null;
            ((Scene)client.Scene).TryGetScenePresence(target, out targetAvatar);
            ScenePresence parcelManager = null;
            ((Scene)client.Scene).TryGetScenePresence(client.AgentId, out parcelManager);
            System.Threading.Timer Timer;

            if (targetAvatar.GodController.UserLevel < 200)
            {
                ILandObject land = ((Scene)client.Scene).LandChannel.GetLandObject(targetAvatar.AbsolutePosition.X, targetAvatar.AbsolutePosition.Y);
                if (!((Scene)client.Scene).Permissions.CanEditParcelProperties(client.AgentId, land, GroupPowers.LandEjectAndFreeze, true))
                    return;
                if ((flags & 1) == 0) // only lowest bit has meaning for now
                {
                    targetAvatar.AllowMovement = false;
                    targetAvatar.ControllingClient.SendAlertMessage(parcelManager.Firstname + " " + parcelManager.Lastname + " has frozen you for 30 seconds.  You cannot move or interact with the world.");
                    parcelManager.ControllingClient.SendAlertMessage("Avatar Frozen.");
                    System.Threading.TimerCallback timeCB = new System.Threading.TimerCallback(OnEndParcelFrozen);
                    Timer = new System.Threading.Timer(timeCB, targetAvatar, 30000, 0);
                    Timers.Add(targetAvatar.UUID, Timer);
                }
                else
                {
                    targetAvatar.AllowMovement = true;
                    targetAvatar.ControllingClient.SendAlertMessage(parcelManager.Firstname + " " + parcelManager.Lastname + " has unfrozen you.");
                    parcelManager.ControllingClient.SendAlertMessage("Avatar Unfrozen.");
                    Timers.TryGetValue(targetAvatar.UUID, out Timer);
                    Timers.Remove(targetAvatar.UUID);
                    Timer.Dispose();
                }
            }
        }
        private void OnEndParcelFrozen(object avatar)
        {
            ScenePresence targetAvatar = (ScenePresence)avatar;
            targetAvatar.AllowMovement = true;
            System.Threading.Timer Timer;
            Timers.TryGetValue(targetAvatar.UUID, out Timer);
            Timers.Remove(targetAvatar.UUID);
            targetAvatar.ControllingClient.SendAgentAlertMessage("The freeze has worn off; you may go about your business.", false);
        }

        public void ClientOnParcelEjectUser(IClientAPI client, UUID parcelowner, uint flags, UUID target)
        {
            ScenePresence targetAvatar = null;
            ScenePresence parcelManager = null;

            // Must have presences
            if (!_scene.TryGetScenePresence(target, out targetAvatar) ||
                !_scene.TryGetScenePresence(client.AgentId, out parcelManager))
                return;

            // Cannot eject estate managers or gods
            if (_scene.Permissions.IsAdministrator(target))
                return;

            // Check if you even have permission to do this
            ILandObject land = _scene.LandChannel.GetLandObject(targetAvatar.AbsolutePosition.X, targetAvatar.AbsolutePosition.Y);
            if (!_scene.Permissions.CanEditParcelProperties(client.AgentId, land, GroupPowers.LandEjectAndFreeze, true) &&
                !_scene.Permissions.IsAdministrator(client.AgentId))
                return;

            Vector3 pos = _scene.GetNearestAllowedPosition(targetAvatar, land);

            targetAvatar.TeleportOnEject(pos);
            targetAvatar.ControllingClient.SendAlertMessage("You have been ejected by " + parcelManager.Firstname + " " + parcelManager.Lastname);
            parcelManager.ControllingClient.SendAlertMessage("Avatar Ejected.");

            if ((flags & 1) != 0) // Ban TODO: Remove magic number
            {
                LandAccessEntry entry = new LandAccessEntry
                {
                    AgentID = targetAvatar.UUID,
                    Flags = AccessList.Ban,
                    Expires = 0 // Perm
                };

                land.LandData.ParcelAccessList.Add(entry);
            }
        }

        public void ClearAllEnvironments()
        {
            List<ILandObject> parcels = AllParcels();
            for (int i = 0; i < parcels.Count; ++i)
                parcels[i].StoreEnvironment(null);
        }

        /// <summary>
        /// Sets the Home Point.   The LoginService uses this to know where to put a user when they log-in
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        /// <param name="lookAt"></param>
        /// <param name="flags"></param>
        public virtual void ClientOnSetHome(IClientAPI remoteClient, ulong regionHandle, Vector3 position, Vector3 lookAt, uint flags)
        {
            // Let's find the parcel in question
            ILandObject land = GetLandObject(position);
            if (land == null || _scene.GridUserService == null)
            {
                _Dialog.SendAlertToUser(remoteClient, "Set Home request failed.");
                return;
            }

            // Gather some data
            ulong gpowers = remoteClient.GetGroupPowers(land.LandData.GroupID);
            SceneObjectGroup telehub = null;
            if (_scene.RegionInfo.RegionSettings.TelehubObject != UUID.Zero)
                // Does the telehub exist in the scene?
                telehub = _scene.GetSceneObjectGroup(_scene.RegionInfo.RegionSettings.TelehubObject);

            // Can the user set home here?
            if (// Required: local user; foreign users cannot set home
                _scene.UserManagementModule.IsLocalGridUser(remoteClient.AgentId) &&
                (// (a) gods and land managers can set home
                 _scene.Permissions.IsAdministrator(remoteClient.AgentId) ||
                 _scene.Permissions.IsGod(remoteClient.AgentId) ||
                 // (b) land owners can set home
                 remoteClient.AgentId == land.LandData.OwnerID ||
                 // (c) members of the land-associated group in roles that can set home
                 (gpowers & (ulong)GroupPowers.AllowSetHome) == (ulong)GroupPowers.AllowSetHome ||
                 // (d) parcels with telehubs can be the home of anyone
                 telehub != null && land.ContainsPoint((int)telehub.AbsolutePosition.X, (int)telehub.AbsolutePosition.Y)))
            {
                string userId;
                UUID test;
                if (!_scene.UserManagementModule.GetUserUUI(remoteClient.AgentId, out userId))
                {
                    /* Do not set a home position in this grid for a HG visitor */
                    _Dialog.SendAlertToUser(remoteClient, "Set Home request failed. (User Lookup)");
                }
                else if (!UUID.TryParse(userId, out test))
                {
                    _Dialog.SendAlertToUser(remoteClient, "Set Home request failed. (HG visitor)");
                }
                else if (_scene.GridUserService.SetHome(userId, land.RegionUUID, position, lookAt))
                {
                    // FUBAR ALERT: this needs to be "Home position set." so the viewer saves a home-screenshot.
                    _Dialog.SendAlertToUser(remoteClient, "Home position set.");
                }
                else
                {
                    _Dialog.SendAlertToUser(remoteClient, "Set Home request failed.");
                }
            }
            else
                _Dialog.SendAlertToUser(remoteClient, "You are not allowed to set your home location in this parcel.");
        }

        protected void RegisterCommands()
        {
            ICommands commands = MainConsole.Instance.Commands;

            commands.AddCommand(
                "Land", false, "land clear",
                "land clear",
                "Clear all the parcels from the region.",
                "Command will ask for confirmation before proceeding.",
                HandleClearCommand);

            commands.AddCommand(
                "Land", false, "land show",
                "land show [<local-land-id>]",
                "Show information about the parcels on the region.",
                "If no local land ID is given, then summary information about all the parcels is shown.\n"
                    + "If a local land ID is given then full information about that parcel is shown.",
                HandleShowCommand);
        }

        protected void HandleClearCommand(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == _scene))
                return;

            string response = MainConsole.Instance.Prompt(
                string.Format(
                    "Are you sure that you want to clear all land parcels from {0} (y or n)", _scene.Name),
                "n");

            if (response.ToLower() == "y")
            {
                Clear(true);
                MainConsole.Instance.Output("Cleared all parcels from {0}", _scene.Name);
            }
            else
            {
                MainConsole.Instance.Output("Aborting clear of all parcels from {0}", _scene.Name);
            }
        }

        protected void HandleShowCommand(string module, string[] args)
        {
            if (!(MainConsole.Instance.ConsoleScene == null || MainConsole.Instance.ConsoleScene == _scene))
                return;

            StringBuilder report = new StringBuilder();

            if (args.Length <= 2)
            {
                AppendParcelsSummaryReport(report);
            }
            else
            {
                int landLocalId;

                if (!ConsoleUtil.TryParseConsoleInt(MainConsole.Instance, args[2], out landLocalId))
                    return;

                ILandObject lo = null;

                lock (_landList)
                {
                    if (!_landList.TryGetValue(landLocalId, out lo))
                    {
                        MainConsole.Instance.Output("No parcel found with local ID {0}", landLocalId);
                        return;
                    }
                }

                AppendParcelReport(report, lo);
            }

            MainConsole.Instance.Output(report.ToString());
        }

        private void AppendParcelsSummaryReport(StringBuilder report)
        {
            report.AppendFormat("Land information for {0}\n", _scene.Name);

            ConsoleDisplayTable cdt = new ConsoleDisplayTable();
            cdt.AddColumn("Parcel Name", ConsoleDisplayUtil.ParcelNameSize);
            cdt.AddColumn("ID", 3);
            cdt.AddColumn("Area", 6);
            cdt.AddColumn("Starts", ConsoleDisplayUtil.VectorSize);
            cdt.AddColumn("Ends", ConsoleDisplayUtil.VectorSize);
            cdt.AddColumn("Owner", ConsoleDisplayUtil.UserNameSize);
            cdt.AddColumn("fakeID", 38);

            lock (_landList)
            {
                foreach (ILandObject lo in _landList.Values)
                {
                    LandData ld = lo.LandData;
                    string ownerName;
                    if (ld.IsGroupOwned)
                    {
                        GroupRecord rec = _groupManager.GetGroupRecord(ld.GroupID);
                        ownerName = rec != null ? rec.GroupName : "Unknown Group";
                    }
                    else
                    {
                        ownerName = _userManager.GetUserName(ld.OwnerID);
                    }
                    cdt.AddRow(
                        ld.Name, ld.LocalID, ld.Area, lo.StartPoint, lo.EndPoint, ownerName, lo.FakeID);
                }
            }

            report.Append(cdt.ToString());
        }

        private void AppendParcelReport(StringBuilder report, ILandObject lo)
        {
            LandData ld = lo.LandData;

            ConsoleDisplayList cdl = new ConsoleDisplayList();
            cdl.AddRow("Parcel name", ld.Name);
            cdl.AddRow("Local ID", ld.LocalID);
            cdl.AddRow("Fake ID", ld.FakeID);
            cdl.AddRow("Description", ld.Description);
            cdl.AddRow("Snapshot ID", ld.SnapshotID);
            cdl.AddRow("Area", ld.Area);
            cdl.AddRow("AABB Min", ld.AABBMin);
            cdl.AddRow("AABB Max", ld.AABBMax);
            string ownerName;
            if (ld.IsGroupOwned)
            {
                GroupRecord rec = _groupManager.GetGroupRecord(ld.GroupID);
                ownerName = rec != null ? rec.GroupName : "Unknown Group";
            }
            else
            {
                ownerName = _userManager.GetUserName(ld.OwnerID);
            }
            cdl.AddRow("Owner", ownerName);
            cdl.AddRow("Is group owned?", ld.IsGroupOwned);
            cdl.AddRow("GroupID", ld.GroupID);

            cdl.AddRow("Status", ld.Status);
            cdl.AddRow("Flags", (ParcelFlags)ld.Flags);

            cdl.AddRow("Landing Type", (LandingType)ld.LandingType);
            cdl.AddRow("User Location", ld.UserLocation);
            cdl.AddRow("User look at", ld.UserLookAt);

            cdl.AddRow("Other clean time", ld.OtherCleanTime);

            cdl.AddRow("Max Prims", lo.GetParcelMaxPrimCount());
            cdl.AddRow("Simwide Max Prims (owner)", lo.GetSimulatorMaxPrimCount());
            IPrimCounts pc = lo.PrimCounts;
            cdl.AddRow("Owner Prims", pc.Owner);
            cdl.AddRow("Group Prims", pc.Group);
            cdl.AddRow("Other Prims", pc.Others);
            cdl.AddRow("Selected Prims", pc.Selected);
            cdl.AddRow("Total Prims", pc.Total);
            cdl.AddRow("SimWide Prims (owner)", pc.Simulator);

            cdl.AddRow("Music URL", ld.MusicURL);
            cdl.AddRow("Obscure Music", ld.ObscureMusic);

            cdl.AddRow("Media ID", ld.MediaID);
            cdl.AddRow("Media Autoscale", Convert.ToBoolean(ld.MediaAutoScale));
            cdl.AddRow("Media URL", ld.MediaURL);
            cdl.AddRow("Media Type", ld.MediaType);
            cdl.AddRow("Media Description", ld.MediaDescription);
            cdl.AddRow("Media Width", ld.MediaWidth);
            cdl.AddRow("Media Height", ld.MediaHeight);
            cdl.AddRow("Media Loop", ld.MediaLoop);
            cdl.AddRow("Obscure Media", ld.ObscureMedia);

            cdl.AddRow("Parcel Category", ld.Category);

            cdl.AddRow("Claim Date", ld.ClaimDate);
            cdl.AddRow("Claim Price", ld.ClaimPrice);
            cdl.AddRow("Pass Hours", ld.PassHours);
            cdl.AddRow("Pass Price", ld.PassPrice);

            cdl.AddRow("Auction ID", ld.AuctionID);
            cdl.AddRow("Authorized Buyer ID", ld.AuthBuyerID);
            cdl.AddRow("Sale Price", ld.SalePrice);

            cdl.AddToStringBuilder(report);
        }
    }
}
