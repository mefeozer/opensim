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

using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Data.Null
{
    public class NullDataService : ISimulationDataService
    {
        private readonly NullDataStore _store;

        public NullDataService()
        {
            _store = new NullDataStore();
        }

        public void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            _store.StoreObject(obj, regionUUID);
        }

        public void RemoveObject(UUID uuid, UUID regionUUID)
        {
            _store.RemoveObject(uuid, regionUUID);
        }

        public void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
        {
            _store.StorePrimInventory(primID, items);
        }

        public List<SceneObjectGroup> LoadObjects(UUID regionUUID)
        {
            return _store.LoadObjects(regionUUID);
        }

        public void StoreTerrain(double[,] terrain, UUID regionID)
        {
            _store.StoreTerrain(terrain, regionID);
        }

        public void StoreTerrain(TerrainData terrain, UUID regionID)
        {
            _store.StoreTerrain(terrain, regionID);
        }

        public void StoreBakedTerrain(TerrainData terrain, UUID regionID)
        {
            _store.StoreBakedTerrain(terrain, regionID);
        }

        public double[,] LoadTerrain(UUID regionID)
        {
            return _store.LoadTerrain(regionID);
        }

        public TerrainData LoadTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            return _store.LoadTerrain(regionID, pSizeX, pSizeY, pSizeZ);
        }

        public TerrainData LoadBakedTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            return _store.LoadBakedTerrain(regionID, pSizeX, pSizeY, pSizeZ);
        }

        public void StoreLandObject(ILandObject Parcel)
        {
            _store.StoreLandObject(Parcel);
        }

        public void RemoveLandObject(UUID globalID)
        {
            _store.RemoveLandObject(globalID);
        }

        public List<LandData> LoadLandObjects(UUID regionUUID)
        {
            return _store.LoadLandObjects(regionUUID);
        }

        public void StoreRegionSettings(RegionSettings rs)
        {
            _store.StoreRegionSettings(rs);
        }

        public RegionSettings LoadRegionSettings(UUID regionUUID)
        {
            return _store.LoadRegionSettings(regionUUID);
        }

        public string LoadRegionEnvironmentSettings(UUID regionUUID)
        {
            return _store.LoadRegionEnvironmentSettings(regionUUID);
        }

        public void StoreRegionEnvironmentSettings(UUID regionUUID, string settings)
        {
            _store.StoreRegionEnvironmentSettings(regionUUID, settings);
        }

        public void RemoveRegionEnvironmentSettings(UUID regionUUID)
        {
            _store.RemoveRegionEnvironmentSettings(regionUUID);
        }

        public UUID[] GetObjectIDs(UUID regionID)
        {
            return new UUID[0];
        }

        public void SaveExtra(UUID regionID, string name, string value)
        {
        }

        public void RemoveExtra(UUID regionID, string name)
        {
        }

        public Dictionary<string, string> GetExtra(UUID regionID)
        {
            return null;
        }
    }

    /// <summary>
    /// Mock region data plugin.  This obeys the api contract for persistence but stores everything in memory, so that
    /// tests can check correct persistence.
    /// </summary>
    public class NullDataStore : ISimulationDataStore
    {
//        private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        protected Dictionary<UUID, RegionSettings> _regionSettings = new Dictionary<UUID, RegionSettings>();
        protected Dictionary<UUID, SceneObjectPart> _sceneObjectParts = new Dictionary<UUID, SceneObjectPart>();
        protected Dictionary<UUID, ICollection<TaskInventoryItem>> _primItems
            = new Dictionary<UUID, ICollection<TaskInventoryItem>>();
        protected Dictionary<UUID, TerrainData> _terrains = new Dictionary<UUID, TerrainData>();
        protected Dictionary<UUID, TerrainData> _bakedterrains = new Dictionary<UUID, TerrainData>();
        protected Dictionary<UUID, LandData> _landData = new Dictionary<UUID, LandData>();

        public void Initialise(string dbfile)
        {
            return;
        }

        public void Dispose()
        {
        }

        public void StoreRegionSettings(RegionSettings rs)
        {
            _regionSettings[rs.RegionUUID] = rs;
        }

        #region Environment Settings
        public string LoadRegionEnvironmentSettings(UUID regionUUID)
        {
            //This connector doesn't support the Environment module yet
            return string.Empty;
        }

        public void StoreRegionEnvironmentSettings(UUID regionUUID, string settings)
        {
            //This connector doesn't support the Environment module yet
        }

        public void RemoveRegionEnvironmentSettings(UUID regionUUID)
        {
            //This connector doesn't support the Environment module yet
        }
        #endregion

        public RegionSettings LoadRegionSettings(UUID regionUUID)
        {
            RegionSettings rs = null;
            _regionSettings.TryGetValue(regionUUID, out rs);

            if (rs == null)
                rs = new RegionSettings();

            return rs;
        }

        public void StoreObject(SceneObjectGroup obj, UUID regionUUID)
        {
            // We can't simply store groups here because on delinking, OpenSim will not update the original group
            // directly.  Rather, the newly delinked parts will be updated to be in their own scene object group
            // Therefore, we need to store parts rather than groups.
            foreach (SceneObjectPart prim in obj.Parts)
            {
//                _log.DebugFormat(
//                    "[MOCK REGION DATA PLUGIN]: Storing part {0} {1} in object {2} {3} in region {4}",
//                    prim.Name, prim.UUID, obj.Name, obj.UUID, regionUUID);

                _sceneObjectParts[prim.UUID] = prim;
            }
        }

        public void RemoveObject(UUID obj, UUID regionUUID)
        {
            // All parts belonging to the object with the uuid are removed.
            List<SceneObjectPart> parts = new List<SceneObjectPart>(_sceneObjectParts.Values);
            foreach (SceneObjectPart part in parts)
            {
                if (part.ParentGroup.UUID == obj)
                {
//                    _log.DebugFormat(
//                        "[MOCK REGION DATA PLUGIN]: Removing part {0} {1} as part of object {2} from {3}",
//                        part.Name, part.UUID, obj, regionUUID);
                    _sceneObjectParts.Remove(part.UUID);
                }
            }
        }

        public void StorePrimInventory(UUID primID, ICollection<TaskInventoryItem> items)
        {
            _primItems[primID] = items;
        }

        public List<SceneObjectGroup> LoadObjects(UUID regionUUID)
        {
            Dictionary<UUID, SceneObjectGroup> objects = new Dictionary<UUID, SceneObjectGroup>();

            // Create all of the SOGs from the root prims first
            foreach (SceneObjectPart prim in _sceneObjectParts.Values)
            {
                if (prim.IsRoot)
                {
//                    _log.DebugFormat(
//                        "[MOCK REGION DATA PLUGIN]: Loading root part {0} {1} in {2}", prim.Name, prim.UUID, regionUUID);
                    objects[prim.UUID] = new SceneObjectGroup(prim);
                }
            }

            // Add all of the children objects to the SOGs
            foreach (SceneObjectPart prim in _sceneObjectParts.Values)
            {
                SceneObjectGroup sog;
                if (prim.UUID != prim.ParentUUID)
                {
                    if (objects.TryGetValue(prim.ParentUUID, out sog))
                    {
                        int originalLinkNum = prim.LinkNum;

                        sog.AddPart(prim);

                        // SceneObjectGroup.AddPart() tries to be smart and automatically set the LinkNum.
                        // We override that here
                        if (originalLinkNum != 0)
                            prim.LinkNum = originalLinkNum;
                    }
                    else
                    {
//                        _log.WarnFormat(
//                            "[MOCK REGION DATA PLUGIN]: Database contains an orphan child prim {0} {1} in region {2} pointing to missing parent {3}.  This prim will not be loaded.",
//                            prim.Name, prim.UUID, regionUUID, prim.ParentUUID);
                    }
                }
            }

            // TODO: Load items.  This is assymetric - we store items as a separate method but don't retrieve them that
            // way!

            return new List<SceneObjectGroup>(objects.Values);
        }

        public void StoreTerrain(TerrainData ter, UUID regionID)
        {
            _terrains[regionID] = ter;
        }

        public void StoreBakedTerrain(TerrainData ter, UUID regionID)
        {
            _bakedterrains[regionID] = ter;
        }

        public void StoreTerrain(double[,] ter, UUID regionID)
        {
            _terrains[regionID] = new TerrainData(ter);
        }

        public TerrainData LoadTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            if (_terrains.ContainsKey(regionID))
                return _terrains[regionID];
            else
                return null;
        }

        public TerrainData LoadBakedTerrain(UUID regionID, int pSizeX, int pSizeY, int pSizeZ)
        {
            if (_bakedterrains.ContainsKey(regionID))
                return _bakedterrains[regionID];
            else
                return null;
        }

        public double[,] LoadTerrain(UUID regionID)
        {
            if (_terrains.ContainsKey(regionID))
                return _terrains[regionID].GetDoubles();
            else
                return null;
        }

        public void RemoveLandObject(UUID globalID)
        {
            if (_landData.ContainsKey(globalID))
                _landData.Remove(globalID);
        }

        public void StoreLandObject(ILandObject land)
        {
            _landData[land.LandData.GlobalID] = land.LandData;
        }

        public List<LandData> LoadLandObjects(UUID regionUUID)
        {
            return new List<LandData>(_landData.Values);
        }

        public void Shutdown()
        {
        }

        public UUID[] GetObjectIDs(UUID regionID)
        {
            return new UUID[0];
        }

        public void SaveExtra(UUID regionID, string name, string value)
        {
        }

        public void RemoveExtra(UUID regionID, string name)
        {
        }

        public Dictionary<string, string> GetExtra(UUID regionID)
        {
            return null;
        }
    }
}
