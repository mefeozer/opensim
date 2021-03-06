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
using OpenSim.Framework.Monitoring;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Connectors.Hypergrid;
using OpenSim.Services.Interfaces;

using OpenMetaverse;
using log4net;
using Nini.Config;
using Mono.Addins;

using GridRegion = OpenSim.Services.Interfaces.GridRegion;


namespace OpenSim.Region.CoreModules.Framework.EntityTransfer
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HGEntityTransferModule")]
    public class HGEntityTransferModule
        : EntityTransferModule, INonSharedRegionModule, IEntityTransferModule, IUserAgentVerificationModule
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private int _levelHGTeleport = 0;

        private GatekeeperServiceConnector _GatekeeperConnector;
        private IUserAgentService _UAS;

        protected bool _RestrictAppearanceAbroad;
        protected string _AccountName;
        protected List<AvatarAppearance> _ExportedAppearances;
        protected List<AvatarAttachment> _Attachs;

        protected List<AvatarAppearance> ExportedAppearance
        {
            get
            {
                if (_ExportedAppearances != null)
                    return _ExportedAppearances;

                _ExportedAppearances = new List<AvatarAppearance>();
                _Attachs = new List<AvatarAttachment>();

                string[] names = _AccountName.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string name in names)
                {
                    string[] parts = name.Trim().Split();
                    if (parts.Length != 2)
                    {
                        _log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Wrong user account name format {0}. Specify 'First Last'", name);
                        return null;
                    }
                    UserAccount account = Scene.UserAccountService.GetUserAccount(UUID.Zero, parts[0], parts[1]);
                    if (account == null)
                    {
                        _log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Unknown account {0}", _AccountName);
                        return null;
                    }
                    AvatarAppearance a = Scene.AvatarService.GetAppearance(account.PrincipalID);
                    if (a != null)
                        _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Successfully retrieved appearance for {0}", name);

                    foreach (AvatarAttachment att in a.GetAttachments())
                    {
                        InventoryItemBase item = Scene.InventoryService.GetItem(account.PrincipalID, att.ItemID);
                        if (item != null)
                            a.SetAttachment(att.AttachPoint, att.ItemID, item.AssetID);
                        else
                            _log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Unable to retrieve item {0} from inventory {1}", att.ItemID, name);
                    }

                    _ExportedAppearances.Add(a);
                    _Attachs.AddRange(a.GetAttachments());
                }

                return _ExportedAppearances;
            }
        }

        /// <summary>
        /// Used for processing analysis of incoming attachments in a controlled fashion.
        /// </summary>
        private JobEngine _incomingSceneObjectEngine;

        #region ISharedRegionModule

        public override string Name => "HGEntityTransferModule";

        public override void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];

            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("EntityTransferModule", "");
                if (name == Name)
                {
                    IConfig transferConfig = source.Configs["EntityTransfer"];
                    if (transferConfig != null)
                    {
                        _levelHGTeleport = transferConfig.GetInt("LevelHGTeleport", 0);

                        _RestrictAppearanceAbroad = transferConfig.GetBoolean("RestrictAppearanceAbroad", false);
                        if (_RestrictAppearanceAbroad)
                        {
                            _AccountName = transferConfig.GetString("AccountForAppearance", string.Empty);
                            if (string.IsNullOrEmpty(_AccountName))
                                _log.WarnFormat("[HG ENTITY TRANSFER MODULE]: RestrictAppearanceAbroad is on, but no account has been given for avatar appearance!");
                        }
                    }

                    InitialiseCommon(source);
                    _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: {0} enabled.", Name);
                }
            }
        }

        public override void AddRegion(Scene scene)
        {
            base.AddRegion(scene);

            if (_Enabled)
            {
                scene.RegisterModuleInterface<IUserAgentVerificationModule>(this);
                //scene.EventManager.OnIncomingSceneObject += OnIncomingSceneObject;

                _incomingSceneObjectEngine
                    = new JobEngine(
                        string.Format("HG Incoming Scene Object Engine ({0})", scene.Name),
                        "HG INCOMING SCENE OBJECT ENGINE", 30000);

                StatsManager.RegisterStat(
                    new Stat(
                        "HGIncomingAttachmentsWaiting",
                        "Number of incoming attachments waiting for processing.",
                        "",
                        "",
                        "entitytransfer",
                        Name,
                        StatType.Pull,
                        MeasuresOfInterest.None,
                        stat => stat.Value = _incomingSceneObjectEngine.JobsWaiting,
                        StatVerbosity.Debug));

                _incomingSceneObjectEngine.Start();
            }
        }

        public override void RegionLoaded(Scene scene)
        {
            base.RegionLoaded(scene);

            if (_Enabled)
            {
                _GatekeeperConnector = new GatekeeperServiceConnector(scene.AssetService);
                _UAS = scene.RequestModuleInterface<IUserAgentService>();
                if (_UAS == null)
                    _UAS = new UserAgentServiceConnector(_thisGridInfo.HomeURL);

            }
        }

        public override void RemoveRegion(Scene scene)
        {
            base.RemoveRegion(scene);

            if (_Enabled)
            {
                scene.UnregisterModuleInterface<IUserAgentVerificationModule>(this);
                _incomingSceneObjectEngine.Stop();
            }
        }

        #endregion

        #region HG overrides of IEntityTransferModule

        protected override GridRegion GetFinalDestination(GridRegion region, UUID agentID, string agentHomeURI, out string message)
        {
            int flags = Scene.GridService.GetRegionFlags(Scene.RegionInfo.ScopeID, region.RegionID);
            _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: region {0} flags: {1}", region.RegionName, flags);
            message = null;

            if ((flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
            {
                _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Destination region is hyperlink");
                GridRegion real_destination = _GatekeeperConnector.GetHyperlinkRegion(region, region.RegionID, agentID, agentHomeURI, out message);
                if (real_destination != null)
                    _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: GetFinalDestination: ServerURI={0}", real_destination.ServerURI);
                else
                    _log.WarnFormat("[HG ENTITY TRANSFER MODULE]: GetHyperlinkRegion of region {0} from Gatekeeper {1} failed: {2}", region.RegionID, region.ServerURI, message);
                return real_destination;
            }

            return region;
        }

        protected override bool NeedsClosing(GridRegion reg, bool OutViewRange)
        {
            if (OutViewRange)
                return true;

            int flags = Scene.GridService.GetRegionFlags(Scene.RegionInfo.ScopeID, reg.RegionID);
            if (flags == -1 || (flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
                return true;

            return false;
        }

        protected override void AgentHasMovedAway(ScenePresence sp, bool logout)
        {
            base.AgentHasMovedAway(sp, logout);
            if (logout)
            {
                // Log them out of this grid
                Scene.PresenceService.LogoutAgent(sp.ControllingClient.SessionId);
                string userId = Scene.UserManagementModule.GetUserUUI(sp.UUID);
                Scene.GridUserService.LoggedOut(userId, UUID.Zero, Scene.RegionInfo.RegionID, sp.AbsolutePosition, sp.Lookat);
            }
        }

        protected override bool CreateAgent(ScenePresence sp, GridRegion reg, GridRegion finalDestination, AgentCircuitData agentCircuit, uint teleportFlags, EntityTransferContext ctx, out string reason, out bool logout)
        {
            _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: CreateAgent {0} {1}", reg.ServerURI, finalDestination.ServerURI);
            reason = string.Empty;
            logout = false;
            int flags = Scene.GridService.GetRegionFlags(Scene.RegionInfo.ScopeID, reg.RegionID);
            if (flags == -1 /* no region in DB */ || (flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
            {
                // this user is going to another grid
                // for local users, check if HyperGrid teleport is allowed, based on user level
                if (Scene.UserManagementModule.IsLocalGridUser(sp.UUID) && sp.GodController.UserLevel < _levelHGTeleport)
                {
                    _log.WarnFormat("[HG ENTITY TRANSFER MODULE]: Unable to HG teleport agent due to insufficient UserLevel.");
                    reason = "Hypergrid teleport not allowed";
                    return false;
                }

                if (agentCircuit.ServiceURLs.ContainsKey("HomeURI"))
                {
                    string userAgentDriver = agentCircuit.ServiceURLs["HomeURI"].ToString();
                    IUserAgentService connector;

                    if (_thisGridInfo.IsLocalHome(userAgentDriver) == 1 && _UAS != null)
                        connector = _UAS;
                    else
                        connector = new UserAgentServiceConnector(userAgentDriver);

                    GridRegion source = new GridRegion(Scene.RegionInfo)
                    {
                        RawServerURI = _thisGridInfo.GateKeeperURL
                    };

                    bool success = connector.LoginAgentToGrid(source, agentCircuit, reg, finalDestination, false, out reason);
                    logout = success; // flag for later logout from this grid; this is an HG TP

                    if (success)
                        Scene.EventManager.TriggerTeleportStart(sp.ControllingClient, reg, finalDestination, teleportFlags, logout);

                    return success;
                }
                else
                {
                    _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent does not have a HomeURI address");
                    return false;
                }
            }

            return base.CreateAgent(sp, reg, finalDestination, agentCircuit, teleportFlags, ctx, out reason, out logout);
        }

        public override void TriggerTeleportHome(UUID id, IClientAPI client)
        {
            TeleportHome(id, client);
        }

        protected override bool ValidateGenericConditions(ScenePresence sp, GridRegion reg, GridRegion finalDestination, uint teleportFlags, out string reason)
        {
            reason = "Please wear your grid's allowed appearance before teleporting to another grid";
            if (!_RestrictAppearanceAbroad)
                return true;

            // The rest is only needed for controlling appearance

            int flags = Scene.GridService.GetRegionFlags(Scene.RegionInfo.ScopeID, reg.RegionID);
            if (flags == -1 /* no region in DB */ || (flags & (int)OpenSim.Framework.RegionFlags.Hyperlink) != 0)
            {
                // this user is going to another grid
                if (Scene.UserManagementModule.IsLocalGridUser(sp.UUID))
                {
                    _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: RestrictAppearanceAbroad is ON. Checking generic appearance");

                    // Check wearables
                    for (int i = 0; i < sp.Appearance.Wearables.Length ; i++)
                    {
                        for (int j = 0; j < sp.Appearance.Wearables[i].Count; j++)
                        {
                            if (sp.Appearance.Wearables[i] == null)
                                continue;

                            bool found = false;
                            foreach (AvatarAppearance a in ExportedAppearance)
                                if (i < a.Wearables.Length && a.Wearables[i] != null)
                                {
                                    found = true;
                                    break;
                                }

                            if (!found)
                            {
                               _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Wearable not allowed to go outside {0}", i);
                               return false;
                            }

                            found = false;
                            foreach (AvatarAppearance a in ExportedAppearance)
                                if (i < a.Wearables.Length && sp.Appearance.Wearables[i][j].AssetID == a.Wearables[i][j].AssetID)
                                {
                                    found = true;
                                    break;
                                }

                            if (!found)
                            {
                                _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Wearable not allowed to go outside {0}", i);
                                return false;
                            }
                        }
                    }

                    // Check attachments
                    foreach (AvatarAttachment att in sp.Appearance.GetAttachments())
                    {
                        bool found = false;
                        foreach (AvatarAttachment att2 in _Attachs)
                        {
                            if (att2.AssetID == att.AssetID)
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Attachment not allowed to go outside {0}", att.AttachPoint);
                            return false;
                        }
                    }
                }
            }

            reason = string.Empty;
            return true;
        }


        //protected override bool UpdateAgent(GridRegion reg, GridRegion finalDestination, AgentData agentData, ScenePresence sp)
        //{
        //    int flags = Scene.GridService.GetRegionFlags(Scene.RegionInfo.ScopeID, reg.RegionID);
        //    if (flags == -1 /* no region in DB */ || (flags & (int)OpenSim.Data.RegionFlags.Hyperlink) != 0)
        //    {
        //        // this user is going to another grid
        //        if (_RestrictAppearanceAbroad && Scene.UserManagementModule.IsLocalGridUser(agentData.AgentID))
        //        {
        //            // We need to strip the agent off its appearance
        //            _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: RestrictAppearanceAbroad is ON. Sending generic appearance");

        //            // Delete existing npc attachments
        //            Scene.AttachmentsModule.DeleteAttachmentsFromScene(sp, false);

        //            // XXX: We can't just use IAvatarFactoryModule.SetAppearance() yet since it doesn't transfer attachments
        //            AvatarAppearance newAppearance = new AvatarAppearance(ExportedAppearance, true);
        //            sp.Appearance = newAppearance;

        //            // Rez needed npc attachments
        //            Scene.AttachmentsModule.RezAttachments(sp);


        //            IAvatarFactoryModule module = Scene.RequestModuleInterface<IAvatarFactoryModule>();
        //            //module.SendAppearance(sp.UUID);
        //            module.RequestRebake(sp, false);

        //            Scene.AttachmentsModule.CopyAttachments(sp, agentData);
        //            agentData.Appearance = sp.Appearance;
        //        }
        //    }

        //    foreach (AvatarAttachment a in agentData.Appearance.GetAttachments())
        //        _log.DebugFormat("[XXX]: {0}-{1}", a.ItemID, a.AssetID);


        //    return base.UpdateAgent(reg, finalDestination, agentData, sp);
        //}


        public override bool TeleportHome(UUID id, IClientAPI client)
        {
            // Let's find out if this is a foreign user or a local user
            IUserManagement uMan = Scene.RequestModuleInterface<IUserManagement>();
            if (uMan != null && uMan.IsLocalGridUser(id))
            {
                // local grid user
                return base.TeleportHome(id, client);
            }

            bool notsame = false;
            if (client == null)
            {
                _log.DebugFormat(
                    "[HG ENTITY TRANSFER MODULE]: Request to teleport {0} home", id);
            }
            else
            {
                if (id == client.AgentId)
                {
                    _log.DebugFormat(
                        "[HG ENTITY TRANSFER MODULE]: Request to teleport {0} {1} home", client.Name, id);
                }
                else
                {
                    notsame = true;
                    _log.DebugFormat(
                        "[HG ENTITY TRANSFER MODULE]: Request to teleport {0} home by {1} {2}", id, client.Name, client.AgentId);
                }
            }

            ScenePresence sp = ((Scene)client.Scene).GetScenePresence(id);
            if (sp == null || sp.IsDeleted || sp.IsChildAgent || sp.ControllingClient == null || !sp.ControllingClient.IsActive)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent not found in the scene");
                _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent not found in the scene");
                return false;
            }

            IClientAPI targetClient = sp.ControllingClient;

            if (sp.IsInTransit)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent already processing a teleport");
                targetClient.SendTeleportFailed("Already processing a teleport");
                _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent still in teleport");
                return false;
            }

            // Foreign user wants to go home
            //
            AgentCircuitData aCircuit = sp.Scene.AuthenticateHandler.GetAgentCircuitData(targetClient.CircuitCode);
            if (aCircuit == null)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent information not found");
                targetClient.SendTeleportFailed("Home information not found");
                _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Unable to locate agent's gateway information");
                return false;
            }
            if (!aCircuit.ServiceURLs.ContainsKey("HomeURI"))
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent home not set");
                targetClient.SendTeleportFailed("Home not set");
                _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent home not set");
                return false;
            }

            string homeURI = aCircuit.ServiceURLs["HomeURI"].ToString();

            IUserAgentService userAgentService = new UserAgentServiceConnector(homeURI);
            Vector3 position = Vector3.UnitY, lookAt = Vector3.UnitY;

            GridRegion finalDestination = null;
            try
            {
                finalDestination = userAgentService.GetHomeRegion(id, out position, out lookAt);
            }
            catch (Exception e)
            {
                _log.Debug("[HG ENTITY TRANSFER MODULE]: GetHomeRegion call failed ", e);
            }

            if (finalDestination == null)
            {
                if (notsame)
                    client.SendAlertMessage("TeleportHome: Agent Home region not found");
                targetClient.SendTeleportFailed("Home region not found");
                _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Agent's home region not found");
                return false;
            }

            GridRegion homeGatekeeper = MakeGateKeeperRegion(homeURI);

            _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: teleporting user {0} {1} home to {2} via {3}:{4}",
                aCircuit.firstname, aCircuit.lastname, finalDestination.RegionName, homeGatekeeper.ServerURI, homeGatekeeper.RegionName);

            DoTeleport(sp, homeGatekeeper, finalDestination, position, lookAt, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaHome));
            return true;
        }

        /// <summary>
        /// Tries to teleport agent to landmark.
        /// </summary>
        /// <param name="remoteClient"></param>
        /// <param name="regionHandle"></param>
        /// <param name="position"></param>
        public override void RequestTeleportLandmark(IClientAPI remoteClient, AssetLandmark lm)
        {
            _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Teleporting agent via landmark to {0} region {1} position {2}",
                string.IsNullOrEmpty(lm.Gatekeeper) ? "local" : lm.Gatekeeper, lm.RegionID, lm.Position);

            if (string.IsNullOrEmpty(lm.Gatekeeper))
            {
                base.RequestTeleportLandmark(remoteClient, lm);
                return;
            }

            GridRegion info = Scene.GridService.GetRegionByUUID(UUID.Zero, lm.RegionID);

            // Local region?
            if (info != null)
            {
                Scene.RequestTeleportLocation(
                    remoteClient, info.RegionHandle, lm.Position,
                    Vector3.Zero, (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
            }
            else
            {
                // Foreign region
                GatekeeperServiceConnector gConn = new GatekeeperServiceConnector();
                GridRegion gatekeeper = MakeGateKeeperRegion(lm.Gatekeeper);
                if (gatekeeper == null)
                {
                    remoteClient.SendTeleportFailed("Could not parse landmark destiny URI");
                    return;
                }

                string homeURI = Scene.GetAgentHomeURI(remoteClient.AgentId);

                GridRegion finalDestination = gConn.GetHyperlinkRegion(gatekeeper, new UUID(lm.RegionID), remoteClient.AgentId, homeURI, out string message);

                if (finalDestination != null)
                {
                    ScenePresence sp = Scene.GetScenePresence(remoteClient.AgentId);

                    if (sp != null)
                    {
                        if (message != null)
                            sp.ControllingClient.SendAgentAlertMessage(message, true);

                        // Validate assorted conditions
                        string reason = string.Empty;
                        if (!ValidateGenericConditions(sp, gatekeeper, finalDestination, 0, out reason))
                        {
                            sp.ControllingClient.SendTeleportFailed(reason);
                            return;
                        }

                        DoTeleport(
                            sp, gatekeeper, finalDestination, lm.Position, Vector3.UnitX,
                            (uint)(Constants.TeleportFlags.SetLastToTarget | Constants.TeleportFlags.ViaLandmark));
                    }
                }
                else
                {
                    remoteClient.SendTeleportFailed(message);
                }

            }
        }

        private void RemoveIncomingSceneObjectJobs(string commonIdToRemove)
        {
            List<JobEngine.Job> jobsToReinsert = new List<JobEngine.Job>();
            int jobsRemoved = 0;

            JobEngine.Job job;
            while ((job = _incomingSceneObjectEngine.RemoveNextJob()) != null)
            {
                if (job.CommonId != commonIdToRemove)
                    jobsToReinsert.Add(job);
                else
                    jobsRemoved++;
            }

            _log.DebugFormat(
                "[HG ENTITY TRANSFER]: Removing {0} jobs with common ID {1} and reinserting {2} other jobs",
                jobsRemoved, commonIdToRemove, jobsToReinsert.Count);

            if (jobsToReinsert.Count > 0)
            {
                foreach (JobEngine.Job jobToReinsert in jobsToReinsert)
                    _incomingSceneObjectEngine.QueueJob(jobToReinsert);
            }
        }

        public override bool HandleIncomingSceneObject(SceneObjectGroup so, Vector3 newPosition)
        {
            UUID OwnerID = so.OwnerID;
            if (Scene.RegionInfo.EstateSettings.IsBanned(OwnerID))
            {
                _log.DebugFormat(
                    "[HG TRANSFER MODULE]: Denied prim crossing of {0} {1} into {2} for banned avatar {3}",
                    so.Name, so.UUID, Scene.Name, so.OwnerID);

                return false;
            }

            // FIXME: We must make it so that we can use SOG.IsAttachment here.  At the moment it is always null!
            if (!so.IsAttachmentCheckFull())
                return base.HandleIncomingSceneObject(so, newPosition);

            // Equally, we can't use so.AttachedAvatar here.
            if (OwnerID == UUID.Zero || Scene.UserManagementModule.IsLocalGridUser(OwnerID))
                return base.HandleIncomingSceneObject(so, newPosition);

            // foreign user
            AgentCircuitData aCircuit = Scene.AuthenticateHandler.GetAgentCircuitData(OwnerID);
            if (aCircuit != null)
            {
                if ((aCircuit.teleportFlags & (uint)Constants.TeleportFlags.ViaHGLogin) == 0)
                {
                    // We have already pulled the necessary attachments from the source grid.
                    base.HandleIncomingSceneObject(so, newPosition);
                }
                else
                {
                    if (aCircuit.ServiceURLs != null && aCircuit.ServiceURLs.ContainsKey("AssetServerURI"))
                    {
                        SceneObjectGroup defso = so;
                        _incomingSceneObjectEngine.QueueJob(
                            string.Format("HG UUID Gather for attachment {0} for {1}", defso.Name, aCircuit.Name),
                            () =>
                            {
                                string url = aCircuit.ServiceURLs["AssetServerURI"].ToString();
    //                            _log.DebugFormat(
    //                                "[HG ENTITY TRANSFER MODULE]: Incoming attachment {0} for HG user {1} with asset service {2}",
    //                                so.Name, so.AttachedAvatar, url);

                                IDictionary<UUID, sbyte> ids = new Dictionary<UUID, sbyte>();
                                HGUuidGatherer uuidGatherer = new HGUuidGatherer(Scene.AssetService, url, ids);
                                uuidGatherer.AddForInspection(defso);

                                while (!uuidGatherer.Complete)
                                {
                                    int tickStart = Util.EnvironmentTickCount();
                                    uuidGatherer.GatherNext();

    //                                _log.DebugFormat(
    //                                    "[HG ENTITY TRANSFER]: Gathered attachment asset uuid {0} for object {1} for HG user {2} took {3} ms with asset service {4}",
    //                                    nextUuid, so.Name, so.OwnerID, Util.EnvironmentTickCountSubtract(tickStart), url);

                                    int ticksElapsed = Util.EnvironmentTickCountSubtract(tickStart);

                                    if (ticksElapsed > 30000)
                                    {
                                        _log.WarnFormat(
                                            "[HG ENTITY TRANSFER]: Removing incoming scene object jobs for HG user {0} as gather of {1} from {2} took {3} ms to respond (> {4} ms)",
                                            so.OwnerID, so.Name, url, ticksElapsed, 30000);

                                        RemoveIncomingSceneObjectJobs(OwnerID.ToString());
                                        return;
                                    }
                                }

    //                            _log.DebugFormat(
    //                                "[HG ENTITY TRANSFER]: Fetching {0} assets for attachment {1} for HG user {2} with asset service {3}",
    //                                ids.Count, so.Name, so.OwnerID, url);

                                foreach (UUID id in ids.Keys)
                                {
                                    int tickStart = Util.EnvironmentTickCount();

                                    uuidGatherer.FetchAsset(id);

                                    int ticksElapsed = Util.EnvironmentTickCountSubtract(tickStart);

                                    if (ticksElapsed > 30000)
                                    {
                                        _log.WarnFormat(
                                            "[HG ENTITY TRANSFER]: Removing incoming scene object jobs for HG user {0} as fetch of {1} from {2} took {3} ms to respond (> {4} ms)",
                                            so.OwnerID, id, url, ticksElapsed, 30000);

                                        RemoveIncomingSceneObjectJobs(OwnerID.ToString());
                                        return;
                                    }
                                }

                                base.HandleIncomingSceneObject(defso, newPosition);

                                defso = null;
                                aCircuit = null;
                                uuidGatherer = null;

    //                            _log.DebugFormat(
    //                                "[HG ENTITY TRANSFER MODULE]: Completed incoming attachment {0} for HG user {1} with asset server {2}",
    //                                so.Name, so.OwnerID, url);
                            },
                            OwnerID.ToString());
                    }
                }
            }

            return true;
        }

        #endregion

        #region IUserAgentVerificationModule

        public bool VerifyClient(AgentCircuitData aCircuit, string token)
        {
            if (aCircuit.ServiceURLs.ContainsKey("HomeURI"))
            {
                string url = aCircuit.ServiceURLs["HomeURI"].ToString();
                IUserAgentService security = new UserAgentServiceConnector(url);
                return security.VerifyClient(aCircuit.SessionID, token);
            }
            else
            {
                _log.DebugFormat(
                    "[HG ENTITY TRANSFER MODULE]: Agent {0} {1} does not have a HomeURI OH NO!",
                    aCircuit.firstname, aCircuit.lastname);
            }

            return false;
        }

        public override void OnConnectionClosed(IClientAPI obj)
        {
            if (obj.SceneAgent.IsChildAgent)
                return;

            // Let's find out if this is a foreign user or a local user
            IUserManagement uMan = Scene.RequestModuleInterface<IUserManagement>();
//          UserAccount account = Scene.UserAccountService.GetUserAccount(Scene.RegionInfo.ScopeID, obj.AgentId);

            if (uMan != null && uMan.IsLocalGridUser(obj.AgentId))
            {
                // local grid user
                _UAS.LogoutAgent(obj.AgentId, obj.SessionId);
                return;
            }

            AgentCircuitData aCircuit = ((Scene)obj.Scene).AuthenticateHandler.GetAgentCircuitData(obj.CircuitCode);
            if (aCircuit != null && aCircuit.ServiceURLs != null && aCircuit.ServiceURLs.ContainsKey("HomeURI"))
            {
                string url = aCircuit.ServiceURLs["HomeURI"].ToString();
                IUserAgentService security = new UserAgentServiceConnector(url);
                security.LogoutAgent(obj.AgentId, obj.SessionId);
                //_log.DebugFormat("[HG ENTITY TRANSFER MODULE]: Sent logout call to UserAgentService @ {0}", url);
            }
            else
            {
                    _log.DebugFormat("[HG ENTITY TRANSFER MODULE]: HomeURI not found for agent {0} logout", obj.AgentId);
            }
            base.OnConnectionClosed(obj);
        }

        #endregion

        private GridRegion MakeGateKeeperRegion(string wantedURI)
        {
            if(!Uri.TryCreate(wantedURI, UriKind.Absolute, out Uri uri))
                return null;

            return new GridRegion()
            {
                ExternalHostName = uri.Host,
                HttpPort = (uint)uri.Port,
                ServerURI = wantedURI,  //uri.AbsoluteUri for some reason default ports are needed
                RegionName = string.Empty,
                InternalEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("0.0.0.0"), 0),
                RegionFlags = OpenSim.Framework.RegionFlags.Hyperlink
            };
        }
    }
}
