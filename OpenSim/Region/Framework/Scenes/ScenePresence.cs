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
using System.Threading;
using OpenMetaverse;
using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Client;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes.Animation;
using OpenSim.Region.PhysicsModules.SharedBase;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;
using OpenSim.Services.Interfaces;
using TeleportFlags = OpenSim.Framework.Constants.TeleportFlags;

namespace OpenSim.Region.Framework.Scenes
{
    [Flags]
    enum ScriptControlled : uint
    {
        CONTROL_ZERO = 0,
        CONTROL_FWD = 1,
        CONTROL_BACK = 2,
        CONTROL_LEFT = 4,
        CONTROL_RIGHT = 8,
        CONTROL_UP = 16,
        CONTROL_DOWN = 32,
        CONTROL_ROT_LEFT = 256,
        CONTROL_ROT_RIGHT = 512,
        CONTROL_LBUTTON = 268435456,
        CONTROL_ML_LBUTTON = 1073741824
    }

    struct ScriptControllers
    {
        public UUID objectID;
        public UUID itemID;
        public ScriptControlled ignoreControls;
        public ScriptControlled eventControls;
    }

    public delegate void SendCoarseLocationsMethod(UUID scene, ScenePresence presence, List<Vector3> coarseLocations, List<UUID> avatarUUIDs);

    public class ScenePresence : EntityBase, IScenePresence, IDisposable
    {
        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        //        ~ScenePresence()
        //        {
        //            _log.DebugFormat("[SCENE PRESENCE]: Destructor called on {0}", Name);
        //        }


        public int EnvironmentVersion = -1;
        private ViewerEnvironment _environment = null;
        public ViewerEnvironment Environment
        {
            get
            {
                return _environment;
            }
            set
            {
                _environment = value;
                if (value == null)
                    EnvironmentVersion = -1;
                else
                {
                    if(EnvironmentVersion <= 0)
                        EnvironmentVersion = 0x7000000 | Util.RandomClass.Next();
                    else
                        ++EnvironmentVersion;
                    _environment.version = EnvironmentVersion;
                }
            }
        }

        public void TriggerScenePresenceUpdated()
        {
            if (_scene != null)
                _scene.EventManager.TriggerScenePresenceUpdated(this);
        }

        public bool IsNPC { get; private set; }

        // simple yes or no isGOD from god level >= 200
        // should only be set by GodController
        // we have two to suport legacy behaviour
        // IsViewerUIGod was controlled by viewer in older versions
        // IsGod may now be also controled by viewer acording to options
        public bool IsViewerUIGod { get; set; }
        public bool IsGod { get; set; }

        private bool _gotRegionHandShake = false;

        private PresenceType _presenceType;
        public PresenceType PresenceType
        {
            get {return _presenceType;}
            private set
            {
                _presenceType = value;
                IsNPC = _presenceType == PresenceType.Npc;
            }
        }

        private readonly ScenePresenceStateMachine _stateMachine;

        /// <summary>
        /// The current state of this presence.  Governs only the existence lifecycle.  See ScenePresenceStateMachine
        /// for more details.
        /// </summary>
        public ScenePresenceState LifecycleState
        {
            get
            {
                return _stateMachine.GetState();
            }

            set
            {
                _stateMachine.SetState(value);
            }
        }

        /// <summary>
        /// This exists to prevent race conditions between two CompleteMovement threads if the simulator is slow and
        /// the viewer fires these in quick succession.
        /// </summary>
        /// <remarks>
        /// TODO: The child -> agent transition should be folded into LifecycleState and the CompleteMovement
        /// regulation done there.
        /// </remarks>
        private readonly object _completeMovementLock = new object();

//        private static readonly byte[] DEFAULT_TEXTURE = AvatarAppearance.GetDefaultTexture().GetBytes();
        private static readonly Array DIR_CONTROL_FLAGS = Enum.GetValues(typeof(Dir_ControlFlags));
        private static readonly Vector3 HEAD_ADJUSTMENT = new Vector3(0f, 0f, 0.3f);

        /// <summary>
        /// Experimentally determined "fudge factor" to make sit-target positions
        /// the same as in SecondLife. Fudge factor was tested for 36 different
        /// test cases including prims of type box, sphere, cylinder, and torus,
        /// with varying parameters for sit target location, prim size, prim
        /// rotation, prim cut, prim twist, prim taper, and prim shear. See mantis
        /// issue #1716
        /// </summary>
        public static readonly Vector3 SIT_TARGET_ADJUSTMENT = new Vector3(0.0f, 0.0f, 0.4f);
        public bool  LegacySitOffsets = true;

        /// <summary>
        /// Movement updates for agents in neighboring regions are sent directly to clients.
        /// This value only affects how often agent positions are sent to neighbor regions
        /// for things such as distance-based update prioritization
        /// this are the square of real distances
        /// </summary>
        public static readonly float MOVEMENT = .25f;
        public static readonly float SIGNIFICANT_MOVEMENT = 16.0f;
        public static readonly float CHILDUPDATES_MOVEMENT = 100.0f;
        public static readonly float CHILDAGENTSCHECK_MOVEMENT = 1024f; // 32m
        public static readonly float CHILDUPDATES_TIME = 2000f; // min time between child updates (ms)

        private UUID _previusParcelUUID = UUID.Zero;
        private UUID _currentParcelUUID = UUID.Zero;
        private bool _previusParcelHide = false;
        private bool _currentParcelHide = false;
        private readonly object parcelLock = new object();
        public double ParcelDwellTickMS;

        public UUID currentParcelUUID
        {
            get { return _currentParcelUUID; }
            set
            {
                lock (parcelLock)
                {
                    bool oldhide = _currentParcelHide;
                    bool checksame = true;
                    if (value != _currentParcelUUID)
                    {
                        ParcelDwellTickMS = Util.GetTimeStampMS();
                        _previusParcelHide = _currentParcelHide;
                        _previusParcelUUID = _currentParcelUUID;
                        checksame = false;
                    }
                    _currentParcelUUID = value;
                    _currentParcelHide = false;

                    ILandObject land = _scene.LandChannel.GetLandObject(AbsolutePosition.X, AbsolutePosition.Y);
                    if (land != null)
                        _currentParcelHide = !land.LandData.SeeAVs;

                    if (_previusParcelUUID != UUID.Zero || checksame)
                        ParcelCrossCheck(_currentParcelUUID, _previusParcelUUID, _currentParcelHide, _previusParcelHide, oldhide,checksame);
                }
            }
        }

        public void sitSOGmoved()
        {
/*
            if (IsDeleted || !IsSatOnObject)
                //what me?  nahh
                return;
            if (IsInTransit)
                return;

            ILandObject land = _scene.LandChannel.GetLandObject(AbsolutePosition.X, AbsolutePosition.Y);
            if (land == null)
                return; //??
            UUID parcelID = land.LandData.GlobalID;
            if (_currentParcelUUID != parcelID)
                currentParcelUUID = parcelID;
*/
        }

        public bool ParcelAllowThisAvatarSounds
        {
            get
            {
                try
                {
                    lock (parcelLock)
                    {
                        ILandObject land = _scene.LandChannel.GetLandObject(AbsolutePosition.X, AbsolutePosition.Y);
                        if (land == null)
                            return true;
                        if (land.LandData.AnyAVSounds)
                            return true;
                        if (!land.LandData.GroupAVSounds)
                            return false;
                        return ControllingClient.IsGroupMember(land.LandData.GroupID);
                    }
                }
                catch
                {
                    return true;
                }
            }
        }

        public bool ParcelHideThisAvatar
        {
            get
            {
                return _currentParcelHide;
            }
        }

        /// <value>
        /// The animator for this avatar
        /// </value>
        public ScenePresenceAnimator Animator { get; private set; }

        /// <value>
        /// Server Side Animation Override
        /// </value>
        public MovementAnimationOverrides Overrides { get; }
        public string sitAnimation = "SIT";
        /// <summary>
        /// Attachments recorded on this avatar.
        /// </summary>
        /// <remarks>
        /// TODO: For some reason, we effectively have a list both here and in Appearance.  Need to work out if this is
        /// necessary.
        /// </remarks>
        private readonly List<SceneObjectGroup> _attachments = new List<SceneObjectGroup>();

        public object AttachmentsSyncLock { get; }

        private readonly Dictionary<UUID, ScriptControllers> scriptedcontrols = new Dictionary<UUID, ScriptControllers>();
        private ScriptControlled IgnoredControls = ScriptControlled.CONTROL_ZERO;
        private ScriptControlled LastCommands = ScriptControlled.CONTROL_ZERO;
        private bool MouseDown = false;
        public Vector3 lastKnownAllowedPosition;
        public bool sentMessageAboutRestrictedParcelFlyingDown;

        public Vector4 CollisionPlane = Vector4.UnitW;

        public Vector4 _lastCollisionPlane = Vector4.UnitW;
        private byte _lastState;
        private Vector3 _lastPosition;
        private Quaternion _lastRotation;
        private Vector3 _lastVelocity;
        private Vector3 _lastSize = new Vector3(0.45f,0.6f,1.9f);
        private int NeedInitialData = 1;

        private readonly int _userFlags;
        public int UserFlags
        {
            get { return _userFlags; }
        }

        // Flying
        public bool Flying
        {
            get { return PhysicsActor != null && PhysicsActor.Flying; }
            set
            {
                if(PhysicsActor != null)
                    PhysicsActor.Flying = value;
            }
        }

         public bool IsColliding
        {
            get { return PhysicsActor != null && PhysicsActor.IsColliding; }
            // We would expect setting IsColliding to be private but it's used by a hack in Scene
            set { PhysicsActor.IsColliding = value; }
        }

        private List<uint> _lastColliders = new List<uint>();
        private bool _lastLandCollide;

        private TeleportFlags _teleportFlags;
        public TeleportFlags TeleportFlags
        {
            get { return _teleportFlags; }
            set { _teleportFlags = value; }
        }

        private uint _requestedSitTargetID;
        private UUID _requestedSitTargetUUID;

        /// <summary>
        /// Are we sitting on the ground?
        /// </summary>
        public bool SitGround { get; private set; }

        private SendCoarseLocationsMethod _sendCoarseLocationsMethod;

        //private Vector3 _requestedSitOffset = new Vector3();

        private Vector3 _LastFinitePos;

        private float _sitAvatarHeight = 2.0f;

        private bool _childUpdatesBusy = false;
        private int _lastChildUpdatesTime;
        private int _lastChildAgentUpdateGodLevel;
        private float _lastChildAgentUpdateDrawDistance;
        private float _lastRegionsDrawDistance;
        private Vector3 _lastChildAgentUpdatePosition;
        private Vector3 _lastChildAgentCheckPosition;
        // private Vector3 _lastChildAgentUpdateCamPosition;

        private Vector3 _lastCameraRayCastCam;
        private Vector3 _lastCameraRayCastPos;

        private float _FOV = 1.04f;

        private const int LAND_VELOCITYMAG_MAX = 12;

        private const float FLY_ROLL_MAX_RADIANS = 1.1f;

        private const float FLY_ROLL_RADIANS_PER_UPDATE = 0.06f;
        private const float FLY_ROLL_RESET_RADIANS_PER_UPDATE = 0.02f;

        private float _health = 100f;
        private float _healRate = 1f;
        private float _healRatePerFrame = 0.05f;

        private readonly Vector3[] Dir_Vectors = new Vector3[12];

        protected int  _reprioritizationLastTime;
        protected bool _reprioritizationBusy;
        protected Vector3 _reprioritizationLastPosition;
        protected float _reprioritizationLastDrawDistance;

        private Quaternion _headrotation = Quaternion.Identity;

        //PauPaw:Proper PID Controler for autopilot************

        private bool _movingToTarget;
        public bool MovingToTarget
        {
            get {return _movingToTarget;}
            private set {_movingToTarget = value; }
        }

        private Vector3 _moveToPositionTarget;
        public Vector3 MoveToPositionTarget
        {
            get {return _moveToPositionTarget;}
            private set {_moveToPositionTarget = value; }
        }

        private float _moveToSpeed;
        public float MoveToSpeed
        {
            get {return _moveToSpeed;}
            private set {_moveToSpeed = value; }
        }

        private double _delayedStop = -1.0;

        /// <summary>
        /// Controls whether an avatar automatically moving to a target will land when it gets there (if flying).
        /// </summary>
        public bool LandAtTarget { get; private set; }

        private bool CameraConstraintActive;

        private readonly object _collisionEventLock = new object();

        private int _movementAnimationUpdateCounter = 0;

        public Vector3 PrevSitOffset { get; set; }

        protected AvatarAppearance _appearance;

        public AvatarAppearance Appearance
        {
            get { return _appearance; }
            set
            {
                _appearance = value;
//                _log.DebugFormat("[SCENE PRESENCE]: Set appearance for {0} to {1}", Name, value);
            }
        }


        /// <summary>
        /// Copy of the script states while the agent is in transit. This state may
        /// need to be placed back in case of transfer fail.
        /// </summary>
        public List<string> InTransitScriptStates
        {
            get { return _InTransitScriptStates; }
            private set { _InTransitScriptStates = value; }
        }
        private List<string> _InTransitScriptStates = new List<string>();

        /// <summary>
        /// Implemented Control Flags
        /// </summary>
        private enum Dir_ControlFlags:uint
        {
            DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
            DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
            DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
            DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
            DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
            DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
            DIR_CONTROL_FLAG_FORWARD_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS,
            DIR_CONTROL_FLAG_BACKWARD_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG,
            DIR_CONTROL_FLAG_LEFT_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS,
            DIR_CONTROL_FLAG_RIGHT_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG,
            DIR_CONTROL_FLAG_UP_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS,
            DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG
        }

        /// <summary>
        /// Position at which a significant movement was made
        /// </summary>
        private Vector3 posLastSignificantMove;
        private Vector3 posLastMove;

        #region For teleports and crossings callbacks

        /// <summary>
        /// the destination simulator sends ReleaseAgent to this address, for very long range tps, HG.
        /// </summary>
        private string _callbackURI; // to remove with v1 support
        private string _newCallbackURI;

        /// <summary>
        /// Records the region from which this presence originated, if not from login.
        /// </summary>
        /// <remarks>
        /// Also acts as a signal in the teleport V2 process to release UpdateAgent after a viewer has triggered
        /// CompleteMovement and made the previous child agent a root agent.
        /// </remarks>
        private UUID _originRegionID;

        /// <summary>
        /// This object is used as a lock before accessing _originRegionID to make sure that every thread is seeing
        /// the very latest value and not using some cached version.  Cannot make _originRegionID itself volatite as
        /// it is a value type.
        /// </summary>
        private readonly object _originRegionIDAccessLock = new object();


        private AutoResetEvent _updateAgentReceivedAfterTransferEvent = new AutoResetEvent(false);

        /// <summary>
        /// Used by the entity transfer module to signal when the presence should not be closed because a subsequent
        /// teleport is reusing the connection.
        /// </summary>
        /// <remarks>May be refactored or move somewhere else soon.</remarks>
        public bool DoNotCloseAfterTeleport { get; set; }

        #endregion

        /// <value>
        /// Script engines present in the scene
        /// </value>
        private IScriptModule[] _scriptEngines;

        private enum LandingPointBehavior
        {
            OS = 1,
            SL = 2
        }

        private readonly LandingPointBehavior _LandingPointBehavior = LandingPointBehavior.OS;

        #region Properties

        /// <summary>
        /// Physical scene representation of this Avatar.
        /// </summary>
        
        PhysicsActor _physActor;
        public PhysicsActor PhysicsActor
        {
            get
            {
                return _physActor;
            }
            private set
            {
                _physActor = value;
            }
        }

        /// <summary>
        /// Record user movement inputs.
        /// </summary>
        public uint MovementFlag { get; private set; }

        /// <summary>
        /// Is the agent stop control flag currently active?
        /// </summary>
        public bool AgentControlStopActive { get; private set; }

        private bool _invulnerable = true;

        public bool Invulnerable
        {
            set
            {
                _invulnerable = value;
                if(value && Health != 100.0f)
                    Health = 100.0f;
            }
            get { return _invulnerable; }
        }

        public GodController GodController { get; }

        private ulong _rootRegionHandle;
        private Vector3 _rootRegionPosition = new Vector3();

        public ulong RegionHandle
        {
            get { return _rootRegionHandle; }
            private set
            {
                _rootRegionHandle = value;
                // position rounded to lower multiple of 256m
                _rootRegionPosition.X = (_rootRegionHandle >> 32) & 0xffffff00;
                _rootRegionPosition.Y = _rootRegionHandle & 0xffffff00;
            }
        }

        #region Client Camera

        /// <summary>
        /// Position of agent's camera in world (region cordinates)
        /// </summary>
//        protected Vector3 _lastCameraPosition;

        private Vector4 _lastCameraCollisionPlane = new Vector4(0f, 0f, 0f, 1);
        private bool _doingCamRayCast = false;

        public Vector3 CameraPosition { get; set; }
        public Quaternion CameraRotation { get; private set; }

        // Use these three vectors to figure out what the agent is looking at
        // Convert it to a Matrix and/or Quaternion
        //
        public Vector3 CameraAtAxis { get; set; }
        public Vector3 CameraLeftAxis { get; set; }
        public Vector3 CameraUpAxis { get; set; }

        public Vector3 Lookat
        {
            get
            {
                Vector3 a = new Vector3(CameraAtAxis.X, CameraAtAxis.Y, 0);
                a.Normalize();
                return a;
            }
        }
        #endregion

        public string Firstname { get; }
        public string Lastname { get; }

        public bool _haveGroupInformation;
        public bool _gotCrossUpdate;
        public byte _crossingFlags;

        public string Grouptitle
        {
            get { return _groupTitle; }
            set { _groupTitle = value; }
        }
        private string _groupTitle;

        // Agent's Draw distance.
        private float _drawDistance = 255f;
        public float DrawDistance
        {
            get
            {
                return _drawDistance;
            }
            set
            {
                _drawDistance = Util.Clamp(value, 32f, _scene.MaxDrawDistance);
            }
        }

        public float RegionViewDistance
        {
            get
            {
                return Util.Clamp(_drawDistance + 64f, _scene.MinRegionViewDistance, _scene.MaxRegionViewDistance);
            }
         }

        public bool AllowMovement { get; set; }

        private bool _setAlwaysRun;
        public bool SetAlwaysRun
        {
            get
            {
                if (PhysicsActor != null)
                {
                    return PhysicsActor.SetAlwaysRun;
                }
                else
                {
                    return _setAlwaysRun;
                }
            }
            set
            {
                _setAlwaysRun = value;
                if (PhysicsActor != null)
                {
                    PhysicsActor.SetAlwaysRun = value;
                }
            }
        }

        public byte State { get; set; }

        private AgentManager.ControlFlags _AgentControlFlags;

        public uint AgentControlFlags
        {
            get { return (uint)_AgentControlFlags; }
            set { _AgentControlFlags = (AgentManager.ControlFlags)value; }
        }

        public IClientAPI ControllingClient { get; set; }

        // dead end do not use
        public IClientCore ClientView
        {
            get { return (IClientCore)ControllingClient; }
        }

        //public UUID COF { get; set; }

//        public Vector3 ParentPosition { get; set; }

        /// <summary>
        /// Position of this avatar relative to the region the avatar is in
        /// </summary>
        public override Vector3 AbsolutePosition
        {
            get
            {
                if (PhysicsActor != null)
                {
                    _pos = PhysicsActor.Position;

//                    _log.DebugFormat(
//                        "[SCENE PRESENCE]: Set position of {0} in {1} to {2} via getting AbsolutePosition!",
//                        Name, Scene.Name, _pos);
                }
                else
                {
//                    _log.DebugFormat("[SCENE PRESENCE]: Fetching abs pos where PhysicsActor == null and parent part {0} for {1}", Name, Scene.Name);
                    // Obtain the correct position of a seated avatar.
                    // In addition to providing the correct position while
                    // the avatar is seated, this value will also
                    // be used as the location to unsit to.
                    //
                    // If ParentID is not 0, assume we are a seated avatar
                    // and we should return the position based on the sittarget
                    // offset and rotation of the prim we are seated on.
                    //
                    // Generally, _pos will contain the position of the avatar
                    // in the sim unless the avatar is on a sit target. While
                    // on a sit target, _pos will contain the desired offset
                    // without the parent rotation applied.
                    if (ParentPart != null)
                    {
                        SceneObjectPart rootPart = ParentPart.ParentGroup.RootPart;
                        //                    if (sitPart != null)
                        //                        return sitPart.AbsolutePosition + (_pos * sitPart.GetWorldRotation());
                        if (rootPart != null)
                            return rootPart.AbsolutePosition + (_pos * rootPart.GetWorldRotation());
                    }
                }

                return _pos;
            }
            set
            {
//                _log.DebugFormat("[SCENE PRESENCE]: Setting position of {0} to {1} in {2}", Name, value, Scene.Name);
//                Util.PrintCallStack();

                if (PhysicsActor != null)
                {
                    try
                    {
                        PhysicsActor.Position = value;
                    }
                    catch (Exception e)
                    {
                        _log.Error("[SCENE PRESENCE]: ABSOLUTE POSITION " + e.Message);
                    }
                }

                // Don't update while sitting.  The PhysicsActor above is null whilst sitting.
                if (ParentID == 0)
                    _pos = value;

                //_log.DebugFormat(
                //    "[ENTITY BASE]: In {0} set AbsolutePosition of {1} to {2}",
                //    Scene.RegionInfo.RegionName, Name, _pos);
                TriggerScenePresenceUpdated();
            }
        }

        /// <summary>
        /// If sitting, returns the offset position from the prim the avatar is sitting on.
        /// Otherwise, returns absolute position in the scene.
        /// </summary>
        public Vector3 OffsetPosition
        {
            get { return _pos; }
            // Don't remove setter. It's not currently used in core but
            // upcoming Avination code needs it.
            set
            {
                // There is no offset position when not seated
                if (ParentID == 0)
                    return;

                _pos = value;
                TriggerScenePresenceUpdated();
            }
        }

        /// <summary>
        /// Current velocity of the avatar.
        /// </summary>
        public override Vector3 Velocity
        {
            get
            {
                if (PhysicsActor != null)
                {
                    _velocity = PhysicsActor.Velocity;

//                    _log.DebugFormat(
//                        "[SCENE PRESENCE]: Set velocity {0} for {1} in {2} via getting Velocity!",
//                        _velocity, Name, Scene.RegionInfo.RegionName);
                }

                return _velocity;
            }

            set
            {
                if (PhysicsActor != null)
                {
                    try
                    {
                        PhysicsActor.TargetVelocity = value;
                    }
                    catch (Exception e)
                    {
                        _log.Error("[SCENE PRESENCE]: VELOCITY " + e.Message);
                    }
                }

                _velocity = value;

//                _log.DebugFormat(
//                    "[SCENE PRESENCE]: In {0} set velocity of {1} to {2}",
//                    Scene.RegionInfo.RegionName, Name, _velocity);
            }
        }

        // requested Velocity for physics engines avatar motors
        // only makes sense if there is a physical rep
        public Vector3 TargetVelocity
        {
            get
            {
                if (PhysicsActor != null)
                    return PhysicsActor.TargetVelocity;
                else
                    return Vector3.Zero;
            }

            set
            {
                if (PhysicsActor != null)
                {
                    try
                    {
                        PhysicsActor.TargetVelocity = value;
                    }
                    catch (Exception e)
                    {
                        _log.Error("[SCENE PRESENCE]: TARGETVELOCITY " + e.Message);
                    }
                }
            }
        }

        private Quaternion _bodyRot = Quaternion.Identity;

        /// <summary>
        /// The rotation of the avatar.
        /// </summary>
        /// <remarks>
        /// If the avatar is not sitting, this is with respect to the world
        /// If the avatar is sitting, this is a with respect to the part that it's sitting upon (a local rotation).
        /// If you always want the world rotation, use GetWorldRotation()
        /// </remarks>
        public Quaternion Rotation
        {
            get
            {
                return _bodyRot;
            }

            set
            {
                _bodyRot = value;

                if (PhysicsActor != null)
                {
                    try
                    {
                        PhysicsActor.Orientation = _bodyRot;
                    }
                    catch (Exception e)
                    {
                        _log.Error("[SCENE PRESENCE]: Orientation " + e.Message);
                    }
                }
//                _log.DebugFormat("[SCENE PRESENCE]: Body rot for {0} set to {1}", Name, _bodyRot);
            }
        }

        // Used for limited viewer 'fake' user rotations.
        private Vector3 _AngularVelocity = Vector3.Zero;

        public Vector3 AngularVelocity
        {
            get { return _AngularVelocity; }
        }

        public bool IsChildAgent { get; set; }

        /// <summary>
        /// If the avatar is sitting, the local ID of the prim that it's sitting on.  If not sitting then zero.
        /// </summary>
        public uint ParentID { get; set; }

        public UUID ParentUUID
        {
            get { return _parentUUID; }
            set { _parentUUID = value; }
        }
        private UUID _parentUUID = UUID.Zero;

        /// <summary>
        /// Are we sitting on an object?
        /// </summary>
        /// <remarks>A more readable way of testing presence sit status than ParentID == 0</remarks>
        public bool IsSatOnObject { get { return ParentID != 0; } }
        public bool IsSitting { get {return SitGround || IsSatOnObject; }}
        /// <summary>
        /// If the avatar is sitting, the prim that it's sitting on.  If not sitting then null.
        /// </summary>
        /// <remarks>
        /// If you use this property then you must take a reference since another thread could set it to null.
        /// </remarks>
        public SceneObjectPart ParentPart { get; set; }

        public float Health
        {
            get { return _health; }
            set { _health = value; }
        }

        public float HealRate
        {
            get { return _healRate; }
            set
            {
                if(value > 100.0f)
                    _healRate = 100.0f;
                else if (value <= 0.0)
                    _healRate = 0.0f;
                else
                    _healRate = value;

                if(Scene != null)
                    _healRatePerFrame = _healRate * Scene.FrameTime;
                else
                    _healRatePerFrame = 0.05f;
            }
        }


        /// <summary>
        /// Gets the world rotation of this presence.
        /// </summary>
        /// <remarks>
        /// Unlike Rotation, this returns the world rotation no matter whether the avatar is sitting on a prim or not.
        /// </remarks>
        /// <returns></returns>
        public Quaternion GetWorldRotation()
        {
            if (IsSatOnObject)
            {
                SceneObjectPart sitPart = ParentPart;

                if (sitPart != null)
                    return sitPart.GetWorldRotation() * Rotation;
            }

            return Rotation;
        }

        /// <summary>
        /// Get velocity relative to the world.
        /// </summary>
        public Vector3 GetWorldVelocity()
        {
            SceneObjectPart sitPart = ParentPart;

            if (sitPart != null)
                return sitPart.ParentGroup.Velocity;

            return Velocity;
        }

        public void AdjustKnownSeeds()
        {
            Dictionary<ulong, string> seeds;

            if (Scene.CapsModule != null)
                seeds = Scene.CapsModule.GetChildrenSeeds(UUID);
            else
                seeds = new Dictionary<ulong, string>();
            KnownRegions = seeds;
        }

        public void DumpKnownRegions()
        {
            _log.Info("================ KnownRegions "+Scene.RegionInfo.RegionName+" ================");
            foreach (KeyValuePair<ulong, string> kvp in KnownRegions)
            {
                Util.RegionHandleToRegionLoc(kvp.Key, out uint x, out uint y);
                _log.Info(" >> "+x+", "+y+": "+kvp.Value);
            }
        }

        private bool _mouseLook;
        private bool _leftButtonDown;

        private bool _inTransit;

        /// <summary>
        /// This signals whether the presence is in transit between neighbouring regions.
        /// </summary>
        /// <remarks>
        /// It is not set when the presence is teleporting or logging in/out directly to a region.
        /// </remarks>
        public bool IsInTransit
        {
            get { return _inTransit; }
            set {
                if(value)
                {
                    if (Flying)
                        _AgentControlFlags |= AgentManager.ControlFlags.AGENT_CONTROL_FLY;
                    else
                        _AgentControlFlags &= ~AgentManager.ControlFlags.AGENT_CONTROL_FLY;
                }
                _inTransit = value;
            }
        }
        // this is is only valid if IsInTransit is true
        // only false on HG tps
        // used work arounf viewers asking source region about destination user
        public bool IsInLocalTransit {get; set; }


        /// <summary>
        /// Modifier for agent movement if we get an AGENT_CONTROL_STOP whilst walking or running
        /// </summary>
        /// <remarks>
        /// AGENT_CONTRL_STOP comes about if user holds down space key on viewers.
        /// </remarks>
        private const float AgentControlStopSlowVel = 0.2f;
        // velocities
        public const float AgentControlNudgeVel = 1.0f; // setting this diferent from normal as no effect currently
        public const float AgentControlNormalVel = 1.0f;

