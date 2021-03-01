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
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using Mono.Addins;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.CoreModules.World.Land
{
    public class ParcelCounts
    {
        public int Owner = 0;
        public int Group = 0;
        public int Others = 0;
        public int Selected = 0;
        public Dictionary <UUID, int> Users = new Dictionary <UUID, int>();
    }

    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "PrimCountModule")]
    public class PrimCountModule : IPrimCountModule, INonSharedRegionModule
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Scene _Scene;
        private readonly Dictionary<UUID, PrimCounts> _PrimCounts =
                new Dictionary<UUID, PrimCounts>();
        private readonly Dictionary<UUID, UUID> _OwnerMap =
                new Dictionary<UUID, UUID>();
        private readonly Dictionary<UUID, int> _SimwideCounts =
                new Dictionary<UUID, int>();
        private readonly Dictionary<UUID, ParcelCounts> _ParcelCounts =
                new Dictionary<UUID, ParcelCounts>();

        /// <value>
        /// For now, a simple simwide taint to get this up. Later parcel based
        /// taint to allow recounting a parcel if only ownership has changed
        /// without recounting the whole sim.
        ///
        /// We start out tainted so that the first get call resets the various prim counts.
        /// </value>
        private bool _Tainted = true;

        private readonly object _TaintLock = new object();

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
        }

        public void AddRegion(Scene scene)
        {
            _Scene = scene;

            _Scene.RegisterModuleInterface<IPrimCountModule>(this);

            _Scene.EventManager.OnObjectAddedToScene += OnParcelPrimCountAdd;
            _Scene.EventManager.OnObjectBeingRemovedFromScene +=  OnObjectBeingRemovedFromScene;
            _Scene.EventManager.OnParcelPrimCountTainted +=  OnParcelPrimCountTainted;
            _Scene.EventManager.OnLandObjectAdded += delegate(ILandObject lo) { OnParcelPrimCountTainted(); };
        }

        public void RegionLoaded(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void Close()
        {
        }

        public string Name => "PrimCountModule";

        private void OnParcelPrimCountAdd(SceneObjectGroup obj)
        {
            // If we're tainted already, don't bother to add. The next
            // access will cause a recount anyway
            lock (_TaintLock)
            {
                if (!_Tainted)
                    AddObject(obj);
//                else
//                    _log.DebugFormat(
//                        "[PRIM COUNT MODULE]: Ignoring OnParcelPrimCountAdd() for {0} on {1} since count is tainted",
//                        obj.Name, _Scene.RegionInfo.RegionName);
            }
        }

        private void OnObjectBeingRemovedFromScene(SceneObjectGroup obj)
        {
            // Don't bother to update tainted counts
            lock (_TaintLock)
            {
                if (!_Tainted)
                    RemoveObject(obj);
//                else
//                    _log.DebugFormat(
//                        "[PRIM COUNT MODULE]: Ignoring OnObjectBeingRemovedFromScene() for {0} on {1} since count is tainted",
//                        obj.Name, _Scene.RegionInfo.RegionName);
            }
        }

        private void OnParcelPrimCountTainted()
        {
//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: OnParcelPrimCountTainted() called on {0}", _Scene.RegionInfo.RegionName);

            lock (_TaintLock)
                _Tainted = true;
        }

        public void TaintPrimCount(ILandObject land)
        {
            lock (_TaintLock)
                _Tainted = true;
        }

        public void TaintPrimCount(int x, int y)
        {
            lock (_TaintLock)
                _Tainted = true;
        }

        public void TaintPrimCount()
        {
            lock (_TaintLock)
                _Tainted = true;
        }

        // NOTE: Call under Taint Lock
        private void AddObject(SceneObjectGroup obj)
        {
            if (obj.IsAttachment)
                return;
            if ((obj.RootPart.Flags & PrimFlags.TemporaryOnRez) != 0)
                return;

            Vector3 pos = obj.AbsolutePosition;
            ILandObject landObject = _Scene.LandChannel.GetLandObject(pos.X, pos.Y);

            // If for some reason there is no land object (perhaps the object is out of bounds) then we can't count it
            if (landObject == null)
            {
//                _log.WarnFormat(
//                    "[PRIM COUNT MODULE]: Found no land object for {0} at position ({1}, {2}) on {3}",
//                    obj.Name, pos.X, pos.Y, _Scene.RegionInfo.RegionName);

                return;
            }

            LandData landData = landObject.LandData;

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: Adding object {0} with {1} parts to prim count for parcel {2} on {3}",
//                obj.Name, obj.Parts.Length, landData.Name, _Scene.RegionInfo.RegionName);

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: Object {0} is owned by {1} over land owned by {2}",
//                obj.Name, obj.OwnerID, landData.OwnerID);

            ParcelCounts parcelCounts;
            if (_ParcelCounts.TryGetValue(landData.GlobalID, out parcelCounts))
            {
                UUID landOwner = landData.OwnerID;
                int partCount = obj.GetPartCount();

                _SimwideCounts[landOwner] += partCount;
                if (parcelCounts.Users.ContainsKey(obj.OwnerID))
                    parcelCounts.Users[obj.OwnerID] += partCount;
                else
                    parcelCounts.Users[obj.OwnerID] = partCount;

                if (obj.IsSelected || obj.GetSittingAvatarsCount() > 0)
                    parcelCounts.Selected += partCount;

                if (obj.OwnerID == landData.OwnerID)
                    parcelCounts.Owner += partCount;
                else if (landData.GroupID != UUID.Zero && obj.GroupID == landData.GroupID)
                    parcelCounts.Group += partCount;
                else
                    parcelCounts.Others += partCount;
            }
        }

        // NOTE: Call under Taint Lock
        private void RemoveObject(SceneObjectGroup obj)
        {
//            _log.DebugFormat("[PRIM COUNT MODULE]: Removing object {0} {1} from prim count", obj.Name, obj.UUID);

            // Currently this is being done by tainting the count instead.
        }

        public IPrimCounts GetPrimCounts(UUID parcelID)
        {
//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetPrimCounts for parcel {0} in {1}", parcelID, _Scene.RegionInfo.RegionName);

            PrimCounts primCounts;

            lock (_PrimCounts)
            {
                if (_PrimCounts.TryGetValue(parcelID, out primCounts))
                    return primCounts;

                primCounts = new PrimCounts(parcelID, this);
                _PrimCounts[parcelID] = primCounts;
            }
            return primCounts;
        }


        /// <summary>
        /// Get the number of prims on the parcel that are owned by the parcel owner.
        /// </summary>
        /// <param name="parcelID"></param>
        /// <returns></returns>
        public int GetOwnerCount(UUID parcelID)
        {
            int count = 0;

            lock (_TaintLock)
            {
                if (_Tainted)
                    Recount();

                ParcelCounts counts;
                if (_ParcelCounts.TryGetValue(parcelID, out counts))
                    count = counts.Owner;
            }

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetOwnerCount for parcel {0} in {1} returning {2}",
//                parcelID, _Scene.RegionInfo.RegionName, count);

            return count;
        }

        /// <summary>
        /// Get the number of prims on the parcel that have been set to the group that owns the parcel.
        /// </summary>
        /// <param name="parcelID"></param>
        /// <returns></returns>
        public int GetGroupCount(UUID parcelID)
        {
            int count = 0;

            lock (_TaintLock)
            {
                if (_Tainted)
                    Recount();

                ParcelCounts counts;
                if (_ParcelCounts.TryGetValue(parcelID, out counts))
                    count = counts.Group;
            }

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetGroupCount for parcel {0} in {1} returning {2}",
//                parcelID, _Scene.RegionInfo.RegionName, count);

            return count;
        }

        /// <summary>
        /// Get the number of prims on the parcel that are not owned by the parcel owner or set to the parcel group.
        /// </summary>
        /// <param name="parcelID"></param>
        /// <returns></returns>
        public int GetOthersCount(UUID parcelID)
        {
            int count = 0;

            lock (_TaintLock)
            {
                if (_Tainted)
                    Recount();

                ParcelCounts counts;
                if (_ParcelCounts.TryGetValue(parcelID, out counts))
                    count = counts.Others;
            }

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetOthersCount for parcel {0} in {1} returning {2}",
//                parcelID, _Scene.RegionInfo.RegionName, count);

            return count;
        }

        /// <summary>
        /// Get the number of selected prims.
        /// </summary>
        /// <param name="parcelID"></param>
        /// <returns></returns>
        public int GetSelectedCount(UUID parcelID)
        {
            int count = 0;

            lock (_TaintLock)
            {
                if (_Tainted)
                    Recount();

                ParcelCounts counts;
                if (_ParcelCounts.TryGetValue(parcelID, out counts))
                    count = counts.Selected;
            }

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetSelectedCount for parcel {0} in {1} returning {2}",
//                parcelID, _Scene.RegionInfo.RegionName, count);

            return count;
        }

        /// <summary>
        /// Get the total count of owner, group and others prims on the parcel.
        /// FIXME: Need to do selected prims once this is reimplemented.
        /// </summary>
        /// <param name="parcelID"></param>
        /// <returns></returns>
        public int GetTotalCount(UUID parcelID)
        {
            int count = 0;

            lock (_TaintLock)
            {
                if (_Tainted)
                    Recount();

                ParcelCounts counts;
                if (_ParcelCounts.TryGetValue(parcelID, out counts))
                {
                    count = counts.Owner;
                    count += counts.Group;
                    count += counts.Others;
                }
            }

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetTotalCount for parcel {0} in {1} returning {2}",
//                parcelID, _Scene.RegionInfo.RegionName, count);

            return count;
        }

        /// <summary>
        /// Get the number of prims that are in the entire simulator for the owner of this parcel.
        /// </summary>
        /// <param name="parcelID"></param>
        /// <returns></returns>
        public int GetSimulatorCount(UUID parcelID)
        {
            int count = 0;

            lock (_TaintLock)
            {
                if (_Tainted)
                    Recount();

                UUID owner;
                if (_OwnerMap.TryGetValue(parcelID, out owner))
                {
                    int val;
                    if (_SimwideCounts.TryGetValue(owner, out val))
                        count = val;
                }
            }

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetOthersCount for parcel {0} in {1} returning {2}",
//                parcelID, _Scene.RegionInfo.RegionName, count);

            return count;
        }

        /// <summary>
        /// Get the number of prims that a particular user owns on this parcel.
        /// </summary>
        /// <param name="parcelID"></param>
        /// <param name="userID"></param>
        /// <returns></returns>
        public int GetUserCount(UUID parcelID, UUID userID)
        {
            int count = 0;

            lock (_TaintLock)
            {
                if (_Tainted)
                    Recount();

                ParcelCounts counts;
                if (_ParcelCounts.TryGetValue(parcelID, out counts))
                {
                    int val;
                    if (counts.Users.TryGetValue(userID, out val))
                        count = val;
                }
            }

//            _log.DebugFormat(
//                "[PRIM COUNT MODULE]: GetUserCount for user {0} in parcel {1} in region {2} returning {3}",
//                userID, parcelID, _Scene.RegionInfo.RegionName, count);

            return count;
        }

        // NOTE: This method MUST be called while holding the taint lock!
        private void Recount()
        {
//            _log.DebugFormat("[PRIM COUNT MODULE]: Recounting prims on {0}", _Scene.RegionInfo.RegionName);

            _OwnerMap.Clear();
            _SimwideCounts.Clear();
            _ParcelCounts.Clear();

            List<ILandObject> land = _Scene.LandChannel.AllParcels();

            foreach (ILandObject l in land)
            {
                LandData landData = l.LandData;

                _OwnerMap[landData.GlobalID] = landData.OwnerID;
                _SimwideCounts[landData.OwnerID] = 0;
//                _log.DebugFormat(
//                    "[PRIM COUNT MODULE]: Initializing parcel count for {0} on {1}",
//                    landData.Name, _Scene.RegionInfo.RegionName);
                _ParcelCounts[landData.GlobalID] = new ParcelCounts();
            }

            _Scene.ForEachSOG(AddObject);

            lock (_PrimCounts)
            {
                List<UUID> primcountKeys = new List<UUID>(_PrimCounts.Keys);
                foreach (UUID k in primcountKeys)
                {
                    if (!_OwnerMap.ContainsKey(k))
                        _PrimCounts.Remove(k);
                }
            }

            _Tainted = false;
        }
    }

    public class PrimCounts : IPrimCounts
    {
        private readonly PrimCountModule _Parent;
        private readonly UUID _ParcelID;
        private readonly UserPrimCounts _UserPrimCounts;

        public PrimCounts (UUID parcelID, PrimCountModule parent)
        {
            _ParcelID = parcelID;
            _Parent = parent;

            _UserPrimCounts = new UserPrimCounts(this);
        }

        public int Owner => _Parent.GetOwnerCount(_ParcelID);

        public int Group => _Parent.GetGroupCount(_ParcelID);

        public int Others => _Parent.GetOthersCount(_ParcelID);

        public int Selected => _Parent.GetSelectedCount(_ParcelID);

        public int Total => _Parent.GetTotalCount(_ParcelID);

        public int Simulator => _Parent.GetSimulatorCount(_ParcelID);

        public IUserPrimCounts Users => _UserPrimCounts;

        public int GetUserCount(UUID userID)
        {
            return _Parent.GetUserCount(_ParcelID, userID);
        }
    }

    public class UserPrimCounts : IUserPrimCounts
    {
        private readonly PrimCounts _Parent;

        public UserPrimCounts(PrimCounts parent)
        {
            _Parent = parent;
        }

        public int this[UUID userID] => _Parent.GetUserCount(userID);
    }
}