        // old normal speed was tuned to match sl normal plus Fast modifiers
        // so we need to rescale it
        private float _speedModifier = 1.0f;

        public float SpeedModifier
        {
            get { return _speedModifier; }
            set { _speedModifier = value; }
        }

        private bool _forceFly;

        public bool ForceFly
        {
            get { return _forceFly; }
            set { _forceFly = value; }
        }

        private bool _flyDisabled;

        public bool FlyDisabled
        {
            get { return _flyDisabled; }
            set { _flyDisabled = value; }
        }

        public string Viewer
        {
            get { return Util.GetViewerName(_scene.AuthenticateHandler.GetAgentCircuitData(ControllingClient.CircuitCode)); }
        }

        #endregion

        #region Constructor(s)

        public ScenePresence(IClientAPI client, Scene world, AvatarAppearance appearance, PresenceType type)
        {
            _scene = world;
            AttachmentsSyncLock = new object();
            AllowMovement = true;
            IsChildAgent = true;
            _sendCoarseLocationsMethod = SendCoarseLocationsDefault;
            Animator = new ScenePresenceAnimator(this);
            Overrides = new MovementAnimationOverrides();
            PresenceType = type;
            _drawDistance = client.StartFar;
            if(_drawDistance > 32)
            {
                if(_drawDistance > world.MaxDrawDistance)
                    _drawDistance = world.MaxDrawDistance;
            }
            else
                _drawDistance = world.DefaultDrawDistance;
            RegionHandle = world.RegionInfo.RegionHandle;
            ControllingClient = client;
            Firstname = ControllingClient.FirstName;
            Lastname = ControllingClient.LastName;
            Name = string.Format("{0} {1}", Firstname, Lastname);
            _uuid = client.AgentId;
            LocalId = _scene.AllocateLocalId();
            LegacySitOffsets = _scene.LegacySitOffsets;
            IsInLocalTransit = true;

            UserAccount account = _scene.UserAccountService.GetUserAccount(_scene.RegionInfo.ScopeID, _uuid);
            if (account != null)
                _userFlags = account.UserFlags;
            else
                _userFlags = 0;

            int userlevel = 0;
            if (account != null)
                userlevel = account.UserLevel;

            GodController = new GodController(world, this, userlevel);

 //           IGroupsModule gm = _scene.RequestModuleInterface<IGroupsModule>();
 //           if (gm != null)
 //              Grouptitle = gm.GetGroupTitle(_uuid);

            _scriptEngines = _scene.RequestModuleInterfaces<IScriptModule>();

            AbsolutePosition = posLastMove = posLastSignificantMove = CameraPosition =
               _reprioritizationLastPosition = ControllingClient.StartPos;

            _reprioritizationLastDrawDistance = -1000;

            // disable updates workjobs for now
            _childUpdatesBusy = true;
            _reprioritizationBusy = true;

            AdjustKnownSeeds();

            RegisterToClientEvents();
            SetDirectionVectors();

            Appearance = appearance;

            _stateMachine = new ScenePresenceStateMachine(this);

            HealRate = 0.5f;

            IConfig sconfig = _scene.Config.Configs["EntityTransfer"];
            if (sconfig != null)
            {
                string lpb = sconfig.GetString("LandingPointBehavior", "LandingPointBehavior_OS");
                if (lpb == "LandingPointBehavior_SL")
                    _LandingPointBehavior = LandingPointBehavior.SL;
            }

            _bandwidth = 100000;
            _lastBandwithTime = Util.GetTimeStamp() + 0.1;
            IConfig cconfig = _scene.Config.Configs["ClientStack.LindenCaps"];
            if (cconfig != null)
            {
                _capbandwidth = cconfig.GetInt("Cap_AssetThrottle", _capbandwidth);
                if(_capbandwidth > 0)
                {
                    _bandwidth = _capbandwidth;
                    if(_bandwidth < 50000)
                        _bandwidth = 50000;
                }
            }
            _bandwidthBurst = _bandwidth / 5;
            ControllingClient.RefreshGroupMembership();
        }

        ~ScenePresence()
        {
            Dispose(false);
        }

        private bool disposed = false;
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!disposed)
            {
                disposed = true;
                IsDeleted = true;
                if (_updateAgentReceivedAfterTransferEvent != null)
                {
                    _updateAgentReceivedAfterTransferEvent.Dispose();
                    _updateAgentReceivedAfterTransferEvent = null;
                }

                RemoveFromPhysicalScene();

                // Clear known regions
                KnownRegions = null;

                _scene.EventManager.OnRegionHeartbeatEnd -= RegionHeartbeatEnd;
                RemoveClientEvents();

                Animator = null;
                Appearance = null;
                /* temporary out: timming issues
                if(_attachments != null)
                {
                    foreach(SceneObjectGroup sog in _attachments)
                        sog.Dispose();
                    _attachments = null;
                }
                */
                scriptedcontrols.Clear();
                ControllingClient = null;
            }
        }

        private float lastHealthSent = 0;

        private void RegionHeartbeatEnd(Scene scene)
        {
            if (IsChildAgent)
                return;

            _movementAnimationUpdateCounter ++;
            if (_movementAnimationUpdateCounter >= 2)
            {
                _movementAnimationUpdateCounter = 0;
                if (Animator != null)
                {
                    // If the parentID == 0 we are not sitting
                    // if !SitGournd then we are not sitting on the ground
                    // Fairly straightforward, now here comes the twist
                    // if ParentUUID is NOT UUID.Zero, we are looking to
                    // be sat on an object that isn't there yet. Should
                    // be treated as if sat.
                    if(ParentID == 0 && !SitGround && ParentUUID == UUID.Zero) // skip it if sitting
                        Animator.UpdateMovementAnimations();
                }
                else
                {
//                    _scene.EventManager.OnRegionHeartbeatEnd -= RegionHeartbeatEnd;
                }
            }

            if(_healRatePerFrame != 0f && Health != 100.0f)
            {
                float last = Health;
                Health += _healRatePerFrame;
                if(Health > 100.0f)
                {
                    Health = 100.0f;
                    lastHealthSent = Health;
                    ControllingClient.SendHealth(Health);
                }
                else if(Math.Abs(Health - lastHealthSent) > 1.0)
                {
                    lastHealthSent = Health;
                    ControllingClient.SendHealth(Health);
                }
            }
        }

        public void RegisterToClientEvents()
        {
            ControllingClient.OnCompleteMovementToRegion += CompleteMovement;
            ControllingClient.OnAgentUpdate += HandleAgentUpdate;
            ControllingClient.OnAgentCameraUpdate += HandleAgentCamerasUpdate;
            ControllingClient.OnAgentRequestSit += HandleAgentRequestSit;
            ControllingClient.OnAgentSit += HandleAgentSit;
            ControllingClient.OnSetAlwaysRun += HandleSetAlwaysRun;
            ControllingClient.OnStartAnim += HandleStartAnim;
            ControllingClient.OnStopAnim += HandleStopAnim;
            ControllingClient.OnChangeAnim += avnHandleChangeAnim;
            ControllingClient.OnForceReleaseControls += HandleForceReleaseControls;
            ControllingClient.OnAutoPilotGo += MoveToTargetHandle;
            ControllingClient.OnUpdateThrottles += RaiseUpdateThrottles;
            ControllingClient.OnRegionHandShakeReply += RegionHandShakeReply;

            // ControllingClient.OnAgentFOV += HandleAgentFOV;

            // ControllingClient.OnChildAgentStatus += new StatusChange(this.ChildStatusChange);
            // ControllingClient.OnStopMovement += new GenericCall2(this.StopMovement);
        }

        public void RemoveClientEvents()
        {
            ControllingClient.OnCompleteMovementToRegion -= CompleteMovement;
            ControllingClient.OnAgentUpdate -= HandleAgentUpdate;
            ControllingClient.OnAgentCameraUpdate -= HandleAgentCamerasUpdate;
            ControllingClient.OnAgentRequestSit -= HandleAgentRequestSit;
            ControllingClient.OnAgentSit -= HandleAgentSit;
            ControllingClient.OnSetAlwaysRun -= HandleSetAlwaysRun;
            ControllingClient.OnStartAnim -= HandleStartAnim;
            ControllingClient.OnStopAnim -= HandleStopAnim;
            ControllingClient.OnChangeAnim -= avnHandleChangeAnim;
            ControllingClient.OnForceReleaseControls -= HandleForceReleaseControls;
            ControllingClient.OnAutoPilotGo -= MoveToTargetHandle;
            ControllingClient.OnUpdateThrottles -= RaiseUpdateThrottles;
            ControllingClient.OnRegionHandShakeReply -= RegionHandShakeReply;

            // ControllingClient.OnAgentFOV += HandleAgentFOV;
        }

        private void SetDirectionVectors()
        {
            Dir_Vectors[0] = new Vector3(AgentControlNormalVel,0,0); //FORWARD
            Dir_Vectors[1] = new Vector3(-AgentControlNormalVel,0,0);; //BACK
            Dir_Vectors[2] = new Vector3(0,AgentControlNormalVel,0); //LEFT
            Dir_Vectors[3] = new Vector3(0,-AgentControlNormalVel,0); //RIGHT
            Dir_Vectors[4] = new Vector3(0,0,AgentControlNormalVel); //UP
            Dir_Vectors[5] = new Vector3(0,0,-AgentControlNormalVel); //DOWN
            Dir_Vectors[6] = new Vector3(AgentControlNudgeVel, 0f, 0f); //FORWARD_NUDGE
            Dir_Vectors[7] = new Vector3(-AgentControlNudgeVel, 0f, 0f);  //BACK_NUDGE
            Dir_Vectors[8] = new Vector3(0f, AgentControlNudgeVel, 0f);  //LEFT_NUDGE
            Dir_Vectors[9] = new Vector3(0f, -AgentControlNudgeVel, 0f);  //RIGHT_NUDGE
            Dir_Vectors[10] = new Vector3(0f, 0f, AgentControlNudgeVel); //UP_Nudge
            Dir_Vectors[11] = new Vector3(0f, 0f, -AgentControlNudgeVel); //DOWN_Nudge
        }
        #endregion

        #region Status Methods

        /// <summary>
        /// Turns a child agent into a root agent.
        /// </summary>
        /// <remarks>
        /// Child agents are logged into neighbouring sims largely to observe changes.  Root agents exist when the
        /// avatar is actual in the sim.  They can perform all actions.
        /// This change is made whenever an avatar enters a region, whether by crossing over from a neighbouring sim,
        /// teleporting in or on initial login.
        ///
        /// This method is on the critical path for transferring an avatar from one region to another.  Delay here
        /// delays that crossing.
        /// </remarks>

        // constants for physics position search
        const float PhysSearchHeight = 300f;
        const float PhysMinSkipGap = 20f;
        const float PhysSkipGapDelta = 30f;
        const int PhysNumberCollisions = 30;

        // only in use as part of completemovement
        // other uses need fix
        private bool MakeRootAgent(Vector3 pos, bool isFlying, ref Vector3 lookat)
        {
            //int ts = Util.EnvironmentTickCount();

            lock (_completeMovementLock)
            {
                if (!IsChildAgent)
                    return false;

                //_log.DebugFormat("[MakeRootAgent] enter lock: {0}ms", Util.EnvironmentTickCountSubtract(ts));
                //_log.DebugFormat("[SCENE]: known regions in {0}: {1}", Scene.RegionInfo.RegionName, KnownChildRegionHandles.Count);

                //            _log.InfoFormat(
                //                "[SCENE]: Upgrading child to root agent for {0} in {1}",
                //                Name, _scene.RegionInfo.RegionName);

                if (ParentUUID != UUID.Zero)
                {
                    _log.DebugFormat("[SCENE PRESENCE]: Sitting avatar back on prim {0}", ParentUUID);
                    SceneObjectPart part = _scene.GetSceneObjectPart(ParentUUID);
                    if (part == null)
                    {
                        _log.ErrorFormat("[SCENE PRESENCE]: Can't find prim {0} to sit on", ParentUUID);
                        ParentID = 0;
                        ParentPart = null;
                        PrevSitOffset = Vector3.Zero;
                        HandleForceReleaseControls(ControllingClient, UUID); // needs testing
                    }
                    else
                    {
                        part.AddSittingAvatar(this);
                        // if not actually on the target invalidate it
                        if(_gotCrossUpdate && (_crossingFlags & 0x04) == 0)
                                part.SitTargetAvatar = UUID.Zero;

                        ParentID = part.LocalId;
                        ParentPart = part;
                        _pos = PrevSitOffset;
                        pos = part.GetWorldPosition();
                        PhysicsActor partPhysActor = part.PhysActor;
                        if(partPhysActor != null)
                        {
                            partPhysActor.OnPhysicsRequestingCameraData -=
                                        physActor_OnPhysicsRequestingCameraData;
                            partPhysActor.OnPhysicsRequestingCameraData +=
                                        physActor_OnPhysicsRequestingCameraData;
                        }
                    }
                    ParentUUID = UUID.Zero;
                }

                IsChildAgent = false;
            }

            //_log.DebugFormat("[MakeRootAgent] out lock: {0}ms", Util.EnvironmentTickCountSubtract(ts));

            // Must reset this here so that a teleport to a region next to an existing region does not keep the flag
            // set and prevent the close of the connection on a subsequent re-teleport.
            // Should not be needed if we are not trying to tell this region to close
            //            DoNotCloseAfterTeleport = false;

            RegionHandle = _scene.RegionInfo.RegionHandle;

            _scene.EventManager.TriggerSetRootAgentScene(_uuid, _scene);
            //_log.DebugFormat("[MakeRootAgent] TriggerSetRootAgentScene: {0}ms", Util.EnvironmentTickCountSubtract(ts));

            if (ParentID == 0)
            {
                bool positionChanged = false;
                bool success = true;
                if (_LandingPointBehavior == LandingPointBehavior.OS)
                    success = CheckAndAdjustLandingPoint_OS(ref pos, ref lookat, ref positionChanged);
                else
                    success = CheckAndAdjustLandingPoint_SL(ref pos, ref lookat, ref positionChanged);

                if (!success)
                    _log.DebugFormat("[SCENE PRESENCE MakeRootAgent]: houston we have a problem.. {0} ({1} got banned)", Name, UUID);

                if (pos.X < 0f || pos.Y < 0f
                          || pos.X >= _scene.RegionInfo.RegionSizeX
                          || pos.Y >= _scene.RegionInfo.RegionSizeY)
                {
                    _log.WarnFormat(
                        "[SCENE PRESENCE]: MakeRootAgent() was given an illegal position of {0} for avatar {1}, {2}. Clamping",
                        pos, Name, UUID);

                    if (pos.X < 0f)
                        pos.X = 0.5f;
                    else if(pos.X >= _scene.RegionInfo.RegionSizeX)
                        pos.X = _scene.RegionInfo.RegionSizeX - 0.5f;
                    if (pos.Y < 0f)
                        pos.Y = 0.5f;
                    else if(pos.Y >= _scene.RegionInfo.RegionSizeY)
                        pos.Y = _scene.RegionInfo.RegionSizeY - 0.5f;
                }

                float groundHeight = _scene.GetGroundHeight(pos.X, pos.Y) + .01f;
                float physTestHeight;

                if(PhysSearchHeight < groundHeight + 100f)
                    physTestHeight = groundHeight + 100f;
                else
                    physTestHeight = PhysSearchHeight;

                float localAVHalfHeight = 0.8f;
                if (Appearance != null && Appearance.AvatarHeight > 0)
                    localAVHalfHeight = 0.5f * Appearance.AvatarHeight;

                groundHeight += localAVHalfHeight;
                if (groundHeight > pos.Z)
                    pos.Z = groundHeight;

                bool checkPhysics = !positionChanged &&
                        _scene.SupportsRayCastFiltered() &&
                        pos.Z < physTestHeight &&
                        ((_teleportFlags & (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)) ==
                            (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)
                        || (_teleportFlags & TeleportFlags.ViaLocation) != 0
                        || (_teleportFlags & TeleportFlags.ViaHGLogin) != 0);

                if(checkPhysics)
                {
                    // land check was done above
                    RayFilterFlags rayfilter = RayFilterFlags.BackFaceCull;
                        rayfilter |= RayFilterFlags.PrimsNonPhantomAgents;

                    int physcount = PhysNumberCollisions;

                    float dist = physTestHeight - groundHeight + localAVHalfHeight;

                    Vector3 direction = new Vector3(0f, 0f, -1f);
                    Vector3 RayStart = pos;
                    RayStart.Z = physTestHeight;

                    List<ContactResult> physresults =
                            (List<ContactResult>)_scene.RayCastFiltered(RayStart, direction, dist, physcount, rayfilter);
                    while (physresults != null && physresults.Count > 0)
                    {
                        float dest = physresults[0].Pos.Z;
                        if (dest - groundHeight > PhysMinSkipGap + PhysSkipGapDelta)
                            break;

                        if (physresults.Count > 1)
                        {
                            physresults.Sort(delegate(ContactResult a, ContactResult b)
                            {
                                return a.Depth.CompareTo(b.Depth);
                            });

                            int sel = 0;
                            int count = physresults.Count;
                            float curd = physresults[0].Depth;
                            float nextd = curd + PhysMinSkipGap;
                            float maxDepth = dist - pos.Z;
                            for(int i = 1; i < count; i++)
                            {
                                curd = physresults[i].Depth;
                                if(curd >= nextd)
                                {
                                    sel = i;
                                    if(curd >= maxDepth || curd >= nextd + PhysSkipGapDelta)
                                        break;
                                }
                                nextd = curd + PhysMinSkipGap;
                            }
                            dest = physresults[sel].Pos.Z;
                        }

                    dest += localAVHalfHeight;
                    if(dest > pos.Z)
                        pos.Z = dest;
                    break;
                    }
                }

                AbsolutePosition = pos;

//                _log.DebugFormat(
//                    "Set pos {0}, vel {1} in {1} to {2} from input position of {3} on MakeRootAgent",
//                    Name, Scene.Name, AbsolutePosition, pos);
//
                if (_teleportFlags == TeleportFlags.Default)
                {
                    Vector3 vel = Velocity;
                    AddToPhysicalScene(isFlying);
                    if (PhysicsActor != null)
                        PhysicsActor.SetMomentum(vel);
                }
                else
                {
                    AddToPhysicalScene(isFlying);

                    // reset camera to avatar pos
                    CameraPosition = pos;
                }

                if (ForceFly)
                {
                    Flying = true;
                }
                else if (FlyDisabled)
                {
                    Flying = false;
                }
            }

            //_log.DebugFormat("[MakeRootAgent] position and physical: {0}ms", Util.EnvironmentTickCountSubtract(ts));
            _scene.SwapRootAgentCount(false);

            // If we don't reset the movement flag here, an avatar that crosses to a neighbouring sim and returns will
            // stall on the border crossing since the existing child agent will still have the last movement
            // recorded, which stops the input from being processed.
            MovementFlag = 0;

            _scene.AuthenticateHandler.UpdateAgentChildStatus(ControllingClient.CircuitCode, false);

            _scene.EventManager.TriggerOnMakeRootAgent(this);
            //_log.DebugFormat("[MakeRootAgent] TriggerOnMakeRootAgent and done: {0}ms", Util.EnvironmentTickCountSubtract(ts));

            return true;
        }

        private void RestartAttachmentScripts()
        {
            // We need to restart scripts here so that they receive the correct changed events (CHANGED_TELEPORT
            // and CHANGED_REGION) when the attachments have been rezzed in the new region.  This cannot currently
            // be done in AttachmentsModule.CopyAttachments(AgentData ad, IScenePresence sp) itself since we are
            // not transporting the required data.
            //
            // We must take a copy of the attachments list here (rather than locking) to avoid a deadlock where a script in one of
            // the attachments may start processing an event (which locks ScriptInstance._Script) that then calls a method here
            // which needs to lock _attachments.  ResumeScripts() needs to take a ScriptInstance._Script lock to try to unset the Suspend status.
            //
            // FIXME: In theory, this deadlock should not arise since scripts should not be processing events until ResumeScripts().
            // But XEngine starts all scripts unsuspended.  Starting them suspended will not currently work because script rezzing
            // is placed in an asynchronous queue in XEngine and so the ResumeScripts() call will almost certainly execute before the
            // script is rezzed.  This means the ResumeScripts() does absolutely nothing when using XEngine.
            List<SceneObjectGroup> attachments = GetAttachments();

            _log.DebugFormat(
                "[SCENE PRESENCE]: Restarting scripts in {0} attachments for {1} in {2}", attachments.Count, Name, Scene.Name);

            // Resume scripts
            foreach (SceneObjectGroup sog in attachments)
            {
                sog.RootPart.ParentGroup.CreateScriptInstances(0, false, _scene.DefaultScriptEngine, GetStateSource());
                sog.ResumeScripts();
                sog.ScheduleGroupForFullUpdate();
            }
        }

        private static bool IsRealLogin(TeleportFlags teleportFlags)
        {
            return (teleportFlags & (TeleportFlags.ViaLogin | TeleportFlags.ViaHGLogin)) == TeleportFlags.ViaLogin;
        }

        /// <summary>
        /// Force viewers to show the avatar's current name.
        /// </summary>
        /// <remarks>
        /// The avatar name that is shown above the avatar in the viewers is sent in ObjectUpdate packets,
        /// and they get the name from the ScenePresence. Unfortunately, viewers have a bug (as of April 2014)
        /// where they ignore changes to the avatar name. However, tey don't ignore changes to the avatar's
        /// Group Title. So the following trick makes viewers update the avatar's name by briefly changing
        /// the group title (to "(Loading)"), and then restoring it.
        /// </remarks>
/*
        public void ForceViewersUpdateName()
        {
            _log.DebugFormat("[SCENE PRESENCE]: Forcing viewers to update the avatar name for " + Name);

            UseFakeGroupTitle = true;


            Util.FireAndForget(o =>
            {
                // Viewers only update the avatar name when idle. Therefore, we must wait long
                // enough for the viewer to show the fake name that we had set above, and only
                // then switch back to the true name. This delay was chosen because it has a high
                // chance of succeeding (we don't want to choose a value that's too low).
                Thread.Sleep(5000);

                UseFakeGroupTitle = false;
                SendAvatarDataToAllClients(false);
            }, null, "Scenepresence.ForceViewersUpdateName");
        }
*/
        public int GetStateSource()
        {
            return _teleportFlags == TeleportFlags.Default ? 2 : 5; // StateSource.PrimCrossing : StateSource.Teleporting
        }

        /// <summary>
        /// This turns a root agent into a child agent
        /// </summary>
        /// <remarks>
        /// when an agent departs this region for a neighbor, this gets called.
        ///
        /// It doesn't get called for a teleport.  Reason being, an agent that
        /// teleports out may not end up anywhere near this region
        /// </remarks>
        public void MakeChildAgent(ulong newRegionHandle)
        {
            _updateAgentReceivedAfterTransferEvent.Reset();
            _haveGroupInformation = false;
            _gotCrossUpdate = false;
            _crossingFlags = 0;
            _scene.EventManager.OnRegionHeartbeatEnd -= RegionHeartbeatEnd;

            RegionHandle = newRegionHandle;

            _log.DebugFormat("[SCENE PRESENCE]: Making {0} a child agent in {1} from root region {2}",
                Name, Scene.RegionInfo.RegionName, newRegionHandle);

            // Reset the _originRegionID as it has dual use as a flag to signal that the UpdateAgent() call orignating
            // from the source simulator has completed on a V2 teleport.
            lock (_originRegionIDAccessLock)
                _originRegionID = UUID.Zero;

            // Reset these so that teleporting in and walking out isn't seen
            // as teleporting back
            TeleportFlags = TeleportFlags.Default;

            MovementFlag = 0;

            // It looks like Animator is set to null somewhere, and MakeChild
            // is called after that. Probably in aborted teleports.
            if (Animator == null)
                Animator = new ScenePresenceAnimator(this);
            else
                Animator.ResetAnimations();

            Environment = null;


//            _log.DebugFormat(
//                 "[SCENE PRESENCE]: Downgrading root agent {0}, {1} to a child agent in {2}",
//                 Name, UUID, _scene.RegionInfo.RegionName);

            // Don't zero out the velocity since this can cause problems when an avatar is making a region crossing,
            // depending on the exact timing.  This shouldn't matter anyway since child agent positions are not updated.
            //Velocity = new Vector3(0, 0, 0);

            IsChildAgent = true;
            _scene.SwapRootAgentCount(true);
            RemoveFromPhysicalScene();
            ParentID = 0; // Child agents can't be sitting

// we dont have land information for child
            _previusParcelHide = false;
            _previusParcelUUID = UUID.Zero;
            _currentParcelHide = false;
            _currentParcelUUID = UUID.Zero;
 
            CollisionPlane = Vector4.UnitW;

            // we need to kill this on agents that do not see the new region
            _scene.ForEachRootScenePresence(delegate(ScenePresence p)
                {
                    if (!p.knowsNeighbourRegion(newRegionHandle))
                    {
                        SendKillTo(p);
                    }
                });
            _scene.AuthenticateHandler.UpdateAgentChildStatus(ControllingClient.CircuitCode, true);

            _scene.EventManager.TriggerOnMakeChildAgent(this);
        }

        /// <summary>
        /// Removes physics plugin scene representation of this agent if it exists.
        /// </summary>
        public void RemoveFromPhysicalScene()
        {
            PhysicsActor pa = Interlocked.Exchange(ref _physActor, null);
            if (pa != null)
            {
//                PhysicsActor.OnRequestTerseUpdate -= SendTerseUpdateToAllClients;

                pa.OnOutOfBounds -= OutOfBoundsCall;
                pa.OnCollisionUpdate -= PhysicsCollisionUpdate;
                pa.UnSubscribeEvents();
                _scene.PhysicsScene.RemoveAvatar(pa);
                pa = null;
            }
//            else
//            {
//                _log.ErrorFormat(
//                    "[SCENE PRESENCE]: Attempt to remove physics actor for {0} on {1} but this scene presence has no physics actor",
//                    Name, Scene.RegionInfo.RegionName);
//            }
        }

        public void Teleport(Vector3 pos)
        {
            TeleportWithMomentum(pos, Vector3.Zero);
        }

        public void TeleportWithMomentum(Vector3 pos, Vector3? v)
        {
            if(!CheckLocalTPLandingPoint(ref pos))
                    return;

            if (IsSitting)
                StandUp(false);

            bool isFlying = Flying;
            Vector3 vel = Velocity;
            RemoveFromPhysicalScene();

            AbsolutePosition = pos;
            AddToPhysicalScene(isFlying);
            if (PhysicsActor != null)
            {
                if (v.HasValue)
                    PhysicsActor.SetMomentum((Vector3)v);
                else
                    PhysicsActor.SetMomentum(vel);
            }

            SendTerseUpdateToAllClients();
        }

        public void TeleportOnEject(Vector3 pos)
        {
            if (IsSitting )
                StandUp(false);

            bool isFlying = Flying;
            RemoveFromPhysicalScene();

            AbsolutePosition = pos;

            AddToPhysicalScene(isFlying);
            SendTerseUpdateToAllClients();
        }

        public void LocalTeleport(Vector3 newpos, Vector3 newvel, Vector3 newlookat, int flags)
        {
            if (newpos.X <= 0)
            {
                newpos.X = 0.1f;
                if (newvel.X < 0)
                    newvel.X = 0;
            }
            else if (newpos.X >= Scene.RegionInfo.RegionSizeX)
            {
                newpos.X = Scene.RegionInfo.RegionSizeX - 0.1f;
                if (newvel.X > 0)
                    newvel.X = 0;
            }

            if (newpos.Y <= 0)
            {
                newpos.Y = 0.1f;
                if (newvel.Y < 0)
                    newvel.Y = 0;
            }
            else if (newpos.Y >= Scene.RegionInfo.RegionSizeY)
            {
                newpos.Y = Scene.RegionInfo.RegionSizeY - 0.1f;
                if (newvel.Y > 0)
                    newvel.Y = 0;
            }

            if (!_scene.TestLandRestrictions(UUID, out string reason, ref newpos.X, ref newpos.Y))
                return ;

            if (IsSitting)
                StandUp();

            if(_movingToTarget)
                ResetMoveToTarget();

            float localHalfAVHeight = 0.8f;
            if (Appearance != null)
                localHalfAVHeight = Appearance.AvatarHeight * 0.5f;

            float posZLimit = (float)Scene.Heightmap[(int)newpos.X, (int)newpos.Y];
            posZLimit += localHalfAVHeight + 0.1f;
            if (newpos.Z < posZLimit)
                newpos.Z = posZLimit;

            if((flags & 0x1e) != 0)
            {
                if ((flags & 8) != 0)
                    Flying = true;
                else if ((flags & 16) != 0)
                    Flying = false;

                uint tpflags = (uint)TeleportFlags.ViaLocation;
                if(Flying)
                    tpflags |= (uint)TeleportFlags.IsFlying;

                Vector3 lookat = Lookat;

                if ((flags & 2) != 0)
                {
                    newlookat.Z = 0;
                    newlookat.Normalize();
                    if (Math.Abs(newlookat.X) > 0.001 || Math.Abs(newlookat.Y) > 0.001)
                        lookat = newlookat;
                }
                else if((flags & 4) != 0)
                {
                    if((flags & 1) != 0)
                        newlookat = newvel;
                    else
                        newlookat = _velocity;
                    newlookat.Z = 0;
                    newlookat.Normalize();
                    if (Math.Abs(newlookat.X) > 0.001 || Math.Abs(newlookat.Y) > 0.001)
                        lookat = newlookat;
                }

                AbsolutePosition = newpos;
                ControllingClient.SendLocalTeleport(newpos, lookat, tpflags);
            }
            else
                AbsolutePosition = newpos;

            if ((flags & 1) != 0)
            {
                if (PhysicsActor != null)
                    PhysicsActor.SetMomentum(newvel);
                _velocity = newvel;
            }

            SendTerseUpdateToAllClients();
        }

        public void StopFlying()
        {
            if (IsInTransit)
                return;

            Vector3 pos = AbsolutePosition;
            if (Appearance.AvatarHeight != 127.0f)
                pos += new Vector3(0f, 0f, (Appearance.AvatarHeight / 6f));
            else
                pos += new Vector3(0f, 0f, (1.56f / 6f));

            AbsolutePosition = pos;

            // attach a suitable collision plane regardless of the actual situation to force the LLClient to land.
            // Collision plane below the avatar's position a 6th of the avatar's height is suitable.
            // Mind you, that this method doesn't get called if the avatar's velocity magnitude is greater then a
            // certain amount..   because the LLClient wouldn't land in that situation anyway.

            // why are we still testing for this really old height value default???
            if (Appearance.AvatarHeight != 127.0f)
                CollisionPlane = new Vector4(0, 0, 0, pos.Z - Appearance.AvatarHeight / 6f);
            else
                CollisionPlane = new Vector4(0, 0, 0, pos.Z - (1.56f / 6f));

            SendAgentTerseUpdate(this);
        }

        /// <summary>
        /// Applies a roll accumulator to the avatar's angular velocity for the avatar fly roll effect.
        /// </summary>
        /// <param name="amount">Postive or negative roll amount in radians</param>
        private void ApplyFlyingRoll(float amount, bool PressingUp, bool PressingDown)
        {

            float rollAmount = Util.Clamp(_AngularVelocity.Z + amount, -FLY_ROLL_MAX_RADIANS, FLY_ROLL_MAX_RADIANS);
            _AngularVelocity.Z = rollAmount;

            // APPLY EXTRA consideration for flying up and flying down during this time.
            // if we're turning left
            if (amount > 0)
            {

                // If we're at the max roll and pressing up, we want to swing BACK a bit
                // Automatically adds noise
                if (PressingUp)
                {
                    if (_AngularVelocity.Z >= FLY_ROLL_MAX_RADIANS - 0.04f)
                        _AngularVelocity.Z -= 0.9f;
                }
                // If we're at the max roll and pressing down, we want to swing MORE a bit
                if (PressingDown)
                {
                    if (_AngularVelocity.Z >= FLY_ROLL_MAX_RADIANS && _AngularVelocity.Z < FLY_ROLL_MAX_RADIANS + 0.6f)
                        _AngularVelocity.Z += 0.6f;
                }
            }
            else  // we're turning right.
            {
                // If we're at the max roll and pressing up, we want to swing BACK a bit
                // Automatically adds noise
                if (PressingUp)
                {
                    if (_AngularVelocity.Z <= (-FLY_ROLL_MAX_RADIANS))
                        _AngularVelocity.Z += 0.6f;
                }
                // If we're at the max roll and pressing down, we want to swing MORE a bit
                if (PressingDown)
                {
                    if (_AngularVelocity.Z >= -FLY_ROLL_MAX_RADIANS - 0.6f)
                        _AngularVelocity.Z -= 0.6f;
                }
            }
        }

        /// <summary>
        /// incrementally sets roll amount to zero
        /// </summary>
        /// <param name="amount">Positive roll amount in radians</param>
        /// <returns></returns>
        private float CalculateFlyingRollResetToZero(float amount)
        {
            const float rollMinRadians = 0f;

            if (_AngularVelocity.Z > 0)
            {
                float leftOverToMin = _AngularVelocity.Z - rollMinRadians;
                if (amount > leftOverToMin)
                    return -leftOverToMin;
                else
                    return -amount;
            }
            else
            {
                float leftOverToMin = -_AngularVelocity.Z - rollMinRadians;
                if (amount > leftOverToMin)
                    return leftOverToMin;
                else
                    return amount;
            }
        }

        // neighbouring regions we have enabled a child agent in
        // holds the seed cap for the child agent in that region
        private Dictionary<ulong, string> _knownChildRegions = new Dictionary<ulong, string>();

        struct spRegionSizeInfo
        {
            public readonly int sizeX;
            public readonly int sizeY;

            public spRegionSizeInfo(int x, int y)
            {
                sizeX = x;
                sizeY = y;
            }
        }

        private readonly Dictionary<ulong, spRegionSizeInfo> _knownChildRegionsSizeInfo = new Dictionary<ulong, spRegionSizeInfo>();

        public void AddNeighbourRegion(GridRegion region, string capsPath)
        {
            lock (_knownChildRegions)
            {
                ulong regionHandle = region.RegionHandle;
                _knownChildRegions[regionHandle] = capsPath;
                _knownChildRegionsSizeInfo[regionHandle] = new spRegionSizeInfo(region.RegionSizeX, region.RegionSizeY); ;
            }
        }

        public void AddNeighbourRegionSizeInfo(GridRegion region)
        {
            lock (_knownChildRegions)
            {
                _knownChildRegionsSizeInfo[region.RegionHandle] = new spRegionSizeInfo(region.RegionSizeX, region.RegionSizeY);
            }
        }

        public void SetNeighbourRegionSizeInfo(List<GridRegion> regionsList)
        {
            lock (_knownChildRegions)
            {
                _knownChildRegionsSizeInfo.Clear();
                foreach (GridRegion region in regionsList)
                {
                    _knownChildRegionsSizeInfo[region.RegionHandle] = new spRegionSizeInfo(region.RegionSizeX, region.RegionSizeY);
                }
            }
        }

        public void RemoveNeighbourRegion(ulong regionHandle)
        {
            lock (_knownChildRegions)
            {
                // Checking ContainsKey is redundant as Remove works either way and returns a bool
                // This is here to allow the Debug output to be conditional on removal
                //if (_knownChildRegions.ContainsKey(regionHandle))
                //    _log.DebugFormat(" !!! removing known region {0} in {1}. Count = {2}", regionHandle, Scene.RegionInfo.RegionName, _knownChildRegions.Count);
                _knownChildRegions.Remove(regionHandle);
                _knownChildRegionsSizeInfo.Remove(regionHandle);
            }
        }

        public bool knowsNeighbourRegion(ulong regionHandle)
        {
            lock (_knownChildRegions)
                return _knownChildRegions.ContainsKey(regionHandle);
        }

        public void DropOldNeighbours(List<ulong> oldRegions)
        {
            foreach (ulong handle in oldRegions)
            {
                RemoveNeighbourRegion(handle);
                Scene.CapsModule.DropChildSeed(UUID, handle);
            }
        }

        public void DropThisRootRegionFromNeighbours()
        {
            ulong handle = _scene.RegionInfo.RegionHandle;
            RemoveNeighbourRegion(handle);
            Scene.CapsModule.DropChildSeed(UUID, handle);
        }

        public Dictionary<ulong, string> KnownRegions
        {
            get
            {
                lock (_knownChildRegions)
                    return new Dictionary<ulong, string>(_knownChildRegions);
            }
            set
            {
                // Replacing the reference is atomic but we still need to lock on
                // the original dictionary object which may be in use elsewhere
                lock (_knownChildRegions)
                    _knownChildRegions = value;
            }
        }

        public List<ulong> KnownRegionHandles
        {
            get
            {
                lock (_knownChildRegions)
                    return new List<ulong>(_knownChildRegions.Keys);
            }
        }

        public int KnownRegionCount
        {
            get
            {
                lock (_knownChildRegions)
                    return _knownChildRegions.Count;
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Sets avatar height in the physics plugin
        /// </summary>
        /// <param name="height">New height of avatar</param>
        public void SetHeight(float height)
        {
            if (PhysicsActor != null && !IsChildAgent)
                PhysicsActor.Size = new Vector3(0.45f, 0.6f, height);
        }

        public void SetSize(Vector3 size, float feetoffset)
        {
            if (PhysicsActor != null && !IsChildAgent)
                PhysicsActor.setAvatarSize(size, feetoffset);
        }

        private bool WaitForUpdateAgent(IClientAPI client)
        {
            // Before the source region executes UpdateAgent
            // (which triggers Scene.IncomingUpdateChildAgent(AgentData cAgentData) here in the destination,
            // _originRegionID is UUID.Zero; after, it's non-Zero.  The CompleteMovement sequence initiated from the
            // viewer (in turn triggered by the source region sending it a TeleportFinish event) waits until it's non-zero

            try
            {
                if(_updateAgentReceivedAfterTransferEvent.WaitOne(10000))
                {
                    UUID originID = UUID.Zero;

                    lock (_originRegionIDAccessLock)
                        originID = _originRegionID;
                    if (originID.Equals(UUID.Zero))
                    {
                        // Movement into region will fail
                        _log.WarnFormat("[SCENE PRESENCE]: Update agent {0} at {1} got invalid origin region id ", client.Name, Scene.Name);
                        return false;
                    }
                    return true;
               }
               else
               {
                   _log.WarnFormat("[SCENE PRESENCE]: Update agent {0} at {1} did not receive agent update ", client.Name, Scene.Name);
                   return false;
               }
            }
            catch { }
            finally
            {
                _updateAgentReceivedAfterTransferEvent?.Reset();
            }

            return false;
        }

        public void RotateToLookAt(Vector3 lookAt)
        {
            if(ParentID == 0)
            {
                float n = lookAt.X * lookAt.X + lookAt.Y * lookAt.Y;
                if(n < 0.0001f)
                {
                    Rotation = Quaternion.Identity;
                    return;
                }
                n = lookAt.X/(float)Math.Sqrt(n);
                float angle = (float)Math.Acos(n);
                angle *= 0.5f;
                float s = (float)Math.Sin(angle);
                if(lookAt.Y < 0)
                    s = -s;
                Rotation = new Quaternion(
                    0f,
                    0f,
                    s,
                    (float)Math.Cos(angle)
                    );
            }
        }

        /// <summary>
        /// Complete Avatar's movement into the region.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="openChildAgents">
        /// If true, send notification to neighbour regions to expect
        /// a child agent from the client.  These neighbours can be some distance away, depending right now on the
        /// configuration of DefaultDrawDistance in the [Startup] section of config
        /// </param>
        public void CompleteMovement(IClientAPI client, bool openChildAgents)
        {
            int ts = Util.EnvironmentTickCount();

            _log.InfoFormat(
                "[SCENE PRESENCE]: Completing movement of {0} into region {1} in position {2}",
                client.Name, Scene.Name, AbsolutePosition);

            _inTransit = true;

            try
            {
                // Make sure it's not a login agent. We don't want to wait for updates during login
                if (!IsNPC && !IsRealLogin(_teleportFlags))
                {
                    // Let's wait until UpdateAgent (called by departing region) is done
                    if (!WaitForUpdateAgent(client))
                        // The sending region never sent the UpdateAgent data, we have to refuse
                        return;
                }

                //_log.DebugFormat("[CompleteMovement] WaitForUpdateAgent: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                bool flying = ((_AgentControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);

                Vector3 look = Lookat;
                look.Z = 0f;
                look.Normalize();
                if ((Math.Abs(look.X) < 0.01) && (Math.Abs(look.Y) < 0.01))
                {
                    look = Velocity;
                    look.Z = 0f;
                    look.Normalize();
                    if ((Math.Abs(look.X) < 0.01) && (Math.Abs(look.Y) < 0.01) )
                        look = new Vector3(0.99f, 0.042f, 0);
                }

                // Check Default Location (Also See EntityTransferModule.TeleportAgentWithinRegion)
                if (AbsolutePosition.X == 128f && AbsolutePosition.Y == 128f && AbsolutePosition.Z == 22.5f)
                    AbsolutePosition = Scene.RegionInfo.DefaultLandingPoint;

                if (!MakeRootAgent(AbsolutePosition, flying, ref look))
                {
                    _log.DebugFormat(
                        "[SCENE PRESENCE]: Aborting CompleteMovement call for {0} in {1} as they are already root",
                        Name, Scene.Name);

                    return;
                }

                if(IsChildAgent)
                {
                    return; // how?
                }

                //_log.DebugFormat("[CompleteMovement] MakeRootAgent: {0}ms", Util.EnvironmentTickCountSubtract(ts));

                if (!IsNPC)
                {
                    if (!_haveGroupInformation)
                    {
                        IGroupsModule gm = _scene.RequestModuleInterface<IGroupsModule>();
                        if (gm != null)
                            Grouptitle = gm.GetGroupTitle(_uuid);

                        //_log.DebugFormat("[CompleteMovement] Missing Grouptitle: {0}ms", Util.EnvironmentTickCountSubtract(ts));
                        /*
                        InventoryFolderBase cof = _scene.InventoryService.GetFolderForType(client.AgentId, (FolderType)46);
                        if (cof == null)
                            COF = UUID.Zero;
                        else
                            COF = cof.ID;

                        _log.DebugFormat("[CompleteMovement]: Missing COF for {0} is {1}", client.AgentId, COF);
                        */
                    }
                }

                if (_teleportFlags > 0)
                    _gotCrossUpdate = false; // sanity check

                if (!_gotCrossUpdate)
                    RotateToLookAt(look);

                _previusParcelHide = false;
                _previusParcelUUID = UUID.Zero;
                _currentParcelHide = false;
                _currentParcelUUID = UUID.Zero;
                ParcelDwellTickMS = Util.GetTimeStampMS();

                _inTransit = false;

                // Tell the client that we're ready to send rest
                if (!_gotCrossUpdate)
                {
                    _gotRegionHandShake = false; // allow it if not a crossing
                    ControllingClient.SendRegionHandshake();
                }

                ControllingClient.MoveAgentIntoRegion(_scene.RegionInfo, AbsolutePosition, look);

                bool isHGTP = (_teleportFlags & TeleportFlags.ViaHGLogin) != 0;

                if(!IsNPC)
                {
                    if( ParentPart != null && (_crossingFlags & 0x08) != 0)
                    {
                        ParentPart.ParentGroup.SendFullAnimUpdateToClient(ControllingClient);
                    }

                    // verify baked textures and cache
                    if (_scene.AvatarFactory != null && !isHGTP)
                    {
                        if (!_scene.AvatarFactory.ValidateBakedTextureCache(this))
                            _scene.AvatarFactory.QueueAppearanceSave(UUID);
                    }
                }

                if(isHGTP)
                {
//                    ControllingClient.SendNameReply(_uuid, Firstname, Lastname);
                    _log.DebugFormat("[CompleteMovement] HG");
                }

                if (!IsNPC)
                {
                    GodController.SyncViewerState();

                    // start sending terrain patchs
                    if (!_gotCrossUpdate)
                        Scene.SendLayerData(ControllingClient);

                    // send initial land overlay and parcel
                    ILandChannel landch = _scene.LandChannel;
                    if (landch != null)
                        landch.sendClientInitialLandInfo(client, !_gotCrossUpdate);
                }

                List<ScenePresence> allpresences = _scene.GetScenePresences();

                // send avatar object to all presences including us, so they cross it into region
                // then hide if necessary
                SendInitialAvatarDataToAllAgents(allpresences);

                // send this look
                if (!IsNPC)
                    SendAppearanceToAgent(this);

                // send this animations

                UUID[] animIDs = null;
                int[] animseqs = null;
                UUID[] animsobjs = null;

                if (Animator != null)
                    Animator.GetArrays(out animIDs, out animseqs, out animsobjs);

                bool haveAnims = (animIDs != null && animseqs != null && animsobjs != null);

                if (!IsNPC && haveAnims)
                    SendAnimPackToAgent(this, animIDs, animseqs, animsobjs);

                // send look and animations to others
                // if not cached we send greys
                // uncomented if will wait till avatar does baking
                //if (cachedbaked)

                {
                    foreach (ScenePresence p in allpresences)
                    {
                        if (p == this)
                            continue;

                        if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                            continue;

                        SendAppearanceToAgentNF(p);
                        if (haveAnims)
                            SendAnimPackToAgentNF(p, animIDs, animseqs, animsobjs);
                    }
                }

                // attachments
                if (IsNPC || IsRealLogin(_teleportFlags))
                {
                    if (Scene.AttachmentsModule != null)
                     {
                        if(IsNPC)
                        {
                            Util.FireAndForget(x =>
                                {
                                    Scene.AttachmentsModule.RezAttachments(this);
                                });
                        }
                        else
                            Scene.AttachmentsModule.RezAttachments(this);
                    }
                }
                else
                {
                    if (_attachments.Count > 0)
                    {
                        foreach (SceneObjectGroup sog in _attachments)
                        {
                            sog.RootPart.ParentGroup.CreateScriptInstances(0, false, _scene.DefaultScriptEngine, GetStateSource());
                            sog.ResumeScripts();
                        }

                        foreach (ScenePresence p in allpresences)
                        {
                            if (p == this)
                            {
                                SendAttachmentsToAgentNF(this);
                                continue;
                            }

                            if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                                continue;

                            SendAttachmentsToAgentNF(p);
                        }
                    }
                }

                if (!IsNPC)
                {
                    if(_gotCrossUpdate)
                    {
                        SendOtherAgentsAvatarFullToMe();

                        // Create child agents in neighbouring regions
                        IEntityTransferModule _agentTransfer = _scene.RequestModuleInterface<IEntityTransferModule>();
                        if (_agentTransfer != null)
                        {
                            _agentTransfer.EnableChildAgents(this);
                        }

                        _lastChildUpdatesTime = Util.EnvironmentTickCount() + 10000;
                        _lastChildAgentUpdatePosition = AbsolutePosition;
                        _lastChildAgentCheckPosition = _lastChildAgentUpdatePosition;
                        _lastChildAgentUpdateDrawDistance = DrawDistance;
                        _lastRegionsDrawDistance = RegionViewDistance;

                        _lastChildAgentUpdateGodLevel = GodController.ViwerUIGodLevel;
                        _childUpdatesBusy = false; // allow them

                    }

                    // send the rest of the world
                    //if (_teleportFlags > 0 || _currentParcelHide)
                        //SendInitialDataToMe();
                        //SendOtherAgentsAvatarFullToMe();

                    // priority uses avatar position only
                    // _reprioritizationLastPosition = AbsolutePosition;
                    // _reprioritizationLastDrawDistance = DrawDistance;
                    // _reprioritizationLastTime = Util.EnvironmentTickCount() + 15000; // delay it
                    // _reprioritizationBusy = false;

                    if (openChildAgents)
                    {
                        IFriendsModule friendsModule = _scene.RequestModuleInterface<IFriendsModule>();
                        if (friendsModule != null)
                        {
                            if(_gotCrossUpdate)
                                friendsModule.IsNowRoot(this);
                            else
                                friendsModule.SendFriendsOnlineIfNeeded(ControllingClient);
                        }
                        //_log.DebugFormat("[CompleteMovement] friendsModule: {0}ms",    Util.EnvironmentTickCountSubtract(ts));
                    }
                }
                else
                    NeedInitialData = -1;
            }
            finally
            {
                _haveGroupInformation = false;
                _gotCrossUpdate = false;
                _crossingFlags = 0;
                _inTransit = false;
            }
 
            _scene.EventManager.OnRegionHeartbeatEnd += RegionHeartbeatEnd;

            _log.DebugFormat("[CompleteMovement] end: {0}ms", Util.EnvironmentTickCountSubtract(ts));
        }

        /// <summary>
        /// Callback for the Camera view block check.  Gets called with the results of the camera view block test
        /// hitYN is true when there's something in the way.
        /// </summary>
        /// <param name="hitYN"></param>
        /// <param name="collisionPoint"></param>
        /// <param name="localid"></param>
        /// <param name="distance"></param>
        ///

        private void checkCameraCollision()
        {
            if(_doingCamRayCast || !_scene.PhysicsScene.SupportsRayCast())
                return;

            if(_mouseLook || ParentID != 0)
            {
                if (CameraConstraintActive)
                {
                    Vector4 plane = new Vector4(0.9f, 0.0f, 0.361f, -10000f); // not right...
                    UpdateCameraCollisionPlane(plane);
                    CameraConstraintActive = false;
                }
                return;
            }
           
            Vector3 posAdjusted = AbsolutePosition;
            posAdjusted.Z += 1.0f; // viewer current camera focus point

            if(posAdjusted.ApproxEquals(_lastCameraRayCastPos, 0.2f) &&
                CameraPosition.ApproxEquals(_lastCameraRayCastCam, 0.2f))
                return;

            _lastCameraRayCastCam = CameraPosition;
            _lastCameraRayCastPos = posAdjusted;

            Vector3 tocam = CameraPosition - posAdjusted;

            float distTocamlen = tocam.LengthSquared();
            if (distTocamlen > 0.01f && distTocamlen < 400)
            {
                distTocamlen = (float)Math.Sqrt(distTocamlen);
                tocam *= (1.0f / distTocamlen);

                _doingCamRayCast = true;
                _scene.PhysicsScene.RaycastWorld(posAdjusted, tocam, distTocamlen + 1.0f, RayCastCameraCallback);
                return;
            }

            if (CameraConstraintActive)
            {
                Vector4 plane = new Vector4(0.9f, 0.0f, 0.361f, -10000f); // not right...
                UpdateCameraCollisionPlane(plane);
                CameraConstraintActive = false;
            }
        }

        private void UpdateCameraCollisionPlane(Vector4 plane)
        {
            if (_lastCameraCollisionPlane != plane)
            {
                _lastCameraCollisionPlane = plane;
                ControllingClient.SendCameraConstraint(plane);
            }
        }

        public void RayCastCameraCallback(bool hitYN, Vector3 collisionPoint, uint localid, float distance, Vector3 pNormal)
        {
            if (hitYN && localid != LocalId)
            {
                if (localid != 0)
                {
                    SceneObjectPart part = _scene.GetSceneObjectPart(localid);
                    if (part != null && !part.VolumeDetectActive)
                    {
                        CameraConstraintActive = true;
                        pNormal.X = (float) Math.Round(pNormal.X, 2);
                        pNormal.Y = (float) Math.Round(pNormal.Y, 2);
                        pNormal.Z = (float) Math.Round(pNormal.Z, 2);
                        pNormal.Normalize();
                        collisionPoint.X = (float) Math.Round(collisionPoint.X, 1);
                        collisionPoint.Y = (float) Math.Round(collisionPoint.Y, 1);
                        collisionPoint.Z = (float) Math.Round(collisionPoint.Z, 1);

                        Vector4 plane = new Vector4(pNormal.X, pNormal.Y, pNormal.Z,
                                                    Vector3.Dot(collisionPoint, pNormal));
                        UpdateCameraCollisionPlane(plane);
                    }
                }
                else
                {
                    CameraConstraintActive = true;
                    pNormal.X = (float) Math.Round(pNormal.X, 2);
                    pNormal.Y = (float) Math.Round(pNormal.Y, 2);
                    pNormal.Z = (float) Math.Round(pNormal.Z, 2);
                    pNormal.Normalize();
                    collisionPoint.X = (float) Math.Round(collisionPoint.X, 1);
                    collisionPoint.Y = (float) Math.Round(collisionPoint.Y, 1);
                    collisionPoint.Z = (float) Math.Round(collisionPoint.Z, 1);

                    Vector4 plane = new Vector4(pNormal.X, pNormal.Y, pNormal.Z,
                                                Vector3.Dot(collisionPoint, pNormal));
                    UpdateCameraCollisionPlane(plane);
                }
            }
            else if(CameraConstraintActive)
            {
                Vector4 plane = new Vector4(0.9f, 0.0f, 0.361f, -9000f); // not right...
                UpdateCameraCollisionPlane(plane);
                CameraConstraintActive = false;
            }

            _doingCamRayCast = false;
        }

        /// <summary>
        /// This is the event handler for client movement. If a client is moving, this event is triggering.
        /// </summary>
        public void HandleAgentUpdate(IClientAPI remoteClient, AgentUpdateArgs agentData)
        {
//            _log.DebugFormat(
//                "[SCENE PRESENCE]: In {0} received agent update from {1}, flags {2}",
//                Scene.Name, remoteClient.Name, (AgentManager.ControlFlags)agentData.ControlFlags);

            if (IsChildAgent)
            {
//                _log.DebugFormat("DEBUG: HandleAgentUpdate: child agent in {0}", Scene.Name);
                return;
            }

            if (IsInTransit)
                return;

            #region Sanity Checking

            // This is irritating.  Really.
            if (!AbsolutePosition.IsFinite())
            {
                bool isphysical = PhysicsActor != null;
                if(isphysical)
                    RemoveFromPhysicalScene();
                _log.Error("[AVATAR]: NonFinite Avatar position detected... Reset Position. Mantis this please. Error #9999902");

                _pos = _LastFinitePos;
                if (!_pos.IsFinite())
                {
                    _pos.X = 127f;
                    _pos.Y = 127f;
                    _pos.Z = 127f;
                    _log.Error("[AVATAR]: NonFinite Avatar on lastFiniteposition also. Reset Position. Mantis this please. Error #9999903");
                }

                if(isphysical)
                    AddToPhysicalScene(false);
            }
            else
            {
                _LastFinitePos = _pos;
            }

            #endregion Sanity Checking

            #region Inputs

            AgentManager.ControlFlags flags = (AgentManager.ControlFlags)agentData.ControlFlags;

            // The Agent's Draw distance setting
            // When we get to the point of re-computing neighbors everytime this
            // changes, then start using the agent's drawdistance rather than the
            // region's draw distance.

            DrawDistance = agentData.Far;

            _mouseLook = (flags & AgentManager.ControlFlags.AGENT_CONTROL_MOUSELOOK) != 0;

            // FIXME: This does not work as intended because the viewer only sends the lbutton down when the button
            // is first pressed, not whilst it is held down.  If this is required in the future then need to look
            // for an AGENT_CONTROL_LBUTTON_UP event and make sure to handle cases where an initial DOWN is not
            // received (e.g. on holding LMB down on the avatar in a viewer).
            _leftButtonDown = (flags & AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0;

            #endregion Inputs

//            // Make anims work for client side autopilot
//            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0)
//                _updateCount = UPDATE_COUNT;
//
//            // Make turning in place work
//            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0 ||
//                (flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
//                _updateCount = UPDATE_COUNT;


            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_STAND_UP) != 0)
            {
                StandUp();
            }

            // Raycast from the avatar's head to the camera to see if there's anything blocking the view
            // this exclude checks may not be complete
            if(agentData.NeedsCameraCollision) // condition parentID may be wrong
                checkCameraCollision();

            uint flagsForScripts = (uint)flags;
            flags = RemoveIgnoredControls(flags, IgnoredControls);

            if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_SIT_ON_GROUND) != 0)
                HandleAgentSitOnGround();

            // In the future, these values might need to go global.
            // Here's where you get them.
            _AgentControlFlags = flags;
            _headrotation = agentData.HeadRotation;
            byte oldState = State;
            State = agentData.State;

            // We need to send this back to the client in order to stop the edit beams
            if ((oldState & (uint)AgentState.Editing) != 0 && State == (uint)AgentState.None)
                SendAgentTerseUpdate(this);

            PhysicsActor actor = PhysicsActor;

            // This will be the case if the agent is sitting on the groudn or on an object.
            if (actor == null)
            {
                SendControlsToScripts(flagsForScripts);
                return;
            }

            if (AllowMovement && !SitGround)
            {
//                _log.DebugFormat("[SCENE PRESENCE]: Initial body rotation {0} for {1}", agentData.BodyRotation, Name);
                bool update_rotation = false;
                if (!IsSatOnObject && agentData.BodyRotation != Rotation)
                {
                    Rotation = agentData.BodyRotation;
                    update_rotation = true;
                }

                bool update_movementflag = false;
                bool mvToTarget = _movingToTarget;
                if (agentData.UseClientAgentPosition)
                {
                    _movingToTarget = (agentData.ClientAgentPosition - AbsolutePosition).LengthSquared() > 0.04f;
                    _moveToPositionTarget = agentData.ClientAgentPosition;
                    _moveToSpeed = -1f;
                }

                int i = 0;
                bool DCFlagKeyPressed = false;
                Vector3 agent_control_v3 = Vector3.Zero;

                bool newFlying = false;

                if (ForceFly)
                    newFlying = true;
                else if (FlyDisabled)
                    newFlying = false;
                else if(mvToTarget)
                    newFlying = actor.Flying;
                else
                    newFlying = (flags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0;

                if (actor.Flying != newFlying)
                {
                    // Note: ScenePresence.Flying is actually fetched from the physical actor
                    //     so setting PhysActor.Flying here also sets the ScenePresence's value.
                    actor.Flying = newFlying;
                    update_movementflag = true;
                }

                if (ParentID == 0)
                {
                    bool bAllowUpdateMoveToPosition = false;

                    Vector3[] dirVectors;

                    // use camera up angle when in mouselook and not flying or when holding the left mouse button down and not flying
                    // this prevents 'jumping' in inappropriate situations.
//                    if (!Flying && (_mouseLook || _leftButtonDown))
//                        dirVectors = GetWalkDirectionVectors();
//                    else
                        dirVectors = Dir_Vectors;

                    // A DIR_CONTROL_FLAG occurs when the user is trying to move in a particular direction.
                    foreach (Dir_ControlFlags DCF in DIR_CONTROL_FLAGS)
                    {
                        if (((uint)flags & (uint)DCF) != 0)
                        {
                            DCFlagKeyPressed = true;

                            try
                            {
                                agent_control_v3 += Dir_Vectors[i];
                                //_log.DebugFormat("[Motion]: {0}, {1}",i, dirVectors[i]);
                            }
                            catch (IndexOutOfRangeException)
                            {
                                // Why did I get this?
                            }

                            if ((MovementFlag & (uint)DCF) == 0)
                            {
                                //_log.DebugFormat("[SCENE PRESENCE]: Updating MovementFlag for {0} with {1}", Name, DCF);
                                MovementFlag |= (uint)DCF;
                                update_movementflag = true;
                            }
                        }
                        else
                        {
                            if ((MovementFlag & (uint)DCF) != 0)
                            {
                                //_log.DebugFormat("[SCENE PRESENCE]: Updating MovementFlag for {0} with lack of {1}", Name, DCF);
                                MovementFlag &= (uint)~DCF;
                                update_movementflag = true;

                                /*
                                    if ((DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD_NUDGE || DCF == Dir_ControlFlags.DIR_CONTROL_FLAG_BACKWARD_NUDGE)
                                    && ((MovementFlag & (byte)nudgehack) == nudgehack))
                                    {
                                        _log.Debug("Removed Hack flag");
                                    }
                                */
                            }
                            else
                            {
                                bAllowUpdateMoveToPosition = true;
                            }
                        }

                        i++;
                    }

                    // Detect AGENT_CONTROL_STOP state changes
                    if (AgentControlStopActive != ((flags & AgentManager.ControlFlags.AGENT_CONTROL_STOP) != 0))
                    {
                        AgentControlStopActive = !AgentControlStopActive;
                        update_movementflag = true;
                    }

                    if (_movingToTarget)
                    {
                        // If the user has pressed a key then we want to cancel any move to target.
                        if (DCFlagKeyPressed)
                        {
                            ResetMoveToTarget();
                            update_movementflag = true;
                        }
                        else if (bAllowUpdateMoveToPosition)
                        {
                            // The UseClientAgentPosition is set if parcel ban is forcing the avatar to move to a
                            // certain position.  It's only check for tolerance on returning to that position is 0.2
                            // rather than 1, at which point it removes its force target.
                            if (HandleMoveToTargetUpdate(agentData.UseClientAgentPosition ? 0.2f : 0.5f, ref agent_control_v3))
                                update_movementflag = true;
                        }
                    }
                }

                // Cause the avatar to stop flying if it's colliding
                // with something with the down arrow pressed.

                // Only do this if we're flying
                if (Flying && !ForceFly)
                {
                    // Need to stop in mid air if user holds down AGENT_CONTROL_STOP
                    //                    if (AgentControlStopActive)
                    //                    {
                    //                        agent_control_v3 = Vector3.Zero;
                    //                    }
                    //                    else
                    {
                        // Landing detection code

                        // Are the landing controls requirements filled?
                        bool controlland = (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0 ||
                                           (flags & AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0;

                        //_log.Debug("[CONTROL]: " +flags);
                        // Applies a satisfying roll effect to the avatar when flying.
                        if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_TURN_LEFT) != 0 && (flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0)
                        {
                            ApplyFlyingRoll(
                                FLY_ROLL_RADIANS_PER_UPDATE,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0);
                        }
                        else if ((flags & AgentManager.ControlFlags.AGENT_CONTROL_TURN_RIGHT) != 0 &&
                                 (flags & AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
                        {
                            ApplyFlyingRoll(
                                -FLY_ROLL_RADIANS_PER_UPDATE,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0,
                                (flags & AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0);
                        }
                        else
                        {
                            if (_AngularVelocity.Z != 0)
                                _AngularVelocity.Z += CalculateFlyingRollResetToZero(FLY_ROLL_RESET_RADIANS_PER_UPDATE);
                        }

                        /*
                                                if (Flying && IsColliding && controlland)
                                                {
                                                    // nesting this check because LengthSquared() is expensive and we don't
                                                    // want to do it every step when flying.
                                                    if ((Velocity.LengthSquared() <= LAND_VELOCITYMAG_MAX))
                                                        StopFlying();
                                                }
                         */
                    }
                }
                else if (IsColliding && agent_control_v3.Z < 0f)
                    agent_control_v3.Z = 0;
//                else if(AgentControlStopActive %% Velocity.Z <0.01f)

//                _log.DebugFormat("[SCENE PRESENCE]: MovementFlag {0} for {1}", MovementFlag, Name);

                // If the agent update does move the avatar, then calculate the force ready for the velocity update,
                // which occurs later in the main scene loop
                // We also need to update if the user rotates their avatar whilst it is slow walking/running (if they
                // held down AGENT_CONTROL_STOP whilst normal walking/running).  However, we do not want to update
                // if the user rotated whilst holding down AGENT_CONTROL_STOP when already still (which locks the
                // avatar location in place).

                if (update_movementflag
                    || update_rotation && DCFlagKeyPressed && (!AgentControlStopActive || MovementFlag != 0))
                {

                    if (AgentControlStopActive)
                    {
                        //                        if (MovementFlag == 0 && Animator.Falling)
                        if (MovementFlag == 0 && Animator.currentControlState == ScenePresenceAnimator.motionControlStates.falling)
                        {
                            AddNewMovement(agent_control_v3, AgentControlStopSlowVel, true);
                        }
                        else
                            AddNewMovement(agent_control_v3, AgentControlStopSlowVel);
                    }
                    else
                    {
                        if(_movingToTarget ||
                                 Animator.currentControlState != ScenePresenceAnimator.motionControlStates.flying &&
                                 Animator.currentControlState != ScenePresenceAnimator.motionControlStates.onsurface
                                 )
                            AddNewMovement(agent_control_v3);
                        else
                        {
                            if (MovementFlag != 0)
                                AddNewMovement(agent_control_v3);
                            else
                                _delayedStop = Util.GetTimeStampMS() + 200.0;
                        }
                    }
                }
/*
                if (update_movementflag && ParentID == 0 && _delayedStop < 0)
                {
//                    _log.DebugFormat("[SCENE PRESENCE]: Updating movement animations for {0}", Name);
                    Animator.UpdateMovementAnimations();
                }
*/
                SendControlsToScripts(flagsForScripts);
            }

            // We need to send this back to the client in order to see the edit beams
            if ((State & (uint)AgentState.Editing) != 0)
                SendAgentTerseUpdate(this);

//            _scene.EventManager.TriggerOnClientMovement(this);
        }

        private void HandleAgentFOV(IClientAPI remoteClient, float _fov)
        {
            _FOV = _fov;
        }

        /// <summary>
        /// This is the event handler for client cameras. If a client is moving, or moving the camera, this event is triggering.
        /// </summary>
        private void HandleAgentCamerasUpdate(IClientAPI remoteClient, AgentUpdateArgs agentData)
        {
            //_log.DebugFormat(
            //    "[SCENE PRESENCE]: In {0} received agent camera update from {1}, flags {2}",
            //    Scene.RegionInfo.RegionName, remoteClient.Name, (AgentManager.ControlFlags)agentData.ControlFlags);

            if (IsChildAgent)
                return;

            if(IsInTransit)
                return;

//            AgentManager.ControlFlags flags = (AgentManager.ControlFlags)agentData.ControlFlags;

            // Camera location in world.  We'll need to raytrace
            // from this location from time to time.
            CameraPosition = agentData.CameraCenter;
            // Use these three vectors to figure out what the agent is looking at
            // Convert it to a Matrix and/or Quaternion

            // this may need lock
            CameraAtAxis = agentData.CameraAtAxis;
            CameraLeftAxis = agentData.CameraLeftAxis;
            CameraUpAxis = agentData.CameraUpAxis;
            DrawDistance = agentData.Far;

            CameraAtAxis.Normalize();
            CameraLeftAxis.Normalize();
            CameraUpAxis.Normalize();
            Quaternion camRot = Util.Axes2Rot(CameraAtAxis, CameraLeftAxis, CameraUpAxis);
            CameraRotation = camRot;

            if(agentData.NeedsCameraCollision)
                checkCameraCollision();

            TriggerScenePresenceUpdated();
        }

        /// <summary>
        /// Calculate an update to move the presence to the set target.
        /// </summary>
        /// <remarks>
        /// This doesn't actually perform the movement.  Instead, it adds its vector to agent_control_v3.
        /// </remarks>
        /// <param value="agent_control_v3">Cumulative agent movement that this method will update.</param>
        /// <returns>True if movement has been updated in some way.  False otherwise.</returns>
        public bool HandleMoveToTargetUpdate(float tolerance, ref Vector3 agent_control_v3)
        {
//            _log.DebugFormat("[SCENE PRESENCE]: Called HandleMoveToTargetUpdate() for {0}", Name);

            bool updated = false;

            Vector3 LocalVectorToTarget3D = _moveToPositionTarget - AbsolutePosition;

//            _log.DebugFormat(
//                "[SCENE PRESENCE]: bAllowUpdateMoveToPosition {0}, _moveToPositionInProgress {1}, _autopilotMoving {2}",
//                allowUpdate, _moveToPositionInProgress, _autopilotMoving);

            float distanceToTarget;
            if(Flying && !LandAtTarget)

                distanceToTarget = LocalVectorToTarget3D.LengthSquared();
            else
                distanceToTarget = (LocalVectorToTarget3D.X * LocalVectorToTarget3D.X) + (LocalVectorToTarget3D.Y * LocalVectorToTarget3D.Y);


            // _log.DebugFormat(
            //      "[SCENE PRESENCE]: Abs pos of {0} is {1}, target {2}, distance {3}",
            //           Name, AbsolutePosition, MoveToPositionTarget, distanceToTarget);

            // Check the error term of the current position in relation to the target position
            if (distanceToTarget <= tolerance * tolerance)
            {
                // We are close enough to the target
                Velocity = Vector3.Zero;
                if (Flying)
                {
                    if (LandAtTarget)
                    {
                        Flying = false;

                    // A horrible hack to stop the avatar dead in its tracks rather than having them overshoot
                    // the target if flying.
                    // We really need to be more subtle (slow the avatar as it approaches the target) or at
                    // least be able to set collision status once, rather than 5 times to give it enough
                    // weighting so that that PhysicsActor thinks it really is colliding.
                        for (int i = 0; i < 5; i++)
                            IsColliding = true;
                    }
                }
                else
                    _moveToPositionTarget.Z = AbsolutePosition.Z;

                AbsolutePosition = _moveToPositionTarget;

                ResetMoveToTarget();
                return false;
            }

            if (_moveToSpeed > 0 &&
                    distanceToTarget <= _moveToSpeed * _moveToSpeed * Scene.FrameTime * Scene.FrameTime)
                _moveToSpeed = (float)Math.Sqrt(distanceToTarget) / Scene.FrameTime;

            try
            {
                // move avatar in 3D towards target, in avatar coordinate frame.
                // This movement vector gets added to the velocity through AddNewMovement().
                // Theoretically we might need a more complex PID approach here if other
                // unknown forces are acting on the avatar and we need to adaptively respond
                // to such forces, but the following simple approach seems to works fine.

                float angle = 0.5f * (float)Math.Atan2(LocalVectorToTarget3D.Y, LocalVectorToTarget3D.X);
                Quaternion rot = new Quaternion(0,0, (float)Math.Sin(angle),(float)Math.Cos(angle));
                Rotation = rot;
                LocalVectorToTarget3D = LocalVectorToTarget3D * Quaternion.Inverse(rot); // change to avatar coords
                if(!Flying)
                    LocalVectorToTarget3D.Z = 0;
                LocalVectorToTarget3D.Normalize();

                // update avatar movement flags. the avatar coordinate system is as follows:
                //
                //                        +X (forward)
                //
                //                        ^
                //                        |
                //                        |
                //                        |
                //                        |
                //     (left) +Y <--------o--------> -Y
                //                       avatar
                //                        |
                //                        |
                //                        |
                //                        |
                //                        v
                //                        -X
                //

                // based on the above avatar coordinate system, classify the movement into
                // one of left/right/back/forward.

                const uint noMovFlagsMask = (uint)(~(Dir_ControlFlags.DIR_CONTROL_FLAG_BACK |
                    Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD | Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT |
                    Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT | Dir_ControlFlags.DIR_CONTROL_FLAG_UP |
                    Dir_ControlFlags.DIR_CONTROL_FLAG_DOWN));

                MovementFlag &= noMovFlagsMask;
                uint tmpAgentControlFlags = (uint)_AgentControlFlags;
                tmpAgentControlFlags &= noMovFlagsMask;

                if (LocalVectorToTarget3D.X < 0) //MoveBack
                {
                    MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
                    tmpAgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_BACK;
                    updated = true;
                }
                else if (LocalVectorToTarget3D.X > 0) //Move Forward
                {
                    MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
                    tmpAgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_FORWARD;
                    updated = true;
                }

                if (LocalVectorToTarget3D.Y > 0) //MoveLeft
                {
                    MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
                    tmpAgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_LEFT;
                    updated = true;
                }
                else if (LocalVectorToTarget3D.Y < 0) //MoveRight
                {
                    MovementFlag |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
                    tmpAgentControlFlags |= (uint)Dir_ControlFlags.DIR_CONTROL_FLAG_RIGHT;
                    updated = true;
                }
               
                if (LocalVectorToTarget3D.Z > 0) //Up
                     updated = true;
 
                else if (LocalVectorToTarget3D.Z < 0) //Down
                     updated = true;
 
//                        _log.DebugFormat(
//                            "[SCENE PRESENCE]: HandleMoveToTargetUpdate adding {0} to move vector {1} for {2}",
//                            LocalVectorToTarget3D, agent_control_v3, Name);

                _AgentControlFlags = (AgentManager.ControlFlags) tmpAgentControlFlags;
                if(updated)
                    agent_control_v3 += LocalVectorToTarget3D;
            }
            catch (Exception e)
            {
                //Avoid system crash, can be slower but...
                _log.DebugFormat("Crash! {0}", e.ToString());
            }

            return updated;
//                AddNewMovement(agent_control_v3);
        }

        public void MoveToTargetHandle(Vector3 pos, bool noFly, bool landAtTarget)
        {
            MoveToTarget(pos, noFly, landAtTarget, false);
        }

        /// <summary>
        /// Move to the given target over time.
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="noFly">
        /// If true, then don't allow the avatar to fly to the target, even if it's up in the air.
        /// This is to allow movement to targets that are known to be on an elevated platform with a continuous path
        /// from start to finish.
        /// </param>
        /// <param name="landAtTarget">
        /// If true and the avatar starts flying during the move then land at the target.
        /// </param>
        public void MoveToTarget(Vector3 pos, bool noFly, bool landAtTarget, bool running, float tau = -1f)
        { 
            _delayedStop = -1;

            if (IsSitting)
                StandUp();

//            _log.DebugFormat(
//                "[SCENE PRESENCE]: Avatar {0} received request to move to position {1} in {2}",
//                Name, pos, _scene.RegionInfo.RegionName);

            // Allow move to another sub-region within a megaregion
            Vector2 regionSize;
            regionSize = new Vector2(_scene.RegionInfo.RegionSizeX, _scene.RegionInfo.RegionSizeY);

            if (pos.X < 0.5f)
                pos.X = 0.5f;
            else if (pos.X > regionSize.X - 0.5f)
                pos.X = regionSize.X - 0.5f;
            if (pos.Y < 0.5f)
                pos.Y = 0.5f;
            else if (pos.Y > regionSize.Y - 0.5f)
                pos.Y = regionSize.Y - 0.5f;

            float terrainHeight;
            Scene targetScene = _scene;
            terrainHeight = _scene.GetGroundHeight(pos.X, pos.Y);

            // dont try to land underground
            terrainHeight += Appearance.AvatarHeight * 0.5f + 0.2f;

            if(terrainHeight > pos.Z)
                pos.Z = terrainHeight;

            //            _log.DebugFormat(
            //                "[SCENE PRESENCE]: Avatar {0} set move to target {1} (terrain height {2}) in {3}",
            //                Name, pos, terrainHeight, _scene.RegionInfo.RegionName);

            bool shouldfly = true;
            if(IsNPC)
            {
                if (!Flying)
                    shouldfly = noFly ? false : (pos.Z > terrainHeight + Appearance.AvatarHeight);
                LandAtTarget = landAtTarget & shouldfly;
            }
            else
            {   
                // we have no control on viewer fly state
                shouldfly = Flying || (pos.Z > terrainHeight + Appearance.AvatarHeight);
                LandAtTarget = false;
            }

            // _log.DebugFormat("[SCENE PRESENCE]: Local vector to target is {0},[1}", localVectorToTarget3D.X,localVectorToTarget3D.Y);

            _movingToTarget = true;
            _moveToPositionTarget = pos;
            if(tau > 0)
            {
                if(tau < Scene.FrameTime)
                    tau = Scene.FrameTime;
                Vector3 localVectorToTarget3D = pos - AbsolutePosition;
                if (!shouldfly)
                    localVectorToTarget3D.Z = 0;
                _moveToSpeed = localVectorToTarget3D.Length() / tau;
                if(_moveToSpeed < 0.5f) //to tune
                    _moveToSpeed = 0.5f;
                else if(_moveToSpeed > 50f)
                    _moveToSpeed = 50f;
            }
            else
                _moveToSpeed = 4.096f * _speedModifier;

            SetAlwaysRun = running;
            Flying = shouldfly;

            Vector3 control = Vector3.Zero;
            if(HandleMoveToTargetUpdate(0.5f, ref control))
                AddNewMovement(control);
        }

        /// <summary>
        /// Reset the move to target.
        /// </summary>
        public void ResetMoveToTarget()
        {
//            _log.DebugFormat("[SCENE PRESENCE]: Resetting move to target for {0}", Name);

            _movingToTarget = false;
            _moveToSpeed = -1f;
//            MoveToPositionTarget = Vector3.Zero;
//            lock(_forceToApplyLock)
//               _forceToApplyValid = false; // cancel possible last action

            // We need to reset the control flag as the ScenePresenceAnimator uses this to determine the correct
            // resting animation (e.g. hover or stand).  NPCs don't have a client that will quickly reset this flag.
            // However, the line is here rather than in the NPC module since it also appears necessary to stop a
            // viewer that uses "go here" from juddering on all subsequent avatar movements.
            AgentControlFlags = (uint)AgentManager.ControlFlags.NONE;
            if(IsNPC)
                Animator.UpdateMovementAnimations();
        }

        /// <summary>
        /// Perform the logic necessary to stand the avatar up.  This method also executes
        /// the stand animation.
        /// </summary>
        public void StandUp(bool addPhys = true)
        {
//            _log.DebugFormat("[SCENE PRESENCE]: StandUp() for {0}", Name);

            bool satOnObject = IsSatOnObject;
            SceneObjectPart part = ParentPart;
            SitGround = false;

            if (satOnObject)
            {
                PrevSitOffset = _pos; // Save sit offset
                UnRegisterSeatControls(part.ParentGroup.UUID);

                TaskInventoryDictionary taskIDict = part.TaskInventory;
                if (taskIDict != null)
                {
                    lock (taskIDict)
                    {
                        foreach (UUID taskID in taskIDict.Keys)
                        {
                            UnRegisterControlEventsToScript(LocalId, taskID);
                            taskIDict[taskID].PermsMask &= ~(
                                2048 | //PERMISSION_CONTROL_CAMERA
                                4); // PERMISSION_TAKE_CONTROLS
                        }
                    }
                }

                ControllingClient.SendClearFollowCamProperties(part.ParentUUID);

                ParentID = 0;
                ParentPart = null;

                Quaternion standRotation = part.ParentGroup.RootPart.RotationOffset;
                Vector3 sitWorldPosition = part.ParentGroup.AbsolutePosition + _pos * standRotation;

                standRotation = standRotation * _bodyRot;
                _bodyRot = standRotation;

                Quaternion standRotationZ;
                Vector3 adjustmentForSitPose = part.StandOffset;
                if (adjustmentForSitPose.X == 0 &&
                    adjustmentForSitPose.Y == 0 &&
                    adjustmentForSitPose.Z == 0)
                {
                    standRotationZ = new Quaternion(0, 0, standRotation.Z, standRotation.W);
                    float t = standRotationZ.W * standRotationZ.W + standRotationZ.Z * standRotationZ.Z;
                    if (t > 0)
                    {
                        t = 1.0f / (float)Math.Sqrt(t);
                        standRotationZ.W *= t;
                        standRotationZ.Z *= t;
                    }
                    else
                    {
                        standRotationZ.W = 1.0f;
                        standRotationZ.Z = 0f;
                    }
                    adjustmentForSitPose = new Vector3(0.65f, 0, _sitAvatarHeight * 0.5f + .1f) * standRotationZ;
                }
                else
                {
                    sitWorldPosition = part.GetWorldPosition();

                    standRotation = part.GetWorldRotation();
                    standRotationZ = new Quaternion(0, 0, standRotation.Z, standRotation.W);
                    float t = standRotationZ.W * standRotationZ.W + standRotationZ.Z * standRotationZ.Z;
                    if (t > 0)
                    {
                        t = 1.0f / (float)Math.Sqrt(t);
                        standRotationZ.W *= t;
                        standRotationZ.Z *= t;
                    }
                    else
                    {
                        standRotationZ.W = 1.0f;
                        standRotationZ.Z = 0f;
                    }
                    adjustmentForSitPose *= standRotationZ;

                    if (Appearance != null && Appearance.AvatarHeight > 0)
                        adjustmentForSitPose.Z += 0.5f * Appearance.AvatarHeight + .1f;
                    else
                        adjustmentForSitPose.Z += .9f;
                }

                _pos = sitWorldPosition + adjustmentForSitPose;
            }

            if (addPhys && PhysicsActor == null)
                AddToPhysicalScene(false);

            if (satOnObject)
            {
                _requestedSitTargetID = 0;
                part.RemoveSittingAvatar(this);
                part.ParentGroup.TriggerScriptChangedEvent(Changed.LINK);

                SendAvatarDataToAllAgents();
                _scene.EventManager.TriggerParcelPrimCountTainted(); // update select/ sat on
            }

            // reset to default sitAnimation
            sitAnimation = "SIT";

            Animator.SetMovementAnimations("STAND");

            TriggerScenePresenceUpdated();
        }

        private SceneObjectPart FindNextAvailableSitTarget(UUID targetID)
        {
            SceneObjectPart targetPart = _scene.GetSceneObjectPart(targetID);
            if (targetPart == null)
                return null;

            // If the primitive the player clicked on has a sit target and that sit target is not full, that sit target is used.
            // If the primitive the player clicked on has no sit target, and one or more other linked objects have sit targets that are not full, the sit target of the object with the lowest link number will be used.

            // Get our own copy of the part array, and sort into the order we want to test
            SceneObjectPart[] partArray = targetPart.ParentGroup.Parts;
            Array.Sort(partArray, delegate(SceneObjectPart p1, SceneObjectPart p2)
                       {
                           // we want the originally selected part first, then the rest in link order -- so make the selected part link num (-1)
                           int linkNum1 = p1==targetPart ? -1 : p1.LinkNum;
                           int linkNum2 = p2==targetPart ? -1 : p2.LinkNum;
                           return linkNum1 - linkNum2;
                       }
                );

            //look for prims with explicit sit targets that are available
            foreach (SceneObjectPart part in partArray)
            {
                if (part.IsSitTargetSet && part.SitTargetAvatar == UUID.Zero && part.SitActiveRange >= 0)
                {
                    //switch the target to this prim
                    return part;
                }
            }

            // no explicit sit target found - use original target
            return targetPart;
        }

        private void SendSitResponse(UUID targetID, Vector3 offset, Quaternion sitOrientation)
        {
            Vector3 cameraEyeOffset = Vector3.Zero;
            Vector3 cameraAtOffset = Vector3.Zero;
            bool forceMouselook = false;

            SceneObjectPart part = FindNextAvailableSitTarget(targetID);
            if (part == null)
                return;

            float range = part.SitActiveRange;
            if (range < 0)
                return;

            Vector3 pos = part.AbsolutePosition + offset;
            if (range > 1e-5f)
            {
                if (Vector3.DistanceSquared(AbsolutePosition, pos) > range * range)
                    return;
            }

            if (PhysicsActor != null)
                _sitAvatarHeight = PhysicsActor.Size.Z * 0.5f;

            if (part.IsSitTargetSet && part.SitTargetAvatar == UUID.Zero)
            {
                offset = part.SitTargetPosition;
                sitOrientation = part.SitTargetOrientation;
            }
            else
            {
                if (PhysicsSit(part,offset)) // physics engine
                    return;

                if (Vector3.DistanceSquared(AbsolutePosition, pos) > 100f)
                    return;

                AbsolutePosition = pos + new Vector3(0.0f, 0.0f, _sitAvatarHeight);
            }

            if (PhysicsActor != null)
                RemoveFromPhysicalScene();

            if (_movingToTarget)
                ResetMoveToTarget();

            Velocity = Vector3.Zero;
            _AngularVelocity = Vector3.Zero;

            part.AddSittingAvatar(this);

            cameraAtOffset = part.GetCameraAtOffset();
            cameraEyeOffset = part.GetCameraEyeOffset();

            forceMouselook = part.GetForceMouselook();

            if (!part.IsRoot)
            {
                sitOrientation = part.RotationOffset * sitOrientation;
                offset = offset * part.RotationOffset;
                offset += part.OffsetPosition;

                if (cameraAtOffset == Vector3.Zero && cameraEyeOffset == Vector3.Zero)
                {
                    cameraAtOffset = part.ParentGroup.RootPart.GetCameraAtOffset();
                    cameraEyeOffset = part.ParentGroup.RootPart.GetCameraEyeOffset();
                }
                else
                {
                    cameraAtOffset = cameraAtOffset * part.RotationOffset;
                    cameraAtOffset += part.OffsetPosition;
                    cameraEyeOffset = cameraEyeOffset * part.RotationOffset;
                    cameraEyeOffset += part.OffsetPosition;
                }
            }

            sitOrientation = part.ParentGroup.RootPart.RotationOffset * sitOrientation;
            ControllingClient.SendSitResponse(
                part.ParentGroup.UUID, offset, sitOrientation,
                true, cameraAtOffset, cameraEyeOffset, forceMouselook);

            _requestedSitTargetUUID = part.UUID;

            HandleAgentSit(ControllingClient, UUID);

            // Moved here to avoid a race with default sit anim
            // The script event needs to be raised after the default sit anim is set.
            //part.ParentGroup.TriggerScriptChangedEvent(Changed.LINK);
            //_scene.EventManager.TriggerParcelPrimCountTainted(); // update select/ sat on
        }

        public void HandleAgentRequestSit(IClientAPI remoteClient, UUID agentID, UUID targetID, Vector3 offset)
        {
            if (IsChildAgent)
                return;

            if (ParentID != 0)
            {
                if (ParentPart.UUID == targetID)
                    return; // already sitting here, ignore

                StandUp();
            }
            else if(SitGround)
                StandUp();

            SceneObjectPart part = FindNextAvailableSitTarget(targetID);

            if (part != null)
            {
                _requestedSitTargetID = part.LocalId;
                _requestedSitTargetUUID = part.UUID;
            }
            else
            {
                _log.Warn("Sit requested on unknown object: " + targetID.ToString());
            }

            SendSitResponse(targetID, offset, Quaternion.Identity);
        }

        // returns  false if does not suport so older sit can be tried
        public bool PhysicsSit(SceneObjectPart part, Vector3 offset)
        {
            if (part == null || part.ParentGroup.IsAttachment)
                return true;

            if ( _scene.PhysicsScene == null)
                return false;

            if (part.PhysActor == null)
            {
                // none physics shape
                if (part.PhysicsShapeType == (byte)PhysicsShapeType.None)
                    ControllingClient.SendAlertMessage(" There is no suitable surface to sit on, try another spot.");
                else
                { // non physical phantom  TODO
                    //ControllingClient.SendAlertMessage(" There is no suitable surface to sit on, try another spot.");
                    return false;
                }
                return true;
            }

            if (_scene.PhysicsScene.SitAvatar(part.PhysActor, AbsolutePosition, CameraPosition, offset, new Vector3(0.35f, 0, 0.65f), PhysicsSitResponse) != 0)
            {
                return true;
            }

            return false;
        }

        private bool CanEnterLandPosition(Vector3 testPos)
        {
            ILandObject land = _scene.LandChannel.GetLandObject(testPos.X, testPos.Y);

            if (land == null || land.LandData.Name == "NO_LAND")
                return true;

            return land.CanBeOnThisLand(UUID,testPos.Z);
        }

        // status
        //          < 0 ignore
        //          0   bad sit spot
        public void PhysicsSitResponse(int status, uint partID, Vector3 offset, Quaternion Orientation)
        {
            if (status < 0)
                return;

            if (status == 0)
            {
                ControllingClient.SendAlertMessage(" There is no suitable surface to sit on, try another spot.");
                return;
            }

            SceneObjectPart part = _scene.GetSceneObjectPart(partID);
            if (part == null)
                return;

            Vector3 targetPos = part.GetWorldPosition() + offset * part.GetWorldRotation();
            if(!CanEnterLandPosition(targetPos))
            {
                ControllingClient.SendAlertMessage(" Sit position on restricted land, try another spot");
                return;
            }

            RemoveFromPhysicalScene();

            if (_movingToTarget)
                ResetMoveToTarget();

            Velocity = Vector3.Zero;
            _AngularVelocity = Vector3.Zero;

            _requestedSitTargetID = 0;
            part.AddSittingAvatar(this);

            ParentPart = part;
            ParentID = part.LocalId;

            Vector3 cameraAtOffset = part.GetCameraAtOffset();
            Vector3 cameraEyeOffset = part.GetCameraEyeOffset();

            if (!part.IsRoot)
            {
                Orientation = part.RotationOffset * Orientation;
                offset = offset * part.RotationOffset;
                offset += part.OffsetPosition;

                if (cameraAtOffset == Vector3.Zero && cameraEyeOffset == Vector3.Zero)
                {
                    cameraAtOffset = part.ParentGroup.RootPart.GetCameraAtOffset();
                    cameraEyeOffset = part.ParentGroup.RootPart.GetCameraEyeOffset();
                }
                else
                {
                    cameraAtOffset = cameraAtOffset * part.RotationOffset;
                    cameraAtOffset += part.OffsetPosition;
                    cameraEyeOffset = cameraEyeOffset * part.RotationOffset;
                    cameraEyeOffset += part.OffsetPosition;
                }
            }

            _bodyRot = Orientation;
            _pos = offset;

            Orientation = part.ParentGroup.RootPart.RotationOffset * Orientation;

            ControllingClient.SendSitResponse(
                part.ParentGroup.UUID, offset, Orientation, true, cameraAtOffset, cameraEyeOffset, part.GetForceMouselook());

            SendAvatarDataToAllAgents();

            if (status == 3)
                sitAnimation = "SIT_GROUND";
            else
                sitAnimation = "SIT";

            Animator.SetMovementAnimations("SIT");
            part.ParentGroup.TriggerScriptChangedEvent(Changed.LINK);
            _scene.EventManager.TriggerParcelPrimCountTainted(); // update select/ sat on
        }

        public void HandleAgentSit(IClientAPI remoteClient, UUID agentID)
        {
            if (IsChildAgent)
                return;

            if(IsSitting)
                return;

            SceneObjectPart part = _scene.GetSceneObjectPart(_requestedSitTargetID);

            if (part != null)
            {
                if (part.ParentGroup.IsAttachment)
                {
                    _log.WarnFormat(
                        "[SCENE PRESENCE]: Avatar {0} tried to sit on part {1} from object {2} in {3} but this is an attachment for avatar id {4}",
                        Name, part.Name, part.ParentGroup.Name, Scene.Name, part.ParentGroup.AttachedAvatar);

                    return;
                }

                RemoveFromPhysicalScene();

                if (part.SitTargetAvatar == UUID)
                {
                    Vector3 sitTargetPos = part.SitTargetPosition;
                    Quaternion sitTargetOrient = part.SitTargetOrientation;

//                        _log.DebugFormat(
//                            "[SCENE PRESENCE]: Sitting {0} at sit target {1}, {2} on {3} {4}",
//                            Name, sitTargetPos, sitTargetOrient, part.Name, part.LocalId);

                    double x, y, z, m;
                    Vector3 sitOffset;
                    Quaternion r = sitTargetOrient;

                    Vector3 newPos;
                    Quaternion newRot;

                    if (LegacySitOffsets)
                    {
                        double m1,m2;

                        m1 = r.X * r.X + r.Y * r.Y;
                        m2 = r.Z * r.Z + r.W * r.W;

                        // Rotate the vector <0, 0, 1>
                        x = 2 * (r.X * r.Z + r.Y * r.W);
                        y = 2 * (-r.X * r.W + r.Y * r.Z);
                        z = m2 - m1;

                        // Set m to be the square of the norm of r.
                        m = m1 + m2;

                        // This constant is emperically determined to be what is used in SL.
                        // See also http://opensimulator.org/mantis/view.php?id=7096
                        double offset = 0.05;

                        // Normally m will be ~ 1, but if someone passed a handcrafted quaternion
                        // to llSitTarget with values so small that squaring them is rounded off
                        // to zero, then m could be zero. The result of this floating point
                        // round off error (causing us to skip this impossible normalization)
                        // is only 5 cm.
                        if (m > 0.000001)
                        {
                            offset /= m;
                        }

                        Vector3 up = new Vector3((float)x, (float)y, (float)z);
                        sitOffset = up * (float)offset;
                        newPos = sitTargetPos - sitOffset + SIT_TARGET_ADJUSTMENT;
                    }
                    else
                    {
                        m = r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W;

                        if (Math.Abs(1.0 - m) > 0.000001)
                        {
                            if(m != 0)
                            {
                                m = 1.0 / Math.Sqrt(m);
                                r.X *= (float)m;
                                r.Y *= (float)m;
                                r.Z *= (float)m;
                                r.W *= (float)m;
                            }
                            else
                            {
                                r.X = 0.0f;
                                r.Y = 0.0f;
                                r.Z = 0.0f;
                                r.W = 1.0f;
                                m = 1.0f;
                            }
                        }

                        x = 2 * (r.X * r.Z + r.Y * r.W);
                        y = 2 * (-r.X * r.W + r.Y * r.Z);
                        z = -r.X * r.X - r.Y * r.Y + r.Z * r.Z + r.W * r.W;
                        Vector3 up = new Vector3((float)x, (float)y, (float)z);
                        sitOffset = up * Appearance.AvatarHeight * 0.02638f;
                        newPos = sitTargetPos + sitOffset + SIT_TARGET_ADJUSTMENT;
                    }

                    if (part.IsRoot)
                    {
                        newRot = sitTargetOrient;
                    }
                    else
                    {
                        newPos = newPos * part.RotationOffset;
                        newRot = part.RotationOffset * sitTargetOrient;
                    }

                    newPos += part.OffsetPosition;
                    _pos = newPos;
                    Rotation = newRot;

                    //                    ParentPosition = part.AbsolutePosition;
                }
                else
                {
                    // An viewer expects to specify sit positions as offsets to the root prim, even if a child prim is
                    // being sat upon.
                    _pos -= part.GroupPosition;
                }

                part.AddSittingAvatar(this);
                ParentPart = part;
                ParentID = _requestedSitTargetID;

                _AngularVelocity = Vector3.Zero;
                Velocity = Vector3.Zero;

                _requestedSitTargetID = 0;

                SendAvatarDataToAllAgents();

                if (string.IsNullOrEmpty(part.SitAnimation))
                    sitAnimation = "SIT";
                else
                    sitAnimation = part.SitAnimation;

                Animator.SetMovementAnimations("SIT");
//                TriggerScenePresenceUpdated();
                part.ParentGroup.TriggerScriptChangedEvent(Changed.LINK);
                _scene.EventManager.TriggerParcelPrimCountTainted(); // update select/ sat on
            }
        }

        public void HandleAgentSitOnGround()
        {
            if (IsChildAgent)
                return;

            sitAnimation = "SIT_GROUND_CONSTRAINED";
            SitGround = true;
            RemoveFromPhysicalScene();

            _AngularVelocity = Vector3.Zero;
            Velocity = Vector3.Zero;

            Animator.SetMovementAnimations("SITGROUND");
            TriggerScenePresenceUpdated();
        }

        /// <summary>
        /// Event handler for the 'Always run' setting on the client
        /// Tells the physics plugin to increase speed of movement.
        /// </summary>
        public void HandleSetAlwaysRun(IClientAPI remoteClient, bool pSetAlwaysRun)
        {
            SetAlwaysRun = pSetAlwaysRun;
        }

        public void HandleStartAnim(IClientAPI remoteClient, UUID animID)
        {
            Animator.AddAnimation(animID, UUID.Zero);
            TriggerScenePresenceUpdated();
        }

        public void HandleStopAnim(IClientAPI remoteClient, UUID animID)
        {
            Animator.RemoveAnimation(animID, false);
            TriggerScenePresenceUpdated();
        }

        public void avnHandleChangeAnim(UUID animID, bool addRemove,bool sendPack)
        {
            Animator.avnChangeAnim(animID, addRemove, sendPack);
        }

        /// <summary>
        /// Rotate the avatar to the given rotation and apply a movement in the given relative vector
        /// </summary>
        /// <param name="vec">The vector in which to move.  This is relative to the rotation argument</param>
        /// <param name="thisAddSpeedModifier">
        /// Optional additional speed modifier for this particular add.  Default is 1</param>
        public void AddNewMovement(Vector3 vec, float thisAddSpeedModifier = 1, bool breaking = false)
        {
            //            _log.DebugFormat(
            //                "[SCENE PRESENCE]: Adding new movement {0} with rotation {1}, thisAddSpeedModifier {2} for {3}",
            //                vec, Rotation, thisAddSpeedModifier, Name);
            _delayedStop = -1;
            // rotate from avatar coord space to world
            Quaternion rot = Rotation;
            if (!Flying && PresenceType != PresenceType.Npc)
            {
                // force rotation to be around Z only, if not flying
                // needed for mouselook
                rot.X = 0;
                rot.Y = 0;
            }

            Vector3 direc = vec * rot;
            direc.Normalize();

            if ((vec.Z == 0f) && !Flying)
                direc.Z = 0f; // Prevent camera WASD up.

            bool notmvtrgt = !_movingToTarget || _moveToSpeed <= 0;
            // odd rescalings
            if(notmvtrgt)
                direc *= 4.096f * SpeedModifier * thisAddSpeedModifier;
            else
                direc *= _moveToSpeed;

            //            _log.DebugFormat("[SCENE PRESENCE]: Force to apply before modification was {0} for {1}", direc, Name);

            if (Animator.currentControlState == ScenePresenceAnimator.motionControlStates.falling
                    && (PhysicsActor == null || !PhysicsActor.PIDHoverActive))
            {
                if (breaking)
                    direc.Z = -9999f; //hack to tell physics to stop on Z
                else
                    direc = Vector3.Zero;
            }
            else if (Flying)
            {
                if (IsColliding && direc.Z < 0)
                    // landing situation, prevent avatar moving or it may fail to land
                    // animator will handle this condition and do the land
                    direc = Vector3.Zero;
                else if(notmvtrgt)
                    direc *= 4.0f;
            }
            else if (IsColliding)
            {
                if (direc.Z > 2.0f && notmvtrgt) // reinforce jumps
                {
                    direc.Z *= 2.6f;
                }
                else if (direc.Z < 0) // on a surface moving down (pg down) only changes animation
                    direc.Z = 0;
            }

            TargetVelocity = direc;
            Animator.UpdateMovementAnimations();
        }

        #endregion

        #region Overridden Methods

       const float ROTATION_TOLERANCE = 0.01f;
       const float VELOCITY_TOLERANCE = 0.1f;
       const float LOWVELOCITYSQ = 0.1f;
       const float POSITION_LARGETOLERANCE = 5f;
       const float POSITION_SMALLTOLERANCE = 0.05f;

        public override void Update()
        {
            if (IsDeleted)
                return;

            if (NeedInitialData > 0)
            {
                SendInitialData();
                return;
            }

            if (IsChildAgent || IsInTransit)
                return;

            CheckForBorderCrossing();

            if (_movingToTarget)
            {
                _delayedStop = -1;
                Vector3 control = Vector3.Zero;
                if(HandleMoveToTargetUpdate(0.5f, ref control))
                    AddNewMovement(control);
            }
            else if(_delayedStop > 0)
            {
                if(IsSatOnObject)
                    _delayedStop = -1;  
                else
                if(Util.GetTimeStampMS() > _delayedStop)
                    AddNewMovement(Vector3.Zero);
            }

            if (Appearance.AvatarSize != _lastSize)
                SendAvatarDataToAllAgents();

            // Send terse position update if not sitting and position, velocity, or rotation
            //      has changed significantly from last sent update
            if (!IsSatOnObject)
            {
                // this does need to be more complex later
                Vector3 vel = Velocity;
                Vector3 dpos = _pos - _lastPosition;
                if(     State != _lastState ||
                        !vel.ApproxEquals(_lastVelocity) ||
                        !_bodyRot.ApproxEquals(_lastRotation) ||

                        (vel ==  Vector3.Zero && _lastVelocity != Vector3.Zero) ||

                        Math.Abs(dpos.X) > POSITION_LARGETOLERANCE ||
                        Math.Abs(dpos.Y) > POSITION_LARGETOLERANCE ||
                        Math.Abs(dpos.Z) > POSITION_LARGETOLERANCE ||

                        (  (Math.Abs(dpos.X) > POSITION_SMALLTOLERANCE ||
                            Math.Abs(dpos.Y) > POSITION_SMALLTOLERANCE ||
                            Math.Abs(dpos.Z) > POSITION_SMALLTOLERANCE)
                            && vel.LengthSquared() < LOWVELOCITYSQ
                        ) ||

                        Math.Abs(CollisionPlane.X - _lastCollisionPlane.X) > POSITION_SMALLTOLERANCE ||
                        Math.Abs(CollisionPlane.Y - _lastCollisionPlane.Y) > POSITION_SMALLTOLERANCE ||
                        Math.Abs(CollisionPlane.W - _lastCollisionPlane.W) > POSITION_SMALLTOLERANCE
                        )
                {
                    SendTerseUpdateToAllClients();
                }
            }
            CheckForSignificantMovement();
        }

        #endregion

        #region Update Client(s)

        public void SendUpdateToAgent(ScenePresence p)
        {
            IClientAPI remoteClient = p.ControllingClient;

            if (remoteClient.IsActive)
            {
                //_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", _pos, Rotation, _velocity);
                remoteClient.SendEntityUpdate(this, PrimUpdateFlags.FullUpdate);
                _scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        public void SendFullUpdateToClient(IClientAPI remoteClient)
        {
            if (remoteClient.IsActive)
            {
                //_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", _pos, Rotation, _velocity);
                remoteClient.SendEntityUpdate(this, PrimUpdateFlags.FullUpdate);
                _scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        // this is diferente from SendTerseUpdateToClient
        // this sends bypassing entities updates
        public void SendAgentTerseUpdate(ISceneEntity p)
        {
            ControllingClient.SendAgentTerseUpdate(p);
        }

        /// <summary>
        /// Sends a location update to the client connected to this scenePresence
        /// via entity updates
        /// </summary>
        /// <param name="remoteClient"></param>
        public void SendTerseUpdateToClient(IClientAPI remoteClient)
        {
            // If the client is inactive, it's getting its updates from another
            // server.
            if (remoteClient.IsActive)
            {
                //_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", _pos, Rotation, _velocity);
                remoteClient.SendEntityUpdate(
                    this,
                    PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                    | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity);

                _scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        public void SendTerseUpdateToAgent(ScenePresence p)
        {
            IClientAPI remoteClient = p.ControllingClient;

            if (!remoteClient.IsActive)
                return;

            if (ParcelHideThisAvatar && p.currentParcelUUID != currentParcelUUID && !p.IsViewerUIGod)
                return;

            //_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", _pos, Rotation, _velocity);
            remoteClient.SendEntityUpdate(
                this,
                PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity);

            _scene.StatsReporter.AddAgentUpdates(1);
        }

        public void SendTerseUpdateToAgentNF(ScenePresence p)
        {
            IClientAPI remoteClient = p.ControllingClient;
            if (remoteClient.IsActive)
            {
                //_log.DebugFormat("[SCENE PRESENCE]: " + Name + " sending TerseUpdate to " + remoteClient.Name + " : Pos={0} Rot={1} Vel={2}", _pos, Rotation, _velocity);
                remoteClient.SendEntityUpdate(this,
                    PrimUpdateFlags.Position | PrimUpdateFlags.Rotation | PrimUpdateFlags.Velocity
                    | PrimUpdateFlags.Acceleration | PrimUpdateFlags.AngularVelocity);
                _scene.StatsReporter.AddAgentUpdates(1);
            }
        }

        /// <summary>
        /// Send a location/velocity/accelleration update to all agents in scene
        /// </summary>
        public void SendTerseUpdateToAllClients()
        {
            _lastState = State;
            _lastPosition = _pos;
            _lastRotation = _bodyRot;
            _lastVelocity = Velocity;
            _lastCollisionPlane = CollisionPlane;

            _scene.ForEachScenePresence(SendTerseUpdateToAgent);
            // Update the "last" values
            TriggerScenePresenceUpdated();
        }

        public void SetSendCoarseLocationMethod(SendCoarseLocationsMethod d)
        {
            _sendCoarseLocationsMethod = d;
        }

        public void SendCoarseLocations(List<Vector3> coarseLocations, List<UUID> avatarUUIDs)
        {
            _sendCoarseLocationsMethod?.Invoke(_scene.RegionInfo.originRegionID, this, coarseLocations, avatarUUIDs);
        }

        public void SendCoarseLocationsDefault(UUID sceneId, ScenePresence p, List<Vector3> coarseLocations, List<UUID> avatarUUIDs)
        {
            ControllingClient.SendCoarseLocationUpdate(avatarUUIDs, coarseLocations);
        }

        public void RegionHandShakeReply (IClientAPI client)
        {
            if(IsNPC)
                return;

            lock (_completeMovementLock)
            {
                if(_gotRegionHandShake)
                    return;
                _gotRegionHandShake = true;
                NeedInitialData = 2;
            }
        }

        private void SendInitialData()
        {
            //wait for region handshake
            if (NeedInitialData < 2)
                return;

            uint flags = ControllingClient.GetViewerCaps();
            if ((flags & 0x1000) == 0) // wait for seeds sending
                return;

            // give some extra time to make sure viewers did process seeds
            if (++NeedInitialData < 6) // needs fix if update rate changes on heartbeat
               return;

            NeedInitialData = -1;

            bool selfappearance = (flags & 4) != 0;

            // this should enqueued on the client processing job to save threads
            Util.FireAndForget(delegate
            {
                if(!IsChildAgent)
                {
                    // close v1 sender region obsolete
                    if (!string.IsNullOrEmpty(_callbackURI))
                    {
                        _log.DebugFormat(
                            "[SCENE PRESENCE({0})]: Releasing {1} {2} with old callback to {3}",
                            Scene.RegionInfo.RegionName, Name, UUID, _callbackURI);

                        UUID originID;

                        lock (_originRegionIDAccessLock)
                            originID = _originRegionID;

                        Scene.SimulationService.ReleaseAgent(originID, UUID, _callbackURI);
                        _callbackURI = null;
                    }
                    // v0.7 close HG sender region
                    else if (!string.IsNullOrEmpty(_newCallbackURI))
                    {
                        _log.DebugFormat(
                            "[SCENE PRESENCE({0})]: Releasing {1} {2} with callback to {3}",
                            Scene.RegionInfo.RegionName, Name, UUID, _newCallbackURI);

                        UUID originID;

                        lock (_originRegionIDAccessLock)
                            originID = _originRegionID;

                        Scene.SimulationService.ReleaseAgent(originID, UUID, _newCallbackURI);
                        _newCallbackURI = null;
                    }
                    IEntityTransferModule _agentTransfer = _scene.RequestModuleInterface<IEntityTransferModule>();
                    if (_agentTransfer != null)
                    {
                        _agentTransfer.CloseOldChildAgents(this);
                    }
                }

                _log.DebugFormat("[SCENE PRESENCE({0})]: SendInitialData for {1}", Scene.RegionInfo.RegionName, UUID);
                if (_teleportFlags <= 0)
                {
                    Scene.SendLayerData(ControllingClient);

                    ILandChannel landch = _scene.LandChannel;
                    if (landch != null)
                        landch.sendClientInitialLandInfo(ControllingClient, true);
                }

                SendOtherAgentsAvatarFullToMe();

                if (_scene.ObjectsCullingByDistance)
                {
                    _reprioritizationBusy = true;
                    _reprioritizationLastPosition = AbsolutePosition;
                    _reprioritizationLastDrawDistance = DrawDistance;

                    ControllingClient.ReprioritizeUpdates();
                    _reprioritizationLastTime = Util.EnvironmentTickCount();
                    _reprioritizationBusy = false;
                }
                else
                {
                    //bool cacheCulling = (flags & 1) != 0;
                    bool cacheEmpty = (flags & 2) != 0;;

                    EntityBase[] entities = Scene.Entities.GetEntities();
                    if(cacheEmpty)
                    {
                        foreach (EntityBase e in entities)
                        {
                            if (e != null && e is SceneObjectGroup && !((SceneObjectGroup)e).IsAttachment)
                                ((SceneObjectGroup)e).SendFullAnimUpdateToClient(ControllingClient);
                        }
                    }
                    else
                    {
                        foreach (EntityBase e in entities)
                        {
                            if (e != null && e is SceneObjectGroup && !((SceneObjectGroup)e).IsAttachment)
                            {
                                SceneObjectGroup grp = e as SceneObjectGroup;
                                if(grp.IsViewerCachable)
                                    grp.SendUpdateProbes(ControllingClient);
                                else
                                   grp.SendFullAnimUpdateToClient(ControllingClient);
                            }
                        }
                    }

                    _reprioritizationLastPosition = AbsolutePosition;
                    _reprioritizationLastDrawDistance = DrawDistance;
                    _reprioritizationLastTime = Util.EnvironmentTickCount() + 15000; // delay it

                    _reprioritizationBusy = false;
                }

                if (!IsChildAgent)
                {
                    // Create child agents in neighbouring regions
                    IEntityTransferModule _agentTransfer = _scene.RequestModuleInterface<IEntityTransferModule>();
                    if (_agentTransfer != null)
                    {
                        _agentTransfer.EnableChildAgents(this);
                    }

                    _lastChildUpdatesTime = Util.EnvironmentTickCount() + 10000;
                    _lastChildAgentUpdatePosition = AbsolutePosition;
                    _lastChildAgentCheckPosition = _lastChildAgentUpdatePosition;
                    _lastChildAgentUpdateDrawDistance = DrawDistance;
                    _lastRegionsDrawDistance = RegionViewDistance;

                    _lastChildAgentUpdateGodLevel = GodController.ViwerUIGodLevel;
                    _childUpdatesBusy = false; // allow them
                }
            });

        }

        /// <summary>
        /// Send avatar full data appearance and animations for all other root agents to this agent, this agent
        /// can be either a child or root
        /// </summary>
        public void SendOtherAgentsAvatarFullToMe()
        {
            int count = 0;
            _scene.ForEachRootScenePresence(delegate(ScenePresence p)
            {
                // only send information about other root agents
                if (p.UUID == UUID)
                    return;

                // get the avatar, then a kill if can't see it
                p.SendInitialAvatarDataToAgent(this);

                if (p.ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !IsViewerUIGod)
                    return;

                p.SendAppearanceToAgentNF(this);
                p.SendAnimPackToAgentNF(this);
                p.SendAttachmentsToAgentNF(this);
                count++;
            });

            _scene.StatsReporter.AddAgentUpdates(count);
        }

        /// <summary>
        /// Send this agent's avatar data to all other root and child agents in the scene
        /// This agent must be root. This avatar will receive its own update.
        /// </summary>
        public void SendAvatarDataToAllAgents()
        {
            //_log.DebugFormat("[SCENE PRESENCE] SendAvatarDataToAllAgents: {0} ({1})", Name, UUID);
            // only send update from root agents to other clients; children are only "listening posts"
            if (IsChildAgent)
            {
                _log.WarnFormat(
                    "[SCENE PRESENCE]: Attempt to send avatar data from a child agent for {0} in {1}",
                    Name, Scene.RegionInfo.RegionName);
                return;
            }

            _lastSize = Appearance.AvatarSize;
            int count = 0;

            _scene.ForEachScenePresence(delegate(ScenePresence scenePresence)
            {
                SendAvatarDataToAgent(scenePresence);
                count++;
            });

            _scene.StatsReporter.AddAgentUpdates(count);
        }
        // sends avatar object to all clients so they cross it into region
        // then sends kills to hide
        public void SendInitialAvatarDataToAllAgents(List<ScenePresence> presences)
        {
            _lastSize = Appearance.AvatarSize;
            int count = 0;
            SceneObjectPart sitroot = null;
            if (ParentID != 0 && ParentPart != null) //  we need to send the sitting root prim
            {
                sitroot = ParentPart.ParentGroup.RootPart;
            }
            foreach (ScenePresence p in presences)
            {
                if (sitroot != null) //  we need to send the sitting root prim
                {
                    p.ControllingClient.SendEntityFullUpdateImmediate(ParentPart.ParentGroup.RootPart);
                }
                p.ControllingClient.SendEntityFullUpdateImmediate(this);
                if (p != this && ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                    // either just kill the object
                    // p.ControllingClient.SendKillObject(new List<uint> {LocalId});
                    // or also attachments viewer may still know about
                    SendKillTo(p);
                count++;
            }
            _scene.StatsReporter.AddAgentUpdates(count);
        }

        public void SendInitialAvatarDataToAgent(ScenePresence p)
        {
            if(ParentID != 0 && ParentPart != null) //  we need to send the sitting root prim
            {
                p.ControllingClient.SendEntityFullUpdateImmediate(ParentPart.ParentGroup.RootPart);
            }
            p.ControllingClient.SendEntityFullUpdateImmediate(this);
            if (p != this && ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                    // either just kill the object
                    // p.ControllingClient.SendKillObject(new List<uint> {LocalId});
                    // or also attachments viewer may still know about
                SendKillTo(p);
        }

        /// <summary>
        /// Send avatar data to an agent.
        /// </summary>
        /// <param name="avatar"></param>
        public void SendAvatarDataToAgent(ScenePresence avatar)
        {
            //_log.DebugFormat("[SCENE PRESENCE] SendAvatarDataToAgent from {0} ({1}) to {2} ({3})", Name, UUID, avatar.Name, avatar.UUID);
            if (ParcelHideThisAvatar && currentParcelUUID != avatar.currentParcelUUID && !avatar.IsViewerUIGod)
                return;
            avatar.ControllingClient.SendEntityFullUpdateImmediate(this);
        }

        public void SendAvatarDataToAgentNF(ScenePresence avatar)
        {
             avatar.ControllingClient.SendEntityFullUpdateImmediate(this);
        }

        /// <summary>
        /// Send this agent's appearance to all other root and child agents in the scene
        /// This agent must be root.
        /// </summary>
        public void SendAppearanceToAllOtherAgents()
        {
            //            _log.DebugFormat("[SCENE PRESENCE] SendAppearanceToAllOtherAgents: {0} {1}", Name, UUID);

            // only send update from root agents to other clients; children are only "listening posts"
            if (IsChildAgent)
            {
                _log.WarnFormat(
                    "[SCENE PRESENCE]: Attempt to send avatar data from a child agent for {0} in {1}",
                    Name, Scene.RegionInfo.RegionName);

                return;
            }

            int count = 0;
            _scene.ForEachScenePresence(delegate(ScenePresence scenePresence)
            {
                // only send information to other root agents
                if (scenePresence.UUID == UUID)
                    return;

                SendAppearanceToAgent(scenePresence);
                count++;
            });
            _scene.StatsReporter.AddAgentUpdates(count);
        }

        public void SendAppearanceToAgent(ScenePresence avatar)
        {
            //            _log.DebugFormat(
            //                "[SCENE PRESENCE]: Sending appearance data from {0} {1} to {2} {3}", Name, _uuid, avatar.Name, avatar.UUID);
            if (ParcelHideThisAvatar && currentParcelUUID != avatar.currentParcelUUID && !avatar.IsViewerUIGod)
                return;
            SendAppearanceToAgentNF(avatar);
        }

        public void SendAppearanceToAgentNF(ScenePresence avatar)
        {
            avatar.ControllingClient.SendAppearance(UUID, Appearance.VisualParams, Appearance.Texture.GetBakesBytes(), Appearance.AvatarPreferencesHoverZ);
        }

        public void SendAnimPackToAgent(ScenePresence p)
        {
            if (IsChildAgent || Animator == null)
                return;

            if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                return;

            Animator.SendAnimPackToClient(p.ControllingClient);
        }

        public void SendAnimPackToAgent(ScenePresence p, UUID[] animations, int[] seqs, UUID[] objectIDs)
        {
            if (IsChildAgent)
                return;

            if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                return;

            p.ControllingClient.SendAnimations(animations, seqs, ControllingClient.AgentId, objectIDs);
        }

        public void SendAnimPackToAgentNF(ScenePresence p)
        {
            if (IsChildAgent || Animator == null)
                return;
            Animator.SendAnimPackToClient(p.ControllingClient);
        }

        public void SendAnimPackToAgentNF(ScenePresence p, UUID[] animations, int[] seqs, UUID[] objectIDs)
        {
            p.ControllingClient.SendAnimations(animations, seqs, ControllingClient.AgentId, objectIDs);
        }

        public void SendAnimPack(UUID[] animations, int[] seqs, UUID[] objectIDs)
        {
            if (IsChildAgent)
                return;

            _scene.ForEachScenePresence(delegate(ScenePresence p)
            {
                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                    return;
                p.ControllingClient.SendAnimations(animations, seqs, ControllingClient.AgentId, objectIDs);
            });
        }

        #endregion

        #region Significant Movement Method

        private void checkRePrioritization()
        {
            if(IsDeleted || !ControllingClient.IsActive)
                return;

            if(_reprioritizationBusy)
                return;

            float limit = Scene.ReprioritizationDistance;
            bool byDrawdistance = Scene.ObjectsCullingByDistance;
            if(byDrawdistance)
            {
                float minregionSize = Scene.RegionInfo.RegionSizeX;
                if(minregionSize > Scene.RegionInfo.RegionSizeY)
                    minregionSize = Scene.RegionInfo.RegionSizeY;
                minregionSize *= 0.5f;
                if(DrawDistance > minregionSize && _reprioritizationLastDrawDistance > minregionSize)
                    byDrawdistance = false;
                else
                    byDrawdistance = (Math.Abs(DrawDistance - _reprioritizationLastDrawDistance) > 0.5f * limit);
            }

            int tdiff =  Util.EnvironmentTickCountSubtract(_reprioritizationLastTime);
            if(!byDrawdistance && tdiff < Scene.ReprioritizationInterval)
                return;
            // priority uses avatar position
            Vector3 pos = AbsolutePosition;
            Vector3 diff = pos - _reprioritizationLastPosition;
            limit *= limit;
            if (!byDrawdistance && diff.LengthSquared() < limit)
                return;

            _reprioritizationBusy = true;
            _reprioritizationLastPosition = pos;
            _reprioritizationLastDrawDistance = DrawDistance;

            Util.FireAndForget(
                o =>
                {
                    ControllingClient.ReprioritizeUpdates();
                    _reprioritizationLastTime = Util.EnvironmentTickCount();
                    _reprioritizationBusy = false;
                }, null, "ScenePresence.Reprioritization");
        }
        /// <summary>
        /// This checks for a significant movement and sends a coarselocationchange update
        /// </summary>
        protected void CheckForSignificantMovement()
        {
            Vector3 pos = AbsolutePosition;

            Vector3 diff = pos - posLastMove;
            if (diff.LengthSquared() > MOVEMENT)
            {
                posLastMove = pos;
                _scene.EventManager.TriggerOnClientMovement(this);
            }

            diff = pos - posLastSignificantMove;
            if (diff.LengthSquared() > SIGNIFICANT_MOVEMENT)
            {
                posLastSignificantMove = pos;
                _scene.EventManager.TriggerSignificantClientMovement(this);
            }

            if(IsNPC)
                return;

            // updates priority recalc
            checkRePrioritization();

            if(_childUpdatesBusy || RegionViewDistance == 0)
                return;

            int tdiff = Util.EnvironmentTickCountSubtract(_lastChildUpdatesTime);
            if (tdiff < CHILDUPDATES_TIME)
                return;

            bool viewchanged = Math.Abs(RegionViewDistance - _lastRegionsDrawDistance) > 32.0f;

            IEntityTransferModule _agentTransfer = _scene.RequestModuleInterface<IEntityTransferModule>();
            float dx = pos.X - _lastChildAgentCheckPosition.X;
            float dy = pos.Y - _lastChildAgentCheckPosition.Y;
            if ((_agentTransfer != null) && (viewchanged || ((dx * dx + dy * dy) > CHILDAGENTSCHECK_MOVEMENT)))
            {
                _childUpdatesBusy = true;
                _lastChildAgentCheckPosition = pos;
                _lastChildAgentUpdatePosition = pos;
                _lastChildAgentUpdateGodLevel = GodController.ViwerUIGodLevel;
                _lastChildAgentUpdateDrawDistance = DrawDistance;
                _lastRegionsDrawDistance = RegionViewDistance;
                // _lastChildAgentUpdateCamPosition = CameraPosition;

                Util.FireAndForget(
                    o =>
                    {
                        _agentTransfer.EnableChildAgents(this);
                        _lastChildUpdatesTime = Util.EnvironmentTickCount();
                        _childUpdatesBusy = false;
                    }, null, "ScenePresence.CheckChildAgents");
            }
            else
            {
                //possible KnownRegionHandles always contains current region and this check is not needed
                int minhandles = KnownRegionHandles.Contains(RegionHandle) ? 1 : 0;
                if(KnownRegionHandles.Count > minhandles)
                {
                    bool doUpdate = false;
                    if (_lastChildAgentUpdateGodLevel != GodController.ViwerUIGodLevel)
                        doUpdate = true;

                    if (Math.Abs(DrawDistance - _lastChildAgentUpdateDrawDistance) > 32.0f)
                        doUpdate = true;

                    if(!doUpdate)
                    {
                        diff = pos - _lastChildAgentUpdatePosition;
                        if (diff.LengthSquared() > CHILDUPDATES_MOVEMENT)
                            doUpdate = true;
                    }

                    if (doUpdate)
                    {
                        _childUpdatesBusy = true;
                        _lastChildAgentUpdatePosition = pos;
                        _lastChildAgentUpdateGodLevel = GodController.ViwerUIGodLevel;
                        _lastChildAgentUpdateDrawDistance = DrawDistance;
                        // _lastChildAgentUpdateCamPosition = CameraPosition;

                        AgentPosition agentpos = new AgentPosition()
                        {
                            AgentID = new UUID(UUID.Guid),
                            SessionID = ControllingClient.SessionId,
                            Size = Appearance.AvatarSize,
                            Center = CameraPosition,
                            Far = DrawDistance,
                            Position = AbsolutePosition,
                            Velocity = Velocity,
                            RegionHandle = RegionHandle,
                            GodData = GodController.State(),
                            Throttles = ControllingClient.GetThrottlesPacked(1)
                        };

                        // Let's get this out of the update loop
                        Util.FireAndForget(
                            o =>
                            {
                                _scene.SendOutChildAgentUpdates(agentpos, this);
                                _lastChildUpdatesTime = Util.EnvironmentTickCount();
                                _childUpdatesBusy = false;
                            }, null, "ScenePresence.SendOutChildAgentUpdates");
                    }
                }
            }
        }

        #endregion

        #region Border Crossing Methods

        /// <summary>
        /// Starts the process of moving an avatar into another region if they are crossing the border.
        /// </summary>
        /// <remarks>
        /// Also removes the avatar from the physical scene if transit has started.
        /// </remarks>
        protected void CheckForBorderCrossing()
        {
            // Check that we we are not a child
            if (IsChildAgent || IsInTransit)
                return;

            // If we don't have a PhysActor, we can't cross anyway
            // Also don't do this while sat, sitting avatars cross with the
            // object they sit on. ParentUUID denoted a pending sit, don't
            // interfere with it.
            if (ParentID != 0 || PhysicsActor == null || ParentUUID != UUID.Zero)
                return;

            Vector3 pos2 = AbsolutePosition;
            Vector3 vel = Velocity;

            float timeStep = 0.1f;
            pos2.X += vel.X * timeStep;
            pos2.Y += vel.Y * timeStep;
            pos2.Z += vel.Z * timeStep;

//                    _log.DebugFormat(
//                        "[SCENE PRESENCE]: Testing border check for projected position {0} of {1} in {2}",
//                        pos2, Name, Scene.Name);

            if (Scene.PositionIsInCurrentRegion(pos2))
                return;

            if (!CrossToNewRegion() && _requestedSitTargetUUID == UUID.Zero)
            {
                // we don't have entity transfer module
                Vector3 pos = AbsolutePosition;
                vel = Velocity;
                float px = pos.X;
                if (px < 0)
                    pos.X += vel.X * 2;
                else if (px > _scene.RegionInfo.RegionSizeX)
                    pos.X -= vel.X * 2;

                float py = pos.Y;
                if (py < 0)
                    pos.Y += vel.Y * 2;
                else if (py > _scene.RegionInfo.RegionSizeY)
                    pos.Y -= vel.Y * 2;

                Velocity = Vector3.Zero;
                _AngularVelocity = Vector3.Zero;
                AbsolutePosition = pos;
            }
        }

        public void CrossToNewRegionFail()
        {
            if (_requestedSitTargetUUID == UUID.Zero)
            {
                bool isFlying = Flying;
                RemoveFromPhysicalScene();

                Vector3 pos = AbsolutePosition;
                Vector3 vel = Velocity;
                float px = pos.X;
                if (px < 0)
                    pos.X += vel.X * 2;
                else if (px > _scene.RegionInfo.RegionSizeX)
                    pos.X -= vel.X * 2;

                float py = pos.Y;
                if (py < 0)
                    pos.Y += vel.Y * 2;
                else if (py > _scene.RegionInfo.RegionSizeY)
                    pos.Y -= vel.Y * 2;

                Velocity = Vector3.Zero;
                _AngularVelocity = Vector3.Zero;
                AbsolutePosition = pos;

                AddToPhysicalScene(isFlying);
            }
        }

        /// <summary>
        /// Moves the agent outside the region bounds
        /// Tells neighbor region that we're crossing to it
        /// If the neighbor accepts, remove the agent's viewable avatar from this scene
        /// set them to a child agent.
        /// </summary>
        protected bool CrossToNewRegion()
        {
            bool result = false;
//            parcelRegionCross(false);
            try
            {
                result = _scene.CrossAgentToNewRegion(this, Flying);
            }
            catch
            {
//                result = _scene.CrossAgentToNewRegion(this, false);
                return false;
            }
 //           if(!result)
 //               parcelRegionCross(true);

            return result;

        }

        /// <summary>
        /// Computes which child agents to close when the scene presence moves to another region.
        /// Removes those regions from _knownRegions.
        /// </summary>
        /// <param name="newRegionHandle">The new region's handle</param>
        /// <param name="newRegionSizeX">The new region's size x</param>
        /// <param name="newRegionSizeY">The new region's size y</param>
        /// <returns></returns>
        public List<ulong> GetChildAgentsToClose(ulong newRegionHandle, int newRegionSizeX, int newRegionSizeY)
        {
            ulong curRegionHandle = _scene.RegionInfo.RegionHandle;
            List<ulong> byebyeRegions = new List<ulong>();

            if(newRegionHandle == curRegionHandle) //??
                return byebyeRegions;

            List<ulong> knownRegions = KnownRegionHandles;
            _log.DebugFormat(
                "[SCENE PRESENCE]: Closing child agents. Checking {0} regions in {1}",
                knownRegions.Count, Scene.RegionInfo.RegionName);

            Util.RegionHandleToRegionLoc(newRegionHandle, out uint newRegionX, out uint newRegionY);

            foreach (ulong handle in knownRegions)
            {
                if(newRegionY == 0) // HG
                    byebyeRegions.Add(handle);
                else if(handle == curRegionHandle)
                {
                    continue;
                    /*
                    RegionInfo curreg = _scene.RegionInfo;
                    if (Util.IsOutsideView(255, curreg.RegionLocX, newRegionX, curreg.RegionLocY, newRegionY,
                            (int)curreg.RegionSizeX, (int)curreg.RegionSizeX, newRegionSizeX, newRegionSizeY))
                    {
                        byebyeRegions.Add(handle);
                    }
                    */
                }
                else    
                {
                    Util.RegionHandleToRegionLoc(handle, out uint x, out uint y);
                    if (_knownChildRegionsSizeInfo.TryGetValue(handle, out spRegionSizeInfo regInfo))
                    {
//                            if (Util.IsOutsideView(RegionViewDistance, x, newRegionX, y, newRegionY,
                        // for now need to close all but first order bc RegionViewDistance it the target value not ours
                        if (Util.IsOutsideView(255, x, newRegionX, y, newRegionY,
                            regInfo.sizeX, regInfo.sizeY, newRegionSizeX, newRegionSizeY))
                        {
                            byebyeRegions.Add(handle);
                        }
                    }
                    else
                    {
//                        if (Util.IsOutsideView(RegionViewDistance, x, newRegionX, y, newRegionY,
                        if (Util.IsOutsideView(255, x, newRegionX, y, newRegionY,
                            (int)Constants.RegionSize, (int)Constants.RegionSize, newRegionSizeX, newRegionSizeY))
                        {
                            byebyeRegions.Add(handle);
                        }
                    }
                }
            }
            return byebyeRegions;
        }

        public void CloseChildAgents(List<ulong> byebyeRegions)
        {
            byebyeRegions.Remove(Scene.RegionInfo.RegionHandle);
            if (byebyeRegions.Count > 0)
            {
                _log.Debug("[SCENE PRESENCE]: Closing " + byebyeRegions.Count + " child agents");

                AgentCircuitData acd = Scene.AuthenticateHandler.GetAgentCircuitData(UUID);
                string auth = string.Empty;
                if (acd != null)
                    auth = acd.SessionID.ToString();
                _scene.SceneGridService.SendCloseChildAgentConnections(ControllingClient.AgentId, auth, byebyeRegions);
            }

            foreach (ulong handle in byebyeRegions)
            {
                RemoveNeighbourRegion(handle);
                Scene.CapsModule.DropChildSeed(UUID, handle);
            }
        }

        public void closeAllChildAgents()
        {
            List<ulong> byebyeRegions = new List<ulong>();
            List<ulong> knownRegions = KnownRegionHandles;
            foreach (ulong handle in knownRegions)
            {
                if (handle != Scene.RegionInfo.RegionHandle)
                {
                    byebyeRegions.Add(handle);
                    RemoveNeighbourRegion(handle);
                    Scene.CapsModule.DropChildSeed(UUID, handle);
                }
            }

            if (byebyeRegions.Count > 0)
            {
                _log.Debug("[SCENE PRESENCE]: Closing " + byebyeRegions.Count + " child agents");

                AgentCircuitData acd = Scene.AuthenticateHandler.GetAgentCircuitData(UUID);
                string auth = string.Empty;
                if (acd != null)
                    auth = acd.SessionID.ToString();
                _scene.SceneGridService.SendCloseChildAgentConnections(ControllingClient.AgentId, auth, byebyeRegions);
            }
        }

        #endregion

        /// <summary>
        /// handle god level requests.
        /// </summary>
        public void GrantGodlikePowers(UUID token, bool godStatus)
        {
            if (IsNPC)
                return;

            bool wasgod = IsViewerUIGod;
            GodController.RequestGodMode(godStatus);
            if (wasgod != IsViewerUIGod)
                parcelGodCheck(_currentParcelUUID);
        }

        #region Child Agent Updates

        public void UpdateChildAgent(AgentData cAgentData)
        {
//            _log.Debug("   >>> ChildAgentDataUpdate <<< " + Scene.RegionInfo.RegionName);
            if (!IsChildAgent)
                return;

            CopyFrom(cAgentData);
            _updateAgentReceivedAfterTransferEvent.Set();
        }

        private static Vector3 marker = new Vector3(-1f, -1f, -1f);

        private void RaiseUpdateThrottles()
        {
            if(_capbandwidth > 0)
                return;
            _bandwidth = 4 * ControllingClient.GetAgentThrottleSilent((int)ThrottleOutPacketType.Texture);
            if(_bandwidth < 50000)
                _bandwidth = 50000;
            _bandwidthBurst = _bandwidth / 5;
        }

        /// <summary>
        /// This updates important decision making data about a child agent
        /// The main purpose is to figure out what objects to send to a child agent that's in a neighboring region
        /// </summary>
        public void UpdateChildAgent(AgentPosition cAgentData)
        {
            if (!IsChildAgent)
                return;

            GodController.SetState(cAgentData.GodData);

            RegionHandle = cAgentData.RegionHandle;
            uint rRegionX = (uint)(RegionHandle >> 40);
            uint rRegionY = (((uint)RegionHandle) >> 8);
            uint tRegionX = _scene.RegionInfo.RegionLocX;
            uint tRegionY = _scene.RegionInfo.RegionLocY;

            //_log.Debug("   >>> ChildAgentPositionUpdate <<< " + rRegionX + "-" + rRegionY);
            int shiftx = ((int)rRegionX - (int)tRegionX) * (int)Constants.RegionSize;
            int shifty = ((int)rRegionY - (int)tRegionY) * (int)Constants.RegionSize;

            Vector3 offset = new Vector3(shiftx, shifty, 0f);

            DrawDistance = cAgentData.Far;

            _pos = cAgentData.Position + offset;
            CameraPosition = cAgentData.Center + offset;

            if (cAgentData.ChildrenCapSeeds != null && cAgentData.ChildrenCapSeeds.Count > 0)
            {
                if (Scene.CapsModule != null)
                {
                    Scene.CapsModule.SetChildrenSeed(UUID, cAgentData.ChildrenCapSeeds);
                }

                KnownRegions = cAgentData.ChildrenCapSeeds;
            }

            if ((cAgentData.Throttles != null) && cAgentData.Throttles.Length > 0)
            {
                // some scaling factor
                float x = _pos.X;
                if (x > _scene.RegionInfo.RegionSizeX)
                    x -= _scene.RegionInfo.RegionSizeX;
                float y = _pos.Y;
                if (y > _scene.RegionInfo.RegionSizeY)
                    y -= _scene.RegionInfo.RegionSizeY;

                x = x * x + y * y;

                float factor = 1.0f - x * 0.3f / Constants.RegionSize / Constants.RegionSize;
                if (factor < 0.2f)
                    factor = 0.2f;

                ControllingClient.SetChildAgentThrottle(cAgentData.Throttles,factor);
            }

            //cAgentData.AVHeight;
            //_velocity = cAgentData.Velocity;
            checkRePrioritization();
        }

        public void CopyTo(AgentData cAgent, bool isCrossUpdate)
        {
            cAgent.CallbackURI = _callbackURI;
            cAgent.NewCallbackURI = _newCallbackURI;

            cAgent.AgentID = UUID;
            cAgent.RegionID = Scene.RegionInfo.RegionID;
            cAgent.SessionID = ControllingClient.SessionId;

            cAgent.Position = AbsolutePosition;
            cAgent.Velocity = _velocity;
            cAgent.Center = CameraPosition;
            cAgent.AtAxis = CameraAtAxis;
            cAgent.LeftAxis = CameraLeftAxis;
            cAgent.UpAxis = CameraUpAxis;

            cAgent.Far = DrawDistance;
            cAgent.GodData = GodController.State();

            // Throttles
            cAgent.Throttles = ControllingClient.GetThrottlesPacked(1);

            cAgent.HeadRotation = _headrotation;
            cAgent.BodyRotation = Rotation;
            cAgent.ControlFlags = (uint)_AgentControlFlags;

            cAgent.AlwaysRun = SetAlwaysRun;

            // make clear we want the all thing
            cAgent.Appearance = new AvatarAppearance(Appearance,true,true);

            cAgent.ParentPart = ParentUUID;
            cAgent.SitOffset = PrevSitOffset;

            lock (scriptedcontrols)
            {
                ControllerData[] controls = new ControllerData[scriptedcontrols.Count];
                int i = 0;

                foreach (ScriptControllers c in scriptedcontrols.Values)
                {
                    controls[i++] = new ControllerData(c.objectID, c.itemID, (uint)c.ignoreControls, (uint)c.eventControls);
                }
                cAgent.Controllers = controls;
            }

            // Animations
            try
            {
                cAgent.Anims = Animator.Animations.ToArray();
            }
            catch { }
            cAgent.DefaultAnim = Animator.Animations.DefaultAnimation;
            cAgent.AnimState = Animator.Animations.ImplicitDefaultAnimation;

            cAgent.MovementAnimationOverRides = Overrides.CloneAOPairs();

            cAgent.MotionState = (byte)Animator.currentControlState;

            if (Scene.AttachmentsModule != null)
                Scene.AttachmentsModule.CopyAttachments(this, cAgent);

            if(isCrossUpdate)
            {
                cAgent.CrossingFlags = _crossingFlags;
                cAgent.CrossingFlags |= 1;
                cAgent.CrossExtraFlags = 0;
                if((LastCommands & ScriptControlled.CONTROL_LBUTTON) != 0)
                    cAgent.CrossExtraFlags |= 1;
                if((LastCommands & ScriptControlled.CONTROL_ML_LBUTTON) != 0)
                    cAgent.CrossExtraFlags |= 2;
            }
            else
                 cAgent.CrossingFlags = 0;

            if(isCrossUpdate)
            {
                //cAgent.agentCOF = COF;
                cAgent.ActiveGroupID = ControllingClient.ActiveGroupId;
                cAgent.ActiveGroupName = ControllingClient.ActiveGroupName;
                if(Grouptitle == null)
                    cAgent.ActiveGroupTitle = string.Empty;
                else
                    cAgent.ActiveGroupTitle = Grouptitle;
            }
        }

        private void CopyFrom(AgentData cAgent)
        {
            _callbackURI = cAgent.CallbackURI;
            _newCallbackURI = cAgent.NewCallbackURI;
            //            _log.DebugFormat(
            //                "[SCENE PRESENCE]: Set callback for {0} in {1} to {2} in CopyFrom()",
            //                Name, _scene.RegionInfo.RegionName, _callbackURI);

            GodController.SetState(cAgent.GodData);

            _pos = cAgent.Position;
            _velocity = cAgent.Velocity;
            CameraPosition = cAgent.Center;
            CameraAtAxis = cAgent.AtAxis;
            CameraLeftAxis = cAgent.LeftAxis;
            CameraUpAxis = cAgent.UpAxis;

            Quaternion camRot = Util.Axes2Rot(CameraAtAxis, CameraLeftAxis, CameraUpAxis);
            CameraRotation = camRot;

            ParentUUID = cAgent.ParentPart;
            PrevSitOffset = cAgent.SitOffset;

            // When we get to the point of re-computing neighbors everytime this
            // changes, then start using the agent's drawdistance rather than the
            // region's draw distance.
            DrawDistance = cAgent.Far;
            //DrawDistance = Scene.DefaultDrawDistance;

            if (cAgent.ChildrenCapSeeds != null && cAgent.ChildrenCapSeeds.Count > 0)
            {
                if (Scene.CapsModule != null)
                {
                    Scene.CapsModule.SetChildrenSeed(UUID, cAgent.ChildrenCapSeeds);
                }
                KnownRegions = cAgent.ChildrenCapSeeds;
            }

            if ((cAgent.Throttles != null) && cAgent.Throttles.Length > 0)
                ControllingClient.SetChildAgentThrottle(cAgent.Throttles, 1.0f);

            _headrotation = cAgent.HeadRotation;
            Rotation = cAgent.BodyRotation;
            _AgentControlFlags = (AgentManager.ControlFlags)cAgent.ControlFlags;

            SetAlwaysRun = cAgent.AlwaysRun;

            Appearance = new AvatarAppearance(cAgent.Appearance, true, true);
/*
            bool isFlying = ((_AgentControlFlags & AgentManager.ControlFlags.AGENT_CONTROL_FLY) != 0);

            if (PhysicsActor != null)
            {
                RemoveFromPhysicalScene();
                AddToPhysicalScene(isFlying);
            }
*/

            if (Scene.AttachmentsModule != null)
                Scene.AttachmentsModule.CopyAttachments(cAgent, this);

            try
            {
                lock (scriptedcontrols)
                {
                    if (cAgent.Controllers != null)
                    {
                        scriptedcontrols.Clear();
                        IgnoredControls = ScriptControlled.CONTROL_ZERO;

                        foreach (ControllerData c in cAgent.Controllers)
                        {
                            ScriptControllers sc = new ScriptControllers()
                            {
                                objectID = c.ObjectID,
                                itemID = c.ItemID,
                                ignoreControls = (ScriptControlled)c.IgnoreControls,
                                eventControls = (ScriptControlled)c.EventControls
                            };

                            scriptedcontrols[sc.itemID] = sc;
                            IgnoredControls |= sc.ignoreControls; // this is not correct, aparently only last applied should count
                        }
                    }
                }
            }
            catch { }

            // we are losing animator somewhere
            if (Animator == null)
                Animator = new ScenePresenceAnimator(this);
            else
                Animator.ResetAnimations();

            Overrides.CopyAOPairsFrom(cAgent.MovementAnimationOverRides);
            int nanim = ControllingClient.NextAnimationSequenceNumber;
            // FIXME: Why is this null check necessary?  Where are the cases where we get a null Anims object?
            if (cAgent.DefaultAnim != null)
            {
                if (cAgent.DefaultAnim.SequenceNum > nanim)
                    nanim = cAgent.DefaultAnim.SequenceNum;
                Animator.Animations.SetDefaultAnimation(cAgent.DefaultAnim.AnimID, cAgent.DefaultAnim.SequenceNum, UUID.Zero);
            }
            if (cAgent.AnimState != null)
            {
                if (cAgent.AnimState.SequenceNum > nanim)
                    nanim = cAgent.AnimState.SequenceNum;
                Animator.Animations.SetImplicitDefaultAnimation(cAgent.AnimState.AnimID, cAgent.AnimState.SequenceNum, UUID.Zero);
            }
            if (cAgent.Anims != null)
            {
                int canim = Animator.Animations.FromArray(cAgent.Anims);
                if(canim > nanim)
                    nanim = canim;
            }
            ControllingClient.NextAnimationSequenceNumber = ++nanim;

            if (cAgent.MotionState != 0)
                Animator.currentControlState = (ScenePresenceAnimator.motionControlStates) cAgent.MotionState;

            _crossingFlags = cAgent.CrossingFlags;
            _gotCrossUpdate = (_crossingFlags != 0);
            if(_gotCrossUpdate)
            {
                LastCommands &= ~(ScriptControlled.CONTROL_LBUTTON | ScriptControlled.CONTROL_ML_LBUTTON);
                if((cAgent.CrossExtraFlags & 1) != 0)
                    LastCommands |= ScriptControlled.CONTROL_LBUTTON;
                if((cAgent.CrossExtraFlags & 2) != 0)
                    LastCommands |= ScriptControlled.CONTROL_ML_LBUTTON;
                MouseDown = (cAgent.CrossExtraFlags & 3) != 0;
            }

            _haveGroupInformation = false;
            // using this as protocol detection don't want to mess with the numbers for now
            if(cAgent.ActiveGroupTitle != null)
            {
                _haveGroupInformation = true;
                //COF = cAgent.agentCOF;
                if(ControllingClient.IsGroupMember(cAgent.ActiveGroupID))
                {
                    ControllingClient.ActiveGroupId = cAgent.ActiveGroupID;
                    ControllingClient.ActiveGroupName = cAgent.ActiveGroupName;
                    Grouptitle = cAgent.ActiveGroupTitle;
                    ControllingClient.ActiveGroupPowers =
                            ControllingClient.GetGroupPowers(cAgent.ActiveGroupID);
                }
                else
                {
                    // we got a unknown active group so get what groups thinks about us
                    IGroupsModule gm = _scene.RequestModuleInterface<IGroupsModule>();
                    if (gm != null)
                        gm.SendAgentGroupDataUpdate(ControllingClient);
                }
            }

            lock (_originRegionIDAccessLock)
                _originRegionID = cAgent.RegionID;
        }

        public bool CopyAgent(out IAgentData agent)
        {
            agent = new CompleteAgentData();
            CopyTo((AgentData)agent, false);
            return true;
        }

        #endregion Child Agent Updates

        /// <summary>
        /// Handles part of the PID controller function for moving an avatar.
        /// </summary>
        public void UpdateMovement()
        {
/*
            if (IsInTransit)
                return;

            lock(_forceToApplyLock)
            {
                if (_forceToApplyValid)
                {
                    Velocity = _forceToApply;

                    _forceToApplyValid = false;
                    TriggerScenePresenceUpdated();
                }
            }
*/
        }

        /// <summary>
        /// Adds a physical representation of the avatar to the Physics plugin
        /// </summary>
        public void AddToPhysicalScene(bool isFlying)
        {
//            _log.DebugFormat(
//                "[SCENE PRESENCE]: Adding physics actor for {0}, ifFlying = {1} in {2}",
//                Name, isFlying, Scene.RegionInfo.RegionName);

            if (PhysicsActor != null)
            {
                _log.ErrorFormat(
                    "[SCENE PRESENCE]: Adding physics actor for {0} to {1} but this scene presence already has a physics actor",
                    Name, Scene.RegionInfo.RegionName);
            }

            if (Appearance.AvatarHeight == 0)
//                Appearance.SetHeight();
                Appearance.SetSize(new Vector3(0.45f,0.6f,1.9f));

//            lock(_forceToApplyLock)
//                _forceToApplyValid = false;

            PhysicsScene scene = _scene.PhysicsScene;
            Vector3 pVec = AbsolutePosition;

            PhysicsActor pa = scene.AddAvatar(
                LocalId, Firstname + "." + Lastname, pVec,
                Appearance.AvatarBoxSize,Appearance.AvatarFeetOffset, isFlying);
            pa.Orientation = _bodyRot;
            //PhysicsActor.OnRequestTerseUpdate += SendTerseUpdateToAllClients;
            pa.OnCollisionUpdate += PhysicsCollisionUpdate;
            pa.OnOutOfBounds += OutOfBoundsCall; // Called for PhysicsActors when there's something wrong
            pa.SubscribeEvents(100);
            pa.LocalID = LocalId;
            pa.SetAlwaysRun = _setAlwaysRun;
            PhysicsActor = pa;
        }

        private void OutOfBoundsCall(Vector3 pos)
        {
            //bool flying = Flying;
            //RemoveFromPhysicalScene();

            //AddToPhysicalScene(flying);
            if (ControllingClient != null)
                ControllingClient.SendAgentAlertMessage("Physics is having a problem with your avatar.  You may not be able to move until you relog.", true);
        }

        /// <summary>
        /// Event called by the physics plugin to tell the avatar about a collision.
        /// </summary>
        /// <remarks>
        /// This function is called continuously, even when there are no collisions.  If the avatar is walking on the
        /// ground or a prim then there will be collision information between the avatar and the surface.
        ///
        /// FIXME: However, we can't safely avoid calling this yet where there are no collisions without analyzing whether
        /// any part of this method is relying on an every-frame call.
        /// </remarks>
        /// <param name="e"></param>
        public void PhysicsCollisionUpdate(EventArgs e)
        {
            if (IsChildAgent)
                return;

            if(IsInTransit)
                return;

            //if ((Math.Abs(Velocity.X) > 0.1e-9f) || (Math.Abs(Velocity.Y) > 0.1e-9f))
            // The Physics Scene will send updates every 500 ms grep: PhysicsActor.SubscribeEvents(
            // as of this comment the interval is set in AddToPhysicalScene

//                if (_updateCount > 0)
//                {
//            if (Animator != null && Animator.UpdateMovementAnimations())
//                TriggerScenePresenceUpdated();
//                    _updateCount--;
//                }

            CollisionEventUpdate collisionData = (CollisionEventUpdate)e;
            Dictionary<uint, ContactPoint> coldata = collisionData._objCollisionList;

//            // No collisions at all means we may be flying. Update always
//            // to make falling work
//            if (_lastColCount != coldata.Count || coldata.Count == 0)
//            {
//                _updateCount = UPDATE_COUNT;
//                _lastColCount = coldata.Count;
//            }

            if (coldata.Count != 0)
            {
                ContactPoint lowest;
                lowest.SurfaceNormal = Vector3.Zero;
                lowest.Position = Vector3.Zero;
                lowest.Position.Z = float.MaxValue;

                foreach (ContactPoint contact in coldata.Values)
                {
                    if (contact.CharacterFeet && contact.Position.Z < lowest.Position.Z)
                        lowest = contact;
                }

                if (lowest.Position.Z != float.MaxValue)
                {
                    lowest.SurfaceNormal = -lowest.SurfaceNormal;
                    CollisionPlane = new Vector4(lowest.SurfaceNormal, Vector3.Dot(lowest.Position, lowest.SurfaceNormal));
                }
                else
                   CollisionPlane = Vector4.UnitW;
            }
            else
                CollisionPlane = Vector4.UnitW;

            RaiseCollisionScriptEvents(coldata);

            // Gods do not take damage and Invulnerable is set depending on parcel/region flags
            if (Invulnerable || IsViewerUIGod)
                return;

            // The following may be better in the ICombatModule
            // probably tweaking of the values for ground and normal prim collisions will be needed
            float startHealth = Health;
            if(coldata.Count > 0)
            {
                uint killerObj = 0;
                SceneObjectPart part = null;
                float rvel; // relative velocity, negative on approch
                foreach (uint localid in coldata.Keys)
                {
                    if (localid == 0)
                    {
                        // 0 is the ground
                        rvel = coldata[0].RelativeSpeed;
                        if(rvel < -5.0f)
                            Health -= 0.01f * rvel * rvel;
                    }
                    else
                    {
                        part = Scene.GetSceneObjectPart(localid);

                        if(part != null && !part.ParentGroup.IsVolumeDetect)
                        {
                            if (part.ParentGroup.Damage > 0.0f)
                            {
                                // Something with damage...
                                Health -= part.ParentGroup.Damage;
                                part.ParentGroup.Scene.DeleteSceneObject(part.ParentGroup, false);
                            }
                            else
                            {
                                // An ordinary prim
                                rvel = coldata[localid].RelativeSpeed;
                                if(rvel < -5.0f)
                                {
                                    Health -=  0.005f * rvel * rvel;
                                }
                            }
                        }
                        else
                        {

                        }
                    }

                    if (Health <= 0.0f)
                    {
                        if (localid != 0)
                            killerObj = localid;
                    }
                }

                if (Health <= 0)
                {
                    ControllingClient.SendHealth(Health);
                    _scene.EventManager.TriggerAvatarKill(killerObj, this);
                    return;
                }
            }

            if(Math.Abs(Health - startHealth) > 1.0)
                ControllingClient.SendHealth(Health);
        }

        public void setHealthWithUpdate(float health)
        {
            Health = health;
            ControllingClient.SendHealth(Health);
        }


        public void AddAttachment(SceneObjectGroup gobj)
        {
            lock (_attachments)
            {
                // This may be true when the attachment comes back
                // from serialization after login. Clear it.
                gobj.IsDeleted = false;

                _attachments.Add(gobj);
            }

            IBakedTextureModule bakedModule = _scene.RequestModuleInterface<IBakedTextureModule>();
            if (bakedModule != null)
                bakedModule.UpdateMeshAvatar(_uuid);
        }

        public int GetAttachmentsCount()
        {
            return _attachments.Count;
        }


        /// <summary>
        /// Get all the presence's attachments.
        /// </summary>
        /// <returns>A copy of the list which contains the attachments.</returns>
        public List<SceneObjectGroup> GetAttachments()
        {
            lock (_attachments)
                return new List<SceneObjectGroup>(_attachments);
        }

        /// <summary>
        /// Get the scene objects attached to the given point.
        /// </summary>
        /// <param name="attachmentPoint"></param>
        /// <returns>Returns an empty list if there were no attachments at the point.</returns>
        public List<SceneObjectGroup> GetAttachments(uint attachmentPoint)
        {
            List<SceneObjectGroup> attachments = new List<SceneObjectGroup>();

            if (attachmentPoint >= 0)
            {
                lock (_attachments)
                {
                    foreach (SceneObjectGroup so in _attachments)
                    {
                        if (attachmentPoint == so.AttachmentPoint)
                            attachments.Add(so);
                    }
                }
            }

            return attachments;
        }

        public bool HasAttachments()
        {
            lock (_attachments)
                return _attachments.Count > 0;
        }

        /// <summary>
        /// Returns the total count of scripts in all parts inventories.
        /// </summary>
        public int ScriptCount()
        {
            int count = 0;
            lock (_attachments)
            {
                foreach (SceneObjectGroup gobj in _attachments)
                {
                    if (gobj != null)
                    {
                        count += gobj.ScriptCount();
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// A float the value is a representative execution time in milliseconds of all scripts in all attachments.
        /// </summary>
        public float ScriptExecutionTime()
        {
            float time = 0.0f;
            lock (_attachments)
            {
                foreach (SceneObjectGroup gobj in _attachments)
                {
                    if (gobj != null)
                    {
                        time += gobj.ScriptExecutionTime();
                    }
                }
            }
            return time;
        }

        /// <summary>
        /// Returns the total count of running scripts in all parts.
        /// </summary>
        public int RunningScriptCount()
        {
            int count = 0;
            lock (_attachments)
            {
                foreach (SceneObjectGroup gobj in _attachments)
                {
                    if (gobj != null)
                    {
                        count += gobj.RunningScriptCount();
                    }
                }
            }
            return count;
        }

        public bool HasScriptedAttachments()
        {
            lock (_attachments)
            {
                foreach (SceneObjectGroup gobj in _attachments)
                {
                    if (gobj != null)
                    {
                        if (gobj.RootPart.Inventory.ContainsScripts())
                            return true;
                    }
                }
            }
            return false;
        }

        public void RemoveAttachment(SceneObjectGroup gobj)
        {
            lock (_attachments)
                _attachments.Remove(gobj);
        }

        /// <summary>
        /// Clear all attachments
        /// </summary>
        public void ClearAttachments()
        {
            lock (_attachments)
                _attachments.Clear();
        }

        /// <summary>
        /// This is currently just being done for information.
        /// </summary>
        public bool ValidateAttachments()
        {
            bool validated = true;

            lock (_attachments)
            {
                // Validate
                foreach (SceneObjectGroup gobj in _attachments)
                {
                    if (gobj == null)
                    {
                        _log.WarnFormat(
                            "[SCENE PRESENCE]: Failed to validate an attachment for {0} since it was null.  Continuing", Name);

                        validated = false;
                    }
                    else if (gobj.IsDeleted)
                    {
                        _log.WarnFormat(
                            "[SCENE PRESENCE]: Failed to validate attachment {0} {1} for {2} since it had been deleted.  Continuing",
                            gobj.Name, gobj.UUID, Name);

                        validated = false;
                    }
                }
            }

            return validated;
        }
/*
        public void SendAttachmentsToAllAgents()
        {
            lock (_attachments)
            {
                foreach (SceneObjectGroup sog in _attachments)
                {
                    _scene.ForEachScenePresence(delegate(ScenePresence p)
                    {
                        if (p != this && sog.HasPrivateAttachmentPoint)
                            return;

                        if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                            return;

                        SendTerseUpdateToAgentNF(p);
                        SendAttachmentFullUpdateToAgentNF(sog, p);
                    });
                }
            }
        }
*/
        // send attachments to a client without filters except for huds
        // for now they are checked in several places down the line...
        public void SendAttachmentsToAgentNF(ScenePresence p)
        {
            SendTerseUpdateToAgentNF(p);
//            SendAvatarDataToAgentNF(this);
            lock (_attachments)
            {
                foreach (SceneObjectGroup sog in _attachments)
                {
                    SendAttachmentFullUpdateToAgentNF(sog, p);
                }
            }
        }

        public void SendAttachmentFullUpdateToAgentNF(SceneObjectGroup sog, ScenePresence p)
        {
            if (p != this && sog.HasPrivateAttachmentPoint)
                return;

            SceneObjectPart[] parts = sog.Parts;
            SceneObjectPart rootpart = sog.RootPart;

            PrimUpdateFlags update = PrimUpdateFlags.FullUpdate;
            if (rootpart.Shape.MeshFlagEntry)
                update = PrimUpdateFlags.FullUpdatewithAnim;

            p.ControllingClient.SendEntityUpdate(rootpart, update);

            for (int i = 0; i < parts.Length; i++)
            {
                SceneObjectPart part = parts[i];
                if (part == rootpart)
                    continue;
                p.ControllingClient.SendEntityUpdate(part, update);
            }
        }

        public void SendAttachmentScheduleUpdate(SceneObjectGroup sog)
        {
            if (IsChildAgent || IsInTransit)
                return;

            
            SceneObjectPart[] origparts = sog.Parts;
            SceneObjectPart[] parts = new SceneObjectPart[origparts.Length];
            PrimUpdateFlags[] flags = new PrimUpdateFlags[origparts.Length];

            SceneObjectPart rootpart = sog.RootPart;
            PrimUpdateFlags cur = sog.RootPart.GetAndClearUpdateFlag();
            bool noanim = !rootpart.Shape.MeshFlagEntry;

            int nparts = 0;
            if (noanim || rootpart.Animations == null)
                cur &= ~PrimUpdateFlags.Animations;
            if (cur != PrimUpdateFlags.None)
            {
                flags[nparts] = cur;
                parts[nparts] = rootpart;
                ++nparts;
            }

            for (int i = 0; i < origparts.Length; i++)
            {
                if (origparts[i] == rootpart)
                    continue;

                cur = origparts[i].GetAndClearUpdateFlag();
                if (noanim || origparts[i].Animations == null)
                    cur &= ~PrimUpdateFlags.Animations;
                if (cur == PrimUpdateFlags.None)
                    continue;
                flags[nparts] = cur;
                parts[nparts] = origparts[i];
                ++nparts;
            }

            if (nparts == 0)
                return;

            for (int i = 0; i < nparts; i++)
                ControllingClient.SendEntityUpdate(parts[i], flags[i]);

            if (sog.HasPrivateAttachmentPoint)
                return;

            List<ScenePresence> allPresences = _scene.GetScenePresences();
            foreach (ScenePresence p in allPresences)
            {
                if (p == this)
                    continue;

                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                    continue;

                for (int i = 0; i < nparts; i++)
                    p.ControllingClient.SendEntityUpdate(parts[i], flags[i]);
            }
        }

        public void SendAttachmentUpdate(SceneObjectGroup sog, PrimUpdateFlags update)
        {
            if (IsChildAgent || IsInTransit)
                return;

            SceneObjectPart[] origparts = sog.Parts;
            SceneObjectPart[] parts = new SceneObjectPart[origparts.Length];
            PrimUpdateFlags[] flags = new PrimUpdateFlags[origparts.Length];

            SceneObjectPart rootpart = sog.RootPart;
            bool noanim = !rootpart.Shape.MeshFlagEntry;

            int nparts = 0;
            PrimUpdateFlags cur = update;
            if (noanim || rootpart.Animations == null)
                cur &= ~PrimUpdateFlags.Animations;
            if (cur != PrimUpdateFlags.None)
            {
                flags[nparts] = cur;
                parts[nparts] = rootpart;
                ++nparts;
            }

            for (int i = 0; i < origparts.Length; i++)
            {
                if (origparts[i] == rootpart)
                    continue;

                cur = update;
                if (noanim || origparts[i].Animations == null)
                    cur &= ~PrimUpdateFlags.Animations;
                if (cur == PrimUpdateFlags.None)
                    continue;
                flags[nparts] = cur;
                parts[nparts] = origparts[i];
                ++nparts;
            }

            if (nparts == 0)
                return;

            for(int i = 0; i < nparts; i++)
                ControllingClient.SendEntityUpdate(parts[i], flags[i]);

            if (sog.HasPrivateAttachmentPoint)
                return;

            List<ScenePresence> allPresences = _scene.GetScenePresences();
            foreach (ScenePresence p in allPresences)
            {
                if (p == this)
                    continue;

                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                    continue;

                p.ControllingClient.SendEntityUpdate(rootpart, update);

                for (int i = 0; i < nparts; i++)
                    p.ControllingClient.SendEntityUpdate(parts[i], flags[i]);
            }
        }

        public void SendAttachmentUpdate(SceneObjectPart part, PrimUpdateFlags update)
        {
            if (IsChildAgent || IsInTransit)
                return;

            if ((update & PrimUpdateFlags.Animations) != 0 && part.Animations == null)
            {
                update &= ~PrimUpdateFlags.Animations;
                if (update == PrimUpdateFlags.None)
                    return;
            }

            ControllingClient.SendEntityUpdate(part, update);

            if (part.ParentGroup.HasPrivateAttachmentPoint)
                return;

            List<ScenePresence> allPresences = _scene.GetScenePresences();
            foreach (ScenePresence p in allPresences)
            {
                if (p == this)
                    continue;
                if (ParcelHideThisAvatar && currentParcelUUID != p.currentParcelUUID && !p.IsViewerUIGod)
                    continue;

                p.ControllingClient.SendEntityUpdate(part, update);
            }
        }

        public void SendScriptChangedEventToAttachments(Changed val)
        {
            lock (_attachments)
            {
                foreach (SceneObjectGroup grp in _attachments)
                {
                    if ((grp.ScriptEvents & scriptEvents.changed) != 0)
                    {
                        foreach(SceneObjectPart sop in grp.Parts)
                        {
                            sop.TriggerScriptChangedEvent(val);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets the mass.
        /// </summary>
        /// <returns>
        /// The mass.
        /// </returns>
        public float GetMass()
        {
            PhysicsActor pa = PhysicsActor;

            if (pa != null)
                return pa.Mass;
            else
                return 0;
        }

        internal void PushForce(Vector3 impulse)
        {
            if (PhysicsActor != null)
            {
                PhysicsActor.AddForce(impulse,true);
            }
        }

        CameraData physActor_OnPhysicsRequestingCameraData()
        {
            return new CameraData
            {
                Valid = true,
                MouseLook = this._mouseLook,
                CameraRotation = this.CameraRotation,
                CameraAtAxis = this.CameraAtAxis
            };
        }

        public void RegisterControlEventsToScript(int controls, int accept, int pass_on, uint Obj_localID, UUID Script_ite_UUID)
        {
            SceneObjectPart part = _scene.GetSceneObjectPart(Obj_localID);
            if (part == null)
                return;

            ControllingClient.SendTakeControls(controls, false, false);
            ControllingClient.SendTakeControls(controls, true, false);

            ScriptControllers obj = new ScriptControllers()
            {
                ignoreControls = ScriptControlled.CONTROL_ZERO,
                eventControls = ScriptControlled.CONTROL_ZERO,
                objectID = part.ParentGroup.UUID,
                itemID = Script_ite_UUID
            };

            if (pass_on == 0 && accept == 0)
            {
                IgnoredControls |= (ScriptControlled)controls;
                obj.ignoreControls = (ScriptControlled)controls;
            }

            if (pass_on == 0 && accept == 1)
            {
                IgnoredControls |= (ScriptControlled)controls;
                obj.ignoreControls = (ScriptControlled)controls;
                obj.eventControls = (ScriptControlled)controls;
            }

            if (pass_on == 1 && accept == 1)
            {
                IgnoredControls = ScriptControlled.CONTROL_ZERO;
                obj.eventControls = (ScriptControlled)controls;
                obj.ignoreControls = ScriptControlled.CONTROL_ZERO;
            }

            lock (scriptedcontrols)
            {
                if (pass_on == 1 && accept == 0)
                {
                    IgnoredControls &= ~(ScriptControlled)controls;
                    if (scriptedcontrols.ContainsKey(Script_ite_UUID))
                        RemoveScriptFromControlNotifications(Script_ite_UUID, part);
                }
                else
                {
                    AddScriptToControlNotifications(Script_ite_UUID, part, ref obj);
                }
            }

            ControllingClient.SendTakeControls(controls, pass_on == 1 ? true : false, true);
        }

        private void AddScriptToControlNotifications(OpenMetaverse.UUID Script_ite_UUID, SceneObjectPart part, ref ScriptControllers obj)
        {
            scriptedcontrols[Script_ite_UUID] = obj;

            PhysicsActor physActor = part.ParentGroup.RootPart.PhysActor;
            if (physActor != null)
            {
                physActor.OnPhysicsRequestingCameraData -= physActor_OnPhysicsRequestingCameraData;
                physActor.OnPhysicsRequestingCameraData += physActor_OnPhysicsRequestingCameraData;
            }
        }

        private void RemoveScriptFromControlNotifications(OpenMetaverse.UUID Script_ite_UUID, SceneObjectPart part)
        {
            scriptedcontrols.Remove(Script_ite_UUID);

            if (part != null)
            {
                PhysicsActor physActor = part.ParentGroup.RootPart.PhysActor;
                if (physActor != null)
                {
                    physActor.OnPhysicsRequestingCameraData -= physActor_OnPhysicsRequestingCameraData;
                }
            }
        }

        public void HandleForceReleaseControls(IClientAPI remoteClient, UUID agentID)
        {
            foreach (ScriptControllers c in scriptedcontrols.Values)
            {
                SceneObjectGroup sog = _scene.GetSceneObjectGroup(c.objectID);
                if(sog != null && !sog.IsDeleted && sog.RootPart.PhysActor != null)
                    sog.RootPart.PhysActor.OnPhysicsRequestingCameraData -= physActor_OnPhysicsRequestingCameraData;
            }

            IgnoredControls = ScriptControlled.CONTROL_ZERO;
            lock (scriptedcontrols)
            {
                scriptedcontrols.Clear();
            }
            ControllingClient.SendTakeControls(int.MaxValue, false, false);
        }

        public void HandleRevokePermissions(UUID objectID, uint permissions )
        {

        // still skeleton code
            if((permissions & (16 | 0x8000 ))  == 0) //PERMISSION_TRIGGER_ANIMATION | PERMISSION_OVERRIDE_ANIMATIONS
                return;
            if(objectID == _scene.RegionInfo.RegionID) // for all objects
            {
                List<SceneObjectGroup> sogs = _scene.GetSceneObjectGroups();
                for(int i = 0; i < sogs.Count; ++i)
                    sogs[i].RemoveScriptsPermissions(this, (int)permissions);
            }
            else
            {
                SceneObjectPart part = _scene.GetSceneObjectPart(objectID);
                if(part != null)
                {
                    part.Inventory.RemoveScriptsPermissions(this, (int)permissions);
                }
            }
        }

        public void ClearControls()
        {
            IgnoredControls = ScriptControlled.CONTROL_ZERO;
            lock (scriptedcontrols)
            {
                scriptedcontrols.Clear();
            }
        }

        public void UnRegisterSeatControls(UUID obj)
        {
            List<UUID> takers = new List<UUID>();

            foreach (ScriptControllers c in scriptedcontrols.Values)
            {
                if (c.objectID == obj)
                    takers.Add(c.itemID);
            }
            foreach (UUID t in takers)
            {
                UnRegisterControlEventsToScript(0, t);
            }
        }

        public void UnRegisterControlEventsToScript(uint Obj_localID, UUID Script_ite_UUID)
        {
            SceneObjectPart part = _scene.GetSceneObjectPart(Obj_localID);

            lock (scriptedcontrols)
            {
                if (scriptedcontrols.TryGetValue(Script_ite_UUID, out ScriptControllers takecontrols))
                {
                    ScriptControlled sctc = takecontrols.eventControls;

                    ControllingClient.SendTakeControls((int)sctc, false, false);
                    ControllingClient.SendTakeControls((int)sctc, true, false);

                    RemoveScriptFromControlNotifications(Script_ite_UUID, part);
                    IgnoredControls = ScriptControlled.CONTROL_ZERO;
                    foreach (ScriptControllers scData in scriptedcontrols.Values)
                    {
                        IgnoredControls |= scData.ignoreControls;
                    }
                }
            }
        }

        private void SendControlsToScripts(uint flags)
        {
            // Notify the scripts only after calling UpdateMovementAnimations(), so that if a script
            // (e.g., a walking script) checks which animation is active it will be the correct animation.
            lock (scriptedcontrols)
            {
                if (scriptedcontrols.Count <= 0)
                    return;

                ScriptControlled allflags;
                // convert mouse from edge to level
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP) != 0 ||
                            (flags & unchecked((uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_UP)) != 0)
                {
                    allflags = ScriptControlled.CONTROL_ZERO;
                }
                else // recover last state of mouse
                    allflags = LastCommands & (ScriptControlled.CONTROL_ML_LBUTTON | ScriptControlled.CONTROL_LBUTTON);

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN) != 0)
                    allflags |= ScriptControlled.CONTROL_ML_LBUTTON;

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN) != 0)
                    allflags |= ScriptControlled.CONTROL_LBUTTON;

                // find all activated controls, whether the scripts are interested in them or not
                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_FWD;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_BACK;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_UP;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_DOWN;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_LEFT;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG) != 0 || (flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_RIGHT;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_ROT_RIGHT;
                }

                if ((flags & (uint)AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS) != 0)
                {
                    allflags |= ScriptControlled.CONTROL_ROT_LEFT;
                }

                // optimization; we have to check per script, but if nothing is pressed and nothing changed, we can skip that
                if (allflags != ScriptControlled.CONTROL_ZERO || allflags != LastCommands)
                {
                    foreach (KeyValuePair<UUID, ScriptControllers> kvp in scriptedcontrols)
                    {
                        UUID scriptUUID = kvp.Key;
                        ScriptControllers scriptControlData = kvp.Value;

                        ScriptControlled localHeld = allflags & scriptControlData.eventControls;     // the flags interesting for us
                        ScriptControlled localLast = LastCommands & scriptControlData.eventControls; // the activated controls in the last cycle
                        ScriptControlled localChange = localHeld ^ localLast;                        // the changed bits

                        if (localHeld != ScriptControlled.CONTROL_ZERO || localChange != ScriptControlled.CONTROL_ZERO)
                        {
                            // only send if still pressed or just changed
                            _scene.EventManager.TriggerControlEvent(scriptUUID, UUID, (uint)localHeld, (uint)localChange);
                        }
                    }
                }

                LastCommands = allflags;
                MouseDown = (allflags & (ScriptControlled.CONTROL_ML_LBUTTON | ScriptControlled.CONTROL_LBUTTON)) != 0;
            }
        }

        internal static AgentManager.ControlFlags RemoveIgnoredControls(AgentManager.ControlFlags flags, ScriptControlled ignored)
        {
            if (ignored == ScriptControlled.CONTROL_ZERO)
                return flags;

            if ((ignored & ScriptControlled.CONTROL_BACK) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_NEG);
            if ((ignored & ScriptControlled.CONTROL_FWD) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_AT_POS | AgentManager.ControlFlags.AGENT_CONTROL_AT_POS);
            if ((ignored & ScriptControlled.CONTROL_DOWN) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG);
            if ((ignored & ScriptControlled.CONTROL_UP) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_POS | AgentManager.ControlFlags.AGENT_CONTROL_UP_POS);
            if ((ignored & ScriptControlled.CONTROL_LEFT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS | AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_POS);
            if ((ignored & ScriptControlled.CONTROL_RIGHT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_LEFT_NEG | AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG);
            if ((ignored & ScriptControlled.CONTROL_ROT_LEFT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_YAW_NEG);
            if ((ignored & ScriptControlled.CONTROL_ROT_RIGHT) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_YAW_POS);
            if ((ignored & ScriptControlled.CONTROL_ML_LBUTTON) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_ML_LBUTTON_DOWN);
            if ((ignored & ScriptControlled.CONTROL_LBUTTON) != 0)
                flags &= ~(AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_UP | AgentManager.ControlFlags.AGENT_CONTROL_LBUTTON_DOWN);

            //DIR_CONTROL_FLAG_FORWARD = AgentManager.ControlFlags.AGENT_CONTROL_AT_POS,
            //DIR_CONTROL_FLAG_BACK = AgentManager.ControlFlags.AGENT_CONTROL_AT_NEG,
            //DIR_CONTROL_FLAG_LEFT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_POS,
            //DIR_CONTROL_FLAG_RIGHT = AgentManager.ControlFlags.AGENT_CONTROL_LEFT_NEG,
            //DIR_CONTROL_FLAG_UP = AgentManager.ControlFlags.AGENT_CONTROL_UP_POS,
            //DIR_CONTROL_FLAG_DOWN = AgentManager.ControlFlags.AGENT_CONTROL_UP_NEG,
            //DIR_CONTROL_FLAG_DOWN_NUDGE = AgentManager.ControlFlags.AGENT_CONTROL_NUDGE_UP_NEG

            return flags;
        }

        // returns true it local teleport allowed and sets the destiny position into pos

        public bool CheckLocalTPLandingPoint(ref Vector3 pos)
        {
            // Never constrain lures
            if ((TeleportFlags & TeleportFlags.ViaLure) != 0)
                return true;

            if (_scene.RegionInfo.EstateSettings.AllowDirectTeleport)
                return true;

            // do not constrain gods and estate managers
            if(_scene.Permissions.IsGod(_uuid) ||
                _scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(_uuid))
                return true;

            // will teleport to a telehub spawn point or landpoint if that results in getting closer to target
            // if not the local teleport fails.

            float currDistanceSQ = Vector3.DistanceSquared(AbsolutePosition, pos);

            // first check telehub

            UUID TelehubObjectID = _scene.RegionInfo.RegionSettings.TelehubObject;
            if ( TelehubObjectID != UUID.Zero)
            {
                SceneObjectGroup telehubSOG =  _scene.GetSceneObjectGroup(TelehubObjectID);
                if(telehubSOG != null)
                {
                    Vector3 spawnPos;
                    float spawnDistSQ;

                    SpawnPoint[] spawnPoints = _scene.RegionInfo.RegionSettings.SpawnPoints().ToArray();
                    if(spawnPoints.Length == 0)
                    {
                        spawnPos = new Vector3(128.0f, 128.0f, pos.Z);
                        spawnDistSQ = Vector3.DistanceSquared(spawnPos, pos);
                    }
                    else
                    {
                        Vector3 hubPos = telehubSOG.AbsolutePosition;
                        Quaternion hubRot = telehubSOG.GroupRotation;

                        spawnPos = spawnPoints[0].GetLocation(hubPos, hubRot);
                        spawnDistSQ = Vector3.DistanceSquared(spawnPos, pos);

                        float testDistSQ;
                        Vector3 testSpawnPos;
                        for(int i = 1; i< spawnPoints.Length; i++)
                        {
                            testSpawnPos = spawnPoints[i].GetLocation(hubPos, hubRot);
                            testDistSQ =  Vector3.DistanceSquared(testSpawnPos, pos);

                            if(testDistSQ < spawnDistSQ)
                            {
                                spawnPos = testSpawnPos;
                                spawnDistSQ = testDistSQ;
                            }
                        }
                    }
                    if (currDistanceSQ < spawnDistSQ)
                    {
                        // we are already close
                        ControllingClient.SendAlertMessage("Can't teleport closer to destination");
                        return false;
                    }
                    else
                    {
                        pos = spawnPos;
                        return true;
                    }
                }
            }

            ILandObject land = _scene.LandChannel.GetLandObject(pos.X, pos.Y);

            if (land.LandData.LandingType != (byte)LandingType.LandingPoint
                        || land.LandData.OwnerID == _uuid)
                return true;

            Vector3 landLocation = land.LandData.UserLocation;
            if(landLocation == Vector3.Zero)
                return true;

            if (currDistanceSQ < Vector3.DistanceSquared(landLocation, pos))
            {
                ControllingClient.SendAlertMessage("Can't teleport closer to destination");
                return false;
            }

            pos = land.LandData.UserLocation;
            return true;
        }

        const TeleportFlags TeleHubTPFlags = TeleportFlags.ViaLogin
                    | TeleportFlags.ViaHGLogin | TeleportFlags.ViaLocation;

        private bool CheckAndAdjustTelehub(SceneObjectGroup telehub, ref Vector3 pos, ref bool positionChanged)
        {
            // forcing telehubs on any tp that reachs this
            if ((_teleportFlags & TeleHubTPFlags) != 0 ||
                (_scene.TelehubAllowLandmarks == true ? false : ((_teleportFlags & TeleportFlags.ViaLandmark) != 0 )))
            {
                ILandObject land;
                Vector3 teleHubPosition = telehub.AbsolutePosition;

                SpawnPoint[] spawnPoints = _scene.RegionInfo.RegionSettings.SpawnPoints().ToArray();
                if(spawnPoints.Length == 0)
                {
                    land = _scene.LandChannel.GetLandObject(teleHubPosition.X,teleHubPosition.Y);
                    if(land != null)
                    {
                        pos = teleHubPosition;
                        if(land.IsEitherBannedOrRestricted(UUID))
                            return false;
                        positionChanged = true;
                        return true;
                    }
                    else
                        return false;
                }

                int index;
                int tries;
                bool selected = false;
                bool validhub = false;
                Vector3 spawnPosition;

                Quaternion teleHubRotation = telehub.GroupRotation;

                switch(_scene.SpawnPointRouting)
                {
                    case "random":
                        tries = spawnPoints.Length;
                        if(tries < 3) // no much sense in random with a few points when there same can have bans
                            goto case "sequence";
                        do
                        {
                            index = Util.RandomClass.Next(spawnPoints.Length - 1);

                            spawnPosition = spawnPoints[index].GetLocation(teleHubPosition, teleHubRotation);
                            land = _scene.LandChannel.GetLandObject(spawnPosition.X,spawnPosition.Y);
                            if(land != null && !land.IsEitherBannedOrRestricted(UUID))
                                selected = true;

                        } while(selected == false && --tries > 0 );

                        if(tries <= 0)
                            goto case "sequence";

                        pos = spawnPosition;
                        return true;

                    case "sequence":
                        tries = spawnPoints.Length;
                        selected = false;
                        validhub = false;
                        do
                        {
                            index = _scene.SpawnPoint();
                            spawnPosition = spawnPoints[index].GetLocation(teleHubPosition, teleHubRotation);
                            land = _scene.LandChannel.GetLandObject(spawnPosition.X,spawnPosition.Y);
                            if(land != null)
                            {
                                validhub = true;
                                if(land.IsEitherBannedOrRestricted(UUID))
                                    selected = false;
                                else
                                    selected = true;
                            }

                        } while(selected == false && --tries > 0);

                        if(!validhub)
                            return false;

                        pos = spawnPosition;

                        if(!selected)
                            return false;
                        positionChanged = true;
                        return true;

                    default:
                    case "closest":
                        float distancesq = float.MaxValue;
                        int closest = -1;
                        validhub = false;

                        for(int i = 0; i < spawnPoints.Length; i++)
                        {
                            spawnPosition = spawnPoints[i].GetLocation(teleHubPosition, teleHubRotation);
                            Vector3 offset = spawnPosition - pos;
                            float dsq = offset.LengthSquared();
                            land = _scene.LandChannel.GetLandObject(spawnPosition.X,spawnPosition.Y);
                            if(land == null)
                                continue;

                            validhub = true;
                            if(land.IsEitherBannedOrRestricted(UUID))
                                continue;

                            if(dsq >= distancesq)
                                continue;
                            distancesq = dsq;
                            closest = i;
                        }

                        if(!validhub)
                            return false;

                        if(closest < 0)
                        {
                            pos = spawnPoints[0].GetLocation(teleHubPosition, teleHubRotation);
                            positionChanged = true;
                            return false;
                        }

                        pos = spawnPoints[closest].GetLocation(teleHubPosition, teleHubRotation);
                        positionChanged = true;
                        return true;
                }
            }
            return false;
        }

        const TeleportFlags adicionalLandPointFlags = TeleportFlags.ViaLandmark |
                    TeleportFlags.ViaLocation | TeleportFlags.ViaHGLogin;

        // Modify landing point based on possible banning, telehubs or parcel restrictions.
        // This is the behavior in OpenSim for a very long time, different from SL
        private bool CheckAndAdjustLandingPoint_OS(ref Vector3 pos, ref Vector3 lookat, ref bool positionChanged)
        {
            // Honor bans
            if (!_scene.TestLandRestrictions(UUID, out string reason, ref pos.X, ref pos.Y))
                return false;

            SceneObjectGroup telehub = null;
            if (_scene.RegionInfo.RegionSettings.TelehubObject != UUID.Zero && (telehub = _scene.GetSceneObjectGroup(_scene.RegionInfo.RegionSettings.TelehubObject)) != null)
            {
                if (!_scene.RegionInfo.EstateSettings.AllowDirectTeleport)
                {
                    CheckAndAdjustTelehub(telehub, ref pos, ref positionChanged);
                    return true;
                }
            }

            ILandObject land = _scene.LandChannel.GetLandObject(pos.X, pos.Y);
            if (land != null)
            {
                if (Scene.DebugTeleporting)
                    TeleportFlagsDebug();

                // If we come in via login, landmark or map, we want to
                // honor landing points. If we come in via Lure, we want
                // to ignore them.
                if ((_teleportFlags & (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)) ==
                    (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID) ||
                    (_teleportFlags & adicionalLandPointFlags) != 0)
                {
                    // Don't restrict gods, estate managers, or land owners to
                    // the TP point. This behaviour mimics agni.
                    if (land.LandData.LandingType == (byte)LandingType.LandingPoint &&
                        land.LandData.UserLocation != Vector3.Zero &&
                        !IsViewerUIGod &&
                        ((land.LandData.OwnerID != _uuid &&
                          !_scene.Permissions.IsGod(_uuid) &&
                          !_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(_uuid)) ||
                         (_teleportFlags & TeleportFlags.ViaLocation) != 0 ||
                         (_teleportFlags & Constants.TeleportFlags.ViaHGLogin) != 0))
                    {
                        pos = land.LandData.UserLocation;
                        positionChanged = true;
                    }
                }
            }

            return true;
        }

        // Modify landing point based on telehubs or parcel restrictions.
        // This is a behavior coming from AVN, somewhat mimicking SL
        private bool CheckAndAdjustLandingPoint_SL(ref Vector3 pos, ref Vector3 lookat, ref bool positionChanged)
        {
            // dont mess with gods
            if(IsGod)
                return true;

            // respect region owner and managers
//            if(_scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(_uuid))
//                return true;

            if (!_scene.RegionInfo.EstateSettings.AllowDirectTeleport)
            {
                SceneObjectGroup telehub = null;
                if (_scene.RegionInfo.RegionSettings.TelehubObject != UUID.Zero && (telehub = _scene.GetSceneObjectGroup(_scene.RegionInfo.RegionSettings.TelehubObject)) != null)
                {
                    if(CheckAndAdjustTelehub(telehub, ref pos, ref positionChanged))
                        return true;
                }
            }

            // Honor bans, actually we don't honour them
            if (!_scene.TestLandRestrictions(UUID, out string reason, ref pos.X, ref pos.Y))
                return false;

            ILandObject land = _scene.LandChannel.GetLandObject(pos.X, pos.Y);
            if (land != null)
            {
                if (Scene.DebugTeleporting)
                    TeleportFlagsDebug();

                // If we come in via login, landmark or map, we want to
                // honor landing points. If we come in via Lure, we want
                // to ignore them.
                if ((_teleportFlags & (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)) ==
                                (TeleportFlags.ViaLogin | TeleportFlags.ViaRegionID)
                        || (_teleportFlags & adicionalLandPointFlags) != 0)
                {
                    if (land.LandData.LandingType == (byte)LandingType.LandingPoint &&
                        land.LandData.UserLocation != Vector3.Zero )
                        // &&
                        // land.LandData.OwnerID != _uuid )
                    {
                        pos = land.LandData.UserLocation;
                        if(land.LandData.UserLookAt != Vector3.Zero)
                            lookat = land.LandData.UserLookAt;
                        positionChanged = true;
                    }
                }
            }
            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private DetectedObject CreateDetObject(SceneObjectPart obj)
        {
            return new DetectedObject()
            {
                keyUUID = obj.UUID,
                nameStr = obj.Name,
                ownerUUID = obj.OwnerID,
                posVector = obj.AbsolutePosition,
                rotQuat = obj.GetWorldRotation(),
                velVector = obj.Velocity,
                colliderType = 0,
                groupUUID = obj.GroupID,
                linkNumber = 0
            };
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private DetectedObject CreateDetObject(ScenePresence av)
        {
            DetectedObject detobj = new DetectedObject()
            {
                keyUUID = av.UUID,
                nameStr = av.ControllingClient.Name,
                ownerUUID = av.UUID,
                posVector = av.AbsolutePosition,
                rotQuat = av.Rotation,
                velVector = av.Velocity,
                colliderType = av.IsNPC ? 0x20 : 0x1, // OpenSim\Region\ScriptEngine\Shared\Helpers.cs
                groupUUID = av.ControllingClient.ActiveGroupId,
                linkNumber = 0
            };

            if (av.IsSatOnObject)
                detobj.colliderType |= 0x4; //passive
            else if (detobj.velVector != Vector3.Zero)
                detobj.colliderType |= 0x2; //active
            return detobj;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private DetectedObject CreateDetObjectForGround()
        {
            DetectedObject detobj = new DetectedObject()
            {
                keyUUID = UUID.Zero,
                nameStr = "",
                ownerUUID = UUID.Zero,
                posVector = AbsolutePosition,
                rotQuat = Quaternion.Identity,
                velVector = Vector3.Zero,
                colliderType = 0,
                groupUUID = UUID.Zero,
                linkNumber = 0
            };
            return detobj;
        }

        private ColliderArgs CreateColliderArgs(SceneObjectPart dest, List<uint> colliders)
        {
            ColliderArgs colliderArgs = new ColliderArgs();
            List<DetectedObject> colliding = new List<DetectedObject>();
            foreach (uint localId in colliders)
            {
                if (localId == 0)
                    continue;

                SceneObjectPart obj = _scene.GetSceneObjectPart(localId);
                if (obj != null)
                {
                    if (!dest.CollisionFilteredOut(obj.UUID, obj.Name))
                        colliding.Add(CreateDetObject(obj));
                }
                else
                {
                    ScenePresence av = _scene.GetScenePresence(localId);
                    if (av != null && (!av.IsChildAgent))
                    {
                        if (!dest.CollisionFilteredOut(av.UUID, av.Name))
                            colliding.Add(CreateDetObject(av));
                    }
                }
            }

            colliderArgs.Colliders = colliding;

            return colliderArgs;
        }

        private delegate void ScriptCollidingNotification(uint localID, ColliderArgs message);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void SendCollisionEvent(SceneObjectGroup dest, scriptEvents ev, List<uint> colliders, ScriptCollidingNotification notify)
        {
            if (colliders.Count > 0)
            {
                if ((dest.RootPart.ScriptEvents & ev) != 0)
                {
                    ColliderArgs CollidingMessage = CreateColliderArgs(dest.RootPart, colliders);

                    if (CollidingMessage.Colliders.Count > 0)
                        notify(dest.RootPart.LocalId, CollidingMessage);
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void SendLandCollisionEvent(SceneObjectGroup dest, scriptEvents ev, ScriptCollidingNotification notify)
        {
            if ((dest.RootPart.ScriptEvents & ev) != 0)
            {
                ColliderArgs LandCollidingMessage = new ColliderArgs();
                List<DetectedObject> colliding = new List<DetectedObject>(){CreateDetObjectForGround()};
                LandCollidingMessage.Colliders = colliding;

                notify(dest.RootPart.LocalId, LandCollidingMessage);
            }
        }

        private void RaiseCollisionScriptEvents(Dictionary<uint, ContactPoint> coldata)
        {
            int nattachments = _attachments.Count;
            if (!ParcelAllowThisAvatarSounds && nattachments == 0)
                return;

            try
            {
                List<SceneObjectGroup> attachements;
                int numberCollisions = coldata.Count;

                if (numberCollisions == 0)
                {
                    if (_lastColliders.Count == 0 && !_lastLandCollide)
                        return; // nothing to do

                    if(_attachments.Count > 0)
                    {
                        attachements = GetAttachments();
                        for (int j = 0; j < attachements.Count; ++j)
                        {
                            SceneObjectGroup att = attachements[j];
                            scriptEvents attev = att.RootPart.ScriptEvents;
                            if (_lastLandCollide && (attev & scriptEvents.land_collision_end) != 0)
                                SendLandCollisionEvent(att, scriptEvents.land_collision_end, _scene.EventManager.TriggerScriptLandCollidingEnd);
                            if ((attev & scriptEvents.collision_end) != 0)
                                SendCollisionEvent(att, scriptEvents.collision_end, _lastColliders, _scene.EventManager.TriggerScriptCollidingEnd);
                        }
                    }
                    _lastLandCollide = false;
                    _lastColliders.Clear();
                    return;
                }

                bool thisHitLand = false;
                bool startLand = false;

                List<uint> thisHitColliders = new List<uint>(numberCollisions);
                List<uint> endedColliders = new List<uint>(_lastColliders.Count);
                List<uint> startedColliders = new List<uint>(numberCollisions);

                if(ParcelAllowThisAvatarSounds)
                {
                    List<CollisionForSoundInfo> soundinfolist = new List<CollisionForSoundInfo>();
                    CollisionForSoundInfo soundinfo;
                    ContactPoint curcontact;

                    foreach (uint id in coldata.Keys)
                    {
                        if(id == 0)
                        {
                            thisHitLand = true;
                            startLand = !_lastLandCollide;
                            if (startLand)
                            {
                                startLand = true;
                                curcontact = coldata[id];
                                if (Math.Abs(curcontact.RelativeSpeed) > 0.2)
                                {
                                    soundinfo = new CollisionForSoundInfo()
                                    {
                                        colliderID = id,
                                        position = curcontact.Position,
                                        relativeVel = curcontact.RelativeSpeed
                                    };
                                    soundinfolist.Add(soundinfo);
                                }
                            }
                        }
                        else
                        {
                            thisHitColliders.Add(id);
                            if (!_lastColliders.Contains(id))
                            {
                                startedColliders.Add(id);
                                curcontact = coldata[id];
                                if (Math.Abs(curcontact.RelativeSpeed) > 0.2)
                                {
                                    soundinfo = new CollisionForSoundInfo()
                                    {
                                        colliderID = id,
                                        position = curcontact.Position,
                                        relativeVel = curcontact.RelativeSpeed
                                    };
                                    soundinfolist.Add(soundinfo);
                                }
                            }
                        }
                    }
                    if (soundinfolist.Count > 0)
                        CollisionSounds.AvatarCollisionSound(this, soundinfolist);
                }
                else
                {
                    foreach (uint id in coldata.Keys)
                    {
                        if (id == 0)
                        {
                            thisHitLand = true;
                            startLand = !_lastLandCollide;
                        }
                        else
                        {
                            thisHitColliders.Add(id);
                            if (!_lastColliders.Contains(id))
                                startedColliders.Add(id);
                        }
                    }
                }

                // calculate things that ended colliding
                foreach (uint localID in _lastColliders)
                {
                    if (!thisHitColliders.Contains(localID))
                    {
                        endedColliders.Add(localID);
                    }
                }

                attachements = GetAttachments();
                for (int i = 0; i < attachements.Count; ++i)
                {
                    SceneObjectGroup att = attachements[i];
                    scriptEvents attev = att.RootPart.ScriptEvents;
                    if ((attev & scriptEvents.anyobjcollision) != 0)
                    {
                        SendCollisionEvent(att, scriptEvents.collision_start, startedColliders, _scene.EventManager.TriggerScriptCollidingStart);
                        SendCollisionEvent(att, scriptEvents.collision      , _lastColliders , _scene.EventManager.TriggerScriptColliding);
                        SendCollisionEvent(att, scriptEvents.collision_end  , endedColliders  , _scene.EventManager.TriggerScriptCollidingEnd);
                    }

                    if ((attev & scriptEvents.anylandcollision) != 0)
                    {
                        if (thisHitLand)
                        {
                            if (startLand)
                                SendLandCollisionEvent(att, scriptEvents.land_collision_start, _scene.EventManager.TriggerScriptLandCollidingStart);
                            SendLandCollisionEvent(att, scriptEvents.land_collision, _scene.EventManager.TriggerScriptLandColliding);
                        }
                        else if (_lastLandCollide)
                            SendLandCollisionEvent(att, scriptEvents.land_collision_end, _scene.EventManager.TriggerScriptLandCollidingEnd);
                    }
                }

                _lastLandCollide = thisHitLand;
                _lastColliders = thisHitColliders;
            }
            catch { }
        }

        private void TeleportFlagsDebug() {

            // Some temporary debugging help to show all the TeleportFlags we have...
            bool HG = false;
            if((_teleportFlags & TeleportFlags.ViaHGLogin) == TeleportFlags.ViaHGLogin)
                HG = true;

            _log.InfoFormat("[SCENE PRESENCE]: TELEPORT ******************");

            uint i = 0u;
            for (int x = 0; x <= 30 ; x++, i = 1u << x)
            {
                i = 1u << x;

                if((_teleportFlags & (TeleportFlags)i) == (TeleportFlags)i)
                    if (HG == false)
                        _log.InfoFormat("[SCENE PRESENCE]: Teleport Flags include {0}", ((TeleportFlags) i).ToString());
                    else
                        _log.InfoFormat("[SCENE PRESENCE]: HG Teleport Flags include {0}", ((TeleportFlags)i).ToString());
            }

            _log.InfoFormat("[SCENE PRESENCE]: TELEPORT ******************");

        }

        private void parcelGodCheck(UUID currentParcelID)
        {
            List<ScenePresence> allpresences = _scene.GetScenePresences();

            foreach (ScenePresence p in allpresences)
            {
                if (p.IsDeleted || p.IsChildAgent || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                    continue;

                if (p.ParcelHideThisAvatar && p.currentParcelUUID != currentParcelID)
                {
                    if (IsViewerUIGod)
                        p.SendViewTo(this);
                    else
                        p.SendKillTo(this);
                }
            }
        }

        private void ParcelCrossCheck(UUID currentParcelID,UUID previusParcelID,
                            bool currentParcelHide, bool previusParcelHide, bool oldhide, bool check)
        {
            List<ScenePresence> killsToSendto = new List<ScenePresence>();
            List<ScenePresence> killsToSendme = new List<ScenePresence>();
            List<ScenePresence> viewsToSendto = new List<ScenePresence>();
            List<ScenePresence> viewsToSendme = new List<ScenePresence>();
            List<ScenePresence> allpresences = null;

            if (IsInTransit || IsChildAgent)
                return;

            if (check)
            {
                // check is relative to current parcel only
                if (oldhide == currentParcelHide)
                    return;

                allpresences = _scene.GetScenePresences();

                if (oldhide)
                { // where private
                    foreach (ScenePresence p in allpresences)
                    {
                        if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                            continue;

                        // those on not on parcel see me
                        if (currentParcelID != p.currentParcelUUID)
                        {
                            viewsToSendto.Add(p); // they see me
                        }
                    }
                } // where private end

                else
                { // where public
                    foreach (ScenePresence p in allpresences)
                    {
                        if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                            continue;

                        // those not on parcel dont see me
                        if (currentParcelID != p.currentParcelUUID && !p.IsViewerUIGod)
                        {
                            killsToSendto.Add(p); // they dont see me
                        }
                    }
                } // where public end

                allpresences.Clear();
            }
            else
            {
                if (currentParcelHide)
                {
                    // now on a private parcel
                    allpresences = _scene.GetScenePresences();

                    if (previusParcelHide && previusParcelID != UUID.Zero)
                    {
                        foreach (ScenePresence p in allpresences)
                        {
                            if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                                continue;

                            // only those on previous parcel need receive kills
                            if (previusParcelID == p.currentParcelUUID)
                            {
                                if(!p.IsViewerUIGod)
                                    killsToSendto.Add(p); // they dont see me
                                if(!IsViewerUIGod)
                                    killsToSendme.Add(p);  // i dont see them
                            }
                            // only those on new parcel need see
                            if (currentParcelID == p.currentParcelUUID)
                            {
                                viewsToSendto.Add(p); // they see me
                                viewsToSendme.Add(p); // i see them
                            }
                        }
                    }
                    else
                    {
                        //was on a public area
                        allpresences = _scene.GetScenePresences();

                        foreach (ScenePresence p in allpresences)
                        {
                            if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                                continue;

                            // those not on new parcel dont see me
                            if (currentParcelID != p.currentParcelUUID && !p.IsViewerUIGod)
                            {
                                killsToSendto.Add(p); // they dont see me
                            }
                            else
                            {
                                viewsToSendme.Add(p); // i see those on it
                            }
                        }
                    }
                    allpresences.Clear();
                } // now on a private parcel end

                else
                {
                    // now on public parcel
                    if (previusParcelHide && previusParcelID != UUID.Zero)
                    {
                        // was on private area
                        allpresences = _scene.GetScenePresences();

                        foreach (ScenePresence p in allpresences)
                        {
                            if (p.IsDeleted || p == this || p.ControllingClient == null || !p.ControllingClient.IsActive)
                                continue;
                            // only those old parcel need kills
                            if (previusParcelID == p.currentParcelUUID && !IsViewerUIGod)
                            {
                                killsToSendme.Add(p);  // i dont see them
                            }
                            else
                            {
                                viewsToSendto.Add(p); // they see me
                            }
                        }
                    }
                    else
                        return; // was on a public area also
                } // now on public parcel end
            }

            // send the things

            if (killsToSendto.Count > 0)
            {
                foreach (ScenePresence p in killsToSendto)
                {
//                    _log.Debug("[AVATAR]: killTo: " + Lastname + " " + p.Lastname);
                    SendKillTo(p);
                }
            }

            if (killsToSendme.Count > 0)
            {
                foreach (ScenePresence p in killsToSendme)
                {
//                    _log.Debug("[AVATAR]: killToMe: " + Lastname + " " + p.Lastname);
                    p.SendKillTo(this);
                }
            }

            if (viewsToSendto.Count > 0)
            {
                foreach (ScenePresence p in viewsToSendto)
                {
                    SendViewTo(p);
                }
            }

            if (viewsToSendme.Count > 0 )
            {
                foreach (ScenePresence p in viewsToSendme)
                {
                    if (p.IsChildAgent)
                        continue;
//                   _log.Debug("[AVATAR]: viewMe: " + Lastname + "<-" + p.Lastname);
                    p.SendViewTo(this);
                }
            }
        }

        public void HasMovedAway(bool nearRegion)
        {
            if (nearRegion)
            {
                if (Scene.AttachmentsModule != null)
                    Scene.AttachmentsModule.DeleteAttachmentsFromScene(this, true);

                if (!ParcelHideThisAvatar || IsViewerUIGod)
                    return;

                List<ScenePresence> allpresences = _scene.GetScenePresences();
                foreach (ScenePresence p in allpresences)
                {
                    if (p.IsDeleted || p == this || p.IsChildAgent || p.ControllingClient == null || !p.ControllingClient.IsActive)
                        continue;

                    if (p.currentParcelUUID == _currentParcelUUID)
                    {
                        p.SendKillTo(this);
                    }
                }
            }
            else
            {
                lock (_completeMovementLock)
                {
                    GodController.HasMovedAway();
                    NeedInitialData = -1;
                    _gotRegionHandShake = false;
                }

                List<ScenePresence> allpresences = _scene.GetScenePresences();
                foreach (ScenePresence p in allpresences)
                {
                    if (p == this)
                        continue;
                    SendKillTo(p);
                    if (!p.IsChildAgent)
                        p.SendKillTo(this);
                }

                if (Scene.AttachmentsModule != null)
                    Scene.AttachmentsModule.DeleteAttachmentsFromScene(this, true);
            }
        }


//  kill with attachs root kills
        public void SendKillTo(ScenePresence p)
        {
            List<uint> ids = new List<uint>(_attachments.Count + 1);
            foreach (SceneObjectGroup sog in _attachments)
            {
                ids.Add(sog.RootPart.LocalId);
            }

            ids.Add(LocalId);
            p.ControllingClient.SendKillObject(ids);
        }

/*
// kill with hack
        public void SendKillTo(ScenePresence p)
        {
            foreach (SceneObjectGroup sog in _attachments)
                p.ControllingClient.SendPartFullUpdate(sog.RootPart, LocalId + 1);
            p.ControllingClient.SendKillObject(new List<uint> { LocalId });
        }
*/
        public void SendViewTo(ScenePresence p)
        {
            SendAvatarDataToAgentNF(p);
            SendAppearanceToAgent(p);
            if (Animator != null)
                Animator.SendAnimPackToClient(p.ControllingClient);
            SendAttachmentsToAgentNF(p);
        }

        public void SetAnimationOverride(string animState, UUID animID)
        {
            Overrides.SetOverride(animState, animID);
//            Animator.SendAnimPack();
            Animator.ForceUpdateMovementAnimations();
        }

        public UUID GetAnimationOverride(string animState)
        {
            return Overrides.GetOverriddenAnimation(animState);
        }

        // http caps assets bandwidth control
        private readonly int _capbandwidth = -1;
        private int _bandwidth = 100000;
        private int _bandwidthBurst = 20000;
        private int _bytesControl;
        private double _lastBandwithTime;
        private readonly object _throttleLock = new object();

        public bool CapCanSendAsset(int type, int size)
        {
            if(size == 0)
                return true;

            lock (_throttleLock)
            {
                if (type > 1)
                {
                    // not texture or mesh
                    _bytesControl -= size;
                    return true;
                }

                double currenttime = Util.GetTimeStamp();
                double timeElapsed = currenttime - _lastBandwithTime;
                if (timeElapsed > .02)
                {
                    _lastBandwithTime = currenttime;
                    int add = (int)(_bandwidth * timeElapsed);
                    _bytesControl += add;
                    if (_bytesControl > _bandwidthBurst)
                        _bytesControl = _bandwidthBurst;
                }
                if (_bytesControl > 0 )
                {
                    _bytesControl -= size;
                    return true;
                }
            }
            return false;
        }
    }
}
